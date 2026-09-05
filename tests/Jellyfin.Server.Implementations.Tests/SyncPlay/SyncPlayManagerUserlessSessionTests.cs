using System;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.Requests;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using SyncPlayManager = Emby.Server.Implementations.SyncPlay.SyncPlayManager;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay;

public class SyncPlayManagerUserlessSessionTests : IDisposable
{
    private readonly SyncPlayManager _syncPlayManager;
    private readonly SessionInfo _session;
    private readonly SessionInfo _userlessSession;
    private readonly Guid _userId;

    public SyncPlayManagerUserlessSessionTests()
    {
        _userId = Guid.NewGuid();

        var userManager = new Mock<IUserManager>();
        userManager
            .Setup(m => m.GetUserById(_userId))
            .Returns(new User("user1", "authprovider", "passwordresetprovider"));

        // Mirrors the real UserManager, which throws rather than returning null.
        userManager
            .Setup(m => m.GetUserById(Guid.Empty))
            .Throws(new ArgumentException("Guid can't be empty", "id"));

        var sessionManager = new Mock<ISessionManager>();

        _syncPlayManager = new SyncPlayManager(
            NullLoggerFactory.Instance,
            userManager.Object,
            sessionManager.Object,
            Mock.Of<ILibraryManager>());

        _session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session1",
            UserId = _userId,
            UserName = "user1"
        };

        // An API-key session with no user attached, as the mcp-server session is.
        _userlessSession = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session2",
            UserId = Guid.Empty,
            UserName = string.Empty
        };
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ListGroups_SessionWithoutUser_ReturnsEmptyRatherThanThrowing()
    {
        CreateGroup();

        // The session's user id is independent of the authenticated principal's, so the
        // SyncPlayAccessHandler guard cannot cover this: the policy passes and the empty
        // id reaches GetUserById, which throws and surfaces as a 500 from /SyncPlay/List.
        Assert.Empty(_syncPlayManager.ListGroups(_userlessSession, new ListGroupsRequest()));
    }

    [Fact]
    public void GetGroup_SessionWithoutUser_ReturnsNullRatherThanThrowing()
    {
        var groupId = CreateGroup();

        // Null is what the controller turns into a 404.
        Assert.Null(_syncPlayManager.GetGroup(_userlessSession, groupId));
    }

    [Fact]
    public void JoinGroup_SessionWithoutUser_IsDeniedRatherThanThrowing()
    {
        var groupId = CreateGroup();

        _syncPlayManager.JoinGroup(_userlessSession, new JoinGroupRequest(groupId), CancellationToken.None);

        // Failed closed: the session is not counted as an active participant.
        Assert.False(_syncPlayManager.IsUserActive(Guid.Empty));
        Assert.Single(_syncPlayManager.ListGroups(_session, new ListGroupsRequest()));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _syncPlayManager.Dispose();
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _userlessSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private Guid CreateGroup()
    {
        var groupInfo = _syncPlayManager.NewGroup(_session, new NewGroupRequest("test group"), CancellationToken.None);
        return groupInfo.GroupId;
    }
}
