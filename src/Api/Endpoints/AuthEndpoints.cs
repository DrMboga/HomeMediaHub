using System.Security.Claims;
using Api.Features.Auth;
using Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (
            [FromBody] LoginRequest req,
            UserManager<AppUser> users,
            JwtTokenService jwt,
            CancellationToken ct) =>
        {
            var user = await users.FindByNameAsync(req.Username);
            if (user is null || !await users.CheckPasswordAsync(user, req.Password))
                return Results.Unauthorized();

            var accessToken  = jwt.GenerateAccessToken(user);
            var refreshToken = jwt.GenerateRefreshToken();

            user.RefreshTokenHash   = jwt.HashRefreshToken(refreshToken);
            user.RefreshTokenExpiry = jwt.RefreshTokenExpiry();
            await users.UpdateAsync(user);

            return Results.Ok(new AuthResponse(accessToken, refreshToken, 900));
        });

        g.MapPost("/refresh", async (
            [FromBody] RefreshRequest req,
            UserManager<AppUser> users,
            JwtTokenService jwt,
            CancellationToken ct) =>
        {
            var hash = jwt.HashRefreshToken(req.RefreshToken);
            var now  = DateTimeOffset.UtcNow;

            // Scan both users (only 2 exist) to find matching token hash
            var user = await users.Users
                .Where(u => u.RefreshTokenHash == hash && u.RefreshTokenExpiry > now)
                .FirstOrDefaultAsync(ct);

            if (user is null)
                return Results.Unauthorized();

            var accessToken      = jwt.GenerateAccessToken(user);
            var newRefreshToken  = jwt.GenerateRefreshToken();

            user.RefreshTokenHash   = jwt.HashRefreshToken(newRefreshToken);
            user.RefreshTokenExpiry = jwt.RefreshTokenExpiry();   // sliding window
            await users.UpdateAsync(user);

            return Results.Ok(new AuthResponse(accessToken, newRefreshToken, 900));
        });

        g.MapPost("/logout", async (
            ClaimsPrincipal principal,
            UserManager<AppUser> users,
            CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is not null)
            {
                user.RefreshTokenHash   = null;
                user.RefreshTokenExpiry = null;
                await users.UpdateAsync(user);
            }
            return Results.NoContent();
        }).RequireAuthorization();
    }
}

public record LoginRequest(string Username, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken, int ExpiresIn);
