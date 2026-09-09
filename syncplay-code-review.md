# SyncPlay Code Review — Jellyfin 12.0 Re-base

> **Supersedes** the 10.11-era review of the same name (2026-04 → 2026-09-05). That
> document's incident sections (2026-08-23, 2026-08-29, 2026-09-05) and findings #1–#25
> are preserved here in the status table below; everything else is re-verified against
> the current tree.

## Context

The fork (`bigr4232/jellyfin`, branch `dev-12.0`) was re-based onto the Jellyfin **12.0**
release on 2026-09-07 (base commit `6c073e19dd` "Bump version to 12.0", 122 commits after
`v12.0-rc7`). The fork's ten SyncPlay fix commits were replayed on top:

```text
ab30d1c355  Preserve upstream WaitingGroupState ping tests alongside ported suite
0481377277  Bound SyncPlay Waiting state and guard sessions with no user      (#22, #25)
449324a25c  Bound SyncPlay resume delay and fix recovery delay unit mismatch  (#21, #2)
75fcc2302b  Reset SyncPlay correction budget when session converges in resume path (#20)
3c16fb5bfb  Tolerate Ready report latency in SyncPlay ahead-of-group check    (#19)
4ab8fb777c  Guard empty user id in SyncPlay auth handler to return 403        (#18)
4796e8c588  Fix PluginManagerTests for millisecond JSON timestamps            (#1 fallout)
ecb6189ec6  Guard past-dated pause and cap SyncPlay correction loop           (#16, #17)
9a43522b01  Fix SyncPlay membership loss on WebSocket reconnect               (#15)
b5b6f14179  fix time mismatch on ios                                          (#1)
6c073e19dd  Bump version to 12.0                                             <- base
```

This review re-reads the whole server-side SyncPlay implementation on the new base,
re-verifies every prior finding, and reports what the re-base changed and what it broke.
No new live incidents were recorded between the last 10.11 review (2026-09-05) and this
re-base; the 12.0 deployment (`dev-12.0-local-deploy` branch: patched `Jellyfin.Server`
published self-contained and overlaid on `jellyfin/jellyfin:12.0`) awaits the same live
smoke tests as before.

## What upstream 12.0 changed in SyncPlay (before the fork's fixes)

The 10.11 → 12.0 delta in the SyncPlay code is small but material. Everything the fork's
own fixes touched had already moved under it:

| Upstream change | Commit | Effect on the old review |
| --- | --- | --- |
| `MaxPing` (10 000 ms) + `UpdatePing` clamps reported pings | `7ce911a401` | Closes the "unbounded ping" half of the old #8 discussion; a single chatty client can no longer push the group's resume delay arbitrarily far or overflow the arithmetic |
| `GetHighestPing()` falls back to `DefaultPing` for an empty group | `e356fe9146` | **Closes old #11** |
| `HasAccessToQueue` null-guards `GetItemById` | (12.0 base) | **Partially closes old #9** — the access-check path no longer NREs on a deleted item, but four other dereference sites still do (see #9 below) |
| `JoinGroup` "Restore session" branch no longer increments the user counter | `51a7d5d08a` | Upstream merged the same double-count fix the old #15 review demanded; the fork's grace-period fix now restores into a branch that is already correct |
| `HandleRequest` re-check drops requests whose session vanished while waiting on the group lock | `5acb200c02` | Hardening the old review never covered: a request that queued behind the group lock no longer acts on a session that left in the meantime |
| Resume-delay floor computed in ticks (`DefaultPing × TicksPerMillisecond`) | `0c05d9d1a9` | **Upstream merged old #2's fix independently**; the fork's `449324a25c` lands the same line plus #21's bound |
| `PlayQueueManager`: sorted-mode guard for a redundant `SetShuffleMode(Sorted)` (`e5bfe562bc`), `SetPlayingItemByIndex` off-by-one (`>` → `>=`), `RemoveFromPlaylist` index-shift fix, empty-queue guards in `Next()`/`Previous()` | (12.0 base) | Removes a class of queue corruption the old review never reached |
| Lost WebSockets are disposed so the session ends; new integration test | `8f3eb3205d` (#17079) | Kills the *zombie participant* variant of #15: a socket the keep-alive watchdog gave up on no longer keeps its SyncPlay membership alive forever. Verified by `SyncPlayLostWebSocketTests` (passes on this branch, 50 s) |
| xunit v2 → **xunit.v3 3.2.2** | (12.0 base) | **Broke the fork's test build — see #26, fixed in this review** |
| Session id key now includes the user (`appName + deviceId + userId`) | (12.0 base, [SessionManager.cs:481](Emby.Server.Implementations/Session/SessionManager.cs#L481)) | Corrects a factual claim in the old #15 write-up ("no user" in the key). Session ids are still stable across reconnects *for the same user*; a reconnect as a different user now mints a new session id instead of resurrecting the old membership |

The re-base itself was clean: `git diff 6c073e19dd upstream/master` over the SyncPlay
paths is empty, i.e. upstream has not changed SyncPlay since the fork's base, so nothing
was silently dropped or re-applied with drift. The one re-base casualty was the test
project (xunit v3 analyzer rules — #26), and one upstream test file collided with the
ported suite's name (`WaitingGroupStateTests`); `ab30d1c355` resolved it by moving
upstream's two ping tests to `WaitingGroupStatePingTests.cs`.

## Prior findings — status on 12.0

Verified by re-reading the current tree, not by trusting the old document.

| # | Finding | Status on 12.0 | Where |
| --- | --- | --- | --- |
| 1 | `JsonDateTimeConverter` 7-digit timestamps break iOS | **Fixed, still in place** | [JsonDateTimeConverter.cs:25](src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs#L25) emits `yyyy-MM-ddTHH:mm:ss.fffK`; `PluginManagerTests` adjusted for the global millisecond truncation |
| 2 | Recovery-delay floor compared ticks to ms | **Fixed twice over** — upstream `0c05d9d1a9` and fork `449324a25c` | [WaitingGroupState.cs:740](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L740) |
| 3 | Fire-and-forget broadcasts under the group lock | **Still open** — see #31 (new concrete trigger found) | [Group.cs:518-543](Emby.Server.Implementations/SyncPlay/Group.cs#L518) |
| 4 | 2 s time-sync threshold silently zeroes `elapsedTime` | **Still open** | [WaitingGroupState.cs:519-525](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L519), [Group.cs:129](Emby.Server.Implementations/SyncPlay/Group.cs#L129) |
| 5 | `IsBuffering()` honors `IgnoreGroupWait` ⇒ premature group-ready | **Still open** | [Group.cs:606-617](Emby.Server.Implementations/SyncPlay/Group.cs#L606) |
| 6 | `Ping` endpoint has no `SyncPlayIsInGroup` policy | **Still open** (controller-level `SyncPlayHasAccess` pre-dates 12.0 and does not close it) | [SyncPlayController.cs:430-439](Jellyfin.Api/Controllers/SyncPlayController.cs#L430) |
| 7 | No re-authorization on the broadcast path | **Still open** (design) | [SyncPlayManager.cs:57-58](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L57) map-based, [411](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L411) |
| 8 | No rate limiting on Ping fan-out | **Partially closed** — ping values are now clamped (`MaxPing`), which was the overflow/stall vector; the fan-out volume point remains | [Group.cs:565-571](Emby.Server.Implementations/SyncPlay/Group.cs#L565) |
| 9 | Null-deref on missing library items | **Partially fixed** — access-check path guarded upstream; **four queue-management dereferences remain unguarded** (new detail below) | [Group.cs:652,684,744,759](Emby.Server.Implementations/SyncPlay/Group.cs#L652) |
| 10 | `Distinct()` collapses multi-device users in `Participants` | **Withdrawn (2026-09-08)** — not a defect. `GetInfo()` is a display list of user names; membership itself is per session in `_participants`, and nothing keys off this list | [Group.cs:390](Emby.Server.Implementations/SyncPlay/Group.cs#L390) |
| 11 | `GetHighestPing()` returns `long.MinValue` on empty group | **Fixed upstream** | [Group.cs:574-585](Emby.Server.Implementations/SyncPlay/Group.cs#L574) |
| 12 | `SendCommand.When` carries `LastActivity` as `DateTime` | **Closed** by #1's wire-format fix | — |
| 13 | N×M `GetItemById` calls per queue-access check | **Still open** | [Group.cs:230-249](Emby.Server.Implementations/SyncPlay/Group.cs#L230) |
| 14 | ~~`PlayQueue.GetPlaylist()` materialized per call~~ | **Superseded by #38 (2026-09-08)** — the premise was backwards: it materializes nothing and returns the live internal list. The real problem points the other way | [PlayQueueManager.cs:79-82](MediaBrowser.Controller/SyncPlay/Queue/PlayQueueManager.cs#L79) |
| 15 | Immediate eviction on WebSocket disconnect | **Fixed, still in place** — grace period + reconnect cancel; upstream's rejoin-counter fix landed alongside | [SyncPlayManager.cs:472-528](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L472) |
| 16 | Past-dated Pause command for ahead-of-group sessions | **Fixed, still in place** | [WaitingGroupState.cs:595-628](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L595) |
| 17 | Uncapped seek-correction loop | **Fixed, still in place** — shared 5-attempt budget across all three correction paths | [WaitingGroupState.cs:24](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L24), [1012-1024](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L1012) |
| 18 | `SyncPlayAccessHandler` 500 on empty Guid | **Fixed, still in place** | [SyncPlayAccessHandler.cs:37-43](Jellyfin.Api/Auth/SyncPlayAccessPolicy/SyncPlayAccessHandler.cs#L37) |
| 19 | Zero-tolerance ahead-of-group check | **Fixed, still in place** | [WaitingGroupState.cs:595](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L595) |
| 20 | Correction budget never reset on resume path | **Fixed, still in place** | [WaitingGroupState.cs:580-583](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L580) |
| 21 | Unbounded `LastActivity` push on rejoin | **Fixed, still in place** | [WaitingGroupState.cs:682-719](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L682) |
| 22 | No timeout on `Waiting` state | **Fixed with caveats** — two-tier 2 s/30 s deadline; the flag logic is wrong in both directions (#27, #33) and the deadline slides rather than bounding the state (#34) | [Group.cs:426-471](Emby.Server.Implementations/SyncPlay/Group.cs#L426), [WaitingGroupState.cs:952-1010](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L952) |
| 23 | Unbounded future-dated Pause for behind-group sessions | **Fixed, still in place** | [WaitingGroupState.cs:629-663](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L629) |
| 24 | jellyfin-web never reports `Ready` after in-buffer seek | **Still open** (client-side, not fixable in this fork); **#33 shows the server-side mitigation does not fire in a mixed group**. **Check on 12.0**: the stock web client now ships in the `jellyfin/jellyfin:12.0` base image — worth re-testing whether upstream web fixed the `'ready'`-event gap before relying on #22's timeout as the only scrub recovery | — |
| 25 | `SyncPlayManager` throws on userless sessions | **Fixed, still in place** for `JoinGroup`/`ListGroups`/`GetGroup`; the two deferred corners remain (new detail under #9/#25 below) | [SyncPlayManager.cs:203-210,340-344,372-376](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L203) |

Two of the old document's *review-of-the-fix* notes for #15 are worth carrying forward as
they were never implemented:

- **Liveness re-check before evicting.** `CloseIfNeededAsync` removes the session from
  `_activeConnections` *before* raising `SessionEnded`
  ([SessionManager.cs:308-322](Emby.Server.Implementations/Session/SessionManager.cs#L308)),
  and a client can come back over plain HTTP (no WebSocket) within the grace window —
  `LogSessionActivity` re-adds it via `GetOrAdd` — without ever raising
  `SessionControllerConnected`, which is the fork's only cancel signal
  ([SessionWebSocketListener.cs:129](Emby.Server.Implementations/Session/SessionWebSocketListener.cs#L129)).
  That session is then evicted 30 s after it demonstrably came back. Re-checking
  `_sessionManager.Sessions` for the id at the end of the grace window (before calling
  `LeaveGroup`) closes it.
- **`OnSessionEnded` reads `cts.Token` after publishing the CTS** — see #28.

## New findings (this review)

### HIGH — #26: The re-base broke the SyncPlay test build — ✅ FIXED in this review

12.0 moved the test stack to **xunit.v3 3.2.2** ([Directory.Packages.props:87](Directory.Packages.props#L87)),
whose analyzer ships rule `xUnit1051` (async tests must thread
`TestContext.Current.CancellationToken` into cancellable calls). The repo builds with
`TreatWarningsAsErrors=true` unconditionally
([Directory.Build.props:9](Directory.Build.props#L9)), so the rule is a **compile error**
in every configuration. The fork's test files were written for 10.11's xunit v2 and
violate it in 9 call sites, which means **`dotnet test` could not build
`Jellyfin.Server.Implementations.Tests` at all on this branch** — the entire SyncPlay
regression suite (the thing that pins #15–#23) was unbuildable, and therefore
unverified, after the re-base.

**Reproduce (definitive):** `dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "FullyQualifiedName~SyncPlay" -c Release`
fails in the build with 9 × `error xUnit1051` (4 in `SyncPlayManagerReconnectTests.cs`,
5 in `GroupStateTimeoutTests.cs`). Upstream's own tests in the same project are clean;
only the fork's files trip the rule.

**Fix applied in this review:** pass `TestContext.Current.CancellationToken` at the 9
sites (the `Task.Delay`/`WaitAsync` calls in
[SyncPlayManagerReconnectTests.cs:76,90,104,124](tests/Jellyfin.Server.Implementations.Tests/SyncPlay/SyncPlayManagerReconnectTests.cs#L76)
and [GroupStateTimeoutTests.cs:72,88,102,117,131](tests/Jellyfin.Server.Implementations.Tests/SyncPlay/GroupStateTimeoutTests.cs#L72)).
Behavior is unchanged apart from test cancellation responsiveness.

**Verification after the fix:** `dotnet test` (Release, standard settings, no
`TreatWarningsAsErrors` override) builds the test project with **0 warnings** and
**64/64 SyncPlay tests pass** in `Jellyfin.Server.Implementations.Tests`, 16/16 in
`Jellyfin.Api.Tests` (access-handler matrix), and 1/1 in
`Jellyfin.Server.Integration.Tests` (`SyncPlayLostWebSocketTests`, 50 s).

### MEDIUM — #27: The short seek timeout leaks into item-change requests in the same waiting cycle

#22's two-tier deadline selects the timeout from two instance flags in
[ArmWaitTimeout](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L1001-L1010):

```csharp
var timeout = _startedBySeek && !_sessionReported ? SeekWaitTimeout : WaitTimeout;
```

`_startedBySeek` is set only by
[HandleRequest(SeekGroupRequest)](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L360-L401)
(lines 376/381) and is **never cleared by a request that changes the playing item**.
`_sessionReported` is set only by `Buffer`/`Ready`. A `WaitingGroupState` instance lives
for the whole waiting cycle, so within one cycle the sequence

1. `Playing` → **Seek** (`_startedBySeek = true`, `_sessionReported = false`)
2. (stock web client goes silent — finding #24)
3. **NextItem / PreviousItem / SetNewQueue / SetPlaylistItem** arriving before the 2 s
   deadline and before any `Buffer`/`Ready`

lands in the item-change handlers, which call `SetAllBuffering(true)` and then
`ArmWaitTimeout` with the **stale seek flags** — so a *new item that has to be loaded
from scratch* is held to the **2 s** seek deadline instead of the 30 s load deadline.
On expiry, `OnStateTimeout` does `SetAllBuffering(false)` and broadcasts `Unpause` for
the new item ([WaitingGroupState.cs:952-993](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L952)) —
the group starts the next episode before any client has finished loading it, i.e. the
jellyfin-desktop#139 "audio starts as soon as the first member finishes loading" shape,
re-introduced through the fork's own fix.

Affected handlers (all four change the item and re-arm the deadline):
`HandleRequest(PlayGroupRequest)` line 223, `HandleRequest(SetPlaylistItemGroupRequest)`
line 250, `HandleRequest(NextItemGroupRequest)` line 855,
`HandleRequest(PreviousItemGroupRequest)` line 903. (`SessionJoined` line 141 inherits
the same leak for a joiner arriving in the silent window; lower impact, since the
item is already loaded for the existing members and a late joiner self-corrects via its
own `Buffer`/`Ready`.)

**Why it matters only with #24:** with a reporting client, `_sessionReported` flips to
`true` within the 2 s window and the deadline relaxes. The stock web client is exactly
the case where it never does — so the leak is most likely to bite in the same
environment that made #22 necessary.

**Reproduce (definitive):** two web clients in a group, playing. From client A, scrub a
few seconds ahead (group → `Waiting`, clients silent per #24). Within 2 s, from client
B click **Next Episode** (or `curl -X POST /SyncPlay/NextItem`). Server log shows
`Group "…" switching from Waiting to Playing` ~2 s after the *NextItem*, not after the
clients have loaded the new item; client B's player starts the new episode while client
A is still loading it.

Suggested fix — an item change is, by definition, a load-from-scratch event, so reset
the seek flag in the four handlers before re-arming:

```csharp
// The playing item changed; the "item already loaded" assumption behind the
// short seek deadline no longer holds for anyone.
_startedBySeek = false;
ArmWaitTimeout(context, session);
```

and add a test alongside `HandleRequest_Seek_ArmsShortWaitTimeout`
(Seek → silent → NextItem ⇒ expect the 30 s deadline).

### LOW — #28: `OnSessionEnded` still reads `cts.Token` after publishing the CTS

> Downgraded from MEDIUM to LOW on 2026-09-08: the race's net effect is "no eviction",
> which is the outcome a reconnect should produce, and the exception is caught and logged.
> Worth the one-line fix, but it does not outrank #27, #33 or #32.

The old review's audit of the #15 fix flagged this and it was never closed.
[SyncPlayManager.cs:484-489](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L484-L489):

```csharp
CancelPendingLeave(session.Id);

var cts = new CancellationTokenSource();
_pendingLeaves[session.Id] = cts;

_ = LeaveGroupWhenDisconnected(session, cts.Token);   // token read AFTER the insert
```

`CancelPendingLeave` on any other thread (a fast reconnect raising
`SessionControllerConnected`, or an explicit `JoinGroup`/`LeaveGroup`) can `TryRemove`
and `Dispose` the CTS between the dictionary insert and the `cts.Token` read, throwing
`ObjectDisposedException` inside the event handler. `EventHelper.QueueEventIfNotNull`
catches and logs it ([EventHelper.cs:24-34](MediaBrowser.Common/Events/EventHelper.cs#L24)),
and the net outcome (no eviction) is the one a reconnect should produce — so this is
log noise plus a swallowed exception, not a mis-eviction. Cheap to close:

```csharp
var cts = new CancellationTokenSource();
var token = cts.Token;                 // capture before it is reachable
_pendingLeaves[session.Id] = cts;
_ = LeaveGroupWhenDisconnected(session, token);
```

### MEDIUM — #29: `SetPlayQueue` stores the client's start position un-sanitized

Every other position entry point runs the value through `SanitizePositionTicks`
(clamped to `[0, RunTimeTicks]`): `Seek` (line 385 of `WaitingGroupState`), `Ready`
(line 533). `SetPlayQueue` does not — [Group.cs:620-643](Emby.Server.Implementations/SyncPlay/Group.cs#L620):

```csharp
var item = _libraryManager.GetItemById(PlayQueue.GetPlayingItemId());
RunTimeTicks = item.RunTimeTicks ?? 0;
PositionTicks = startPositionTicks;     // client-supplied, unchecked
```

A `Play` request with a negative or out-of-range `StartPositionTicks` lands verbatim in
`context.PositionTicks`, is stamped into every subsequent `SendCommand` via
`NewSyncPlayCommand` ([Group.cs:546-555](Emby.Server.Implementations/SyncPlay/Group.cs#L546)),
and is the reference every later correction compares against. The same handler's
`SetPlayQueue` failure path is guarded (empty queue / bad index, line 623), so this is
the one unguarded writer. Note the interaction: with `RunTimeTicks == 0` (item with
unknown runtime), `SanitizePositionTicks` clamps *everything* to 0 — so the fix should
clamp to `[0, RunTimeTicks]` only when `RunTimeTicks > 0`, mirroring what `Seek`
effectively does. One line:

```csharp
PositionTicks = RunTimeTicks > 0 ? Math.Clamp(startPositionTicks, 0, RunTimeTicks) : startPositionTicks;
```

### LOW — #30: The deferred #25 corners are now policy-blocked but still unguarded

#18's guard (fail closed with 403 when the principal has no user) means a userless
session can no longer reach *any* SyncPlay endpoint — including `NewGroup` — so the two
corners the old review deliberately deferred are currently unreachable through the API:

- `NewGroup` has no `session.UserId.IsEmpty()` guard
  ([SyncPlayManager.cs:150-188](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L150))
  (the other three manager methods do); a userless creator would also skew the
  `_activeUsers` counter under `Guid.Empty`.
- `AllUsersHaveAccessToQueue` maps participants through
  `_userManager.GetUserById(participant.UserId)` unguarded
  ([Group.cs:251-269](Emby.Server.Implementations/SyncPlay/Group.cs#L251)) and would throw
  `ArgumentException` if a userless member ever got into a group by another route.

Both stay open as defense-in-depth: the moment any SyncPlay policy is relaxed (or a new
entry point is added that resolves a session without a principal user), the 500 returns.
Adding the `NewGroup` guard is a five-line copy of the `JoinGroup` guard and is
recommended.

### LOW — #31: A concrete, guaranteed trigger for old finding #3 (fire-and-forget broadcasts)

Old #3 is still open structurally ([Group.cs:518-543](Emby.Server.Implementations/SyncPlay/Group.cs#L518):
callers ignore the returned `Task`), but the re-base + #15's grace period produced a
trigger that fires on **every** real eviction, not just under load:

1. A session ends for good. After the 30 s grace, `LeaveGroupWhenDisconnected` runs
   `LeaveGroup` → `group.SessionLeave`
   ([Group.cs:344-357](Emby.Server.Implementations/SyncPlay/Group.cs#L344)).
2. `SessionLeave` first removes the participant, then broadcasts
   `SyncPlayGroupLeftUpdate` to the departing session itself:
   `SendGroupUpdate(session, SyncPlayBroadcastType.CurrentSession, ...)`.
3. That session is already gone from `_activeConnections` — `CloseIfNeededAsync`
   removes it *before* raising `SessionEnded`
   ([SessionManager.cs:308-322](Emby.Server.Implementations/Session/SessionManager.cs#L308)) —
   so `SendSyncPlayGroupUpdate` → `GetSession(sessionId)` throws
   `ResourceNotFoundException`
   ([SessionManager.cs:1210-1220](Emby.Server.Implementations/Session/SessionManager.cs#L1210)).
4. The exception lands in the ignored `Task` (unobserved; .NET Core drops it silently).
   `SessionLeave` continues, the "user left" broadcast to the *other* members and the
   empty-group teardown still happen — so nothing is functionally broken, but the
   faulted send is a permanent, invisible leak of the exact failure mode #3 warns
   about, and it masks a real ordering hazard: any future change that *does* observe
   that task (e.g. awaiting `SendGroupUpdate` to fix the ordering point of #3) will
   find the normal eviction path throwing on its first send.

Cheap mitigation inside `Group`: make the current-session sends in
`SessionJoin`/`SessionLeave` tolerant of an already-gone session (the group's map is
the source of truth for membership; the session manager's is for routing). The
structural fix is still #3's original one (await the fan-out under serialized access).

### LOW — #32: The remaining #9 null-deref sites, precisely

With the upstream guard in `HasAccessToQueue` ([Group.cs:242](Emby.Server.Implementations/SyncPlay/Group.cs#L242)),
the old #9 NRE survives in exactly four queue-management dereferences of
`_libraryManager.GetItemById(...)` — all of the form
`RunTimeTicks = item.RunTimeTicks ?? 0` with no null check:

- `SetPlayingItem` — [Group.cs:652-653](Emby.Server.Implementations/SyncPlay/Group.cs#L652)
- `RemoveFromPlayQueue` — [Group.cs:684-685](Emby.Server.Implementations/SyncPlay/Group.cs#L684)
- `NextItemInQueue` — [Group.cs:744-745](Emby.Server.Implementations/SyncPlay/Group.cs#L744)
- `PreviousItemInQueue` — [Group.cs:759-760](Emby.Server.Implementations/SyncPlay/Group.cs#L759)

`SetPlayQueue` line 638 is *protected* (the `AllUsersHaveAccessToQueue` call at line 629
has already dereferenced the same item), so it needs no change. Reproduce: create a
group with a 2-item queue, delete item 2 from the library, send `NextItem` → NRE → 500,
and the group's queue state is left half-mutated (`PlayQueue.Next()` already advanced
the index before the lookup). Fix: the same `item is null` guard as line 242, treating
a missing item as "item found: no" so the state machine falls back to its existing
`else` branch.

## New findings (second review, 2026-09-08)

A second pass over the same tree, focused on the fork's own patches and on the layers the
first pass did not reach (the DTO/controller input surface, the global JSON converter, and
the interaction between the two new timers). Every claim below was checked against the
current source; the two marked **verified empirically** were reproduced by running code.

### HIGH — #33: `_sessionReported` is group-wide, so one answering client hands the silent one the 30 s deadline — ✅ FIXED, and confirmed in production first

**Confirmed in production before the fix** (server log `log_20260909.log`, group
`d4bc96d0-91af-4beb-8bb2-175ebbadd9bb`, on a build that already contained #22's deadline —
image built 2026-09-08 23:25 PDT, after HEAD `eeed0976`). Two scrubs, same shape:

```text
08:23:47.448  eae6c021 (Jellium Desktop)   Seek   → Playing → Waiting   [2 s armed]
08:23:47.520  a1499c70 (Jellyfin MPV Shim) Ready  → "will pause when ready in 0.0039489 seconds"
              ...eae6c021 sends nothing...
08:24:09.096  eae6c021                     Unpause → Waiting → Playing  [manual escape]
```

**21.6 s frozen**; the earlier scrub at 08:23:26.449 froze **12.1 s**. Neither logged
`timed out waiting for Ready reports` — the peer's `Ready`, 72 ms after the seek, moved the
deadline from t≈2 s to t≈30 s exactly as predicted below, and the user gave up first.

Note the silent client here is **Jellium Desktop 0.1.0-dev**, not the stock web client, so
finding #24's scoping is narrower than reality: the seeking client going silent is not
unique to jellyfin-web. The client-side defect is tracked in that project separately.

**Fix applied:** dropped `_sessionReported = true` from the `Ready` handler and renamed the
field to `_sessionBuffered`, so only a `Buffer` report relaxes the deadline. The old test
`HandleRequest_Seek_AfterASessionReported_ArmsLongWaitTimeout` was re-premised on `Buffer`
(now `..._AfterASessionBuffered_...`), and `HandleRequest_Seek_AfterAPeerReportedReady_KeepsShortWaitTimeout`
pins the production case above. 65/65 SyncPlay tests pass.

---

*Original finding follows.*

`ArmWaitTimeout` picks the short deadline only while *no session at all* has reported in
this waiting cycle ([WaitingGroupState.cs:1008](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L1008)):

```csharp
var timeout = _startedBySeek && !_sessionReported ? SeekWaitTimeout : WaitTimeout;
```

`_sessionReported` is a single instance flag set by **any** session's `Buffer`
([line 415](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L415)) **or
`Ready`** ([line 495](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L495)).
But finding #24 — the whole reason `SeekWaitTimeout` exists — is a *per-client* defect:
jellyfin-web goes silent after an in-buffer seek while other clients answer normally. So in
any **mixed group** the short deadline is cancelled by the very clients that are working:

1. Web client A scrubs. `Playing` → `Waiting`, `_startedBySeek = true`,
   `_sessionReported = false`, 2 s armed.
2. Native iOS client B answers `Ready` ~100 ms later. `_sessionReported = true`.
3. B's Ready runs the "others are still buffering" branch and re-arms at
   [line 674](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L674) —
   now with `WaitTimeout`. **The deadline moves from t≈2 s to t≈30 s.**
4. A never reports (#24). The group is frozen ~30 s on every scrub.

This is the deployment described in the review's own context (native iOS app + stock web
client), so the 2 s deadline effectively only applies to a group where *every* client is
silent — the case that is least likely in practice.

Note the existing test `HandleRequest_Seek_AfterASessionReported_ArmsLongWaitTimeout`
([WaitingGroupStateTests.cs:383-395](tests/Jellyfin.Server.Implementations.Tests/SyncPlay/WaitingGroupStateTests.cs#L383))
pins this behavior and justifies it with "a session that reports is a session that is
genuinely working through the seek" — but it drives the case with `HandleReady`, and a
`Ready` report means that session is *finished*, not working. The premise holds for
`Buffer` and not for `Ready`.

**Suggested fix (one line, plus the test's premise):** relax the deadline only on a
`Buffer` report, which is the one that actually says "someone is genuinely still working":
drop `_sessionReported = true` from the `Ready` handler (line 495) and rename the field to
match (`_sessionBuffered`). A stricter version tracks reporters per session and keeps the
short deadline while any *still-buffering* session has not reported, but that needs
`IGroupStateContext` to expose per-session buffering state, which it currently does not.

Together with #27 this is one root cause seen from both sides: both flags are properties of
the *cycle*, while the condition they are trying to express ("is this particular client the
kind that answers, for this particular item?") is a property of a *session and an event*.

### MEDIUM — #34: The wait deadline is a sliding window, so it bounds silence, not the waiting state

`ScheduleStateTimeout` cancels any pending deadline and starts a fresh one
([Group.cs:426-440](Emby.Server.Implementations/SyncPlay/Group.cs#L426)), and
`ArmWaitTimeout` is called on **every** path that keeps the group waiting. So the deadline
does not bound how long a group may sit in `Waiting`; it bounds how long the group may go
without hearing *anything*. A client that is stuck but chatty postpones it indefinitely.

The concrete trigger is the wrong-playlist-item path, which is a loop by construction:
a session reports `Buffer`/`Ready` for a stale `PlaylistItemId`, the server answers with a
`SetCurrentItem` queue update, marks it buffering, and re-arms
([lines 418-429](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L418)
and [498-509](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L498)).
A client whose player is between items — or that has simply lost track after a queue
change — keeps reporting the same stale id, and each report buys another full `WaitTimeout`.
Nothing in the cycle counts these the way `_correctionAttempts` counts corrective seeks.

A second, less exotic instance: the eviction grace period and the wait deadline are both
**30 s** ([SyncPlayManager.cs:108](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L108),
[WaitingGroupState.cs:45](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L45)),
chosen independently and neither expressed in terms of the other. A member that disconnects
mid-`Waiting` stays a participant, still flagged buffering, for 30 s; when it is finally
evicted, `SessionLeaving` re-arms yet another full deadline
([line 183](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L183)) if
anyone else is still buffering. Worst case, a single disconnect holds the group ~60 s.

**Suggested fix:** keep the sliding deadline for responsiveness but add an absolute cap for
the cycle. Record `_waitStartedAt` on the first arm and clamp:

```csharp
var remaining = AbsoluteWaitTimeout - (DateTime.UtcNow - _waitStartedAt);
context.ScheduleStateTimeout(TimeSpan.FromTicks(Math.Clamp(timeout.Ticks, 0, Math.Max(remaining.Ticks, 0))));
```

with `AbsoluteWaitTimeout` at 60 s or so, and add a test that a repeated wrong-item report
does not push the deadline past it.

### MEDIUM — #35: `SanitizePositionTicks` collapses every position to 0 when `RunTimeTicks` is 0

[Group.cs:558-562](Emby.Server.Implementations/SyncPlay/Group.cs#L558):

```csharp
var ticks = positionTicks ?? 0;
return Math.Clamp(ticks, 0, RunTimeTicks);
```

`RunTimeTicks` is `item.RunTimeTicks ?? 0` at every writer, and is set to a literal `0` on
the not-found branches of `SetPlayingItem`
([line 657](Emby.Server.Implementations/SyncPlay/Group.cs#L657)) and `RemoveFromPlayQueue`
([line 689](Emby.Server.Implementations/SyncPlay/Group.cs#L689)). Whenever it is 0 the clamp
degenerates to `Math.Clamp(ticks, 0, 0)` — **every** position becomes 0. Both callers are
affected: `Seek` ([WaitingGroupState.cs:385](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L385))
and `Ready` ([line 533](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L533)).

Consequences, in order of how likely they are to be hit:

- **Seek is a no-op** for any item with unknown runtime (live TV, an in-progress recording,
  items whose metadata carries no runtime). The group can never move the playhead; every
  `Seek` broadcasts position 0.
- **It interacts with #29.** `SetPlayQueue` writes `PositionTicks = startPositionTicks`
  unsanitized ([Group.cs:639](Emby.Server.Implementations/SyncPlay/Group.cs#L639)), so
  `context.PositionTicks` can be non-zero while every `Ready` report sanitizes to 0. Then
  `delayTicks == context.PositionTicks` for every session, every time — which now burns the
  fork's 5-correction budget per session per cycle before the group gives up on all of them.
  The two findings have to be fixed together, or the fix for #29 makes this one worse.
- `Math.Clamp` **throws** `ArgumentException` when `min > max`, so a negative `RunTimeTicks`
  from bad metadata turns any `Seek` or `Ready` into a 500.

**Suggested fix** (subsumes #29's one-liner — apply this and use the same helper there):

```csharp
public long SanitizePositionTicks(long? positionTicks)
{
    var ticks = positionTicks ?? 0;
    // An unknown runtime is not a zero-length item: clamping to it would pin every
    // position to the start. Only the lower bound is knowable in that case.
    return RunTimeTicks > 0 ? Math.Clamp(ticks, 0, RunTimeTicks) : Math.Max(ticks, 0);
}
```

### MEDIUM — #36: The iOS date fix drops the timezone designator for `DateTimeKind.Unspecified`, and truncates every API date to milliseconds — verified empirically

`b5b6f14179` replaced the two-branch legacy converter with a single format string
([JsonDateTimeConverter.cs:25](src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs#L25)):

```csharp
writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture));
```

Serializing through `JsonDefaults.Options` in a scratch console app gives:

| `DateTimeKind` | Output |
| --- | --- |
| `Utc` | `"2026-09-08T12:34:56.789Z"` |
| `Local` | `"2026-09-08T12:34:56.789-07:00"` |
| `Unspecified` | `"2026-09-08T12:34:56.789"` — **no designator at all** |
| `Utc`, +1234567 ticks | `"2026-09-08T12:34:56.123Z"` — sub-ms ticks dropped |

Two things follow.

1. **The stated guarantee does not hold.** `K` renders as the empty string for
   `DateTimeKind.Unspecified`, so the converter does not always emit a valid ISO 8601
   instant. Swift's `ISO8601DateFormatter` with `.withInternetDateTime` rejects a value with
   no designator just as surely as it rejects seven fractional digits — the fix has a hole
   in exactly the shape it was written to close. SyncPlay's own payloads are safe (every one
   of them is derived from `DateTime.UtcNow`), but the converter is global, and Unspecified
   dates do reach the wire: `PluginManifest.Timestamp`
   ([PluginManager.cs:416](Emby.Server.Implementations/Plugins/PluginManager.cs#L416)) is
   `DateTime.Parse(..., DateTimeStyles.AdjustToUniversal)`, which yields `Unspecified` for
   an offset-less manifest string, or `DateTime.MinValue`, which is `Unspecified` too.
   The *old* converter gave those a hardcoded `Z` whenever the millisecond happened to be 0.
2. **The millisecond truncation is server-wide and now load-bearing.** It applies to every
   `DateTime` in every API response, not just SyncPlay's, and `PluginManagerTests` had to be
   changed to round its input before asserting a round trip
   ([PluginManagerTests.cs:403-409](tests/Jellyfin.Server.Implementations.Tests/Plugins/PluginManagerTests.cs#L403)).
   That is a deliberate trade and probably the right one, but it belongs in the record as a
   server-wide wire-format change riding on a SyncPlay fix, not as a SyncPlay fix.

**Suggested fix** — one line, and it restores the old behavior's intent for naked dates
(Jellyfin treats them as UTC elsewhere; see
[SqliteExtensions.cs:109-111](Emby.Server.Implementations/Data/SqliteExtensions.cs#L109)
and [XmlTvProgramEtag.cs:149](src/Jellyfin.LiveTv/Listings/XmlTvProgramEtag.cs#L149)):

```csharp
var normalized = value.Kind == DateTimeKind.Unspecified
    ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
    : value;
writer.WriteStringValue(normalized.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture));
```

If the global truncation is ever judged too broad, the alternative is a SyncPlay-scoped
converter applied via `[JsonConverter]` on `SendCommand`/`GroupUpdate` rather than through
`JsonDefaults`.

### LOW — #37: An explicit JSON `null` in four SyncPlay request bodies is a 500 — verified empirically

The SyncPlay DTOs initialize their reference-typed properties in the constructor, which
reads as a null guard but is not one: `System.Text.Json` overwrites the constructor's value
when the payload carries an explicit `null`, and `JsonDefaults.Options` does not set
`RespectNullableAnnotations`, so the non-nullable declaration is not enforced either.
Deserializing `{"GroupName":null}` and `{"PlayingQueue":null}` through `JsonDefaults.Options`
in a scratch console app yields `null` for both.

| Endpoint | Property | Lands on |
| --- | --- | --- |
| `POST /SyncPlay/New` | `NewGroupRequestDto.GroupName` | `requestData.GroupName.Trim()` — [SyncPlayController.cs:61](Jellyfin.Api/Controllers/SyncPlayController.cs#L61) |
| `POST /SyncPlay/SetNewQueue` | `PlayRequestDto.PlayingQueue` | `playQueue.Count` in `SetPlayQueue` — [Group.cs:623](Emby.Server.Implementations/SyncPlay/Group.cs#L623) |
| `POST /SyncPlay/Queue` | `QueueRequestDto.ItemIds` | `newItems.Count` in `AddToPlayQueue` — [Group.cs:708](Emby.Server.Implementations/SyncPlay/Group.cs#L708) |
| `POST /SyncPlay/RemoveFromPlaylist` | `RemoveFromPlaylistRequestDto.PlaylistItemIds` | `playlistItemIds.Contains(...)` in `RemoveFromPlaylist` — [PlayQueueManager.cs:305](MediaBrowser.Controller/SyncPlay/Queue/PlayQueueManager.cs#L305) |

Each is a `NullReferenceException` surfacing as a 500 rather than a 400. It needs an
authenticated caller with SyncPlay access (and group membership for three of the four), so
this is malformed-input hygiene, not an auth hole — but the queue ones throw from *inside*
the group lock, on the request path, which is a worse place to throw than the controller.

**Suggested fix:** `[Required]` on the three list properties (model validation then returns
400 before the manager is reached) and `requestData.GroupName?.Trim() ?? string.Empty` at
the controller — `[Required]` on `GroupName` would additionally start rejecting the empty
name that is currently legal.

### LOW — #38: `PlayQueueUpdate` ships the group's live playlist list, which is a trap laid for #3's fix

`PlayQueueManager.GetPlaylist()` returns `GetPlaylistInternal()`, which is the **field
itself**, not a copy ([PlayQueueManager.cs:79-82](MediaBrowser.Controller/SyncPlay/Queue/PlayQueueManager.cs#L79),
[543-551](MediaBrowser.Controller/SyncPlay/Queue/PlayQueueManager.cs#L543)), and
`GetPlayQueueUpdate` hands that reference straight into the outbound DTO
([Group.cs:802](Emby.Server.Implementations/SyncPlay/Group.cs#L802)). The DTO is then
serialized inside the send task.

Today this is harmless *by accident*: `Group.SendGroupUpdate` enumerates its fan-out
synchronously, each session has a single `WebSocketController` holding a single socket, and
`WebSocketConnection.SendAsync` serializes before its first `await`
([WebSocketConnection.cs:111](Emby.Server.Implementations/HttpServer/WebSocketConnection.cs#L111)) —
so every serialization happens on the calling thread, still under the group lock. It stops
being harmless the moment any of that changes: a session with a second session controller
(the second one's serialization runs on a continuation, off the lock), a batched or queued
sender, or — most likely — the fix old #3 asks for, awaiting the fan-out under serialized
access. `Queue`, `QueueNext`, `RemoveFromPlaylist`, `ClearPlaylist`, `Reset` and
`MovePlaylistItem` all mutate that list in place, so a concurrent queue change during
serialization is `InvalidOperationException: Collection was modified` — into an ignored
`Task`, i.e. a client that silently never receives the queue update.

**Suggested fix:** one call — `GetPlaylist()` returns `GetPlaylistInternal().ToList()`. The
list is small and rebuilt on every queue change anyway. See also the correction to old #14
below, which has the underlying fact backwards.

### LOW — #39: A group created from a paused session reaches `Paused` without telling anyone

`CreateGroup` sets the waiting state and then passes **that same state's type** as the
previous state ([Group.cs:312](Emby.Server.Implementations/SyncPlay/Group.cs#L312)):

```csharp
SetState(waitingState);                                              // _state.Type is now Waiting
...
_state.SessionJoined(this, _state.Type, session, cancellationToken); // prevState := Waiting
```

so `WaitingGroupState.InitialState` is recorded as `Waiting`, not `Playing`/`Paused`. When
the creator was paused, `ResumePlaying` is `false`, and its first `Ready` takes the
not-resuming branch: the group switches to `PausedGroupState` and then checks
`InitialState` for `Playing` or `Paused`
([WaitingGroupState.cs:807-816](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L807)).
`Waiting` matches neither, so **no `Pause` command and no `SyncPlayStateUpdate` are sent** —
the group settles into `Paused` silently and the client is never told the wait ended.

Upstream, pre-dating the fork, and low impact (the creator already has the group-joined
update, and any later request re-synchronizes). Recorded because it is a genuinely dead
branch that any future work on the initial-state logic will trip over. Passing the real
previous state — `Idle`, or `session.PlayState.IsPaused ? Paused : Playing` — is the fix.

## Contested entries — resolved 2026-09-08

Three entries the second review flagged as miscategorized. All three were reviewed and
accepted; the table above and #28's heading have been edited accordingly. Kept here as the
rationale for the change.

- **Old #10 — `Distinct()` collapses multi-device users in `Participants`. Dropped.** `GetInfo()` builds a list of **user names** for display
  ([Group.cs:390](Emby.Server.Implementations/SyncPlay/Group.cs#L390)); nothing in the
  server keys off it, and membership itself is tracked per session in `_participants`.
  Showing one "Ryan" rather than three when the same account is on three devices is the
  natural reading of a participant list, not a defect. If the intent is a device count, that
  is a feature request, not a bug.

- **Old #14 — "`PlayQueue.GetPlaylist()` materialized per call". Replaced by #38; the fact was backwards.**
  It materializes nothing; it returns the live internal `List<SyncPlayQueueItem>` (see #38).
  The entry is now marked superseded by #38, which is the real problem and points the
  opposite way: the fix is to *start* copying, not to stop.

- **#28 — severity. Downgraded to LOW.** The write-up's own analysis is right that
  the race yields "no eviction", which is the outcome a reconnect should produce, and that
  `EventHelper.QueueEventIfNotNull` catches and logs the `ObjectDisposedException`. A
  swallowed exception with the correct net effect is a LOW. The one-line fix is still worth
  taking; it just should not sort above #27, #33 and #32 on the follow-up list.

## Environment note — Debug builds need a newer SDK than the one on this machine

`Directory.Packages.props` pins `Microsoft.CodeAnalysis.*` **5.9.0**
(lines 32-34) for the custom analyzer, and [Directory.Build.props:23-24](Directory.Build.props#L23)
attaches it to every project **in Debug only**. This machine has SDK 10.0.204 (bundled
Roslyn 5.3.0.0) and 9.0.302, both too old to load the analyzer, so **Debug builds fail
with `CS9057`** here (`dotnet build` / default `dotnet test`). Consequences:

- Run the test suites with `-c Release` (the analyzer is not attached in Release);
  that is what the verification above used.
- The `dev-12.0-local-deploy` Docker build is unaffected (it publishes the server in
  Release on `mcr.microsoft.com/dotnet/sdk:10.0`).
- To build Debug locally, install a newer .NET 10 SDK (one whose bundled Roslyn is
  ≥ 5.9). This is environmental, not a fork defect — 12.0's own CI builds Debug fine —
  but it bites anyone re-running the old review's verification commands.

## Verification (this review)

| Suite | Command | Result |
| --- | --- | --- |
| SyncPlay unit tests (64: ported fork suite + upstream `GroupTests`, `PlayQueueManagerTests`, `SyncPlayManagerTests`, `WaitingGroupStatePingTests`) | `dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "FullyQualifiedName~SyncPlay" -c Release` | **64/64 pass** (after #26 fix; before it: build failure) |
| SyncPlay access-policy tests (16) | `dotnet test tests/Jellyfin.Api.Tests --filter "FullyQualifiedName~SyncPlay" -c Release` | **16/16 pass** |
| Zombie-WebSocket integration test (1, 50 s — covers the upstream 12.0 keep-alive fix + the fork's grace period end-to-end) | `dotnet test tests/Jellyfin.Server.Integration.Tests --filter "FullyQualifiedName~SyncPlayLostWebSocket" -c Release` | **1/1 pass** |
| Test-project builds, standard settings | `dotnet build` on the three test projects, `-c Release` | **0 warnings, 0 errors** (after #26 fix) |

### Verification (second review, 2026-09-08)

| Check | Command | Result |
| --- | --- | --- |
| SyncPlay unit tests, re-run on the current tree | `dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "FullyQualifiedName~SyncPlay" -c Release` | **64/64 pass**, 3 s — #26's fix confirmed still in place |
| `JsonDateTimeConverter` output per `DateTimeKind` (#36) | scratch console app serializing through `JsonDefaults.Options` | `Utc` → `…Z`, `Local` → `…-07:00`, **`Unspecified` → no designator**, sub-ms ticks dropped |
| Explicit-null DTO deserialization (#37) | same app, `{"GroupName":null}` / `{"PlayingQueue":null}` | both properties come back `null`, overwriting the constructor defaults |

## Recommended follow-ups (rough priority)

1. ~~Fix the re-base test-build breakage (#26).~~ **Done — this review.**
2. Fix the two-tier deadline's flags, which are wrong in both directions and are the
   highest-traffic path in SyncPlay: reset `_startedBySeek` on item-change requests
   (#27) and stop letting a `Ready` report relax the seek deadline (#33). One change
   set, one pair of regression tests; #33 also needs the premise of
   `HandleRequest_Seek_AfterASessionReported_ArmsLongWaitTimeout` revisited.
3. Fix the position clamp so an unknown runtime does not pin every position to 0 (#35),
   and clamp `SetPlayQueue`'s start position with the same helper (#29). These two must
   land together.
4. Null-guard the four queue-management `GetItemById` dereferences (#32).
5. Give the waiting cycle an absolute cap on top of the sliding deadline (#34), and
   decide whether the eviction grace (30 s) and the wait deadline (30 s) should be
   related rather than coincidentally equal.
6. Normalize `DateTimeKind.Unspecified` to UTC in `JsonDateTimeConverter` so the iOS
   fix actually always emits a designator (#36).
7. Make the current-session sends in `SessionJoin`/`SessionLeave` tolerant of a
   departed session (#31); structural fix remains old #3. If #3 is taken, snapshot the
   playlist first (#38) — awaiting the fan-out is what makes that aliasing live.
8. Capture the eviction CTS token before publishing it (#28 — LOW, see contested list);
   optionally re-check liveness before evicting (carried from #15).
9. Reject explicit JSON nulls on the four SyncPlay DTO properties (#37).
10. Add the `NewGroup` userless-session guard for consistency (#30).
11. Add `[Authorize(Policy = Policies.SyncPlayIsInGroup)]` to `Ping` (old #6).
12. Investigate the old #5 / jellyfin-desktop#139 `IgnoreGroupWait` interaction.
13. Re-test scrubbing with the **12.0 stock web client** — if upstream web fixed
    finding #24, #22's 2 s deadline can be reconsidered as a pure safety net; if not,
    #27 and #33 make the deadline logic the first thing to fix.
14. Live smoke tests on the 12.0 deployment (reconnect mid-group, iPad/iOS join,
    scrub-while-playing, two-device same-user group). #33 predicts a ~30 s stall on
    every scrub in an iPad-plus-web group; that is the single most valuable thing to
    confirm or refute on hardware.
15. Raise or adapt the 2 s time-sync threshold (old #4); rate-limit Ping fan-out (old #8).
