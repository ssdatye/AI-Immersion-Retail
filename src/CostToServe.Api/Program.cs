using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using CostToServe.Agent.Decisions;
using CostToServe.Agent.Model;
using CostToServe.Agent.Telemetry;
using Azure.Monitor.OpenTelemetry.Exporter;
using CostToServe.Api;
using CostToServe.Api.Cloud;
using CostToServe.Api.Studio;
using CostToServe.Core.Data;
using CostToServe.Core.Engine;
using CostToServe.Core.State;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var command = args.Length > 0 && args[0] is "reset" or "chat" or "seed-sql" ? args[0] : null;
var builder = WebApplication.CreateBuilder(command is null ? args : []);
var options = builder.Configuration.GetSection(CostToServeOptions.SectionName).Get<CostToServeOptions>() ?? new();
var paths = options.Resolve(builder.Environment.ContentRootPath);

// Git-ignored settings written by the deploy and Entra setup scripts (endpoints and IDs, no secrets).
if (paths.LocalSettingsFile is { } localSettings && builder.Configuration.GetValue("CostToServe:LoadLocalSettings", true))
{
    builder.Configuration.AddJsonFile(localSettings, optional: true);
}

// Developer sign-in locally; managed identity on Azure (AZURE_CLIENT_ID selects the user-assigned identity).
// Skip the managed-identity probe in development.
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeManagedIdentityCredential = builder.Environment.IsDevelopment(),
});

if (command == "reset")
{
    (string, Func<(int, int, int)>)? azureReset = string.IsNullOrWhiteSpace(options.Cosmos.Endpoint)
        ? null
        : ($"{options.Cosmos.Endpoint} / {options.Cosmos.Database}", () =>
        {
            using var cosmos = CosmosStores.ConnectAsync(options.Cosmos, credential).GetAwaiter().GetResult();
            return cosmos.ClearAsync().GetAwaiter().GetResult();
        });
    return ResetCommand.Run(args[1..], paths, Console.In, Console.Out, Console.Error, Console.IsInputRedirected, azureReset);
}

if (command == "chat")
{
    return await ChatCommand.RunAsync(args[1..], builder.Configuration, paths);
}

if (command == "seed-sql")
{
    return await SeedSqlCommand.RunAsync(args[1..], options, paths, credential, Console.In, Console.Out, Console.Error, Console.IsInputRedirected);
}

SeedDataSet seed;
string seedVersion;
IDecisionLog decisionLog;
ITranscriptStore transcripts;
IPackageStore packages;
if (options.IsAzure)
{
    // Cost facts from Azure SQL; decisions and transcripts in Cosmos DB. All through the app's managed identity.
    var (tables, version) = await SqlSeedStore.LoadAsync(options.Sql, credential);
    seed = SeedDataSet.Load(tables, "Azure SQL");
    seedVersion = version;
    var cosmos = await CosmosStores.ConnectAsync(options.Cosmos, credential);
    builder.Services.AddSingleton(cosmos);
    decisionLog = await CosmosDecisionLog.LoadAsync(cosmos);
    transcripts = new CosmosTranscriptStore(cosmos);
    packages = new CosmosPackageStore(cosmos.Scenarios);
}
else
{
    var manifest = SeedManifest.Verify(paths.SeedDirectory);
    if (!manifest.IsValid)
    {
        throw new InvalidOperationException(
            $"Seed data in {paths.SeedDirectory} does not match its manifest. Restore it with 'git checkout -- data/seed'. "
            + string.Join(" ", manifest.Problems));
    }

    seed = SeedDataSet.Load(paths.SeedDirectory);
    seedVersion = manifest.Version;
    new LocalDemoState(paths.SeedDirectory, paths.LocalStateDirectory).EnsureInitialized();
    decisionLog = new CsvDecisionLog(paths.LocalStateDirectory);
    transcripts = new JsonlTranscriptStore(paths.LocalStateDirectory);
    packages = new JsonlPackageStore(paths.LocalStateDirectory);
}

var foundry = builder.Configuration.GetSection(FoundryOptions.SectionName).Get<FoundryOptions>() ?? new();
var entra = builder.Configuration.GetSection(EntraOptions.SectionName).Get<EntraOptions>() ?? new();

// Entra sign-in, when configured, replaces dev personas entirely.
var devPersonas = options.DevPersonas && !entra.IsConfigured;
if (entra.IsConfigured)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.Authority = entra.Authority;
        o.MapInboundClaims = false;
        o.TokenValidationParameters.ValidAudiences = [entra.ClientId, $"api://{entra.ClientId}"];
        o.TokenValidationParameters.ValidIssuer = entra.Authority;
        o.TokenValidationParameters.NameClaimType = "name";
        o.TokenValidationParameters.RoleClaimType = "roles";
    });
}

var actionCatalog = ActionCatalog.LoadOrDefault(paths.ActionConfigFile);
builder.Services.AddSingleton(new StudioServices(
    seed, seedVersion, decisionLog, foundry, credential, actionCatalog, transcripts, options.IsAzure ? "azure" : "csv", packages));
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Keeps recent trace spans in memory for the Trust Center timeline (metadata only).
var trustSpans = new TrustSpanStore();
builder.Services.AddSingleton(trustSpans);
builder.Services.AddSingleton(builder.Configuration.GetSection(TrustOptions.SectionName).Get<TrustOptions>() ?? new TrustOptions());
var evaluationSummary = TrustEndpoints.LoadEvaluation(paths.EvalSummaryFile);

var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 } cs ? cs : null;
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("cost-to-serve-studio"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource([.. AgentTelemetry.AllSources])
            .AddProcessor(trustSpans);
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            tracing.AddOtlpExporter();
        }

        if (appInsights is not null)
        {
            tracing.AddAzureMonitorTraceExporter(o =>
            {
                o.ConnectionString = appInsights;
                o.Credential = credential;
            });
        }
    });

if (appInsights is not null)
{
    // Application Insights has local auth disabled; the exporter sends with the app's managed identity.
    builder.Logging.AddOpenTelemetry(o => o.AddAzureMonitorLogExporter(e =>
    {
        e.ConnectionString = appInsights;
        e.Credential = credential;
    }));
}

var app = builder.Build();

// What-If Studio front end: built by Vite into wwwroot (see src/CostToServe.Web).
app.UseDefaultFiles();
app.UseStaticFiles();
if (entra.IsConfigured)
{
    app.UseAuthentication();
}

app.MapStudio(entra, devPersonas, evaluationSummary);
// The architecture document (public, no data), so the Trust Center can link to it from the studio.
app.MapGet("/architecture", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "trust", "architecture.html");
    return File.Exists(path) ? Results.File(path, "text/html; charset=utf-8") : Results.NotFound();
});
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();
return 0;

// Exposes the entry point to WebApplicationFactory in API tests.
public partial class Program;
