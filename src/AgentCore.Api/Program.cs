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
using AgentCore.Infrastructure.Workflows;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;

if (args.Length > 0 && args[0] == "verify-protected-state")
{
    if (args.Length != 3) throw new ArgumentException("Usage: verify-protected-state <database-path> <existing-key-directory>");
    try
    {
        var count = await ProtectedStateAudit.VerifyAsync(args[1], args[2]);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { valid = true, protectedRecords = count }));
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Protected state verification refused. Check matching source/schema, complete database and existing protection keys. No values were printed or repaired.");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length > 0 && args[0] == "migrate-workflows")
{
    if (args.Length != 3) throw new ArgumentException("Usage: migrate-workflows <database-path> <new-backup-path>");
    await FormalWorkflowCutover.MigrateAsync(args[1], args[2]);
    Console.WriteLine("Formal Workflow schemas validated. Backup: " + Path.GetFullPath(args[2]));
    return;
}

if (args.Length > 0 && args[0] == "migrate-evaluations")
{
    if (args.Length != 3) throw new ArgumentException("Usage: migrate-evaluations <database-path> <new-backup-path>");
    var migrated = await EvaluationCutover.MigrateAsync(args[1], args[2]);
    Console.WriteLine($"Evaluation cutover committed ({migrated} historical results). Backup: {Path.GetFullPath(args[2])}");
    return;
}

if (args.Length > 0 && args[0] == "migrate-context")
{
    if (args.Length != 3) throw new ArgumentException("Usage: migrate-context <database-path> <new-backup-path>");
    var migrated = await ContextCutover.MigrateAsync(args[1], args[2]);
    Console.WriteLine($"Context cutover validated and committed ({migrated} retained records). Backup: {Path.GetFullPath(args[2])}");
    return;
}

if (args.Length > 0 && args[0] == "audit-google-grants")
{
    if (args.Length != 4) throw new ArgumentException("Usage: audit-google-grants <database-path> <existing-key-directory> <canonical-client-id>");
    try
    {
        var report = await AgentCore.Infrastructure.Credentials.GoogleGrantAudit.ReadAsync(args[1], args[2], args[3]);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Google grant audit could not read the matching database/key set. No values were printed or records changed.");
        Environment.ExitCode = 1;
    }
    return;
}

var identityBootstrapCommand = args.Length == 1 && args[0] == "bootstrap-identity";
var builder = WebApplication.CreateBuilder(identityBootstrapCommand ? [] : args);
AgentCore.Infrastructure.Configuration.ConfigurationCutover.Validate(builder.Configuration);
AgentCore.Infrastructure.Configuration.ConfigurationCutover.ValidateDeployment(builder.Configuration, builder.Environment.IsDevelopment(), identityBootstrapCommand);
var configuredPersistence = ResolvePersistenceOptions(builder, builder.Configuration["AgentCore:Profile"] ?? "Synthetic");
using var installationLease = configuredPersistence.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)
    ? SqliteInstallationLease.Acquire(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(configuredPersistence.ConnectionString).DataSource) : null;
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
        options => !options.OtlpEnabled || options.HasValidOtlpEndpoint(),
        "Observability:OtlpEndpoint is required when OtlpEnabled is true.")
    .ValidateOnStart();
builder.Services.AddOptions<HostingOptions>()
    .Bind(builder.Configuration.GetSection("Hosting"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BindUrl), "Hosting:BindUrl is required.")
    .ValidateOnStart();
var observability = builder.Configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new ObservabilityOptions();
RuntimeTelemetry.Configure(observability.TimelineCapacity, observability.LogConversationContent);
builder.Services.AddRuntimeExport(observability);
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
builder.Services.AddSingleton<TenantSessionContextService>();
builder.Services.AddSingleton<ITenantSessionGoalControl>(p => (ITenantSessionGoalControl)p.GetRequiredService<ITenantRunDispatcher>());
builder.Services.AddSingleton<ITenantSessionContextControl>(p => (ITenantSessionContextControl)p.GetRequiredService<ITenantRunDispatcher>());
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
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'; object-src 'none'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (!app.Environment.IsDevelopment() && context.Request.IsHttps)
        context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
    await next(context);
});
_ = app.Services.GetRequiredService<AgentCore.Infrastructure.Decisions.DecisionIntelligenceOptions>();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment() && configuredPersistence.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var database = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(configuredPersistence.ConnectionString).DataSource;
    if (new FileInfo(database) is { Exists: true, Length: > 0 })
    {
        try { await ProtectedStateAudit.VerifyAsync(database, configuredPersistence.CredentialProtectionKeyRoot); }
        catch (Exception)
        { throw new InvalidOperationException("Deployment preflight refused: protected_state_or_schema_mismatch. Restore matching database, roots and existing keys. No keys were repaired."); }
    }
}
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
await GeneralAssistantInitialization.ReconcileAsync(app.Services, app.Lifetime.ApplicationStopping);
app.UseIdentityHttp();
IdentityEndpoints.Map(app);
TenantAccessEndpoints.Map(app);
TenantUserProfileEndpoints.Map(app);
TenantMemoryEndpoints.Map(app);
TenantExperienceEndpoints.Map(app);
AuthorizationAuditEndpoints.Map(app);
SystemAdministrationEndpoints.Map(app);
TenantAgentEndpoints.Map(app);
TenantSpaceEndpoints.Map(app);
TenantSpaceResourceEndpoints.Map(app);
TenantEnvironmentEndpoints.Map(app);
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
TenantSessionGoalEndpoints.Map(app);
TenantTaskEndpoints.Map(app);
TenantWorkflowEndpoints.Map(app);
TenantSessionContextEndpoints.Map(app);
TenantActivityEndpoints.Map(app);
TenantOperationPolicyEndpoints.Map(app);
TenantApprovalEndpoints.Map(app);
TenantDefinitionEndpoints.Map(app);
TenantEvaluationEndpoints.Map(app);


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
            HttpMapping.ProtocolVersion, buildVersion, scheduler.Snapshot(), scheduler.WorkersSnapshot())))
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
        await services.InitializeFormalWorkflowRuntimeAsync(freshDatabase: false);
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
    var freshDatabase = false;
    if (store is SqliteMemoryStore sqlite)
    {
        await using var db = await services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
        freshDatabase = !(await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetAppliedMigrationsAsync(db.Database)).Any();
        await sqlite.EnsureCreatedAsync().ConfigureAwait(false);
    }

    await services.InitializeFormalWorkflowRuntimeAsync(freshDatabase);

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
