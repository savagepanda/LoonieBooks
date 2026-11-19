using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Aspire.Hosting;
using MapleLedger.Core;
using MapleLedger.Fdx;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace MapleLedger.Tests;

/// <summary>
/// Starts the whole AppHost against the local SQL Server, using a separate MapleLedgerTests
/// database so test users never land in the development database.
/// </summary>
public sealed class WebTests : IAsyncLifetime
{
    private const string SigningKey = "integration-tests-signing-key-0123456789abcdefghijklmnopqrstuvwxyz";
    private const string PremiumEmail = "premium@example.test";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private DistributedApplication _app = default!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.MapleLedger_AppHost>(
        [
            "--ConnectionStrings:mapleledger=Server=localhost;Database=MapleLedgerTests;Trusted_Connection=True;TrustServerCertificate=True",
            $"--Parameters:api-signing-key={SigningKey}",
            $"--Parameters:premium-emails={PremiumEmail}",
        ], cancellationToken);
        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire.", LogLevel.Debug);
        });

        _app = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await _app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("ledger-web", cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task WebFrontendServesTheAppToAnonymousVisitors()
    {
        var web = _app.CreateHttpClient("ledger-web");

        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
        var me = await web.GetFromJsonAsync<Dictionary<string, object?>>("/account/me", TestContext.Current.CancellationToken);
        Assert.Equal("False", me!["isAuthenticated"]?.ToString());
    }

    [Fact]
    public async Task AnonymousVisitorsCannotReachTheServerLedger()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = _app.CreateHttpClient("ledger-web");

        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync("/fdx/v6/accounts", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync("/api/me", ct)).StatusCode);

        // State-changing calls also need the custom header (CSRF defence), checked before anything else.
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsJsonAsync("/api/accounts", new { }, ct)).StatusCode);
    }

    [Fact]
    public async Task ApiRejectsRequestsWithoutAValidTokenFromTheWebHost()
    {
        var ct = TestContext.Current.CancellationToken;
        var api = _app.CreateHttpClient("ledger-api");

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/fdx/v6/accounts", ct)).StatusCode);

        var forged = Client(api, subject: "mallory", email: PremiumEmail, key: "some-other-key-that-is-long-enough-to-sign-with-hmac-sha256");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/fdx/v6/accounts", ct)).StatusCode);
    }

    [Fact]
    public async Task RegisteredUsersWithoutPremiumCannotStoreDataOnTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var free = Client(_app.CreateHttpClient("ledger-api"), subject: $"free-{Guid.NewGuid():N}", email: "free@example.test");

        var profile = await free.GetFromJsonAsync<UserProfile>("/api/me", ct);
        var create = await free.PostAsJsonAsync("/api/accounts", Chequing(), ct);

        Assert.False(profile!.IsPremium);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await free.GetAsync("/fdx/v6/accounts", ct)).StatusCode);
    }

    [Fact]
    public async Task PremiumUsersOnlySeeTheirOwnData()
    {
        var ct = TestContext.Current.CancellationToken;
        var api = _app.CreateHttpClient("ledger-api");
        var alice = Client(api, subject: $"alice-{Guid.NewGuid():N}", email: PremiumEmail);
        var bob = Client(_app.CreateHttpClient("ledger-api"), subject: $"bob-{Guid.NewGuid():N}", email: PremiumEmail);

        Assert.True((await alice.GetFromJsonAsync<UserProfile>("/api/me", ct))!.IsPremium);
        var created = await alice.PostAsJsonAsync("/api/accounts", Chequing(), ct);
        var account = await created.Content.ReadFromJsonAsync<Account>(ct);
        try
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Contains((await alice.GetFromJsonAsync<AccountsResponse>("/fdx/v6/accounts", ct))!.Accounts, a => a.AccountId == account!.AccountId);

            Assert.Empty((await bob.GetFromJsonAsync<AccountsResponse>("/fdx/v6/accounts", ct))!.Accounts);
            Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/fdx/v6/accounts/{account!.AccountId}", ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/accounts/{account.AccountId}", ct)).StatusCode);
        }
        finally
        {
            await alice.DeleteAsync($"/api/accounts/{account!.AccountId}", ct);
        }
    }

    [Fact]
    public async Task PremiumUsersCanUndoTheirOwnImportsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = Client(_app.CreateHttpClient("ledger-api"), subject: $"alice-{Guid.NewGuid():N}", email: PremiumEmail);
        var bob = Client(_app.CreateHttpClient("ledger-api"), subject: $"bob-{Guid.NewGuid():N}", email: PremiumEmail);

        var account = await (await alice.PostAsJsonAsync("/api/accounts", Chequing(), ct)).Content.ReadFromJsonAsync<Account>(ct);
        try
        {
            var transaction = new LedgerTransaction(new Transaction
            {
                TransactionId = "undo-1",
                AccountId = account!.AccountId,
                TransactionTimestamp = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
                Amount = 5m,
                DebitCreditMemo = DebitCreditMemo.Debit,
                Description = "COFFEE",
            });
            var import = await (await alice.PostAsJsonAsync($"/api/accounts/{account.AccountId}/imports", new ImportTransactionsRequest([transaction], Source: "CSV"), ct))
                .Content.ReadFromJsonAsync<ImportTransactionsResult>(ct);

            var imports = await alice.GetFromJsonAsync<List<ImportSummary>>($"/api/accounts/{account.AccountId}/imports", ct);
            Assert.Equal((import!.BatchId, "CSV", 1), (Assert.Single(imports!).Id, imports![0].Source, imports[0].Remaining));

            Assert.Empty((await bob.GetFromJsonAsync<List<ImportSummary>>($"/api/accounts/{account.AccountId}/imports", ct))!);
            Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/imports/{import.BatchId}", ct)).StatusCode);

            var undo = await alice.DeleteAsync($"/api/imports/{import.BatchId}", ct);
            Assert.Equal(1, (await undo.Content.ReadFromJsonAsync<UndoImportResult>(ct))!.Removed);
            Assert.Empty((await alice.GetFromJsonAsync<List<ImportSummary>>($"/api/accounts/{account.AccountId}/imports", ct))!);
            Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/api/imports/{import.BatchId}", ct)).StatusCode);
        }
        finally
        {
            await alice.DeleteAsync($"/api/accounts/{account!.AccountId}", ct);
        }
    }

    [Fact]
    public async Task SimilarTransactionsCanBeClassifiedTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = Client(_app.CreateHttpClient("ledger-api"), subject: $"alice-{Guid.NewGuid():N}", email: PremiumEmail);
        var bob = Client(_app.CreateHttpClient("ledger-api"), subject: $"bob-{Guid.NewGuid():N}", email: PremiumEmail);

        var account = await (await alice.PostAsJsonAsync("/api/accounts", Chequing(), ct)).Content.ReadFromJsonAsync<Account>(ct);
        try
        {
            LedgerTransaction Walmart(string id, string store) => new(new Transaction
            {
                TransactionId = id,
                AccountId = account!.AccountId,
                TransactionTimestamp = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
                Amount = 5m,
                DebitCreditMemo = DebitCreditMemo.Debit,
                Description = $"WAL-MART SUPERCENTER#{store} KINGSTON",
            });
            await alice.PostAsJsonAsync($"/api/accounts/{account!.AccountId}/imports", new ImportTransactionsRequest([Walmart("w1", "1234"), Walmart("w2", "1235")]), ct);

            var similar = await alice.GetFromJsonAsync<List<LedgerTransaction>>($"/api/accounts/{account.AccountId}/transactions/w1/similar", ct);
            Assert.Equal("w2", Assert.Single(similar!).Transaction.TransactionId);
            Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/accounts/{account.AccountId}/transactions/w1/similar", ct)).StatusCode);

            var request = new ClassifyManyRequest(["w2"], Categories.Shopping);
            Assert.Equal(0, await (await bob.PutAsJsonAsync($"/api/accounts/{account.AccountId}/classification", request, ct)).Content.ReadFromJsonAsync<int>(ct));
            Assert.Equal(1, await (await alice.PutAsJsonAsync($"/api/accounts/{account.AccountId}/classification", request, ct)).Content.ReadFromJsonAsync<int>(ct));
        }
        finally
        {
            await alice.DeleteAsync($"/api/accounts/{account!.AccountId}", ct);
        }
    }

    [Fact]
    public async Task StartingOverDeletesOnlyYourOwnLedger()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = Client(_app.CreateHttpClient("ledger-api"), subject: $"alice-{Guid.NewGuid():N}", email: PremiumEmail);
        var bob = Client(_app.CreateHttpClient("ledger-api"), subject: $"bob-{Guid.NewGuid():N}", email: PremiumEmail);

        var aliceAccount = await (await alice.PostAsJsonAsync("/api/accounts", Chequing(), ct)).Content.ReadFromJsonAsync<Account>(ct);
        var bobAccount = await (await bob.PostAsJsonAsync("/api/accounts", Chequing(), ct)).Content.ReadFromJsonAsync<Account>(ct);
        try
        {
            var cleared = await alice.DeleteAsync("/api/ledger", ct);
            Assert.Equal(1, (await cleared.Content.ReadFromJsonAsync<ClearLedgerResult>(ct))!.Accounts);

            Assert.Empty((await alice.GetFromJsonAsync<AccountsResponse>("/fdx/v6/accounts", ct))!.Accounts);
            Assert.Contains((await bob.GetFromJsonAsync<AccountsResponse>("/fdx/v6/accounts", ct))!.Accounts, a => a.AccountId == bobAccount!.AccountId);
            Assert.True((await alice.GetFromJsonAsync<UserProfile>("/api/me", ct))!.IsPremium);
        }
        finally
        {
            await alice.DeleteAsync($"/api/accounts/{aliceAccount!.AccountId}", ct);
            await bob.DeleteAsync($"/api/accounts/{bobAccount!.AccountId}", ct);
        }
    }

    private static AccountRequest Chequing() =>
        new("Integration test chequing", null, AccountCategory.DepositAccount, AccountType.Checking, null, 100m, null, null);

    /// <summary>A client carrying the token the web host would attach for this user.</summary>
    private static HttpClient Client(HttpClient http, string subject, string email, string key = SigningKey)
    {
        var token = InternalApiToken.Create(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), subject, email, "Test User");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
}
