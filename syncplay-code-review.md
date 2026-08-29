# SyncPlay Code Review — Findings + iPad Blocker Fix

## Context

User reports SyncPlay symptoms:
- Browser clients desync mid-playback and have trouble connecting.
- Native iPad/iOS Jellyfin app does not work at all.
- Scrubbing ahead in a video does not re-sync the other members; pausing and playing
  again is the only recovery. Reproduced live 2026-08-29 — see that incident section
  and findings #19–#22.
- Referenced upstream report: [jellyfin-desktop#139](https://github.com/jellyfin/jellyfin-desktop/issues/139) — when navigating to next episode, audio starts as soon as the *first* member finishes loading rather than waiting for everyone.

This review walks the server-side SyncPlay implementation on `syncplay-fixes`, identifies bugs and perf risks, and applies a targeted fix to the highest-confidence root cause for the iPad failure.

---

## Observed incident — 2026-08-23

Findings #1–#14 below come from a static read of the implementation. This section
records the first time the SyncPlay code was observed failing against real users, on
the live server. Findings #15–#18 were derived from it.

Group `2e28c132-067c-42f2-9d03-df2cd42d2cfc`, 04:39–04:50 UTC, three participants
playing *The Twilight Saga: New Moon* (item `9789773b183989d3f6d3e4e021c9bd65`).
Reported symptoms: one user could not join the group unless he created it himself,
and his video froze repeatedly.

**Session identification.** SyncPlay logs identify participants only by session id.
The mapping below was derived by matching group-leave events against WebSocket
closes and then against the activity log — worth recording, since it is not
recoverable from the SyncPlay log lines alone:

| SyncPlay session | User | Client | IP |
| --- | --- | --- | --- |
| `ecd9d85601e4ad1f8caa4e0a1e9636b7` | jimbo4994 | Jellyfin Desktop 1.0.0 | 24.171.98.216 |
| `25a2bf776b8d3d7860d575599f80225f` | alecd | Jellyfin Desktop 1.0.0 | 76.130.148.158 |
| `5c88b88f54102663350603ce201a98d6` | bigr4232 | Jellium Desktop 0.1.0-dev | — |

### Symptom A — could not rejoin unless he created the group

Eviction is bound to the socket. The group-leave lands one millisecond before the
WebSocket close, and each subsequent reconnect is followed ~300 ms later by a
policy rejection:

```text
04:45:39.296  Session "ecd9d856…" left group "2e28c132-…"
04:45:39.297  WS "24.171.98.216" closed
04:49:20.373  WS "24.171.98.216" request          <- reconnect
04:49:20.689  AuthenticationScheme: "CustomAuthentication" was forbidden.
04:49:53.735  WS "24.171.98.216" request          <- reconnect
04:49:53.913  AuthenticationScheme: "CustomAuthentication" was forbidden.
```

Root cause is finding #15.

### Symptom B — repeated freezing

A fresh `/PlaybackInfo` call killed the running transcode out from under the player,
which was still fetching segments from it:

```text
04:44:45.330  User policy for "jimbo4994"
04:44:45.402  Stopping ffmpeg process ... a72f0cd2…m3u8
04:44:45.926  Deleting partial stream file(s)
04:44:45.990  cannot serve a72f0cd2…42.ts as transcoding quit before we got there
04:44:45.994  500  GET /videos/9789773b…/hls1/main/42.ts
```

The corroborating ffmpeg log
(`FFmpeg.Transcode-2026-08-23_04-43-03_9789773b…_9b8d2a64.log`) shows the job died
having written only through segment 41 (`time=00:02:01.95`) at
**`speed=1.19x, fps=29`**, while the player was requesting segment 42 (126 s). The
client had outrun its own transcoder. A concurrent job on the same file ran at
2.33x for comparison.

What kept re-triggering it was the correction loop in finding #17, driven by the
bad offset in finding #16.

### Environmental contributor (not a code defect)

The source is a 94 Mbps 3840x2160 10-bit HEVC Dolby Vision remux, TrueHD 7.1 Atmos,
with PGS image subtitles flagged default. Every client therefore got
HEVC to `h264_qsv`, 4K to 1080p, `tonemap_vaapi`, TrueHD to AAC, plus **PGS burn-in
via `overlay_qsv` on stream `0:4`**. Burn-in means a seek cannot be served from an
existing segment — it restarts ffmpeg from scratch. This is what turns finding #17's
seek storm from a nuisance into a hard stall, and it is why the same code path is
survivable on direct-play content.

---

## Observed incident — 2026-08-29

Group `ecb1c043-7f55-4559-80ac-fbc64512f1d5`, 06:14–07:39 UTC, two participants playing
*Goodbye Earl*, on the patched build (`dev` @ `10bb67ef7c`). Reported symptom:
**scrubbing ahead in a video does not re-sync the other members; pausing and playing
again fixes it.** Findings #19–#22 were derived from it.

This is the first incident recorded *after* the fixes for #15–#18 shipped, and two of
the four findings below are regressions those fixes introduced.

**Session identification**, extending the 2026-08-23 table's convention:

| SyncPlay session | User | Client | Basis |
| --- | --- | --- | --- |
| `5c88b88f54102663350603ce201a98d6` | bigr4232 | Jellium Desktop 0.1.0-dev | carried over from the 2026-08-23 table |
| `43e35c18dc5ee717d788300d60849d9a` | mellyKcs | Jellyfin MPV Shim 2.9.0 | `User policy for "mellyKcs"` 07:38:19.532 → `PlaybackStartEvent` 07:38:20.556 → group join 07:38:22.874 |

### Root cause

Every seek made while the group is playing routes
[PlayingGroupState.HandleRequest(SeekGroupRequest)](MediaBrowser.Controller/SyncPlay/GroupStates/PlayingGroupState.cs#L108-L114)
into `WaitingGroupState`, which sets `PositionTicks` to the seek target, calls
`SetAllBuffering(true)` and sets `ResumePlaying = true`
([WaitingGroupState.cs:299-332](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L299-L332)).
The group then leaves `Waiting` only when every participant reports `Ready` — or when
someone forces it with an `Unpause`.

The defects all live in the `ResumePlaying == true` branch of
`HandleRequest(ReadyGroupRequest, ...)`
([WaitingGroupState.cs:459-578](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L459-L578)).

**The signature of the bug is an asymmetry between the two branches of that handler.**
The `ResumePlaying == false` branch — seek while *paused*,
[lines 582-615](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L582-L615) —
compares positions against the 500 ms `maxPlaybackOffsetTicks` tolerance **and** resets
the correction counter when a session converges. The `ResumePlaying == true` branch —
seek while *playing* — does neither. That is precisely the user-visible split: pause →
scrub → play works, scrub while playing does not.

### Why pause-then-play is the workaround

`Unpause` while `Waiting` takes the force-start branch
([WaitingGroupState.cs:239-252](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L239-L252)):
`SetAllBuffering(false)` plus `PlayingGroupState { IgnoreBuffering = true }`. The
following Pause → Unpause round-trip then runs
[PlayingGroupState.HandleRequest(UnpauseGroupRequest)](MediaBrowser.Controller/SyncPlay/GroupStates/PlayingGroupState.cs#L62-L87),
which recomputes `LastActivity = UtcNow + max(2×ping, DefaultPing)` and broadcasts one
Unpause carrying the group's `PositionTicks` to everyone. It bypasses `WaitingGroupState`
entirely — no `Ready` comparison, no correction loop, no stale `LastActivity`. That is
why the manual recovery works when the seek itself does not.

---

## Findings (prioritized)

### CRITICAL — iPad/iOS native app blocker (high confidence)

**1. `JsonDateTimeConverter` emits non-ISO-8601-canonical timestamps — ✅ FIXED**

> **Status:** Fixed on `syncplay-fixes` branch. `Write` method now emits `yyyy-MM-ddTHH:mm:ss.fffK` (exactly 3 fractional digits, preserving UTC/offset/unspecified suffix behavior). Awaiting iPad smoke test.
>
> Fallout, fixed 2026-08-22: the converter is global, so every `DateTime` Jellyfin serializes now truncates to milliseconds — including plugin manifests written to disk. `PluginManagerTests.PopulateManifest_ExistingMetafilePlugin_PopulatesMissingFields` and `..._NoMetafile_PreservesManifest` compared a full-precision `DateTime` against one round-tripped through `JsonDefaults` and failed on `Timestamp`. `GenerateTestPackage` now builds its timestamp at millisecond precision, so the assertions stay exact and test what they are about. Full solution suite green afterwards (13 projects). The converter itself still has no unit test pinning the 3-digit contract.

[src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs:21-32](src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs#L21-L32)

```csharp
if (value.Millisecond == 0) {
    writer.WriteStringValue(value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffZ", ...));
} else {
    writer.WriteStringValue(value);   // .NET round-trip format, up to 7 fractional digits
}
```

The converter is registered globally via [src/Jellyfin.Extensions/Json/JsonDefaults.cs:44](src/Jellyfin.Extensions/Json/JsonDefaults.cs#L44) and applied to all API output by [Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:137-156](Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs#L137-L156).

Both branches emit **7 fractional-second digits**. Swift's `JSONDecoder` with `.iso8601` strategy (the default `ISO8601DateFormatter`) accepts **0** fractional digits; with `withFractionalSeconds` it accepts **exactly 3**. It rejects 7. Every SyncPlay DTO that carries a `DateTime` therefore fails to decode on the iOS app:
- [MediaBrowser.Model/SyncPlay/SendCommand.cs:45,63](MediaBrowser.Model/SyncPlay/SendCommand.cs#L45) — `When`, `EmittedAt`
- [MediaBrowser.Model/SyncPlay/UtcTimeResponse.cs:25,27](MediaBrowser.Model/SyncPlay/UtcTimeResponse.cs#L25) — `RequestReceptionTime`, `ResponseTransmissionTime`
- [MediaBrowser.Model/SyncPlay/GroupInfoDto.cs:9-57](MediaBrowser.Model/SyncPlay/GroupInfoDto.cs) — `LastUpdatedAt`
- `PlayQueueUpdate.LastUpdate`, `SessionInfoDto.LastActivityDate`, etc.

A failed `UtcTimeResponse` decode breaks the iOS client's clock-offset computation, which in turn means every subsequent SyncPlay command lands at a wildly wrong timestamp — explaining why iOS appears to "do nothing." This is consistent with the previously-recorded memory note from 2026-04-25.

The branch also has a secondary bug: when `Millisecond == 0` the code hardcodes `Z`; when `Millisecond != 0` it delegates to `WriteStringValue(DateTime)` which respects `DateTimeKind` (UTC→`Z`, Unspecified→nothing, Local→offset). The output format is inconsistent depending on the value.

**Reproduce — wire format observation (definitive):**
1. Run the server: `dotnet run --project Jellyfin.Server`.
2. From any HTTP client with a valid auth token, hit `GET /SyncPlay/GetUtcTime`.
3. Observe response body — `RequestReceptionTime` and `ResponseTransmissionTime` end with 7 fractional digits (e.g. `"2026-04-26T14:30:45.1234567Z"`).
4. Anywhere a value happens to land on a whole millisecond, expect `.0000000Z` instead.

**Reproduce — Swift decode failure (definitive):**
A 5-line Swift script confirms the iOS impact without needing the Jellyfin app:
```swift
import Foundation
let json = "\"2026-04-26T14:30:45.1234567Z\"".data(using: .utf8)!
let dec = JSONDecoder()
dec.dateDecodingStrategy = .iso8601
do { _ = try dec.decode(Date.self, from: json); print("ok") }
catch { print("FAIL:", error) }
```
Output: `FAIL: dataCorrupted(...)` — same exception thrown when iOS tries to deserialize `UtcTimeResponse`.

**Reproduce — iPad symptom (end-to-end):**
1. Updated server, untouched client.
2. From the native iOS app, attempt to create or join a SyncPlay group.
3. Tap any playback control (play/pause/seek). Server logs show the request hit (`HandleRequest`), but the client never appears to act on the response. Group state on other clients is updated; iPad client is "frozen."
4. Optional: enable verbose logging on the iOS app — `JSONDecoder` failure is logged before any SyncPlay action would be attempted.

**This is the fix being applied. See "Fix" section below.**

---

### CRITICAL — SyncPlay membership lost on WebSocket reconnect (confirmed live)

**15. `OnSessionEnded` evicts from the group with no grace period — ✅ FIXED**

> **Status:** Fixed. `OnSessionEnded` now defers the eviction by `EvictionGracePeriod` (30 s, `SyncPlayManager.EvictionGracePeriod`) and `SessionControllerConnected` cancels the pending leave, so a reconnecting client keeps its map entry, its `_activeUsers` count (which is what stops the `IsInGroup` 403), and its group. The map/participant/counter state is untouched during the grace window, so `IsGroupEmpty()`-driven teardown cannot reap the group early. `JoinGroup` (restore branch) and `LeaveGroup` also clear the pending leave.
>
> One addition beyond the suggested fix: the `JoinGroup` "Restore session" branch previously called `UpdateSessionsCounter(session.UserId, 1)` — unreachable dead code before this fix, but reachable on the reconnect path now. Since the map entry (and its count) survives the grace window, that increment double-counts the user, leaving `IsUserActive` true after the user's last session leaves. The increment was removed; the counter now always equals the number of group memberships the user holds.
>
> Unit tests: `tests/Jellyfin.Server.Implementations.Tests/SyncPlay/SyncPlayManagerTests.cs` (grace period, reconnect cancellation, explicit-leave cancellation, restore double-count). Verified 2026-08-22: all 5 pass, solution builds clean. Awaiting live smoke test (drop a client's socket mid-group).
>
> Review of the applied fix — two hardening nits, neither a blocker, plus one behavioral trade-off:
>
> - `SessionEnded` and `SessionControllerConnected` are both raised through `EventHelper.QueueEventIfNotNull` (`Task.Run`), so their handlers have no ordering guarantee. On a very fast reconnect the cancel can run before the schedule, and the session is evicted 30 s later anyway. Not a regression (old code evicted instantly), but it can be closed by re-checking liveness before evicting: `SessionManager.CloseIfNeededAsync` removes the session from `_activeConnections` *before* raising `SessionEnded`, so `_sessionManager.Sessions.Any(s => string.Equals(s.Id, session.Id, StringComparison.OrdinalIgnoreCase))` at the end of the grace window is a reliable "it came back" test.
> - `OnSessionEnded` reads `cts.Token` after publishing the CTS into `_pendingLeaves`; a concurrent `CancelPendingLeave` can dispose it in between, making `cts.Token` throw `ObjectDisposedException` (swallowed by `EventHelper`). Capture the token before the dictionary insert. Same shape in `Dispose(bool)`: `pendingLeave.Cancel()` on a CTS a racing `CancelPendingLeave` already disposed throws — shutdown-only.
> - Trade-off: a session that ends for good while buffering now lingers as a participant for the full grace period. `WaitingGroupState.SessionLeaving` is what resumes the group when a buffering member departs, so the remaining clients can stay in Waiting up to 30 s where they previously resumed at once.

[Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs:387-396](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L387-L396)

```csharp
private void OnSessionEnded(object sender, SessionEventArgs e)
{
    var session = e.SessionInfo;

    if (_sessionToGroupMap.TryGetValue(session.Id, out _))
    {
        var leaveGroupRequest = new LeaveGroupRequest();
        LeaveGroup(session, leaveGroupRequest, CancellationToken.None);
    }
}
```

`LeaveGroup` runs the moment a session ends, so a transient WebSocket drop is
permanent eviction. It then hits
[SyncPlayManager.cs:238](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L238),
`UpdateSessionsCounter(session.UserId, -1)`, dropping the user's counter to zero so
`IsUserActive` returns false
([SyncPlayManager.cs:362-370](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L362-L370)).
[SyncPlayAccessHandler.cs:66-72](Jellyfin.Api/Auth/SyncPlayAccessPolicy/SyncPlayAccessHandler.cs#L66-L72)
gates the `IsInGroup` requirement on exactly that, so **every SyncPlay endpoint
carrying `Policies.SyncPlayIsInGroup` returns 403 after a reconnect**. Creating a
group uses `SyncPlayCreateGroup`, which checks the user's access level rather than
group membership — which is precisely why the only recovery the affected user found
was starting the group himself.

Two facts make the fix small:

- **Session ids are stable across reconnects.**
  [SessionManager.cs:554](Emby.Server.Implementations/Session/SessionManager.cs#L554)
  sets `Id = key.GetMD5()` where `key = appName + deviceId`
  ([SessionManager.cs:486-487](Emby.Server.Implementations/Session/SessionManager.cs#L486-L487))
  — no user, no socket, no timestamp. The reconnecting client returns under the
  *same* session id; nothing needs re-keying. The membership row had simply been
  deleted.
- **The restore path already exists but is unreachable.** `JoinGroup` carries a
  "Restore session" branch at
  [SyncPlayManager.cs:180-188](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L180-L188)
  for a session already in the target group. It is dead code on the reconnect path,
  because `LeaveGroup` at
  [SyncPlayManager.cs:226](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L226)
  removed the `_sessionToGroupMap` entry first. Membership was designed to survive a
  reconnect; `OnSessionEnded` fires too eagerly.

**Reproduce (definitive):**
1. Join a SyncPlay group from a browser client, with a second client also in the group.
2. In DevTools, Network conditions, toggle offline for ~3 seconds, then back online —
   or just kill the WebSocket frame connection. The client reconnects on its own.
3. Server log shows `Session {id} left group {gid}` at the same millisecond as
   `WS "{ip}" closed`.
4. From the reconnected client, press pause. Observe `403 Forbidden` and
   `AuthenticationScheme: "CustomAuthentication" was forbidden.` in the server log.
5. Confirm the asymmetry: `POST /SyncPlay/New` from the same client succeeds.

Suggested fix — defer eviction rather than performing it inline:

```csharp
private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingLeaves
    = new(StringComparer.OrdinalIgnoreCase);

private void OnSessionEnded(object sender, SessionEventArgs e)
{
    var session = e.SessionInfo;
    if (!_sessionToGroupMap.ContainsKey(session.Id))
    {
        return;
    }

    var cts = new CancellationTokenSource();
    _pendingLeaves[session.Id] = cts;

    _ = Task.Delay(TimeSpan.FromSeconds(30), cts.Token)
        .ContinueWith(
            _ => LeaveGroup(session, new LeaveGroupRequest(), CancellationToken.None),
            cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
}
```

Cancel it on reconnect. `SessionManager` already raises `SessionControllerConnected`
when a socket attaches
([SessionWebSocketListener.cs:129](Emby.Server.Implementations/Session/SessionWebSocketListener.cs#L129)),
so subscribe alongside the existing `SessionEnded` hook at
[SyncPlayManager.cs:92](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L92):

```csharp
private void OnSessionControllerConnected(object sender, SessionEventArgs e)
{
    var session = e.SessionInfo;
    if (_pendingLeaves.TryRemove(session.Id, out var cts))
    {
        cts.Cancel();
        cts.Dispose();
    }
}
```

Because the map entry and the `_activeUsers` count are untouched during the grace
window, `IsUserActive` stays true — the specific thing that stops the 403.

Two correctness details for whoever implements this:

- `JoinGroup` and the explicit `LeaveGroup` path must also clear `_pendingLeaves`,
  or a stale timer will evict a session that deliberately rejoined.
- `IsGroupEmpty()` / group teardown must treat a grace-window member as still
  present, or the group is reaped before the client returns. That is what happened at
  04:50:18 in the incident: `Group 2e28c132-… is empty, removing it`.

**Client-side alternative, and why it is not preferred:** the web client could
re-issue `JoinGroup` on socket reconnect. Smaller, no server change — but it fixes
only the patched client, and the same drop already affects Jellium, Android TV, and
the iOS app. The server-side grace period covers every client at once.

Note this also partially mitigates Symptom B: fewer forced rejoins means fewer
`/PlaybackInfo` calls, which is what was killing the in-flight ffmpeg jobs.

---

### HIGH — Sync correctness on browser

**2. Unit-mismatch bug in late-recovery clamp**

[MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs:562-563](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L562-L563)
(cited as 502-503 before the #16/#17 fixes shifted the file)

```csharp
delayTicks = context.GetHighestPing() * 2 * TimeSpan.TicksPerMillisecond;
delayTicks = Math.Max(delayTicks, context.DefaultPing);   // BUG: ticks vs ms
```

`delayTicks` is in **100-ns ticks**. `context.DefaultPing` returns **500** with a docstring that says "default ping value used for sessions" — i.e., 500 milliseconds ([Group.cs:91](Emby.Server.Implementations/SyncPlay/Group.cs#L91)). The `Math.Max` is comparing 100-ns ticks to a millisecond integer, so the clamp is effectively a no-op (any non-trivial delay in ticks already exceeds 500). The intent — "don't let the resume delay drop below the default ping" — never fires.

Real-world impact: in the path where a buffering client recovers but doesn't hit the high-ping threshold ([line 544](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L544)), the group is told to resume in `delayTicks` units, but `delayTicks` may correspond to a much smaller real interval than the ping floor was meant to enforce. Slow clients fall further behind.

**Fix this together with finding #21**, which is the `if` half of the same branch — see the note at the end of #21.

**Reproduce — unit test (definitive):**
The branch is straightforward to exercise with a fake `IGroupStateContext`. The key behavior to assert: after the buggy line, `delayTicks` is unchanged for any input ≥ 500 ticks (≥ 50 µs). A unit test that calls `WaitingGroupState.HandleRequest(ReadyGroupRequest)` with a context whose `GetHighestPing()` returns 100 (ms) and a request whose `When = DateTime.UtcNow` (so `delayTicks` ≈ 0 to a few ms in ticks, ~tens of thousands of ticks) will hit line 502, then line 503's `Math.Max` returns the same `delayTicks` rather than promoting to `DefaultPing × TicksPerMillisecond`. Assert that the broadcast `command.When` lands ~5 s in the future once the unit fix is applied — currently it does not.

**Reproduce — observational (suggestive, not definitive):**
Two clients, one with high ping. Pause-then-unpause repeatedly. Look for `_logger.LogWarning("Session {SessionId} resumed playback, group {GroupId} has {Delay} seconds to recover.", ...)` — the `{Delay}` value will be tiny (sub-ms) because `delayTicks` was effectively unmodified.

Suggested fix:
```csharp
delayTicks = Math.Max(delayTicks, context.DefaultPing * TimeSpan.TicksPerMillisecond);
```

**3. Async fire-and-forget under a lock**

[Emby.Server.Implementations/SyncPlay/Group.cs:389-414](Emby.Server.Implementations/SyncPlay/Group.cs#L389-L414) and [SyncPlayManager.cs:333-351](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L333-L351)

`SendGroupUpdate` and `SendCommand` return `Task.WhenAll(...)` but every caller in the state machine ignores the returned `Task` (`SessionJoined`, `HandleRequest(...)`, etc. all call them as void statements). The enclosing `lock (group)` is released as soon as the synchronous portion returns — *before* the WebSocket sends complete. Two consequences:

- **No ordering guarantee across requests.** A second request entering the lock can mutate state and fire its own broadcast that races with the first broadcast on the wire.
- **Exceptions are swallowed.** If `_sessionManager.SendSyncPlayGroupUpdate` faults (e.g., a closed websocket), the `Task` is never awaited, so the exception is observed only by the unobserved-task handler.

This is the most plausible explanation for browser desync that "sometimes" happens: under pause/seek storms or many participants, ordering at the session level is best-effort.

**Reproduce — log-based (suggestive):**
Run server with debug logging on `Emby.Server.Implementations.SyncPlay`. Have 3+ browser clients in a group. From one client, programmatically (DevTools console) fire 20 pause/unpause requests in a tight loop:
```javascript
// in browser console while connected to a SyncPlay group
for (let i = 0; i < 20; i++) {
  fetch('/SyncPlay/Pause', {method:'POST', headers:{'X-Emby-Authorization':'...'}});
  await new Promise(r=>setTimeout(r,5));
  fetch('/SyncPlay/Unpause', {method:'POST', headers:{'X-Emby-Authorization':'...'}});
}
```
Watch other clients — at least one will end up in the wrong play/pause state relative to the firing client. The server log will show requests handled in order, but the other clients' UI state lands inconsistent because the broadcasts to each session race independently.

**Reproduce — instrumented (definitive):**
Add temporary `await Task.Delay(50)` inside `_sessionManager.SendSyncPlayGroupUpdate` before the actual send. The firing client's request returns immediately (lock released), but the broadcast to peers arrives after a subsequent request has already mutated state. Verify with two requests issued ~10 ms apart that produce visibly out-of-order updates on a peer client.

Refactor candidates: have `Apply()` and the state-machine methods return `Task` and await them under the lock (preferred, but requires replacing `lock(group)` with a `SemaphoreSlim`), or queue broadcasts on a per-group serialized channel that drains outside the lock.

**4. iPad/web clock-skew silent zeroing**

[WaitingGroupState.cs:425-433](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L425-L433)

When `|elapsedTime| > TimeSyncOffset (2000 ms)` the server logs a warning and zeros `elapsedTime`. A backgrounded browser tab — or an iOS app that just woke from suspension — easily exceeds 2 seconds of timer skew. The result is that the server stops compensating for that client's reported time, the client's "Ready" position is treated as authoritative-now, and the group converges on a wrong position.

The threshold may also be too tight given that `TimeSyncOffset` is documented as "2000 ms" but the comparison is `Math.Abs(elapsedTime.Ticks) > timeSyncThresholdTicks` — correct. The threshold itself is the issue, not the math.

**Reproduce — backgrounded browser (definitive):**
1. Two browser clients in a SyncPlay group, both playing.
2. On client A, switch to a different tab for ~30 seconds (modern browsers throttle background timers; `Date.now()` on resume is real time but any internally-scheduled callbacks may have drifted).
3. Switch back. Client A's next `Ready` event will carry a `When` value that — depending on whether A was buffering during the throttle — may be off by more than 2 s relative to server time.
4. Server logs `"Session {SessionId} is not time syncing properly. Ignoring elapsed time."` then proceeds with `elapsedTime = 0`. Client A is now out of position; the next position computation treats its reported tick count as authoritative-now, and the group converges on an incorrect position.

**Reproduce — clock-skewed client (definitive):**
On a Windows or Linux client, change the system clock by 3 seconds (`sudo date -s '+3 seconds'`) immediately before issuing a `/SyncPlay/Ready`. Server log will show the warning and the elapsed time zeroing.

Suggested change: raise threshold to ~5 s, or use the client's running ping estimate as an adaptive bound.

**5. `IsBuffering()` honors `IgnoreGroupWait` ⇒ premature group-ready**

[Group.cs:475-486](Emby.Server.Implementations/SyncPlay/Group.cs#L475-L486)

```csharp
foreach (var session in _participants.Values) {
    if (session.IsBuffering && !session.IgnoreGroupWait) { return true; }
}
return false;
```

If any session has `IgnoreGroupWait = true`, that session is treated as "not buffering" for group-ready purposes — by design. But this is also the symptom path for [jellyfin-desktop#139](https://github.com/jellyfin/jellyfin-desktop/issues/139). After a `NextItem` the queue resets all participants to `buffering=true` ([WaitingGroupState.cs:590](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L590)), but if any session previously toggled IgnoreWait on (e.g., the web client's "ignore group wait" UI), that session's buffering state is never blocking. As soon as the *first* non-ignoring session reports Ready, `IsBuffering()` returns false and the group unpauses.

This is consistent with the bug report. Worth checking whether `IgnoreGroupWait` should be reset to `false` on a `SetAllBuffering(true)` or queue change.

**Reproduce — exact scenario from issue #139 (definitive):**
1. Two browser clients (A and B) in a SyncPlay group, currently playing an episode.
2. On client A, click "Ignore Group Wait" (the toggle that sends `IgnoreWaitGroupRequest` with `IgnoreWait=true`). Verify with server log: `"Ignoring session {SessionId}, group {GroupId} is ready."`
3. From either client, click "Next Episode."
4. Both clients enter buffering state. Client B (whichever is faster — typically the one without throttled disk/network) finishes loading first and emits `Ready`.
5. **Bug observed**: as soon as B reports Ready, the server transitions to `PlayingGroupState` and broadcasts Unpause. B starts audio. A is still buffering — A may catch up later or fall behind permanently.
6. **Expected**: server should wait for A's Ready, even though A has IgnoreWait set, because A is also buffering on a queue change (not just slow on a Ping).

Reduce to one client to verify it's not noise: with just A (IgnoreWait=true) alone in the group, NextItem will start immediately because no one else is even buffering — that's by design. The bug is the *interaction* between `IgnoreWait` and a fresh `SetAllBuffering(true)`.

**Reproduce — without UI (curl):**
After joining a group from two sessions:
```
POST /SyncPlay/SetIgnoreWait  body: { "IgnoreWait": true }     # from session A
POST /SyncPlay/NextItem       body: { "PlaylistItemId": "..." } # from session B
POST /SyncPlay/Ready          body: { ..., IsPlaying: true }    # from session B only
```
Observe server log: `"Session {B} is recovering, group {G} will resume in ..."` — fired before A reports Ready. Confirms the premature transition.

**16. Negative `delayTicks` schedules a Pause command in the past — ✅ FIXED**

> **Status:** Fixed, together with #17. The `delayTicks < 0` branch now issues a corrective Seek instead of a past-dated Pause, drawing on the shared correction budget from #17 (`MaxCorrectionAttempts`, 5 per session per waiting cycle).
>
> Once that budget is spent the session is paused *in place* — `delayTicks` clamped to 0 — rather than left alone: it stops drifting further ahead while the group waits, and stays out of the buffering set so the group proceeds without it. The bare `Math.Max(delayTicks, 0)` this review rejected as a standalone fix is the right fallback *after* corrections have been tried and failed, which is the case the rejection did not cover.
>
> Unit tests: `HandleRequest_Ready_AheadOfGroup_SendsSeekCorrection`, `HandleRequest_Ready_AheadOfGroup_StopsCorrectingAfterMaxAttempts` (asserts no past-dated command survives the give-up), `HandleRequest_Ready_AheadOfGroup_LastBufferingSession_ResumesGroup`, `HandleRequest_Ready_BehindGroup_SchedulesFuturePause`.
>
> **⚠️ This fix left a regression — see finding #19.** The new `delayTicks < 0` guard had no tolerance band, so it treated the one-way latency of a `Ready` report as "ahead of the group" and dragged perfectly in-sync clients into the correction loop it was meant to protect them from. Confirmed live 2026-08-29.
>
> **Regression resolved 2026-08-29 — the area is closed.** The guard now uses the same 500 ms `MaxPlaybackOffset` tolerance as its sibling position tests (`delayTicks < -maxPlaybackOffsetTicks`), and the fall-through clamps `delayTicks` to 0 before scheduling the Pause, so the relaxed band only ever absorbs sub-tolerance jitter and can never re-introduce the past-dated command. Unit test: `HandleRequest_Ready_SlightlyAheadWithinTolerance_SchedulesPauseNow`. All 15 SyncPlay tests pass.

[WaitingGroupState.cs:443](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L443)
computes the offset between the group and the reporting client:

```csharp
var delayTicks = context.PositionTicks - clientPosition.Ticks;
```

[WaitingGroupState.cs:475](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L475)
then uses it directly as a fire time, with no sign guard:

```csharp
command.When = currentTime.AddTicks(delayTicks);
```

A negative `delayTicks` means the client is *ahead* of the group, and the command is
scheduled in the past. The client fires it immediately and lands further out of
position. The `!request.IsPlaying` guard at
[WaitingGroupState.cs:452](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L452)
does not cover this — that path only catches a client that is *paused* and lost.

**Live evidence.** alecd's session, logged three times at 04:43:01 after the group
restarted the item and reset `context.PositionTicks` while his client still reported
its previous position:

```text
Session "25a2bf77…" will pause when ready in -76.4098339 seconds.
```

A Pause scheduled 76 seconds in the past. This is what put that session permanently
out of position and fed finding #17's correction loop.

**Reproduce (definitive):** two clients in a group. Have client A report a `Ready`
with `PositionTicks` well ahead of the group position while `IsPlaying: true` — the
simplest route is to seek A forward during a group `Waiting` state. Server logs
`will pause when ready in {negative} seconds`; inspect the `SyncPlayCommand` frame on
A's WebSocket and confirm `When` is earlier than the frame's arrival time.

Suggested fix — treat "ahead of the group" the way line 452 treats "lost in time",
rather than scheduling into the past. Replace the
[WaitingGroupState.cs:471-479](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L471-L479)
block with:

```csharp
if (context.IsBuffering())
{
    // A negative delay means this client is *ahead* of the group. Scheduling a
    // command in the past makes the client fire it immediately and land further
    // out of position, so correct it instead of pausing it.
    if (delayTicks < 0)
    {
        context.SetBuffering(session, true);

        var seek = context.NewSyncPlayCommand(SendCommandType.Seek);
        context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, seek, cancellationToken);
        SendGroupStateUpdate(context, request, session, cancellationToken);

        _logger.LogWarning(
            "Session {SessionId} is ahead of group {GroupId} by {Delay} seconds, correcting.",
            session.Id,
            context.GroupId.ToString(),
            TimeSpan.FromTicks(-delayTicks).TotalSeconds);
        return;
    }

    // Others are still buffering, tell this client to pause when ready.
    var command = context.NewSyncPlayCommand(SendCommandType.Pause);
    command.When = currentTime.AddTicks(delayTicks);
    context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);

    _logger.LogInformation("Session {SessionId} will pause when ready in {Delay} seconds. Group {GroupId} is waiting for all ready events.", session.Id, TimeSpan.FromTicks(delayTicks).TotalSeconds, context.GroupId.ToString());
}
```

A bare `Math.Max(delayTicks, 0)` clamp was considered and rejected: it stops the
past-dated command but leaves the client silently 76 s out of position with no
correction issued.

**This guard requires finding #17 to be safe.** The corrective Seek added above is
itself a correction — without an attempt cap, a client that is ahead and cannot seek
back will loop on this new path instead of the old one.

**17. Seek-correction loop has no backoff or attempt cap — ✅ FIXED, ⚠️ left a regression (#20)**

> **Status:** Fixed, together with #16. All three seek-correction loops in `HandleRequest(ReadyGroupRequest, ...)` now share one budget through `RegisterCorrectionAttempt`: the out-of-tolerance path this finding named, #16's new ahead-of-group path, and the "got lost in time" path (`!request.IsPlaying`, [WaitingGroupState.cs:462-491](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L462-L491)) — that third one has the same unbounded shape and was not in the suggested fix. After 5 corrections the session is treated as ready and left desynced, per the judgement call below.
>
> Two changes against the first implementation of this fix:
>
> - **The give-up stranded the group.** It called `context.SetBuffering(session, false)` and then `return`ed, which skipped the `if (!context.IsBuffering())` block immediately below — so when the abandoned session was the last one the group was waiting on, the group sat in `Waiting` forever. That is precisely the hostage situation the cap exists to end. The give-up now falls through to the shared "session is ready" path instead of returning. Regression test: `HandleRequest_Ready_OutOfTolerance_GivingUpReleasesGroup`, confirmed failing against the earlier version (`Assert.IsType() Failure: Value is null`, expected `PausedGroupState`).
> - **The counter reset undid the give-up.** Resets sat on every non-correcting path, including the Pause-scheduling path a given-up session still passes through. A session that could not converge would therefore get a fresh budget of 5 seeks every sixth `Ready` — a slower storm, not a bounded one. The reset now sits only where the session is measured to have reached the group position; a give-up never resets. The redundant reset on the group-resume path was dropped: the state object is discarded there anyway.
>
> Unit tests: `tests/Jellyfin.Server.Implementations.Tests/SyncPlay/WaitingGroupStateTests.cs` — 9 tests over both findings, all passing 2026-08-22 (`dotnet test --filter FullyQualifiedName~SyncPlay`, 14 with #15's). Awaiting live smoke test (a client on a transcode slower than realtime, or a forced seek ahead during `Waiting`).
>
> **⚠️ This fix left a regression — see finding #20.** The counter reset landed on the `ResumePlaying == false` branch only, so on the seek-while-playing path the budget never resets and the give-up branch re-fires on every subsequent `Ready` — "after 6 … 7 … 8 … 11 corrections". That is the bounded-storm outcome this finding set out to produce, but only on half the handler. The area is not closed.

[WaitingGroupState.cs:519-535](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L519-L535)
sends a `Seek` on every out-of-tolerance `Ready` and returns. A client that cannot
converge inside `MaxPlaybackOffset` (500 ms,
[Group.cs:103](Emby.Server.Implementations/SyncPlay/Group.cs#L103)) generates an
unbounded correction storm: each Seek prompts a fresh `Ready`, which is still out of
tolerance, which prompts another Seek.

**Live evidence.** Dozens of `is seeking to wrong position, correcting` per second at
04:42:45–46 and 04:44:23–25, concentrated on alecd's stuck session but hitting all
three participants. Combined with the burn-in transcode described in the incident
section, every one of those seeks restarted an ffmpeg job — which is the mechanism
behind Symptom B.

**Reproduce (definitive):** put one client in a state it cannot correct out of — the
easiest is finding #16's condition, or a client on a transcoded stream slower than
realtime. Watch the server log: the warning repeats without limit for as long as the
group stays in `Waiting`.

Suggested fix — cap the corrections. `group.HandleRequest` is invoked under
`lock (group)`
([SyncPlayManager.cs:333](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L333)),
so a plain `Dictionary` field on the state object is safe, and a `WaitingGroupState`
instance lives exactly one Waiting cycle — the counter resets on its own at the state
transition. Add alongside the existing fields at
[WaitingGroupState.cs:23-51](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L23-L51):

```csharp
/// <summary>
/// Number of corrective seeks issued per session during this waiting cycle.
/// </summary>
private readonly Dictionary<string, int> _correctionAttempts = new();

/// <summary>
/// Maximum corrective seeks before a session is left behind rather than looped on.
/// </summary>
private const int MaxCorrectionAttempts = 5;
```

Then gate the correction block:

```csharp
if (Math.Abs(context.PositionTicks - requestTicks) > maxPlaybackOffsetTicks)
{
    _correctionAttempts.TryGetValue(session.Id, out var attempts);
    attempts++;
    _correctionAttempts[session.Id] = attempts;

    if (attempts > MaxCorrectionAttempts)
    {
        // Client cannot reach the group position (slow transcode, bad clock,
        // unseekable stream). Stop correcting: looping starves it further and
        // restarts its transcode on every seek.
        context.SetBuffering(session, false);

        _logger.LogWarning(
            "Session {SessionId} failed to reach position after {Attempts} corrections in group {GroupId}; proceeding without it.",
            session.Id,
            attempts,
            context.GroupId.ToString());
        return;
    }

    // Session still not ready.
    context.SetBuffering(session, true);
    // Session is seeking to wrong position, correcting.
    var command = context.NewSyncPlayCommand(SendCommandType.Seek);
    context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, command, cancellationToken);

    SendGroupStateUpdate(context, request, session, cancellationToken);

    _logger.LogWarning("Session {SessionId} is seeking to wrong position, correcting.", session.Id);
    return;
}

_correctionAttempts.Remove(session.Id);
```

Note the reset must sit outside the `if`, or a session that converges once and then
drifts again carries a stale count into its next correction.

Two judgement calls worth review before implementing:

- **`SetBuffering(session, false)` on give-up is deliberate.** It unblocks
  `IsBuffering()` so the rest of the group can proceed, at the cost of leaving one
  member desynced. The alternative — hold the whole group hostage to the slowest
  client — is what the current code effectively does. A follow-up could emit a new
  group update type so that client can surface "you are out of sync" rather than
  drifting silently.
- **Backoff alone is the weaker option.** Spacing the seeks out still restarts the
  transcode on each one; it makes the storm slower without making it terminate. The
  cap is what ends it. Backoff is worth adding *on top* if corrections need to stay
  open-ended.

**19. Zero-tolerance "ahead of group" check bounces in-sync clients — regression from #16 — ✅ FIXED**

> **Status:** Fixed 2026-08-29, as suggested. The guard is `delayTicks < -maxPlaybackOffsetTicks` — the same 500 ms tolerance the sibling comparisons use — and the fall-through clamps `delayTicks` to 0 before scheduling the Pause, so the relaxed band only ever absorbs sub-tolerance jitter. A 200 ms lead (one-way report latency) now produces an immediate pause with no Seek correction; a 100 s lead still corrects and gives up per #16/#17. Unit test: `HandleRequest_Ready_SlightlyAheadWithinTolerance_SchedulesPauseNow`; the existing ahead-of-group tests (100 s lead) are unchanged and passing. The #20 regression (budget reset on the `ResumePlaying` path) is a separate finding, fixed the same day — see #20.

[WaitingGroupState.cs:500](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L500)

```csharp
if (delayTicks < 0)
```

`delayTicks = context.PositionTicks - (requestTicks + elapsedTime)`, where `elapsedTime
= currentTime - request.When` is the one-way network latency of the `Ready` report
([lines 436-454](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L436-L454)).
During a waiting cycle `context.PositionTicks` is frozen at the seek target, and a
healthy client lands exactly on it — so `delayTicks ≈ -latency`, **always slightly
negative**. The guard has no tolerance band, while both sibling position tests in the
same method use `maxPlaybackOffsetTicks` (500 ms,
[Group.cs:103](Emby.Server.Implementations/SyncPlay/Group.cs#L103)):
[line 463](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L463) and
[line 582](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L582).

The consequence compounds: [line 493](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L493)
marks the session ready, then [line 507](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L507)
puts it **back** into the buffering set to seek somewhere it already is. An in-sync
client re-enters the wait it had just cleared, and burns a correction attempt doing it.

**Live evidence.** The check fires on essentially every waiting cycle, at 20–330 ms —
an order of magnitude inside the tolerance every other comparison respects:

```text
2026-08-28 03:23:44.822  bb45e7b8… is ahead of group "7db1ff68-…" by 0.2606669 seconds, correcting.
2026-08-28 03:31:29.773  0602754e… is ahead of group "7db1ff68-…" by 0.0371733 seconds, correcting.
2026-08-29 06:16:11.769  5c88b88f… is ahead of group "ecb1c043-…" by 0.029884  seconds, correcting.
```

**Reproduce (definitive):** two clients in a group, playing. Scrub forward from either
one. Every participant that reports `Ready` promptly logs `is ahead of group … by
0.0x seconds, correcting` — five times each, then the give-up branch. Confirm the value
tracks round-trip latency rather than any real desync by comparing it against the
session's `Ping`.

Suggested fix — give the check the same tolerance as its siblings, and clamp the
fall-through so the relaxed band cannot re-introduce #16's past-dated Pause:

```csharp
if (delayTicks < -maxPlaybackOffsetTicks)
{
    // ... existing correction / give-up block, unchanged ...
}

// Others are still buffering, tell this client to pause when ready.
var command = context.NewSyncPlayCommand(SendCommandType.Pause);
command.When = currentTime.AddTicks(Math.Max(delayTicks, 0));
```

The `Math.Max` is what #16 rejected as a *standalone* fix, and that rejection still
holds — but as the floor under a 500 ms tolerance band it only ever absorbs sub-tolerance
jitter, which is exactly what it should do.

**20. Correction budget never resets on the `ResumePlaying` path — regression from #17 — ✅ FIXED**

> **Status:** Fixed 2026-08-29, as suggested. The `ResumePlaying == true` branch now resets `_correctionAttempts` wherever the session is measured at the group position — `Math.Abs(delayTicks) <= maxPlaybackOffsetTicks` — checked right after the lost-in-time branch, so a given-up session that is still out of tolerance never resets. Both branches now follow the same rule and, with #19 applied, share the one tolerance constant. Unit test: `HandleRequest_Ready_ConvergedSessionWhileResuming_ResetsCorrectionCounter` (exhausts the budget, converges, drifts again, and expects a fresh budget of 5 seeks). All 16 SyncPlay tests pass.

`_correctionAttempts.Remove(session.Id)` appears once, in the `ResumePlaying == false`
branch ([WaitingGroupState.cs:614](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L614)).
The `ResumePlaying == true` branch — the one a seek-while-playing takes — has no reset at
all, so its counter only ever increments. Once a session passes `MaxCorrectionAttempts`
the give-up branch re-fires on **every** subsequent `Ready`, sending another Pause each
time to a client that is in fact in sync:

```text
2026-08-28 03:31:29.862  0602754e… is still ahead … after 6 corrections; pausing it in place.
2026-08-28 03:31:29.864  0602754e… is still ahead … after 7 corrections; pausing it in place.
2026-08-28 03:31:29.937  0602754e… is still ahead … after 8 corrections; pausing it in place.
2026-08-28 03:31:29.941  0602754e… is still ahead … after 9 … 10 … 11 corrections; pausing it in place.
```

This is #17's own caveat — "the reset must sit outside the `if`, or a session that
converges once and then drifts again carries a stale count into its next correction" —
applied to one branch and missed on the other.

**Reproduce (definitive):** unit test. Drive `HandleRequest(ReadyGroupRequest)` with
`ResumePlaying = true` and a session that converges to the group position, then assert
`_correctionAttempts` no longer holds an entry for it. Currently it does.

Suggested fix: reset the counter on the `ResumePlaying` path wherever the session is
measured at the group position — `Math.Abs(delayTicks) <= maxPlaybackOffsetTicks` — so
both branches follow the same rule. With #19 applied, that is the same predicate the
corrected guard uses, so the two changes share one tolerance constant.

**21. Unbounded `LastActivity` push schedules the group's resume arbitrarily far ahead**

[WaitingGroupState.cs:544-557](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L544-L557)

```csharp
if (delayTicks > context.GetHighestPing() * 2 * TimeSpan.TicksPerMillisecond)
{
    context.LastActivity = currentTime.AddTicks(delayTicks);
    var command = context.NewSyncPlayCommand(SendCommandType.Unpause);
    ...
}
```

When the last buffering session reports `Ready` while *behind* the group — a positive
`delayTicks`, which is what a forward scrub produces on a client that has not finished
seeking, or on one that just rejoined — the server pushes the group's `LastActivity` by
that full amount. `NewSyncPlayCommand` publishes `LastActivity` as the command's `When`
([Group.cs:417-426](Emby.Server.Implementations/SyncPlay/Group.cs#L417-L426)), so every
other client is told to resume at that time. Nothing clamps it.

**Live evidence**, with arithmetic that checks out:

```text
07:37:56.406  Playback stopped … "Goodbye Earl". Stopped at "2081955" ms   <- mellyK leaves at 34.7 min
07:38:22.874  Session "43e35c18…" joined group "ecb1c043-…"                <- rejoins at position 0
07:38:23.034  Session "43e35c18…" is recovering, group "ecb1c043-…"
              will resume in 2106.9428685 seconds.
07:38:23.034  Group "ecb1c043-…" switching from Waiting to Playing.
```

bigr4232 had kept playing from 2082 s, so 27 s later the group sat at ≈2107 s — exactly
the `delayTicks` reported. The group's resume was scheduled **35 minutes in the future**
while its state read `Playing`. Nothing plays, and no state transition ever corrects it;
the only escape is a fresh Pause/Unpause.

The intent — let a lagging client catch up by playing, and start everyone else when it
arrives — is only coherent for sub-second lag. Past that the client is not lagging, it
is in the wrong place, and the existing correction machinery already knows what to do
about that.

**Reproduce (definitive):** two clients in a group, playing. Stop playback on client B,
let the group run on for a minute, then rejoin B. B reports `Ready` at position 0 as the
last buffering session. Server logs `is recovering, group … will resume in {minutes}
seconds`; client A freezes with the group nominally in `Playing`.

Suggested fix — bound the wait, and correct the session instead of stalling the group
past it:

```csharp
var resumeDelayBound = TimeSpan.FromMilliseconds(context.MaxPlaybackOffset).Ticks;
if (delayTicks > resumeDelayBound)
{
    // The session is not lagging, it is in the wrong place. Correct it under the
    // shared correction budget rather than making everyone else wait it out.
    context.SetBuffering(session, true);
    var seek = context.NewSyncPlayCommand(SendCommandType.Seek);
    context.SendCommand(session, SyncPlayBroadcastType.CurrentSession, seek, cancellationToken);
    SendGroupStateUpdate(context, request, session, cancellationToken);
    return;
}
```

Route it through `RegisterCorrectionAttempt` the way #16 and #17 do, so a session that
cannot converge is left behind rather than looping — and so this path cannot become a
new unbounded storm.

**Fix this together with finding #2.** #2 is the unit mismatch in the `else` half of
this same `if` ([line 563](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L563),
`Math.Max(delayTicks, context.DefaultPing)` comparing ticks to milliseconds). The two
findings are the two halves of one branch; fixing either alone leaves the resume-delay
computation half-wrong.

**22. No timeout on `Waiting` — a silent participant holds the group indefinitely**

Nothing re-requests `Ready` and nothing gives up on the group as a whole, so a client
that simply never answers strands everyone. `Unpause` is the only exit
([WaitingGroupState.cs:239-252](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L239-L252)),
and it requires a human to press it.

**Live evidence.** The 2026-08-29 reproduction itself — **zero** `Ready` events in the
33 s after the seek, ended only by the user forcing his way out:

```text
07:38:37.981  Session "5c88b88f…" requested Seek in group "ecb1c043-…" that is Playing.
07:38:37.981  Group "ecb1c043-…" switching from Playing to Waiting.
              (33 seconds — no Ready events from any participant)
07:39:10.908  Session "5c88b88f…" requested Seek in group … that is Waiting.    <- user retries
07:39:12.580  Session "5c88b88f…" requested Unpause in group … that is Waiting. <- manual escape
07:39:14.898  Session "5c88b88f…" requested Pause …
07:39:16.554  Session "5c88b88f…" requested Unpause …                           <- the workaround
07:39:17.575  Ready ×3
```

The same shape appears at 06:16:10–06:16:21, where 43e35c18 took a corrective Seek and
never reported again; the group sat in `Waiting` for 11 s until a manual Unpause.

**Deliberately scoped out of the immediate fix.** #19–#21 explain every *desync* the
logs show, and they are cheap, local changes. A `Waiting` timeout is the safety net for
a client that goes silent for unrelated reasons (a stalled transcode, a wedged player) —
worth having, but it needs a per-group timer in `Group`/`SyncPlayManager` with real
lifecycle concerns: it must take the group lock the state machine already relies on
([SyncPlayManager.cs:376](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L376)),
be cancelled on every state transition, and be disposed with the group. Track it
separately.

**Reproduce (definitive):** two clients in a group, playing. Kill client B's player
process (leaving the WebSocket up, so #15's grace period does not apply) and then seek
from client A. The group enters `Waiting` and never leaves it.

---

### MEDIUM — Auth and protection

**6. `Ping` endpoint missing `[Authorize]`**

[Jellyfin.Api/Controllers/SyncPlayController.cs:430-439](Jellyfin.Api/Controllers/SyncPlayController.cs#L430-L439)

Every other SyncPlay endpoint has `[Authorize(Policy = Policies.SyncPlayXxx)]`. `Ping` does not. The controller still pulls the session via `RequestHelpers.GetSession`, so an *unauthenticated* request fails — but a logged-in user with `SyncPlayAccess.None` can still POST pings. `HandleRequest` will route to the not-in-group path and emit a `NotInGroupUpdate`. Low severity but inconsistent. Add `[Authorize(Policy = Policies.SyncPlayIsInGroup)]`.

**Reproduce (definitive):**
1. In Jellyfin admin, set a test user's "SyncPlay access" to `None`.
2. Log in as that user, obtain auth token.
3. `curl -X POST https://server/SyncPlay/Ping -H 'Authorization: ...' -d '{"Ping": 100}' -H 'Content-Type: application/json'`
4. Observe response: `204 No Content` (request accepted).
5. Repeat against `POST /SyncPlay/Pause` — observe `403 Forbidden`. The asymmetry confirms Ping is unprotected by policy.

**18. `SyncPlayAccessHandler` throws on empty Guid instead of returning 403 — ✅ FIXED**

> **Status:** Fixed, as suggested. `HandleRequirementAsync` guards `userId.IsEmpty()` before the lookup and returns without `context.Succeed`, so the policy fails as a 403 instead of letting `ArgumentException: Guid can't be empty` escape as a 500. The `ResourceNotFoundException` throw for a non-empty id that resolves to no user is untouched. Finding #6 (`Ping` missing `[Authorize]`) is still open on this same layer.
>
> This brings the handler in line with its siblings, which were already guarded: [UserPermissionHandler.cs:38](Jellyfin.Api/Auth/UserPermissionPolicy/UserPermissionHandler.cs#L38) (`if (!userId.IsEmpty())`) and [DefaultAuthorizationHandler.cs:46](Jellyfin.Api/Auth/DefaultAuthorizationPolicy/DefaultAuthorizationHandler.cs#L46) (`if (!isApiKey && userId.IsEmpty())`). `SyncPlayAccessHandler` was the only policy handler missing the guard.
>
> One deliberate divergence: both siblings detect API keys with `GetIsApiKey()` and *succeed* the requirement ("api keys are unrestricted"); this one fails closed instead. That is right for SyncPlay, where groups are built from sessions with users — every endpoint then calls `RequestHelpers.GetSession`, which for a key-only request reaches `LogSessionActivity` with a null user and throws `ResourceNotFoundException("Session not found")`. Succeeding the policy would trade the 500 for a downstream 404; 403 at the policy layer is the honest answer.
>
> Unit tests: `tests/Jellyfin.Api.Tests/Auth/SyncPlayAccessPolicy/SyncPlayAccessHandlerTests.cs` — 16 cases through a real `IAuthorizationService` with all four policies registered: the no-user-context case on each, plus the access matrix per policy. All passing 2026-08-22 (95/95 in `Jellyfin.Api.Tests`). Verified to bite: removing the guard turns all four `ShouldFailWithoutUserContext` cases red. Note they go red on AutoFixture's auto-mock (`Can not instantiate proxy of class: User`) rather than on production's `ArgumentException`, so the guard is protected but the real failure mode is not modelled; `_userManagerMock.Setup(u => u.GetUserById(Guid.Empty)).Throws(...)` would pin it.

[SyncPlayAccessHandler.cs:36-41](Jellyfin.Api/Auth/SyncPlayAccessPolicy/SyncPlayAccessHandler.cs#L36-L41)

```csharp
var userId = context.User.GetUserId();
var user = _userManager.GetUserById(userId);
if (user is null)
{
    throw new ResourceNotFoundException();
}
```

The lookup is unguarded. Under API-key authentication there is no user context, so
`GetUserId()` returns `Guid.Empty` and `UserManager.GetUserById` throws
`ArgumentException: Guid can't be empty` before the null check is ever reached. The
policy layer therefore surfaces a **500** where it should produce a 403.

**Live evidence.** `GET /SyncPlay/List` with an API key, 05:24:12:

```text
[ERR] Jellyfin.Api.Middleware.ExceptionMiddleware: Error processing request.
      URL "GET" "/SyncPlay/List".
System.ArgumentException: Guid can't be empty (Parameter 'id')
   at Jellyfin.Server.Implementations.Users.UserManager.GetUserById(Guid id)
   at Jellyfin.Api.Auth.SyncPlayAccessPolicy.SyncPlayAccessHandler.HandleRequirementAsync(...)
```

**Reproduce (definitive):**
1. Create an API key in the Jellyfin dashboard.
2. `curl -H 'Authorization: MediaBrowser Token="<api-key>"' https://server/SyncPlay/List`
3. Observe `500`, and the `ArgumentException` stack above in the server log. Any
   SyncPlay endpoint behind one of the four policies reproduces it.

Suggested fix — guard before the lookup, at the top of `HandleRequirementAsync`:

```csharp
var userId = context.User.GetUserId();
if (userId.IsEmpty())
{
    // No user context (e.g. API-key auth). Fail closed as a 403 rather than
    // letting GetUserById throw and surface as a 500.
    return Task.CompletedTask;
}

var user = _userManager.GetUserById(userId);
if (user is null)
{
    throw new ResourceNotFoundException();
}
```

Returning without calling `context.Succeed` is what produces the 403. The existing
`ResourceNotFoundException` throw is left alone — a non-empty id that resolves to no
user does represent a real inconsistency, unlike the API-key case.

**7. No re-authorization on the WebSocket broadcast path**

`SessionManager.SendSyncPlayGroupUpdate` (lines 1387-1391) trusts the in-memory session-to-group map. If a queue is replaced after a join, the existing members can receive PlayQueue updates referencing items they no longer have access to. Library access is checked only at the `JoinGroup` boundary ([SyncPlayManager.cs:171](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L171)) and in `AllUsersHaveAccessToQueue` for *new* queue items, never on subsequent broadcasts.

**Reproduce (multi-step, requires admin):**
1. User U1 has access to library L1; user U2 has access to L1 and L2.
2. Both join a SyncPlay group with a queue containing items from L1.
3. Admin (separate session) revokes U1's access to L1.
4. From U2's session, send `Pause` / `Unpause` / `Seek`. U1's session continues to receive `SyncPlayCommand` messages referencing L1 items via the WebSocket. There is no re-check.
5. Severity is bounded — U1 already had access at join time, and revocation while connected is a soft case — but it is observable via WebSocket inspector.

**8. No rate limiting / debounce on broadcast fan-out**

`SendGroupUpdate` immediately fires `Task.WhenAll` over all recipients. Nothing protects against a chatty client (e.g., a Ping every 50 ms) inducing fan-out storms across the group. With N participants and rate R, the server allocates and tracks N×R outbound message tasks per second. Add a per-session ping floor (server-side rate-limit) and consider coalescing.

**Reproduce (definitive):**
A 5-line script floods Ping:
```bash
while true; do
  curl -s -X POST https://server/SyncPlay/Ping \
    -H 'Authorization: ...' -H 'Content-Type: application/json' \
    -d '{"Ping": 100}' &
done
```
Observe server CPU and the `_logger` debug stream — every ping flows through `HandleRequest`, takes the group lock, and updates the participant's ping. There is no 429, no minimum interval, no client throttling. Verifies the absence of any rate limit; not by itself a bug, but a hardening gap given finding #6.

---

### MEDIUM — Robustness

**9. Null-deref risks on missing items**

[Group.cs:208](Emby.Server.Implementations/SyncPlay/Group.cs#L208), [Group.cs:506-507](Emby.Server.Implementations/SyncPlay/Group.cs#L506-L507), [Group.cs:521](Emby.Server.Implementations/SyncPlay/Group.cs#L521): `_libraryManager.GetItemById(itemId)` returns `null` for missing items, then the code dereferences `item.IsVisibleStandalone(user)` / `item.RunTimeTicks`. A user deleting a queued item while a group is alive would crash the request. Add null guards.

**Reproduce (definitive):**
1. Create a group, set queue to a single item with id `X`.
2. From admin/another session, delete item `X` from the library (or unmount the storage).
3. Have a second user attempt to join the group → `JoinGroup` calls `HasAccessToPlayQueue` → `_libraryManager.GetItemById(X)` returns `null` → `item.IsVisibleStandalone(...)` throws NRE. Server log shows the exception; the join HTTP request returns 500.

**10. Distinct() on participant list collapses multi-device users**

[Group.cs:357](Emby.Server.Implementations/SyncPlay/Group.cs#L357): `_participants.Values.Select(s => s.UserName).Distinct().ToList()`. If the same user joins from two devices, only one shows up in `GroupInfoDto.Participants`. Browsers that render this list will silently misreport occupancy.

**Reproduce (definitive):**
1. Log in as user U on a browser session B1, create a SyncPlay group.
2. Log in as user U on a second browser session B2 (private window with a fresh token).
3. From B2, `POST /SyncPlay/JoinGroup` with the group id.
4. Either session calls `GET /SyncPlay/List` — response shows `Participants: ["U"]` (one entry) despite two sessions in the group. Inspect `_participants.Count` via debugger to confirm there are two entries internally.

**11. `GetHighestPing()` returns `long.MinValue` for empty group**

[Group.cs:445-454](Emby.Server.Implementations/SyncPlay/Group.cs#L445-L454): seeded with `long.MinValue` instead of `DefaultPing`. If ever called on an empty group (unlikely on the live path but possible during teardown races), `* 2 * TicksPerMillisecond` overflows. Seed with `DefaultPing`.

**Reproduce — theoretical only:**
On the live code paths, `GetHighestPing()` is only invoked from inside state handlers, which are only reached while `_participants` is non-empty (the manager drops requests on empty groups at [SyncPlayManager.cs:343-346](Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs#L343-L346)). The bug is reachable only via direct unit test of `Group.GetHighestPing()` on a freshly constructed Group: assert that result is sensible — currently `long.MinValue`. No known live trigger, so this is a defensive hardening item, not a confirmed live bug.

**12. `LastActivity` published as `DateTime` in `SendCommand.When`**

[Group.cs:417-426](Emby.Server.Implementations/SyncPlay/Group.cs#L417-L426): commands carry an absolute UTC `DateTime` for "fire at." This is correct, but every published command is subject to issue #1 above. A millisecond-precision wire format is more than sufficient since network jitter dominates sub-ms precision.

---

### LOW — Performance

**13. N library lookups per queue-access check**

[Group.cs:198-216](Emby.Server.Implementations/SyncPlay/Group.cs#L198-L216) and [Group.cs:218-236](Emby.Server.Implementations/SyncPlay/Group.cs#L218-L236): `HasAccessToQueue` does N×M `GetItemById` calls for N items × M users. Group of 5 watching a 200-item playlist costs 1000 lookups on every queue change. Cache the item visibility per user, or batch-load.

**Reproduce — measure (definitive):**
Add a trace in `_libraryManager.GetItemById` (or use a profiler). Call `POST /SyncPlay/SetNewQueue` with a 200-item playlist body in a 5-member group. Count invocations: 1000.

**14. `PlayQueue.GetPlaylist()` materialized inside `HasAccessToPlayQueue`**

[Group.cs:366-370](Emby.Server.Implementations/SyncPlay/Group.cs#L366-L370): each call materializes the whole list. Called from `JoinGroup`, `ListGroups`, `GetGroup`. Cheap but unnecessary.

---

## Fix being applied (iPad blocker)

Replace [src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs:21-32](src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs#L21-L32) with a single-format writer that emits exactly 3 fractional-second digits:

```csharp
public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
{
    // Emit ISO 8601 with exactly 3 fractional-second digits.
    // The .NET round-trip format ("O") emits up to 7 digits, which Swift's
    // JSONDecoder iso8601 + withFractionalSeconds strategy rejects (it requires 0 or 3).
    // Three digits is sufficient precision for any consumer (browsers parse millisecond
    // precision via Date) and breaks no JSON parser that handles ISO 8601.
    writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture));
}
```

`K` emits `Z` for UTC, the offset for Local, nothing for Unspecified — the same convention the .NET round-trip writer uses, just with the precision normalized.

### Files to modify
- [src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs](src/Jellyfin.Extensions/Json/Converters/JsonDateTimeConverter.cs) — rewrite the `Write` method.

### Tests to check / update
Search for tests that pin the wire format to 7 digits before applying:
- `tests/Jellyfin.Extensions.Tests/Json/JsonDateTimeConverterTests.cs` (if present).
- Any DTO snapshot tests under `tests/Jellyfin.Server.Implementations.Tests/SyncPlay/`.

If there are tests that assert the literal 7-digit format, update them to expect 3-digit precision (the new contract).

### Verification

End-to-end:
1. Build server: `dotnet build`.
2. Run unit tests: `dotnet test src/Jellyfin.Extensions.Tests` (and any SyncPlay test project).
3. Wire-format check: hit `GET /SyncPlay/GetUtcTime` against a running server and confirm both `RequestReceptionTime` and `ResponseTransmissionTime` look like `2026-04-26T14:30:45.123Z` (exactly 3 fractional digits, `Z` suffix).
4. iPad smoke test: with the updated server, join an existing SyncPlay group from the native iOS app on iPad. Expected: `UtcTimeResponse` decodes successfully (no longer falls into the iOS `JSONDecoder` failure path), commands fire at correct timestamps, group sync works.
5. Browser regression: open the web client in two browsers, create a group, join from the second, navigate Next Episode, pause/seek. Expect identical behavior to before — no parse breakage from shorter precision.

### Risk assessment
- Wire format becomes shorter, not longer; every well-formed ISO 8601 parser handles variable fractional precision.
- DateTimeKind handling is preserved (Z / offset / nothing).
- Sub-millisecond precision is lost from any timestamp that previously had it; SyncPlay does not need it (network jitter ≫ 1 ms), and other server features (logging, library activity timestamps) do not rely on sub-ms precision either.

---

## Out of scope for this fix (recommended follow-ups)

In rough priority:

1. **Repair the seek-while-playing path in `WaitingGroupState` (findings #19, #20, #21 + #2).**
   This is the active user-facing bug — scrubbing ahead does not re-sync the group, and
   pause/play is the only recovery. ~~#19 and #20 are regressions the #16/#17 fixes left
   behind;~~ #19 and #20 are done — see their findings. #21 and #2 are the two halves of
   the resume-delay branch and must land together. All four are local changes inside one
   method
   ([WaitingGroupState.cs:459-578](MediaBrowser.Controller/SyncPlay/GroupStates/WaitingGroupState.cs#L459-L578))
   and share one tolerance constant, so they are best done as a single pass with tests
   added to the existing `WaitingGroupStateTests.cs`.
2. ~~**Add a disconnect grace period before SyncPlay eviction (finding #15).**~~ Done — see finding #15.
3. ~~**Guard the negative `delayTicks` path and cap the correction loop (findings #16 and #17).** These must land together; see the note at the end of #16.~~ Done, but each left a regression — see #19 and #20.
4. Investigate the [#139](https://github.com/jellyfin/jellyfin-desktop/issues/139) symptom with the `IgnoreGroupWait` interaction (finding #5).
5. Add `[Authorize]` to the `Ping` endpoint (finding #6). ~~Guard the empty-Guid throw (finding #18).~~ Done — see finding #18. Both are the same policy layer, so #6 is still worth one pass.
6. Add a `Waiting`-state timeout so a silent participant cannot strand the group (finding #22). Deferred from item 1 — it needs a per-group timer with real lifecycle concerns, and #19–#21 cover every desync observed so far.
7. Refactor `Group.SendGroupUpdate` / `SendCommand` to await tasks under serialized access (finding #3 — biggest correctness lift but largest scope).
8. Raise the 2-second time-sync threshold or make it adaptive (finding #4).
9. Add null-guards on `_libraryManager.GetItemById` results (finding #9).
