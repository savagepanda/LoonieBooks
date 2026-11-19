using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// The short-lived token the web host attaches when it forwards a signed-in user's request to the
/// API. The API is never reachable from the browser; this token is how it learns who the user is,
/// and it refuses every request without one.
/// </summary>
public static class InternalApiToken
{
    public const string Issuer = "mapleledger-web";
    public const string Audience = "mapleledger-api";
    public const string SigningKeyConfig = "Auth:SigningKey";

    public const string SubjectClaim = "sub";
    public const string EmailClaim = "email";
    public const string NameClaim = "name";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>The shared HMAC key, or null when none is configured (the API then refuses everything).</summary>
    public static SymmetricSecurityKey? SigningKey(IConfiguration configuration) =>
        configuration[SigningKeyConfig] is { Length: >= 32 } key ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)) : null;

    public static string Create(SymmetricSecurityKey key, string subject, string email, string? name)
    {
        var claims = new List<Claim> { new(SubjectClaim, subject), new(EmailClaim, email) };
        if (!string.IsNullOrEmpty(name)) claims.Add(new Claim(NameClaim, name));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.Add(Lifetime),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    public static TokenValidationParameters ValidationParameters(SymmetricSecurityKey? key) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        // With no key configured, validate against a random throwaway key so nothing passes.
        IssuerSigningKey = key ?? new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = NameClaim,
    };
}
