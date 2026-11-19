using MapleLedger.Core;

namespace MapleLedger.Web.Client.Services;

/// <summary>
/// The category tree with the ledger's own categories, loaded once and shared by every picker and
/// filter; adding or removing a category through here refreshes it.
/// </summary>
public sealed class CategoryCatalog(ILedgerStore store)
{
    private Task<IReadOnlyList<CustomCategory>>? _custom;

    /// <summary>The ledger's categories changed (added, removed, or the ledger was replaced).</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<CustomCategory>> GetCustomAsync()
    {
        var load = _custom ??= store.GetCustomCategoriesAsync();
        try
        {
            return await load;
        }
        catch
        {
            if (_custom == load) _custom = null; // try again next time
            throw;
        }
    }

    /// <summary>The full tree; the built-in one when the ledger's own categories can't be loaded.</summary>
    public async Task<IReadOnlyList<CategoryNode>> GetTreeAsync()
    {
        try
        {
            return Categories.WithCustom(await GetCustomAsync());
        }
        catch (Exception ex) when (ex is LedgerStoreException or HttpRequestException)
        {
            return Categories.Tree;
        }
    }

    public async Task<CustomCategory> AddAsync(CustomCategoryRequest request)
    {
        var added = await store.AddCustomCategoryAsync(request);
        Invalidate();
        return added;
    }

    public async Task DeleteAsync(string id)
    {
        await store.DeleteCustomCategoryAsync(id);
        Invalidate();
    }

    /// <summary>Reload on next use, e.g. after a backup is restored or the ledger is cleared.</summary>
    public void Invalidate()
    {
        _custom = null;
        Changed?.Invoke();
    }
}
