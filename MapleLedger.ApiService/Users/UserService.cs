using System.Security.Claims;
using MapleLedger.Core;
using MapleLedger.Data;
using Microsoft.EntityFrameworkCore;

namespace MapleLedger.ApiService.Users;

/// <summary>The signed-in user behind the current request, as vouched for by the web host.</summary>
public sealed record CurrentUser(string Id, string Email, string? DisplayName)
{
    public static CurrentUser? From(ClaimsPrincipal principal) =>
        principal.FindFirstValue(InternalApiToken.SubjectClaim) is { Length: > 0 } id &&
        principal.FindFirstValue(InternalApiToken.EmailClaim) is { Length: > 0 } email
            ? new CurrentUser(id, email, principal.FindFirstValue(InternalApiToken.NameClaim))
            : null;
}

/// <summary>
/// Registered users and their tier. Premium is granted by an administrator: either by setting
/// <c>Tier</c> in the Users table, or by listing the email in <c>Premium:Emails</c> (comma
/// separated), which is how the first account gets it.
/// </summary>
public sealed class UserService(LedgerDbContext db, IConfiguration configuration)
{
    public async Task<LedgerUser> SignInAsync(CurrentUser current, CancellationToken ct)
    {
        var user = await FindOrCreateAsync(current, ct);
        user.Email = current.Email;
        user.DisplayName = current.DisplayName;
        user.LastSignInAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<bool> IsPremiumAsync(CurrentUser current, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == current.Id, ct)
            ?? await SignInAsync(current, ct);
        return user.Tier == UserTier.Premium || IsListedPremium(current.Email);
    }

    public static UserProfile ToProfile(LedgerUser user) => new(user.Id, user.Email, user.DisplayName, user.Tier == UserTier.Premium);

    private async Task<LedgerUser> FindOrCreateAsync(CurrentUser current, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == current.Id, ct);
        if (user is null)
        {
            user = new LedgerUser { Id = current.Id, Email = current.Email, CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
        }

        if (user.Tier != UserTier.Premium && IsListedPremium(current.Email))
        {
            user.Tier = UserTier.Premium;
        }

        return user;
    }

    private bool IsListedPremium(string email) =>
        (configuration["Premium:Emails"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(email, StringComparer.OrdinalIgnoreCase);
}
