using AgentCore.Api;
using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Admin;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
});

var agentDirectory = ResolveAgentDirectory(builder);
var profile = builder.Configuration["AgentCore:Profile"] ?? "Synthetic";
var languageModel = builder.Configuration.GetSection("Providers:LanguageModels:primary-llm")
    .Get<LanguageModelProviderOptions>() ?? new LanguageModelProviderOptions { Adapter = "Scripted" };
if (string.IsNullOrEmpty(languageModel.ApiKey))
{
    languageModel.ApiKey = builder.Configuration["OPENROUTER_API_KEY"];
}

builder.Services.AddAgentCoreInfrastructure(
    agentDirectory,
    profile,
    languageModel,
    ResolvePersistenceOptions(builder, profile),
    new InteractionPolicy(
        DegradedInterruptMs: builder.Configuration.GetValue("Interaction:DegradedInterruptMs", 250),
        PendingVoiceTimeoutMs: builder.Configuration.GetValue("Interaction:PendingVoiceTimeoutMs", 30_000),
        BackchannelMaxMs: builder.Configuration.GetValue("Interaction:BackchannelMaxMs", 700),
        MaxUtteranceSeconds: Math.Clamp(builder.Configuration.GetValue("Voice:MaxUtteranceSeconds", 120), 1, 180)));
builder.Services.Configure<AgentCoreOptions>(builder.Configuration.GetSection("AgentCore"));
builder.Services.AddOptions<ObservabilityOptions>()
    .Bind(builder.Configuration.GetSection("Observability"))
    .Validate(options => options.TimelineCapacity is >= 1 and <= 1000, "Observability:TimelineCapacity must be between 1 and 1000.")
    .Validate(
        options => !options.OtlpEnabled || Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out _),
        "Observability:OtlpEndpoint is required when OtlpEnabled is true.")
    .ValidateOnStart();
builder.Services.AddOptions<HostingOptions>()
    .Bind(builder.Configuration.GetSection("Hosting"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BindUrl), "Hosting:BindUrl is required.")
    .ValidateOnStart();
var observability = builder.Configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new ObservabilityOptions();
RuntimeTelemetry.Configure(observability.TimelineCapacity, observability.LogConversationContent);
builder.Services.AddSingleton<AdminReadService>();
builder.Services.AddSingleton<AdminMemoryService>();
builder.Services.AddSingleton<AdminAutomationService>();
builder.Services.AddSingleton<AgentDefinitionLifecycleService>();
builder.Services.AddSingleton<AgentDefinitionDraftValidationService>();
builder.Services.AddSingleton<AgentDefinitionDraftPublishService>();
builder.Services.AddSingleton<AgentDefinitionDraftDiffService>();
builder.Services.AddSingleton<AgentDefinitionResourceService>();
builder.Services.AddSingleton<SessionHost>();
builder.Services.AddSingleton<IProfileLiveUpdateNotifier, LazyProfileLiveUpdateNotifier>();
builder.Services.AddHostedService<SessionShutdownHostedService>();
builder.Services.AddHostedService<AttachmentTtlHostedService>();
builder.Services.AddHostedService<TriggerSchedulerHostedService>();
builder.Services.AddHostedService<DurableWorkIntakeHostedService>();
builder.Services.AddHostedService<DurableWorkHostedService>();
builder.Services.AddSingleton<IEnvironmentEventIngress>(provider => provider.GetRequiredService<SessionHost>());
builder.Services.AddSingleton<ILiveOccurrenceDirectory>(provider => provider.GetRequiredService<SessionHost>());
builder.Services.AddSingleton<IOccurrenceMailbox>(provider => provider.GetRequiredService<SessionHost>());
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 32 * 1024;
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.MaximumParallelInvocationsPerClient = 4;
}).AddMessagePackProtocol();
builder.Services.AddSingleton<OutboundHttpProbe>();
if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<ForbiddenOutboundHandler>();
    builder.Services.ConfigureHttpClientDefaults(client =>
        client.AddHttpMessageHandler<ForbiddenOutboundHandler>());
}
builder.Services.AddOpenApi();

var app = builder.Build();
await InitializePersistenceAsync(app.Services).ConfigureAwait(false);

var spaIndex = ResolveSpaIndex(app);
if (spaIndex is not null)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHub<SessionHub>("/hubs/session");
AdminEndpoints.Map(app);
SessionCatalogEndpoints.Map(app);
TriggerScheduleEndpoints.Map(app);
WorkItemEndpoints.Map(app);
ProfileEndpoints.Map(app);
HostSessionEndpoints.Map(app);
LegacySessionEndpoints.Map(app);
AttachmentEndpoints.Map(app);
WorkspaceEndpoints.Map(app);
ArtifactEndpoints.Map(app);

app.MapGet("/health", (IConfiguration configuration) =>
        Results.Json(new HealthResponse(
            "healthy",
            configuration["AgentCore:Profile"] ?? "Synthetic",
            HttpMapping.ProtocolVersion)))
    .WithName("Health");

app.MapGet("/api/v1/agents", async (SessionManager sessions, CancellationToken cancellationToken) =>
{
    var agents = await sessions.ListAgentsAsync(cancellationToken).ConfigureAwait(false);
    return Results.Json(new AgentListResponse(agents.Select(HttpMapping.ToAgent).ToArray()));
});

app.MapGet("/api/v1/agents/{agentId}", async (string agentId, int? version, SessionManager sessions, CancellationToken cancellationToken) =>
{
    try
    {
        var agent = await sessions.GetAgentAsync(agentId, version, cancellationToken).ConfigureAwait(false);
        return Results.Json(HttpMapping.ToAgent(agent));
    }
    catch (AgentCoreException ex)
    {
        return ProblemResults.From(ex);
    }
});

MapSpaFallback(app, spaIndex);

app.Run();

static string ResolveAgentDirectory(WebApplicationBuilder builder)
{
    var configured = builder.Configuration["AgentCore:AgentDirectory"] ?? "agents";
    if (Path.IsPathRooted(configured) && Directory.Exists(configured))
    {
        return configured;
    }

    var candidates = new[]
    {
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configured)),
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", configured)),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", configured))
    };

    return candidates.FirstOrDefault(Directory.Exists)
           ?? throw new InvalidOperationException("Agent definition directory was not found.");
}

static string? ResolveSpaIndex(WebApplication app)
{
    var webRoot = app.Environment.WebRootPath;
    if (string.IsNullOrEmpty(webRoot))
    {
        return null;
    }

    var index = Path.Combine(webRoot, "index.html");
    return File.Exists(index) ? index : null;
}

static void MapSpaFallback(WebApplication app, string? index)
{
    if (index is null)
    {
        return;
    }

    app.MapFallback(async context =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index).ConfigureAwait(false);
    });
}

static PersistenceOptions ResolvePersistenceOptions(WebApplicationBuilder builder, string profile)
{
    var persistence = builder.Configuration.GetSection("Persistence").Get<PersistenceOptions>() ?? new PersistenceOptions();
    if (string.Equals(profile, "Real", StringComparison.OrdinalIgnoreCase)
        && string.Equals(persistence.Provider, "InMemory", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Persistence__Provider")))
    {
        persistence.Provider = "Sqlite";
    }

    return persistence;
}

static async Task InitializePersistenceAsync(IServiceProvider services)
{
    var persistence = services.GetRequiredService<PersistenceOptions>();
    if (!string.IsNullOrWhiteSpace(persistence.WorkspaceRoot))
    {
        Directory.CreateDirectory(persistence.WorkspaceRoot);
    }

    if (!string.IsNullOrWhiteSpace(persistence.ArtifactRoot))
    {
        Directory.CreateDirectory(persistence.ArtifactRoot);
    }

    if (!string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var dataSource = persistence.ConnectionString
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
        .FirstOrDefault(part => part.Length == 2 && part[0].Equals("Data Source", StringComparison.OrdinalIgnoreCase));
    if (dataSource is { Length: 2 })
    {
        var directory = Path.GetDirectoryName(dataSource[1]);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var attachments = persistence.AttachmentRoot;
        if (!string.IsNullOrWhiteSpace(attachments))
        {
            Directory.CreateDirectory(attachments);
        }
    }

    var store = services.GetRequiredService<IMemoryStore>();
    if (store is SqliteMemoryStore sqlite)
    {
        await sqlite.EnsureCreatedAsync().ConfigureAwait(false);
    }

    await services.GetRequiredService<IAgentInstanceService>().BackfillAsync().ConfigureAwait(false);
    await store.RecoverCrashedSessionsAsync().ConfigureAwait(false);
}

public partial class Program;
