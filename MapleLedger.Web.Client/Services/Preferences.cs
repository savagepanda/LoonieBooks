using System.Text.Json;
using Microsoft.JSInterop;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// Small per-browser conveniences (such as the dashboard's chosen accounts), kept in localStorage.
/// Kept apart for each place a ledger lives, since a browser ledger and a cloud ledger have
/// different accounts. Storage can be blocked or cleared, so a missing value is always fine.
/// </summary>
public sealed class Preferences(IJSRuntime js, UserSession session)
{
    private string Key(string name) => $"mapleledger.{session.Storage}.{name}";

    public async Task<T?> GetAsync<T>(string name)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", Key(name));
            return json is null ? default : JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex) when (ex is JSException or JsonException)
        {
            return default;
        }
    }

    public async Task SetAsync<T>(string name, T value)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", Key(name), JsonSerializer.Serialize(value));
        }
        catch (JSException)
        {
            // Storage is blocked (private window, site data off); the choice lasts until reload.
        }
    }
}
