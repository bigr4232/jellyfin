using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using SyncPlayManager = Emby.Server.Implementations.SyncPlay.SyncPlayManager;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay;

public class SyncPlayManagerReconnectTests : IDisposable
{
    private static readonly TimeSpan _testGracePeriod = TimeSpan.FromMilliseconds(100);

    private readonly SyncPlayManager _syncPlayManager;
    private readonly Mock<ISessionManager> _sessionManager;
    private readonly SessionInfo _session;
    private readonly Guid _userId;

    public SyncPlayManagerReconnectTests()
    {
        _userId = Guid.NewGuid();

        var userManager = new Mock<IUserManager>();
        userManager
            .Setup(m => m.GetUserById(_userId))
            .Returns(new User("user1", "authprovider", "passwordresetprovider"));

        _sessionManager = new Mock<ISessionManager>();

        _syncPlayManager = new SyncPlayManager(
            NullLoggerFactory.Instance,
            userManager.Object,
            _sessionManager.Object,
            Mock.Of<ILibraryManager>())
        {
            EvictionGracePeriod = _testGracePeriod
        };

        _session = new SessionInfo(_sessionManager.Object, NullLogger.Instance)
        {
            Id = "session1",
            UserId = _userId,
            UserName = "user1"
        };
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void OnSessionEnded_SessionInGroup_RemainsActiveDuringGracePeriod()
    {
        CreateGroup();

        RaiseSessionEnded();

        Assert.True(_syncPlayManager.IsUserActive(_userId));
    }

    [Fact]
    public async Task OnSessionEnded_SessionInGroup_EvictedAfterGracePeriod()
    {
        CreateGroup();

        RaiseSessionEnded();

        await Task.Delay(_testGracePeriod * 5);

        Assert.False(_syncPlayManager.IsUserActive(_userId));
        Assert.Empty(_syncPlayManager.ListGroups(_session, new ListGroupsRequest()));
    }

    [Fact]
    public async Task OnSessionControllerConnected_PendingLeave_CancelsEviction()
    {
        CreateGroup();

        RaiseSessionEnded();
        RaiseSessionControllerConnected();

        await Task.Delay(_testGracePeriod * 5);

        Assert.True(_syncPlayManager.IsUserActive(_userId));
        Assert.Single(_syncPlayManager.ListGroups(_session, new ListGroupsRequest()));
    }

    [Fact]
    public async Task LeaveGroup_PendingLeave_CancelsEviction()
    {
        CreateGroup();

        RaiseSessionEnded();
        _syncPlayManager.LeaveGroup(_session, new LeaveGroupRequest(), CancellationToken.None);

        await Task.Delay(_testGracePeriod * 5);

        Assert.False(_syncPlayManager.IsUserActive(_userId));
        _sessionManager.Verify(
            m => m.SendSyncPlayGroupUpdate<string>(
                It.IsAny<string>(),
                It.Is<GroupUpdate<string>>(update => update.Type == GroupUpdateType.NotInGroup),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task JoinGroup_RestoreDuringGracePeriod_DoesNotDoubleCountUser()
    {
        var groupId = CreateGroup();

        RaiseSessionEnded();
        _syncPlayManager.JoinGroup(_session, new JoinGroupRequest(groupId), CancellationToken.None);
        _syncPlayManager.LeaveGroup(_session, new LeaveGroupRequest(), CancellationToken.None);

        await Task.Delay(_testGracePeriod * 5);

        Assert.False(_syncPlayManager.IsUserActive(_userId));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _syncPlayManager.Dispose();
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void RaiseSessionEnded()
    {
        _sessionManager.Raise(
            ev => ev.SessionEnded += null,
            this,
            new SessionEventArgs { SessionInfo = _session });
    }

    private void RaiseSessionControllerConnected()
    {
        _sessionManager.Raise(
            ev => ev.SessionControllerConnected += null,
            this,
            new SessionEventArgs { SessionInfo = _session });
    }

    private Guid CreateGroup()
    {
        var groupInfo = _syncPlayManager.NewGroup(_session, new NewGroupRequest("test group"), CancellationToken.None);
        return groupInfo.GroupId;
    }
}
