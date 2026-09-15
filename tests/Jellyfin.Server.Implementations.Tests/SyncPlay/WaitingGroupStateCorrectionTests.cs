using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.GroupStates;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Queue;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using SyncPlayGroup = Emby.Server.Implementations.SyncPlay.Group;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay;

/// <summary>
/// Covers the correction budget and the seek deadline that the fork carries on top of
/// upstream's <see cref="WaitingGroupState"/>.
/// </summary>
public class WaitingGroupStateCorrectionTests : IDisposable
{
    private static readonly Guid ItemId = Guid.NewGuid();

    private readonly WaitingGroupState _state;
    private readonly FakeGroupStateContext _context;
    private readonly SessionInfo _session;
    private readonly SessionInfo _otherSession;

    public WaitingGroupStateCorrectionTests()
    {
        _state = new WaitingGroupState(NullLoggerFactory.Instance);
        _context = new FakeGroupStateContext(ItemId);

        var sessionManager = Mock.Of<ISessionManager>();

        _session = new SessionInfo(sessionManager, Mock.Of<ILogger>())
        {
            Id = "session1",
            UserId = Guid.NewGuid(),
            UserName = "user1"
        };

        _otherSession = new SessionInfo(sessionManager, Mock.Of<ILogger>())
        {
            Id = "session2",
            UserId = Guid.NewGuid(),
            UserName = "user2"
        };

        // Another member is still buffering, so the group stays in the waiting state.
        _context.SetBuffering(_otherSession, true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void HandleRequest_Ready_AheadOfGroup_SendsSeekCorrection()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = 0;

        HandleReady(ReadyRequest(TimeSpan.FromSeconds(100).Ticks, isPlaying: true));

        var command = Assert.Single(_context.Commands);
        Assert.Equal(SendCommandType.Seek, command.Command);
        Assert.True(_context.IsBuffering(_session.Id));
    }

    [Fact]
    public void HandleRequest_Ready_AheadOfGroupWithinCatchUpAllowance_SendsSeekCorrection()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = 0;

        // Catching up after buffering explains lagging behind the group, never running ahead of
        // it, so the tight tolerance applies even without a position jump.
        HandleReady(ReadyRequest(TimeSpan.FromSeconds(10).Ticks, isPlaying: true));

        var command = Assert.Single(_context.Commands);
        Assert.Equal(SendCommandType.Seek, command.Command);
        Assert.True(_context.IsBuffering(_session.Id));
    }

    [Fact]
    public void HandleRequest_Ready_SlightlyAheadWithinTolerance_SchedulesPauseNow()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = 0;

        var before = DateTime.UtcNow;
        HandleReady(ReadyRequest(TimeSpan.FromMilliseconds(200).Ticks, isPlaying: true));
        var after = DateTime.UtcNow;

        // A sub-tolerance lead is the one-way latency of the Ready report, not a real desync:
        // the session is not corrected, and its pause is never dated in the past.
        var command = Assert.Single(_context.Commands);
        Assert.Equal(SendCommandType.Pause, command.Command);
        Assert.InRange(command.When, before, after);
        Assert.False(_context.IsBuffering(_session.Id));
    }

    [Fact]
    public void HandleRequest_Ready_AheadOfGroup_StopsCorrectingAfterMaxAttempts()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = 0;

        var before = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(100).Ticks, isPlaying: true));
        }

        // At most 5 corrective seeks. The session is then paused where it is, so that it
        // stops drifting further ahead, and left behind so the group can proceed.
        Assert.Equal(5, _context.Commands.Count(c => c.Command == SendCommandType.Seek));
        Assert.False(_context.IsBuffering(_session.Id));

        // Never a command dated in the past, which is what the client fires immediately.
        var pause = Assert.Single(_context.Commands, c => c.Command == SendCommandType.Pause);
        Assert.True(pause.When >= before);
    }

    [Fact]
    public void HandleRequest_Ready_LostInTime_StopsCorrectingAfterMaxAttempts()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = 0;

        // A paused client that reports a position it cannot correct out of.
        for (var i = 0; i < 6; i++)
        {
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(60).Ticks, isPlaying: false));
        }

        Assert.Equal(5, _context.Commands.Count(c => c.Command == SendCommandType.Seek));
        Assert.False(_context.IsBuffering(_session.Id));
    }

    [Fact]
    public void HandleRequest_Ready_BehindGroupBeyondCatchUpAllowance_SendsSeekCorrection()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = TimeSpan.FromSeconds(100).Ticks;

        // Nobody else is buffering, so this session is the last one the group waits on.
        _context.SetBuffering(_otherSession, false);

        HandleReady(ReadyRequest(0, isPlaying: true));

        // A session this far behind is in the wrong place, not catching up: it is corrected and
        // the group stays in the waiting state for it.
        var command = Assert.Single(_context.Commands);
        Assert.Equal(SendCommandType.Seek, command.Command);
        Assert.True(_context.IsBuffering(_session.Id));
    }

    [Fact]
    public void HandleRequest_Ready_BehindGroupBeyondCatchUpAllowance_StopsCorrectingAfterMaxAttempts()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = TimeSpan.FromSeconds(100).Ticks;

        _context.SetBuffering(_otherSession, false);

        var before = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            HandleReady(ReadyRequest(0, isPlaying: true));
        }

        // At most 5 corrective seeks. The session is then left behind and the group resumes
        // with the standard recovery delay instead of waiting the full gap for it to arrive.
        Assert.Equal(5, _context.Commands.Count(c => c.Command == SendCommandType.Seek));

        var unpause = Assert.Single(_context.Commands, c => c.Command == SendCommandType.Unpause);
        Assert.True(unpause.When < before.AddSeconds(5));
        Assert.IsType<PlayingGroupState>(_context.State);
    }

    [Fact]
    public void HandleRequest_Ready_BehindGroupWhileOthersBuffering_StopsCorrectingAfterMaxAttempts()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = TimeSpan.FromSeconds(100).Ticks;

        var before = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            HandleReady(ReadyRequest(0, isPlaying: true));
        }

        var after = DateTime.UtcNow;

        Assert.Equal(5, _context.Commands.Count(c => c.Command == SendCommandType.Seek));

        // The give-up pauses it in place rather than minutes out.
        var pause = Assert.Single(_context.Commands, c => c.Command == SendCommandType.Pause);
        Assert.InRange(pause.When, before, after);
    }

    [Fact]
    public void HandleRequest_Ready_OutOfTolerance_StopsCorrectingAfterMaxAttempts()
    {
        _state.ResumePlaying = false;
        _context.PositionTicks = 0;

        for (var i = 0; i < 6; i++)
        {
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(60).Ticks, isPlaying: false));
        }

        Assert.Equal(5, _context.Commands.Count(c => c.Command == SendCommandType.Seek));
        Assert.False(_context.IsBuffering(_session.Id));
    }

    [Fact]
    public void HandleRequest_Ready_OutOfTolerance_GivingUpReleasesGroup()
    {
        _state.ResumePlaying = false;
        _context.PositionTicks = 0;

        // This session is the only one the group is still waiting on.
        _context.SetBuffering(_otherSession, false);

        for (var i = 0; i < 6; i++)
        {
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(60).Ticks, isPlaying: false));
        }

        Assert.Equal(5, _context.Commands.Count(c => c.Command == SendCommandType.Seek));
        Assert.False(_context.IsBuffering(_session.Id));

        // Giving up on a session must not strand the group in the waiting state.
        Assert.IsType<PausedGroupState>(_context.State);
    }

    [Fact]
    public void HandleRequest_Ready_ConvergedSession_ResetsCorrectionCounter()
    {
        _state.ResumePlaying = false;
        _context.PositionTicks = 0;

        // Exhaust the correction budget.
        for (var i = 0; i < 5; i++)
        {
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(60).Ticks, isPlaying: false));
        }

        // The session reaches the group position.
        HandleReady(ReadyRequest(0, isPlaying: false));

        // It drifts again: a full new budget of corrections is available.
        for (var i = 0; i < 5; i++)
        {
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(60).Ticks, isPlaying: false));
        }

        Assert.Equal(10, _context.Commands.Count(c => c.Command == SendCommandType.Seek));
        Assert.True(_context.IsBuffering(_session.Id));
    }

    [Fact]
    public void HandleRequest_Ready_ConvergedSessionWhileResuming_ResetsCorrectionCounter()
    {
        _state.ResumePlaying = true;
        _context.PositionTicks = 0;

        // Exhaust the correction budget.
        for (var i = 0; i < 5; i++)
        {
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(100).Ticks, isPlaying: true));
        }

        // The session reaches the group position.
        HandleReady(ReadyRequest(0, isPlaying: true));

        // It drifts again: a full new budget of corrections is available, instead of the
        // give-up branch re-firing on every Ready.
        for (var i = 0; i < 5; i++)
        {
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(100).Ticks, isPlaying: true));
        }

        Assert.Equal(10, _context.Commands.Count(c => c.Command == SendCommandType.Seek));
        Assert.True(_context.IsBuffering(_session.Id));
    }

    [Fact]
    public void HandleRequest_Seek_LimitsGroupWaitToTheSeekDeadline()
    {
        // A seek leaves the item loaded, so a client that is going to answer answers at once.
        // The stock web client answers not at all when the seek lands inside the buffered range.
        // The deadline still has to clear the web client's 3 s buffering debounce — see #40.
        HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Playing);

        Assert.Equal(4000, _context.GroupWaitLimit);
    }

    [Fact]
    public void HandleRequest_Seek_AfterASessionBuffered_RestoresTheFullGroupWait()
    {
        HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Playing);

        // A session that reports buffering is genuinely working through the seek, so the group
        // goes back to giving it the full allowance.
        HandleBuffer(BufferRequest(_context.PositionTicks, isPlaying: true));

        Assert.Equal(1, _context.GroupWaitRestores);
    }

    [Fact]
    public void HandleRequest_Seek_AfterAPeerReportedReady_KeepsTheSeekDeadline()
    {
        HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Playing);

        // A Ready report says that session is finished, not that it is still working. In a mixed
        // group the well-behaved clients answer within milliseconds while the silent one never
        // will, so their answers must not extend the deadline. See finding #33.
        HandleReady(ReadyRequest(_context.PositionTicks, isPlaying: true));

        Assert.Equal(0, _context.GroupWaitRestores);
    }

    [Fact]
    public void HandleRequest_Seek_WhileAlreadyBuffering_KeepsTheFullGroupWait()
    {
        HandleBuffer(BufferRequest(_context.PositionTicks, isPlaying: true));

        // The group is waiting on a real buffering stall, which the short deadline would cut off.
        HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Waiting);

        Assert.Null(_context.GroupWaitLimit);
    }

    [Fact]
    public void SessionJoined_KeepsTheFullGroupWait()
    {
        // Loading an item legitimately takes seconds, so it keeps the full allowance.
        _state.SessionJoined(_context, GroupStateType.Playing, _session, CancellationToken.None);

        Assert.Null(_context.GroupWaitLimit);
    }

    [Fact]
    public void Group_SeekWhilePlaying_BringsTheDeadlineForwardUntilSomeoneBuffers()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.LastActivity = DateTime.UtcNow;
        group.SetState(new PlayingGroupState(NullLoggerFactory.Instance));

        group.HandleRequest(harness.First, new SeekGroupRequest(TimeSpan.FromMinutes(1).Ticks), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
        Assert.NotNull(group.GroupWaitDeadline);
        Assert.InRange(group.GroupWaitDeadline!.Value - Environment.TickCount64, 0, 4000);

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, TimeSpan.FromMinutes(1).Ticks, false, harness.PlaylistItemId),
            CancellationToken.None);

        Assert.NotNull(group.GroupWaitDeadline);
        Assert.True(group.GroupWaitDeadline!.Value - Environment.TickCount64 > 20000);
    }

    [Fact]
    public async Task Group_SeekNobodyAnswers_ResumesAfterTheSeekDeadline()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.LastActivity = DateTime.UtcNow;
        group.SetState(new PlayingGroupState(NullLoggerFactory.Instance));

        group.HandleRequest(harness.First, new SeekGroupRequest(TimeSpan.FromMinutes(1).Ticks), CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // Nobody reports anything, as the web client does after a seek inside its buffer. The
        // group recovers on the seek deadline, well before the 30 s group-wait timeout.
        var until = DateTime.UtcNow.AddSeconds(8);
        while (group.GetInfo().State != GroupStateType.Playing && DateTime.UtcNow < until)
        {
            harness.PumpGroupWaitTimeout();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _otherSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private ReadyGroupRequest ReadyRequest(long positionTicks, bool isPlaying)
    {
        return new ReadyGroupRequest(DateTime.UtcNow, positionTicks, isPlaying, _context.PlaylistItemId);
    }

    private BufferGroupRequest BufferRequest(long positionTicks, bool isPlaying)
    {
        return new BufferGroupRequest(DateTime.UtcNow, positionTicks, isPlaying, _context.PlaylistItemId);
    }

    private void HandleReady(ReadyGroupRequest request)
    {
        _state.HandleRequest(request, _context, GroupStateType.Waiting, _session, CancellationToken.None);
    }

    private void HandleBuffer(BufferGroupRequest request)
    {
        _state.HandleRequest(request, _context, GroupStateType.Waiting, _session, CancellationToken.None);
    }

    private void HandleSeek(long positionTicks, GroupStateType prevState)
    {
        _state.HandleRequest(new SeekGroupRequest(positionTicks), _context, prevState, _session, CancellationToken.None);
    }

    private sealed class GroupHarness
    {
        public GroupHarness()
        {
            var userManager = new Mock<IUserManager>();
            var sessionManager = new Mock<ISessionManager>();
            var libraryManager = new Mock<ILibraryManager>();

            var user = new User("tester", "auth-provider", "pwdreset-provider");
            userManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns(user);

            var item = new Mock<BaseItem>();
            item.Setup(i => i.IsVisibleStandalone(It.IsAny<User>())).Returns(true);
            item.Object.RunTimeTicks = TimeSpan.FromHours(2).Ticks;
            libraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns(item.Object);

            sessionManager
                .Setup(m => m.SendSyncPlayCommand(It.IsAny<string>(), It.IsAny<SendCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            sessionManager
                .Setup(m => m.SendSyncPlayGroupUpdate(It.IsAny<string>(), It.IsAny<GroupUpdate<GroupStateUpdate>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Group = new SyncPlayGroup(
                NullLoggerFactory.Instance,
                userManager.Object,
                sessionManager.Object,
                libraryManager.Object);

            First = new SessionInfo(sessionManager.Object, NullLogger.Instance) { Id = "first", UserId = user.Id, UserName = "first" };
            Second = new SessionInfo(sessionManager.Object, NullLogger.Instance) { Id = "second", UserId = user.Id, UserName = "second" };

            Group.CreateGroup(First, new NewGroupRequest("group"), CancellationToken.None);
            Group.SessionJoin(Second, new JoinGroupRequest(Group.GroupId), CancellationToken.None);
            Group.SetPlayQueue(new List<Guid> { Guid.NewGuid() }, 0, 0);
            PlaylistItemId = Group.PlayQueue.GetPlayingItemPlaylistId();
        }

        public SyncPlayGroup Group { get; }

        public SessionInfo First { get; }

        public SessionInfo Second { get; }

        public Guid PlaylistItemId { get; }

        // Mirrors the sweep SyncPlayManager runs on a timer.
        public void PumpGroupWaitTimeout()
        {
            var group = Group;

            // Group lock required as Group is not thread-safe.
            lock (group)
            {
                group.HandleGroupWaitTimeout(CancellationToken.None);
            }
        }
    }

    private sealed class FakeGroupStateContext : IGroupStateContext
    {
        private readonly Dictionary<string, bool> _buffering = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public FakeGroupStateContext(Guid itemId)
        {
            PlayQueue.SetPlaylist(new List<Guid> { itemId });
            PlaylistItemId = PlayQueue.GetPlaylist()[0].PlaylistItemId;
            PlayQueue.SetPlayingItemByPlaylistId(PlaylistItemId);
        }

        public long DefaultPing { get; set; } = 500;

        public long HighestPing { get; set; } = 500;

        public long TimeSyncOffset { get; set; } = 2000;

        public long MaxPlaybackOffset { get; set; } = 500;

        public Guid GroupId { get; } = Guid.NewGuid();

        public long PositionTicks { get; set; }

        public DateTime LastActivity { get; set; } = DateTime.UtcNow;

        public PlayQueueManager PlayQueue { get; } = new PlayQueueManager();

        public IGroupState? State { get; set; }

        public Guid PlaylistItemId { get; }

        public List<SendCommand> Commands { get; } = new List<SendCommand>();

        public long? GroupWaitLimit { get; private set; }

        public int GroupWaitRestores { get; private set; }

        public bool IsBuffering(string sessionId)
        {
            return _buffering.TryGetValue(sessionId, out var isBuffering) && isBuffering;
        }

        public Task SendCommand(SessionInfo from, SyncPlayBroadcastType type, SendCommand message, CancellationToken cancellationToken)
        {
            Commands.Add(message);
            return Task.CompletedTask;
        }

        public long GetHighestPing()
        {
            return HighestPing;
        }

        public SendCommand NewSyncPlayCommand(SendCommandType type)
        {
            // Mirrors Group.NewSyncPlayCommand: commands are dated at the
            // group's LastActivity, which is what the resume paths schedule.
            return new SendCommand(GroupId, PlaylistItemId, LastActivity, type, PositionTicks, DateTime.UtcNow);
        }

        public long SanitizePositionTicks(long? positionTicks)
        {
            return positionTicks ?? 0;
        }

        public void SetBuffering(SessionInfo session, bool isBuffering)
        {
            _buffering[session.Id] = isBuffering;
        }

        public bool IsBuffering()
        {
            return _buffering.Values.Any(v => v);
        }

        public Task SendGroupUpdate<T>(SessionInfo from, SyncPlayBroadcastType type, GroupUpdate<T> message, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void SetState(IGroupState state)
        {
            State = state;
        }

        public void LimitGroupWait(long timeout)
        {
            GroupWaitLimit = timeout;
        }

        public void RestoreGroupWait()
        {
            GroupWaitRestores++;
        }

        public void UpdatePing(SessionInfo session, long ping)
        {
        }

        public void SetAllBuffering(bool isBuffering)
        {
            foreach (var sessionId in _buffering.Keys)
            {
                _buffering[sessionId] = isBuffering;
            }
        }

        public void SetIgnoreGroupWait(SessionInfo session, bool ignoreGroupWait)
        {
        }

        public bool SetPlayQueue(IReadOnlyList<Guid> playQueue, int playingItemPosition, long startPositionTicks)
        {
            return false;
        }

        public bool SetPlayingItem(Guid playlistItemId)
        {
            return false;
        }

        public void ClearPlayQueue(bool clearPlayingItem)
        {
        }

        public bool RemoveFromPlayQueue(IReadOnlyList<Guid> playlistItemIds)
        {
            return false;
        }

        public bool MoveItemInPlayQueue(Guid playlistItemId, int newIndex)
        {
            return false;
        }

        public bool AddToPlayQueue(IReadOnlyList<Guid> newItems, GroupQueueMode mode)
        {
            return false;
        }

        public void RestartCurrentItem()
        {
        }

        public bool NextItemInQueue()
        {
            return false;
        }

        public bool PreviousItemInQueue()
        {
            return false;
        }

        public void SetRepeatMode(GroupRepeatMode mode)
        {
        }

        public void SetShuffleMode(GroupShuffleMode mode)
        {
        }

        public PlayQueueUpdate GetPlayQueueUpdate(PlayQueueUpdateReason reason)
        {
            return null!;
        }
    }
}
