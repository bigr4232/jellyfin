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
        public void HandleRequest_Ready_BehindGroup_SchedulesFuturePause()
        {
            _state.ResumePlaying = true;
            _context.PositionTicks = TimeSpan.FromSeconds(10).Ticks;

            var before = DateTime.UtcNow;
            HandleReady(ReadyRequest(TimeSpan.FromSeconds(5).Ticks, isPlaying: true));
            var after = DateTime.UtcNow;

            var command = Assert.Single(_context.Commands);
            Assert.Equal(SendCommandType.Pause, command.Command);
            Assert.InRange(command.When, before.AddSeconds(4), after.AddSeconds(6));
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

            public long TimeSyncOffset { get; set; } = 2000;

            public long MaxPlaybackOffset { get; set; } = 500;

            public Guid GroupId { get; } = Guid.NewGuid();

            public long PositionTicks { get; set; }

            public DateTime LastActivity { get; set; } = DateTime.UtcNow;

            public PlayQueueManager PlayQueue { get; } = new PlayQueueManager();

            public IGroupState? State { get; set; }

            public Guid PlaylistItemId { get; }

            public List<SendCommand> Commands { get; } = new List<SendCommand>();

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
                return DefaultPing;
            }

            public SendCommand NewSyncPlayCommand(SendCommandType type)
            {
                return new SendCommand(GroupId, PlaylistItemId, DateTime.UtcNow, type, null, DateTime.UtcNow);
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
