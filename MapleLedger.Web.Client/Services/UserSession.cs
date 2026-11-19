using System.Net.Http.Json;
using MapleLedger.Core;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// Who is using the app, worked out once when it starts. Signing in or out reloads the page, so
/// this never changes while the app is running.
/// </summary>
/// <param name="IsDesktop">
/// Run by the desktop launcher with its database: the computer's user is always signed in and the
/// "cloud" is the database on that computer, so there is no sign-in or sign-out.
/// </param>
/// <param name="IsLanDevice">
/// Another device using a desktop install shared on the network, signed in with the household
/// passcode. It can sign out, which makes it ask for the passcode again.
/// </param>
/// <param name="IsLanHost">The computer sharing a desktop install on the network: it can connect a phone with a QR code.</param>
public sealed record UserSession(bool SignInAvailable, bool IsSignedIn, string? Name, string? Email, bool IsPremium, bool IsDesktop = false, bool IsLanDevice = false, bool IsLanHost = false)
{
    public static UserSession Anonymous { get; } = new(false, false, null, null, false);

    /// <summary>Premium users keep their ledger on the server; everyone else keeps it in this browser.</summary>
    public StorageLocation Storage => IsPremium ? StorageLocation.Cloud : StorageLocation.Browser;

    public static async Task<UserSession> LoadAsync(HttpClient http)
    {
        try
        {
            var account = await http.GetFromJsonAsync<AccountStatus>("account/me");
            if (account is null) return Anonymous;
            if (!account.IsAuthenticated) return Anonymous with { SignInAvailable = account.SignInAvailable };

            // Registers the user on first sign-in and reports their tier. If the API can't be
            // reached they still get a working (browser-stored) ledger.
            UserProfile? profile = null;
            try
            {
                profile = await http.GetFromJsonAsync<UserProfile>("api/me");
            }
            catch (HttpRequestException)
            {
            }

            return new UserSession(account.SignInAvailable, true, account.Name, account.Email, profile?.IsPremium == true, account.Desktop, account.LanDevice, account.LanHost);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return Anonymous;
        }
    }

    private sealed record AccountStatus(bool SignInAvailable, bool IsAuthenticated, string? Name, string? Email, bool Desktop = false, bool LanDevice = false, bool LanHost = false);
}
