using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Api.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Api.Features.Auth;

public sealed class JwtTokenService
{
    private readonly IConfiguration _config;

    public JwtTokenService(IConfiguration config) => _config = config;

    public string GenerateAccessToken(AppUser user)
    {
        var key    = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:SecretKey"]!));
        var creds  = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiry = TimeSpan.FromMinutes(int.Parse(_config["Jwt:AccessTokenExpiryMinutes"]!));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub,  user.Id),
            new Claim(JwtRegisteredClaimNames.Name, user.UserName ?? ""),
            new Claim(JwtRegisteredClaimNames.Jti,  Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer:             _config["Jwt:Issuer"],
            audience:           _config["Jwt:Audience"],
            claims:             claims,
            expires:            DateTime.UtcNow.Add(expiry),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public string HashRefreshToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public DateTimeOffset RefreshTokenExpiry() =>
        DateTimeOffset.UtcNow.AddDays(int.Parse(_config["Jwt:RefreshTokenExpiryDays"]!));
}
