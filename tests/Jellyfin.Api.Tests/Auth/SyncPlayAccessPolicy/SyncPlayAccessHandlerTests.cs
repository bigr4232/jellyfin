using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Jellyfin.Api.Auth.SyncPlayAccessPolicy;
using Jellyfin.Api.Constants;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Auth.SyncPlayAccessPolicy
{
    public class SyncPlayAccessHandlerTests
    {
        private readonly Mock<IUserManager> _userManagerMock;
        private readonly Mock<ISyncPlayManager> _syncPlayManagerMock;
        private readonly IAuthorizationService _authorizationService;
        private readonly Guid _userId;

        public SyncPlayAccessHandlerTests()
        {
            var fixture = new Fixture().Customize(new AutoMoqCustomization());
            _userManagerMock = fixture.Freeze<Mock<IUserManager>>();
            _syncPlayManagerMock = fixture.Freeze<Mock<ISyncPlayManager>>();

            var handler = fixture.Create<SyncPlayAccessHandler>();

            var services = new ServiceCollection();
            services.AddAuthorizationCore();
            services.AddLogging();
            services.AddOptions();
            services.AddSingleton<IAuthorizationHandler>(handler);
            services.AddAuthorization(options =>
            {
                options.AddPolicy("SyncPlayHasAccess", policy => policy.Requirements.Add(new SyncPlayAccessRequirement(SyncPlayAccessRequirementType.HasAccess)));
                options.AddPolicy("SyncPlayCreateGroup", policy => policy.Requirements.Add(new SyncPlayAccessRequirement(SyncPlayAccessRequirementType.CreateGroup)));
                options.AddPolicy("SyncPlayJoinGroup", policy => policy.Requirements.Add(new SyncPlayAccessRequirement(SyncPlayAccessRequirementType.JoinGroup)));
                options.AddPolicy("SyncPlayIsInGroup", policy => policy.Requirements.Add(new SyncPlayAccessRequirement(SyncPlayAccessRequirementType.IsInGroup)));
            });
            _authorizationService = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();

            _userId = Guid.NewGuid();
        }

        private ClaimsPrincipal SetupUser(SyncPlayUserAccessType syncPlayAccess, bool isActive)
        {
            var user = new User(
                "jellyfin",
                typeof(DefaultAuthenticationProvider).FullName!,
                typeof(DefaultPasswordResetProvider).FullName!)
            {
                SyncPlayAccess = syncPlayAccess
            };

            _userManagerMock
                .Setup(u => u.GetUserById(_userId))
                .Returns(user);

            _syncPlayManagerMock
                .Setup(m => m.IsUserActive(_userId))
                .Returns(isActive);

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "jellyfin"),
                new Claim(InternalClaimTypes.UserId, _userId.ToString("N", CultureInfo.InvariantCulture)),
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims));
        }

        [Theory]
        [InlineData("SyncPlayHasAccess")]
        [InlineData("SyncPlayCreateGroup")]
        [InlineData("SyncPlayJoinGroup")]
        [InlineData("SyncPlayIsInGroup")]
        public async Task ShouldFailWithoutUserContext(string policy)
        {
            // No user id claim, as with API key authentication.
            var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "jellyfin"),
            }));

            var allowed = await _authorizationService.AuthorizeAsync(claims, policy);

            Assert.False(allowed.Succeeded);
        }

        [Theory]
        [InlineData(SyncPlayUserAccessType.None, false, false)]
        [InlineData(SyncPlayUserAccessType.None, true, true)]
        [InlineData(SyncPlayUserAccessType.JoinGroups, false, true)]
        [InlineData(SyncPlayUserAccessType.CreateAndJoinGroups, false, true)]
        public async Task ShouldRequireHasAccessForHasAccessPolicy(SyncPlayUserAccessType syncPlayAccess, bool isActive, bool shouldSucceed)
        {
            var claims = SetupUser(syncPlayAccess, isActive);

            var allowed = await _authorizationService.AuthorizeAsync(claims, "SyncPlayHasAccess");

            Assert.Equal(shouldSucceed, allowed.Succeeded);
        }

        [Theory]
        [InlineData(SyncPlayUserAccessType.None, false)]
        [InlineData(SyncPlayUserAccessType.JoinGroups, false)]
        [InlineData(SyncPlayUserAccessType.CreateAndJoinGroups, true)]
        public async Task ShouldRequireCreateAndJoinGroupsForCreateGroupPolicy(SyncPlayUserAccessType syncPlayAccess, bool shouldSucceed)
        {
            var claims = SetupUser(syncPlayAccess, isActive: false);

            var allowed = await _authorizationService.AuthorizeAsync(claims, "SyncPlayCreateGroup");

            Assert.Equal(shouldSucceed, allowed.Succeeded);
        }

        [Theory]
        [InlineData(SyncPlayUserAccessType.None, false)]
        [InlineData(SyncPlayUserAccessType.JoinGroups, true)]
        [InlineData(SyncPlayUserAccessType.CreateAndJoinGroups, true)]
        public async Task ShouldRequireJoinAccessForJoinGroupPolicy(SyncPlayUserAccessType syncPlayAccess, bool shouldSucceed)
        {
            var claims = SetupUser(syncPlayAccess, isActive: false);

            var allowed = await _authorizationService.AuthorizeAsync(claims, "SyncPlayJoinGroup");

            Assert.Equal(shouldSucceed, allowed.Succeeded);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public async Task ShouldRequireActiveUserForIsInGroupPolicy(bool isActive, bool shouldSucceed)
        {
            var claims = SetupUser(SyncPlayUserAccessType.None, isActive);

            var allowed = await _authorizationService.AuthorizeAsync(claims, "SyncPlayIsInGroup");

            Assert.Equal(shouldSucceed, allowed.Succeeded);
        }
    }
}
