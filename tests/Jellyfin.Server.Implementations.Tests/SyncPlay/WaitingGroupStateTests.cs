using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.GroupStates;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Queue;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay
{
    public class WaitingGroupStateTests : IDisposable
    {
        private static readonly Guid ItemId = Guid.NewGuid();

        private readonly WaitingGroupState _state;
        private readonly FakeGroupStateContext _context;
        private readonly SessionInfo _session;
        private readonly SessionInfo _otherSession;

        public WaitingGroupStateTests()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory
                .Setup(f => f.CreateLogger(It.IsAny<string>()))
                .Returns(Mock.Of<ILogger>());

            _state = new WaitingGroupState(loggerFactory.Object);
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

        private ReadyGroupRequest ReadyRequest(long positionTicks, bool isPlaying)
        {
            return new ReadyGroupRequest(DateTime.UtcNow, positionTicks, isPlaying, _context.PlaylistItemId);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _otherSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        private void HandleReady(ReadyGroupRequest request)
        {
            _state.HandleRequest(request, _context, GroupStateType.Waiting, _session, CancellationToken.None);
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
        public void HandleRequest_Ready_SlightlyAheadWithinTolerance_SchedulesPauseNow()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = 0;

            var before = DateTime.UtcNow;
            HandleReady(ReadyRequest(TimeSpan.FromMilliseconds(200).Ticks, isPlaying: true));
            var after = DateTime.UtcNow;

            // A sub-tolerance lead is the one-way latency of the Ready report, not
            // a real desync: the session is not corrected, and its pause is never
            // dated in the past.
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
        public void HandleRequest_Ready_AheadOfGroup_LastBufferingSession_ResumesGroup()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = 0;

            // Nobody else is buffering, so there is nothing to hold this session back for.
            _context.SetBuffering(_otherSession, false);

            HandleReady(ReadyRequest(TimeSpan.FromSeconds(100).Ticks, isPlaying: true));

            Assert.DoesNotContain(_context.Commands, c => c.Command == SendCommandType.Seek);
            Assert.IsType<PlayingGroupState>(_context.State);
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
        public void HandleRequest_Ready_BehindGroupWithinTolerance_SchedulesFuturePause()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = TimeSpan.FromMilliseconds(10300).Ticks;

            var before = DateTime.UtcNow;
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(10).Ticks, isPlaying: true));
            var after = DateTime.UtcNow;

            // "Pause when ready in N seconds" is the upstream design for a client lagging by a
            // few hundred milliseconds. Past the tolerance it is corrected instead — see
            // HandleRequest_Ready_BehindGroupWhileOthersBuffering_SendsSeekCorrection.
            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Pause, command.Command);
            Assert.InRange(command.When, before.AddMilliseconds(200), after.AddMilliseconds(400));
            Assert.False(_context.IsBuffering(_session.Id));
        }

        [Fact]
        public void HandleRequest_Ready_BehindGroupBeyondTolerance_SendsSeekCorrection()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = TimeSpan.FromSeconds(100).Ticks;

            // Nobody else is buffering, so this session is the last one the group waits on.
            _context.SetBuffering(_otherSession, false);

            HandleReady(ReadyRequest(0, isPlaying: true));

            // A session this far behind is in the wrong place, not lagging: it is
            // corrected and the group stays in the waiting state for it.
            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Seek, command.Command);
            Assert.True(_context.IsBuffering(_session.Id));
        }

        [Fact]
        public void HandleRequest_Ready_BehindGroupBeyondTolerance_StopsCorrectingAfterMaxAttempts()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = TimeSpan.FromSeconds(100).Ticks;

            _context.SetBuffering(_otherSession, false);

            var before = DateTime.UtcNow;
            for (var i = 0; i < 6; i++)
            {
                HandleReady(ReadyRequest(0, isPlaying: true));
            }

            // At most 5 corrective seeks. The session is then left behind and the
            // group resumes with the standard recovery delay instead of waiting the
            // full gap for it to arrive.
            Assert.Equal(5, _context.Commands.Count(c => c.Command == SendCommandType.Seek));

            var unpause = Assert.Single(_context.Commands, c => c.Command == SendCommandType.Unpause);
            Assert.True(unpause.When < before.AddSeconds(5));
            Assert.IsType<PlayingGroupState>(_context.State);
        }

        [Fact]
        public void HandleRequest_Ready_BehindGroupWithinTolerance_ResumesWhenCatchingUp()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = TimeSpan.FromSeconds(1).Ticks;
            _context.HighestPing = 100;

            _context.SetBuffering(_otherSession, false);

            var before = DateTime.UtcNow;
            // 400 ms behind: more than twice the ping, so the group waits for the
            // session to catch up by playing, but the wait stays within the bound.
            HandleReady(ReadyRequest(TimeSpan.FromMilliseconds(600).Ticks, isPlaying: true));
            var after = DateTime.UtcNow;

            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Unpause, command.Command);
            Assert.InRange(command.When, before.AddMilliseconds(350), after.AddMilliseconds(450));
        }

        [Fact]
        public void HandleRequest_Ready_ResumingWithLowPing_FloorsRecoveryDelayAtDefaultPing()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = 0;
            _context.HighestPing = 100;

            _context.SetBuffering(_otherSession, false);

            var before = DateTime.UtcNow;
            HandleReady(ReadyRequest(0, isPlaying: true));
            var after = DateTime.UtcNow;

            // DefaultPing is in milliseconds, so the floor is 500 ms, not the
            // 200 ms twice-ping value a ticks-vs-milliseconds comparison yields.
            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Unpause, command.Command);
            Assert.InRange(command.When, before.AddMilliseconds(450), after.AddMilliseconds(550));
        }

        [Fact]
        public void HandleRequest_Ready_OutOfTolerance_SendsSeekCorrection()
        {
            _state.ResumePlaying = false;
            _context.PositionTicks = 0;

            HandleReady(ReadyRequest(TimeSpan.FromSeconds(60).Ticks, isPlaying: false));

            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Seek, command.Command);
            Assert.True(_context.IsBuffering(_session.Id));
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

            // It drifts again: a full new budget of corrections is available,
            // instead of the give-up branch re-firing on every Ready.
            for (var i = 0; i < 5; i++)
            {
                HandleReady(ReadyRequest(TimeSpan.FromSeconds(100).Ticks, isPlaying: true));
            }

            Assert.Equal(10, _context.Commands.Count(c => c.Command == SendCommandType.Seek));
            Assert.True(_context.IsBuffering(_session.Id));
        }

        private void HandleSeek(long positionTicks, GroupStateType prevState)
        {
            _state.HandleRequest(new SeekGroupRequest(positionTicks), _context, prevState, _session, CancellationToken.None);
        }

        [Fact]
        public void HandleRequest_Seek_ArmsShortWaitTimeout()
        {
            // A seek leaves the item loaded, so a client that is going to answer answers at
            // once. The stock web client answers not at all when the seek lands inside the
            // buffered range, and the group must not wait on it forever.
            HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Playing);

            Assert.Equal(TimeSpan.FromSeconds(2), _context.LastScheduledTimeout);
        }

        [Fact]
        public void HandleRequest_Seek_AfterASessionReported_ArmsLongWaitTimeout()
        {
            HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Playing);
            _state.ResumePlaying = true;

            // A session that reports is a session that is genuinely working through the seek,
            // so the group goes back to giving it the full allowance.
            HandleReady(ReadyRequest(_context.PositionTicks, isPlaying: true));

            Assert.Equal(TimeSpan.FromSeconds(30), _context.LastScheduledTimeout);
        }

        [Fact]
        public void SessionJoined_ArmsLongWaitTimeout()
        {
            // Loading an item legitimately takes seconds, and clients do report Ready on this
            // path, so it keeps the full allowance rather than the seek deadline.
            _state.SessionJoined(_context, GroupStateType.Playing, _session, CancellationToken.None);

            Assert.Equal(TimeSpan.FromSeconds(30), _context.LastScheduledTimeout);
        }

        [Fact]
        public void OnStateTimeout_ResumePlaying_ResumesGroupAtTheSeekTarget()
        {
            HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Playing);
            _context.Commands.Clear();

            var before = DateTime.UtcNow;
            _state.OnStateTimeout(_context, CancellationToken.None);

            // The automatic equivalent of a participant pressing play to escape the wait.
            Assert.IsType<PlayingGroupState>(_context.State);
            Assert.False(_context.IsBuffering());

            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Unpause, command.Command);
            Assert.Equal(TimeSpan.FromSeconds(30).Ticks, command.PositionTicks);
            Assert.True(command.When >= before);
        }

        [Fact]
        public void OnStateTimeout_ResumePlaying_DoesNotSuppressLaterBufferingReports()
        {
            HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Playing);
            _state.OnStateTimeout(_context, CancellationToken.None);

            // The manual Unpause escape sets IgnoreBuffering, which swallows every Buffer
            // request until the next state change. The automatic one must not.
            var playingState = Assert.IsType<PlayingGroupState>(_context.State);
            Assert.False(playingState.IgnoreBuffering);
        }

        [Fact]
        public void OnStateTimeout_NotResumePlaying_PausesGroupWithoutAdvancingPosition()
        {
            HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Paused);
            _context.Commands.Clear();

            // The group sat in the waiting state for a while: LastActivity is stale.
            _context.LastActivity = DateTime.UtcNow.AddSeconds(-30);
            var positionTicks = _context.PositionTicks;

            _state.OnStateTimeout(_context, CancellationToken.None);

            Assert.IsType<PausedGroupState>(_context.State);
            Assert.False(_context.IsBuffering());

            // Nobody was playing during the wait, so none of it belongs in the position.
            Assert.InRange(_context.PositionTicks, positionTicks, positionTicks + TimeSpan.FromSeconds(1).Ticks);

            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Pause, command.Command);
        }

        [Fact]
        public void OnStateTimeout_GroupAlreadyReady_DoesNothing()
        {
            HandleSeek(TimeSpan.FromSeconds(30).Ticks, GroupStateType.Playing);
            _context.SetAllBuffering(false);
            _context.Commands.Clear();

            // A deadline that lost the race against the group converging is inert.
            _state.OnStateTimeout(_context, CancellationToken.None);

            Assert.Null(_context.State);
            Assert.Empty(_context.Commands);
        }

        [Fact]
        public void HandleRequest_Ready_BehindGroupWhileOthersBuffering_SendsSeekCorrection()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = TimeSpan.FromSeconds(100).Ticks;

            HandleReady(ReadyRequest(0, isPlaying: true));

            // Past the tolerance the session is not lagging, it is in the wrong place, and
            // "pause when ready in 100 seconds" means it never pauses at all.
            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Seek, command.Command);
            Assert.True(_context.IsBuffering(_session.Id));
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

            public List<TimeSpan> ScheduledTimeouts { get; } = new List<TimeSpan>();

            public int CancelledTimeouts { get; private set; }

            public TimeSpan? LastScheduledTimeout =>
                ScheduledTimeouts.Count == 0 ? null : ScheduledTimeouts[^1];

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
                CancelStateTimeout();
                State = state;
            }

            public void ScheduleStateTimeout(TimeSpan delay)
            {
                ScheduledTimeouts.Add(delay);
            }

            public void CancelStateTimeout()
            {
                CancelledTimeouts++;
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
}
