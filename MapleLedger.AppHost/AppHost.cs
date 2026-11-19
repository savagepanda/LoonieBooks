var builder = DistributedApplication.CreateBuilder(args);

// `aspire publish` emits Docker Compose artifacts. docker/docker-compose.yaml is the
// hand-maintained copy that the README's self-hosting steps run.
builder.AddDockerComposeEnvironment("env");

// The web host signs a short-lived token for each request it forwards to the API; the API accepts
// nothing else. Generated once and kept in the AppHost's user secrets.
var signingKey = builder.AddParameter("api-signing-key", new GenerateParameterDefault { MinLength = 64, Special = false }, secret: true, persist: true);

var api = builder.AddProject<Projects.MapleLedger_ApiService>("ledger-api")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Auth__SigningKey", signingKey);

var web = builder.AddProject<Projects.MapleLedger_Web>("ledger-web")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Auth__SigningKey", signingKey)
    .WithReference(api)
    .WaitFor(api);

if (builder.ExecutionContext.IsPublishMode)
{
    // A Linux container can't use Windows Integrated Auth against the host's SQL Server, so the
    // containerized API uses SQL authentication via host.docker.internal. The value comes from
    // AppHost user-secrets (dotnet user-secrets set Parameters:DockerDbConnectionString "...")
    // so it never lands in source control.
    var dockerDbConnectionString = builder.AddParameter("DockerDbConnectionString", secret: true);
    api.WithEnvironment("ConnectionStrings__mapleledger", dockerDbConnectionString);
}
else
{
    // The local SQL Server, via ConnectionStrings:mapleledger in appsettings.Development.json.
    api.WithReference(builder.AddConnectionString("mapleledger"));
}

// Google sign-in and the premium allow-list are optional when running locally: without them
// everyone is anonymous and keeps their ledger in the browser. Set them with
//   dotnet user-secrets set Parameters:google-client-id "..."      (and google-client-secret)
//   dotnet user-secrets set Parameters:premium-emails "you@example.com"
if (builder.ExecutionContext.IsPublishMode || !string.IsNullOrEmpty(builder.Configuration["Parameters:google-client-id"]))
{
    web.WithEnvironment("Authentication__Google__ClientId", builder.AddParameter("google-client-id"))
        .WithEnvironment("Authentication__Google__ClientSecret", builder.AddParameter("google-client-secret", secret: true));
}

if (builder.ExecutionContext.IsPublishMode || !string.IsNullOrEmpty(builder.Configuration["Parameters:premium-emails"]))
{
    api.WithEnvironment("Premium__Emails", builder.AddParameter("premium-emails"));
}

// "Send from phone" on the receipt scanner: when running locally the site is only on localhost,
// which a phone can't reach, so the web host also listens on the network on this port, for the
// phone upload page only. Windows may ask once to allow it through the firewall. Set
// ReceiptRelay:LanPort to 0 in the AppHost's user secrets to turn it off.
var phonePort = int.TryParse(builder.Configuration["ReceiptRelay:LanPort"], out var configuredPort) ? configuredPort : 7099;
if (!builder.ExecutionContext.IsPublishMode && phonePort > 0)
{
    web.WithHttpEndpoint(port: phonePort, name: "phone", isProxied: false)
        .WithEndpoint("phone", endpoint => endpoint.TargetHost = "0.0.0.0")
        .WithEnvironment("ReceiptRelay__LanPort", phonePort.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

// Optional: lets premium users have receipts read by Claude (each one sends the photo to
// Anthropic's API, at the key owner's cost). Set it with
//   dotnet user-secrets set Parameters:anthropic-api-key "sk-ant-..."
if (!string.IsNullOrEmpty(builder.Configuration["Parameters:anthropic-api-key"]))
{
    api.WithEnvironment("Anthropic__ApiKey", builder.AddParameter("anthropic-api-key", secret: true));
}

builder.Build().Run();
