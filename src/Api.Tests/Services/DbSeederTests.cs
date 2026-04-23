using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Api.Tests.Services;

public sealed class DbSeederTests
{
    private static IConfiguration BuildConfig(string mikePassword = "mikepass1", string badgerPassword = "badgerpass1") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:MikePassword"]   = mikePassword,
                ["Seed:BadgerPassword"] = badgerPassword,
            })
            .Build();

    private static UserManager<AppUser> CreateUserManager()
    {
        var store = Substitute.For<IUserStore<AppUser>>();
        return Substitute.For<UserManager<AppUser>>(store, null, null, null, null, null, null, null, null);
    }

    private static IServiceProvider BuildProvider(UserManager<AppUser> userManager, IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(userManager);
        services.AddSingleton(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SeedAsync_UsersDoNotExist_CreatesBothUsers()
    {
        var userManager = CreateUserManager();
        userManager.FindByNameAsync(Arg.Any<string>()).Returns(Task.FromResult<AppUser?>(null));
        userManager.CreateAsync(Arg.Any<AppUser>(), Arg.Any<string>()).Returns(IdentityResult.Success);

        await DbSeeder.SeedAsync(BuildProvider(userManager, BuildConfig()));

        await userManager.Received(1).CreateAsync(Arg.Is<AppUser>(u => u.UserName == "mike"), "mikepass1");
        await userManager.Received(1).CreateAsync(Arg.Is<AppUser>(u => u.UserName == "badger"), "badgerpass1");
    }

    [Fact]
    public async Task SeedAsync_UsersAlreadyExist_DoesNotCreateAgain()
    {
        var userManager = CreateUserManager();
        var existing = new AppUser { UserName = "mike" };
        userManager.FindByNameAsync(Arg.Any<string>()).Returns(Task.FromResult<AppUser?>(existing));

        await DbSeeder.SeedAsync(BuildProvider(userManager, BuildConfig()));

        await userManager.DidNotReceive().CreateAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SeedAsync_MikeExistsBadgerDoesNot_OnlyCreatesBadger()
    {
        var userManager = CreateUserManager();
        var mike = new AppUser { UserName = "mike" };
        userManager.FindByNameAsync("mike").Returns(Task.FromResult<AppUser?>(mike));
        userManager.FindByNameAsync("badger").Returns(Task.FromResult<AppUser?>(null));
        userManager.CreateAsync(Arg.Any<AppUser>(), Arg.Any<string>()).Returns(IdentityResult.Success);

        await DbSeeder.SeedAsync(BuildProvider(userManager, BuildConfig()));

        await userManager.DidNotReceive().CreateAsync(Arg.Is<AppUser>(u => u.UserName == "mike"), Arg.Any<string>());
        await userManager.Received(1).CreateAsync(Arg.Is<AppUser>(u => u.UserName == "badger"), "badgerpass1");
    }

    [Fact]
    public async Task SeedAsync_CreateFails_ThrowsInvalidOperationException()
    {
        var userManager = CreateUserManager();
        userManager.FindByNameAsync(Arg.Any<string>()).Returns(Task.FromResult<AppUser?>(null));
        userManager.CreateAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Description = "Password too weak" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DbSeeder.SeedAsync(BuildProvider(userManager, BuildConfig())));
    }

    [Fact]
    public async Task SeedAsync_MissingMikePasswordConfig_ThrowsInvalidOperationException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:BadgerPassword"] = "badgerpass1",
                // Seed:MikePassword intentionally absent
            })
            .Build();
        var userManager = CreateUserManager();
        userManager.FindByNameAsync(Arg.Any<string>()).Returns(Task.FromResult<AppUser?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DbSeeder.SeedAsync(BuildProvider(userManager, config)));
    }
}
