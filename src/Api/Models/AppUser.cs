using Microsoft.AspNetCore.Identity;

namespace Api.Models;

public sealed class AppUser : IdentityUser
{
    public string? RefreshTokenHash { get; set; }
    public DateTimeOffset? RefreshTokenExpiry { get; set; }
}

