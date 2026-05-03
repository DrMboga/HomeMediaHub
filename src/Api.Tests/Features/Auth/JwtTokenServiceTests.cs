using System.IdentityModel.Tokens.Jwt;
using Api.Features.Auth;
using Api.Models;
using Microsoft.Extensions.Configuration;

namespace Api.Tests.Features.Auth;

public sealed class JwtTokenServiceTests
{
    private static JwtTokenService Build(int accessMinutes = 15, int refreshDays = 30) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"]               = new string('k', 32),
                ["Jwt:Issuer"]                  = "test-issuer",
                ["Jwt:Audience"]                = "test-audience",
                ["Jwt:AccessTokenExpiryMinutes"] = accessMinutes.ToString(),
                ["Jwt:RefreshTokenExpiryDays"]   = refreshDays.ToString(),
            })
            .Build());

    private static AppUser MakeUser() => new()
    {
        Id       = Guid.NewGuid().ToString(),
        UserName = "alice",
    };

    // ── GenerateAccessToken ───────────────────────────────────────────────

    [Fact]
    public void GenerateAccessToken_ProducesReadableJwt()
    {
        var token = Build().GenerateAccessToken(MakeUser());
        Assert.True(new JwtSecurityTokenHandler().CanReadToken(token));
    }

    [Fact]
    public void GenerateAccessToken_ContainsSubAndNameClaims()
    {
        var user  = MakeUser();
        var jwt   = new JwtSecurityTokenHandler().ReadJwtToken(Build().GenerateAccessToken(user));

        Assert.Equal(user.Id,       jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(user.UserName, jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Name).Value);
    }

    [Fact]
    public void GenerateAccessToken_ContainsUniqueJti()
    {
        var svc  = Build();
        var user = MakeUser();
        var jti1 = new JwtSecurityTokenHandler().ReadJwtToken(svc.GenerateAccessToken(user))
                       .Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        var jti2 = new JwtSecurityTokenHandler().ReadJwtToken(svc.GenerateAccessToken(user))
                       .Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;

        Assert.NotEqual(jti1, jti2);
    }

    [Fact]
    public void GenerateAccessToken_ExpiryMatchesConfig()
    {
        var before = DateTime.UtcNow;
        var jwt    = new JwtSecurityTokenHandler().ReadJwtToken(Build(accessMinutes: 15).GenerateAccessToken(MakeUser()));

        Assert.True(jwt.ValidTo >= before.AddMinutes(14));
        Assert.True(jwt.ValidTo <= before.AddMinutes(16));
    }

    // ── GenerateRefreshToken ─────────────────────────────────────────────

    [Fact]
    public void GenerateRefreshToken_IsValidBase64Of64Bytes()
    {
        var token = Build().GenerateRefreshToken();
        var bytes = Convert.FromBase64String(token); // throws if invalid
        Assert.Equal(64, bytes.Length);
    }

    [Fact]
    public void GenerateRefreshToken_EachCallIsUnique()
    {
        var svc = Build();
        Assert.NotEqual(svc.GenerateRefreshToken(), svc.GenerateRefreshToken());
    }

    // ── HashRefreshToken ─────────────────────────────────────────────────

    [Fact]
    public void HashRefreshToken_IsDeterministic()
    {
        var svc   = Build();
        var token = svc.GenerateRefreshToken();
        Assert.Equal(svc.HashRefreshToken(token), svc.HashRefreshToken(token));
    }

    [Fact]
    public void HashRefreshToken_DifferentTokensProduceDifferentHashes()
    {
        var svc = Build();
        Assert.NotEqual(
            svc.HashRefreshToken(svc.GenerateRefreshToken()),
            svc.HashRefreshToken(svc.GenerateRefreshToken()));
    }

    [Fact]
    public void HashRefreshToken_HashDoesNotEqualOriginalToken()
    {
        var svc   = Build();
        var token = svc.GenerateRefreshToken();
        Assert.NotEqual(token, svc.HashRefreshToken(token));
    }

    // ── RefreshTokenExpiry ────────────────────────────────────────────────

    [Fact]
    public void RefreshTokenExpiry_IsInTheFuture()
    {
        var before = DateTimeOffset.UtcNow;
        var expiry = Build(refreshDays: 30).RefreshTokenExpiry();
        Assert.True(expiry > before.AddDays(29));
        Assert.True(expiry < before.AddDays(31));
    }
}
