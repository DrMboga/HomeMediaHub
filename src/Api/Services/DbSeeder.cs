using Api.Models;
using Microsoft.AspNetCore.Identity;

namespace Api.Services;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        var userMgr = services.GetRequiredService<UserManager<AppUser>>();
        var config  = services.GetRequiredService<IConfiguration>();

        var mikePassword = config["Seed:MikePassword"]
            ?? throw new InvalidOperationException("Seed:MikePassword is required");
        var badgerPassword = config["Seed:BadgerPassword"]
            ?? throw new InvalidOperationException("Seed:BadgerPassword is required");

        await CreateIfNotExistsAsync(userMgr, "mike", "mike@homemediahub.local", mikePassword);
        await CreateIfNotExistsAsync(userMgr, "badger", "badger@homemediahub.local", badgerPassword);
    }

    private static async Task CreateIfNotExistsAsync(
        UserManager<AppUser> mgr,
        string username,
        string email,
        string password)
    {
        if (await mgr.FindByNameAsync(username) is not null)
            return;

        var user = new AppUser { UserName = username, Email = email };
        var result = await mgr.CreateAsync(user, password);

        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Seed: failed to create '{username}': {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }
}
