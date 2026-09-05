using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.SyncPlay;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.GroupStates;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay
{
    /// <summary>
    /// Covers the timer plumbing behind <see cref="IGroupStateContext.ScheduleStateTimeout"/>,
    /// which the state-machine tests exercise through a fake and so never actually run.
    /// </summary>
    public class GroupStateTimeoutTests : IDisposable
    {
        private static readonly TimeSpan TestDelay = TimeSpan.FromMilliseconds(50);

        private readonly ILoggerFactory _loggerFactory;
        private readonly Group _group;

        public GroupStateTimeoutTests()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory
                .Setup(f => f.CreateLogger(It.IsAny<string>()))
                .Returns(Mock.Of<ILogger>());

            _loggerFactory = loggerFactory.Object;

            _group = new Group(
                _loggerFactory,
                Mock.Of<IUserManager>(),
                Mock.Of<ISessionManager>(),
                Mock.Of<ILibraryManager>());
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
                _group.Dispose();
            }
        }

        [Fact]
        public async Task ScheduleStateTimeout_Elapsed_CallsBackWithTheSchedulingState()
        {
            IGroupState? scheduledFor = null;
            var fired = new TaskCompletionSource();

            _group.StateTimeoutHandler = (_, state) =>
            {
                scheduledFor = state;
                fired.TrySetResult();
            };

            var currentState = new WaitingGroupState(_loggerFactory);
            _group.SetState(currentState);
            _group.ScheduleStateTimeout(TestDelay);

            await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The deadline belongs to the state that armed it, so the owner can drop it if the
            // group has moved on by the time the lock is taken.
            Assert.Same(currentState, scheduledFor);
        }

        [Fact]
        public async Task SetState_PendingTimeout_CancelsIt()
        {
            var fired = false;
            _group.StateTimeoutHandler = (_, _) => fired = true;

            _group.ScheduleStateTimeout(TestDelay);
            _group.SetState(new PausedGroupState(_loggerFactory));

            await Task.Delay(TestDelay * 6);

            Assert.False(fired);
        }

        [Fact]
        public async Task CancelStateTimeout_PendingTimeout_CancelsIt()
        {
            var fired = false;
            _group.StateTimeoutHandler = (_, _) => fired = true;

            _group.ScheduleStateTimeout(TestDelay);
            _group.CancelStateTimeout();

            await Task.Delay(TestDelay * 6);

            Assert.False(fired);
        }

        [Fact]
        public async Task ScheduleStateTimeout_Rescheduled_OnlyTheLatestFires()
        {
            var fired = 0;
            _group.StateTimeoutHandler = (_, _) => Interlocked.Increment(ref fired);

            _group.ScheduleStateTimeout(TestDelay);
            _group.ScheduleStateTimeout(TestDelay);
            _group.ScheduleStateTimeout(TestDelay);

            await Task.Delay(TestDelay * 6);

            Assert.Equal(1, Volatile.Read(ref fired));
        }

        [Fact]
        public async Task Dispose_PendingTimeout_CancelsIt()
        {
            var fired = false;
            _group.StateTimeoutHandler = (_, _) => fired = true;

            _group.ScheduleStateTimeout(TestDelay);
            _group.Dispose();

            await Task.Delay(TestDelay * 6);

            Assert.False(fired);
        }
    }
}
