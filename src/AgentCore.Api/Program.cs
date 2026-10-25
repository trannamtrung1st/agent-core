using AgentCore.Api;
using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Security;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Security;
using AgentCore.Api.Realtime;
using AgentCore.Application.Admin;
using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;

if (args.Length > 0 && args[0] == "migrate-context")
{
    if (args.Length != 3) throw new ArgumentException("Usage: migrate-context <database-path> <new-backup-path>");
    var migrated = await ContextCutover.MigrateAsync(args[1], args[2]);
    Console.WriteLine($"Context cutover validated and committed ({migrated} retained records). Backup: {Path.GetFullPath(args[2])}");
    return;
}

var identityBootstrapCommand = args.Length == 1 && args[0] == "bootstrap-identity";
var builder = WebApplication.CreateBuilder(identityBootstrapCommand ? [] : args);
// Hosting request diagnostics include raw query strings. OAuth codes/state must never enter logs.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
});

builder.Services.AddAgentCoreIdentity();
builder.Services.AddIdentityHttp(builder.Configuration);
builder.Services.AddSingleton<SchedulerReadiness>();
builder.Services.AddHostedService<OpenSandboxHostedService>();
var agentDirectory = ResolveAgentDirectory(builder);
var profile = builder.Configuration["AgentCore:Profile"] ?? "Synthetic";

builder.Services.AddAgentCoreInfrastructure(
    agentDirectory,
    profile,
    ResolvePersistenceOptions(builder, profile),
    new InteractionPolicy(
        DegradedInterruptMs: builder.Configuration.GetValue("Interaction:DegradedInterruptMs", 250),
        PendingVoiceTimeoutMs: builder.Configuration.GetValue("Interaction:PendingVoiceTimeoutMs", 30_000),
        BackchannelMaxMs: builder.Configuration.GetValue("Interaction:BackchannelMaxMs", 700),
        MaxUtteranceSeconds: Math.Clamp(builder.Configuration.GetValue("Voice:MaxUtteranceSeconds", 120), 1, 180)),
    browser: builder.Configuration.GetSection(BrowserOptions.SectionName).Get<BrowserOptions>() ?? new BrowserOptions());

builder.Services.AddOptions<FilePreviewOptions>()
    .Bind(builder.Configuration.GetSection(FilePreviewOptions.SectionName))
    .Validate(options => options.IsValid(), "FilePreview must use positive, coherent limits within immutable safety ceilings.")
    .ValidateOnStart();
builder.Services.AddSingleton(services => services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FilePreviewOptions>>().Value.Effective());

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
builder.Services.AddSingleton<AdminLifecycleCoordinator>();
builder.Services.AddSingleton<AdminAuthoringOptionsService>();
builder.Services.AddSingleton<TenantRuntimeCapacity>();
builder.Services.AddSingleton<TenantSessionRuntimeHost>();
builder.Services.AddSingleton<ITenantHostedRuntimeStateReader>(p => p.GetRequiredService<TenantSessionRuntimeHost>());
builder.Services.AddSingleton<ITenantRequestIsolationControl>(p => p.GetRequiredService<TenantSessionRuntimeHost>());
builder.Services.AddHostedService<TenantRequestIsolationHostedService>();
builder.Services.AddHostedService<SessionShutdownHostedService>();
builder.Services.AddHostedService<WorkspaceCleanupHostedService>();
builder.Services.AddHostedService<TriggerSchedulerHostedService>();
builder.Services.AddHostedService<EmailWatchHostedService>();
builder.Services.AddSingleton<Func<TenantRunService>>(p => () => p.GetRequiredService<TenantRunService>());
builder.Services.AddSingleton<ITenantRunDispatcher>(p => p.GetRequiredService<TenantSessionRuntimeHost>());
builder.Services.AddSingleton<TenantSessionLiveDelivery>();
builder.Services.AddSingleton<ITenantRunControl>(p => (ITenantRunControl)p.GetRequiredService<ITenantRunDispatcher>());
builder.Services.AddSingleton<ITenantSessionInputControl>(p => (ITenantSessionInputControl)p.GetRequiredService<ITenantRunDispatcher>());
builder.Services.AddSingleton<ITenantSessionForegroundControl>(p => (ITenantSessionForegroundControl)p.GetRequiredService<ITenantRunDispatcher>());
builder.Services.AddSingleton<ITenantDeliveryReceiptControl>(p => (ITenantDeliveryReceiptControl)p.GetRequiredService<ITenantRunDispatcher>());
builder.Services.AddSingleton<TenantBackgroundCompletionService>();
builder.Services.AddSingleton<ITenantRunExecution, TenantRunExecution>();
builder.Services.AddHostedService<AgentRunHostedService>();
builder.Services.AddSingleton<SessionHubDiagnosticFilter>();
builder.Services.AddSignalR(options =>
{
    options.AddFilter<SessionHubDiagnosticFilter>();
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
builder.Services.AddExceptionHandler<DiagnosticExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
await InitializePersistenceAsync(app.Services).ConfigureAwait(false);
if (identityBootstrapCommand)
{
    var persistence = app.Services.GetRequiredService<PersistenceOptions>();
    if (!string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("bootstrap-identity requires explicitly configured SQLite storage; InMemory uses explicit ephemeral bootstrap configuration.");
    var identity = app.Services.GetRequiredService<IdentityService>();
    var result = await identity.BootstrapAsync(
        RequiredBootstrap("LOGIN"), RequiredBootstrap("DISPLAY_NAME"), RequiredBootstrap("PASSWORD"),
        RequiredBootstrap("TENANT_NAME"), RequiredBootstrap("TENANT_SLUG"));
    Console.WriteLine($"Identity bootstrap complete. Account {result.AccountId:D}; organization {result.TenantId:D}. Bootstrap is now disabled.");
    return;
}
await InitializeIdentityAsync(app.Services, app.Configuration).ConfigureAwait(false);
app.UseIdentityHttp();
IdentityEndpoints.Map(app);
TenantAccessEndpoints.Map(app);
TenantUserProfileEndpoints.Map(app);
TenantMemoryEndpoints.Map(app);
TenantExperienceEndpoints.Map(app);
AuthorizationAuditEndpoints.Map(app);
SystemAdministrationEndpoints.Map(app);
TenantAgentEndpoints.Map(app);
TenantCredentialEndpoints.Map(app);
ModelManagementEndpoints.Map(app);
TenantEmailMonitoringEndpoints.Map(app);
TenantConnectedAccountEndpoints.Map(app);
TenantSandboxProfileEndpoints.Map(app);
TenantSandboxRuntimeEndpoints.Map(app);
TenantBrowserEndpoints.Map(app);
SystemBrowserPrivacyEndpoints.Map(app);
TenantArtifactEndpoints.Map(app);
TenantAttachmentEndpoints.Map(app);
TenantWorkspaceEndpoints.Map(app);
TenantCredentialOAuthEndpoints.Map(app);
TenantCredentialBindingEndpoints.Map(app);
TenantAutomationEndpoints.Map(app);
TenantWebhookEndpoints.Map(app);
TenantWebhookIngressEndpoints.Map(app);
TenantSessionEndpoints.Map(app);
TenantActivityEndpoints.Map(app);
TenantOperationPolicyEndpoints.Map(app);
TenantApprovalEndpoints.Map(app);
TenantDefinitionEndpoints.Map(app);


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

app.MapHub<SessionHub>("/hubs/session").RequireAuthorization();


// Read the loaded API assembly, never a mutable checkout or DLL on disk.
var buildVersion = typeof(Program).Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion;
app.MapGet("/health", (IConfiguration configuration, SchedulerReadiness scheduler) =>
        Results.Json(new HealthResponse(
            "healthy",
            configuration["AgentCore:Profile"] ?? "Synthetic",
            HttpMapping.ProtocolVersion, buildVersion, scheduler.Snapshot())))
    .WithName("Health");

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

    await store.RecoverCrashedSessionsAsync().ConfigureAwait(false);
}

static string RequiredBootstrap(string key) => Environment.GetEnvironmentVariable("AGENTCORE_BOOTSTRAP_" + key)
    ?? throw new InvalidOperationException("Set AGENTCORE_BOOTSTRAP_" + key + " for the explicit one-use CLI bootstrap.");

static async Task InitializeIdentityAsync(IServiceProvider services, IConfiguration configuration)
{
    var store = services.GetRequiredService<IIamStore>();
    if (await store.HasBootstrapAsync().ConfigureAwait(false)) return;
    var persistence = services.GetRequiredService<PersistenceOptions>();
    if (!string.Equals(persistence.Provider, "InMemory", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Identity is not initialized. Run bootstrap-identity with explicit SQLite storage and operator-supplied credentials before starting the application.");
    string Required(string key) => configuration["Security:EphemeralBootstrap:" + key]
        ?? throw new InvalidOperationException("InMemory storage requires explicit Security:EphemeralBootstrap:" + key + ". No default account or password is supplied.");
    await services.GetRequiredService<IdentityService>().BootstrapAsync(Required("Login"), Required("DisplayName"), Required("Password"),
        Required("TenantName"), Required("TenantSlug")).ConfigureAwait(false);
}

public partial class Program;
