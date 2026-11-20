using MapleLedger.Core.Categorization;
using MapleLedger.Core.Tax;
using MapleLedger.Data;
using MapleLedger.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);

// The static site has no web host to render the page: the app mounts itself on its index.html.
if (!SiteInfo.HasWebHost)
{
    builder.RootComponents.Add<MapleLedger.Web.Client.Routes>("#app");
    builder.RootComponents.Add<Microsoft.AspNetCore.Components.Web.HeadOutlet>("head::after");
}

// Who is using the app decides where their ledger lives. Signing in or out reloads the page. On the
// static site nobody signs in, so there's no need to ask.
var session = SiteInfo.HasWebHost
    ? await UserSession.LoadAsync(new HttpClient { BaseAddress = baseAddress })
    : UserSession.Anonymous;
builder.Services.AddSingleton(session);

// A plain-language error when the server can't be reached at all (e.g. the desktop app was closed).
builder.Services.AddScoped(_ => new HttpClient(new ServerUnreachableHandler(session)) { BaseAddress = baseAddress });

// The browser ledger: SQLite in a Web Worker, persisted to the Origin Private File System.
builder.Services.AddDbContextFactory<LedgerDbContext>(options =>
    options.UseSqliteWasm(new SqliteWasmConnection("Data Source=MapleLedger.db")));
builder.Services.AddSqliteWasm(options => options.BaseHref = baseAddress.AbsolutePath);
builder.Services.AddSqliteWasmDbContext<LedgerDbContext>();

builder.Services.AddScoped<BrowserLedgerStore>();
builder.Services.AddScoped<ServerLedgerStore>();
builder.Services.AddScoped<ILedgerStore>(sp => session.Storage == StorageLocation.Cloud
    ? sp.GetRequiredService<ServerLedgerStore>()
    : sp.GetRequiredService<BrowserLedgerStore>());

builder.Services.AddScoped<CategoryCatalog>();
builder.Services.AddScoped<TagCatalog>();
builder.Services.AddScoped<FileDownloader>();
builder.Services.AddScoped<Preferences>();
builder.Services.AddScoped<Camera>();
builder.Services.AddScoped<ReceiptOcr>();
builder.Services.AddScoped<PhoneRelay>();
builder.Services.AddScoped<ExchangeRateFeed>();
builder.Services.AddScoped<QuoteFeed>();
builder.Services.AddScoped<HpiFeed>();
builder.Services.AddScoped<PriceHistoryFeed>();
builder.Services.AddScoped<MonthlyRateFeed>();
builder.Services.AddScoped<SampleLedger>();
builder.Services.AddScoped<ListingRequests>();
builder.Services.AddSingleton(new TransactionCategorizer());
builder.Services.AddSingleton(new TaxSuggester());

await builder.Build().RunAsync();
