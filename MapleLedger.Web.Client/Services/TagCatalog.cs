using MapleLedger.Core;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// The ledger's tags, loaded once and shared by every tag picker and filter; adding, renaming or
/// deleting a tag through here refreshes it.
/// </summary>
public sealed class TagCatalog(ILedgerStore store)
{
    private Task<IReadOnlyList<LedgerTag>>? _tags;

    /// <summary>The ledger's tags changed (added, renamed, removed, or the ledger was replaced).</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<LedgerTag>> GetAsync()
    {
        var load = _tags ??= store.GetTagsAsync();
        try
        {
            return await load;
        }
        catch
        {
            if (_tags == load) _tags = null; // try again next time
            throw;
        }
    }

    /// <summary>The tags, or none when they can't be loaded (pickers then just have nothing to offer).</summary>
    public async Task<IReadOnlyList<LedgerTag>> GetOrEmptyAsync()
    {
        try
        {
            return await GetAsync();
        }
        catch (Exception ex) when (ex is LedgerStoreException or HttpRequestException)
        {
            return [];
        }
    }

    public async Task<LedgerTag> AddAsync(string name)
    {
        var added = await store.AddTagAsync(name);
        Invalidate();
        return added;
    }

    public async Task<LedgerTag> RenameAsync(string id, string name)
    {
        var renamed = await store.RenameTagAsync(id, name);
        Invalidate();
        return renamed;
    }

    public async Task DeleteAsync(string id)
    {
        await store.DeleteTagAsync(id);
        Invalidate();
    }

    /// <summary>Reload on next use, e.g. after a backup is restored or the ledger is cleared.</summary>
    public void Invalidate()
    {
        _tags = null;
        Changed?.Invoke();
    }

    /// <summary>Tags whose name contains the search text (ignoring case and extra spaces); all of them for no text.</summary>
    public static IEnumerable<LedgerTag> Matching(IEnumerable<LedgerTag> tags, string? search)
    {
        var text = search?.Trim();
        return string.IsNullOrEmpty(text) ? tags : tags.Where(t => t.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
    }
}
