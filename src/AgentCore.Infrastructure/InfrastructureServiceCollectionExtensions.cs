using AgentCore.Infrastructure.Http;
using AgentCore.Application.Workspaces;
using AgentCore.Application.Admin;
using AgentCore.Application.Credentials;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Application.Agents;
using AgentCore.Application.Identity;
using AgentCore.Application.Memory;
using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.PublicWeb;
using AgentCore.Infrastructure.Tools;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddAgentCoreInfrastructure(
        this IServiceCollection services,
        string agentDirectory,
        string profile = "Synthetic",
        PersistenceOptions? persistence = null,
        InteractionPolicy? interaction = null,
        SpeechProvidersOptions? speech = null,
        BrowserOptions? browser = null)
    {
        services.AddHttpErrorDiagnostics();
        persistence ??= new PersistenceOptions();
        persistence.WorkspaceRoot = Path.GetFullPath(persistence.WorkspaceRoot);
        browser ??= new BrowserOptions();
        services.TryAddSingleton(browser);
        services.TryAddSingleton(provider => {
            var section = provider.GetService<IConfiguration>()?.GetSection("OpenSandbox");
            var options = section?.Get<OpenSandboxOptions>() ?? new OpenSandboxOptions();
            // ConfigurationBinder appends array entries to property initializers. An explicit
            // host allowlist must replace the public defaults so it can actually narrow them.
            // GetChildren also detects an explicitly null entry, whose section.Exists() is false.
            if (section?.GetChildren().FirstOrDefault(child => child.Key.Equals("AllowedImagePrefixes", StringComparison.OrdinalIgnoreCase)) is { } prefixes)
                options.AllowedImagePrefixes = prefixes.Get<string[]>() ?? [];
            options.CanonicalWorkspaceRoot = persistence.WorkspaceRoot;
            options.ValidatePolicy(); return options;
        });
        services.TryAddSingleton<ISandboxVerificationHost, SandboxVerificationHost>();
        services.TryAddSingleton<SandboxImageAuthority>();
        services.TryAddSingleton<ISandboxImageAuthority>(provider => provider.GetRequiredService<SandboxImageAuthority>());
        services.TryAddSingleton<ISandboxStore>(provider => string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
            ? new SqliteSandboxStore(provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()) : new InMemorySandboxStore(provider.GetRequiredService<InMemoryAdminEventStore>()));
        services.TryAddSingleton<OpenSandboxService>();
        services.TryAddSingleton<ISandboxService>(provider => provider.GetRequiredService<OpenSandboxService>());
        services.TryAddSingleton<ITenantSandboxVerifier>(provider => provider.GetRequiredService<OpenSandboxService>());
        services.TryAddSingleton<ITenantSandboxCommandExecutor>(provider => provider.GetRequiredService<OpenSandboxService>());
        services.TryAddSingleton<OpenSandboxReadiness>();
        services.TryAddSingleton<ISandboxReadiness>(provider => provider.GetRequiredService<OpenSandboxReadiness>());
        if (speech is not null)
        {
            services.TryAddSingleton(speech);
        }
        else
        {
            services.TryAddSingleton(provider =>
            {
                var configuration = provider.GetService<IConfiguration>();
                return configuration is null
                    ? new SpeechProvidersOptions()
                    : SpeechProviderBinder.Bind(configuration).Options;
            });
        }
        services.TryAddSingleton(persistence);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(SyntheticProviderAliases.Default);
        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<IModelCatalog>(provider => SyntheticModelCatalog.Create());
        services.TryAddSingleton<ITenantModelCatalogReader>(provider =>
            string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)
                ? new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(
                    provider.GetRequiredService<IIamStore>(), provider.GetRequiredService<ITenantModelStore>())
                : new TenantModelCatalogReader(provider.GetRequiredService<ITenantModelStore>(), provider.GetRequiredService<IIamStore>()));
        services.TryAddSingleton<ILanguageModelResolver>(provider =>
            string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)
                ? new AgentCore.Infrastructure.Providers.Synthetic.SyntheticLanguageModelResolver(
                    provider.GetRequiredService<ILanguageModel>(), provider.GetRequiredService<ITenantModelRouteAuthority>())
                : new LanguageModelResolver(provider.GetRequiredService<IModelProviderFactory>()));
        services.TryAddSingleton<IToolConfigurationGate>(provider => new ToolConfigurationGate(
            provider.GetService<IWebSearchProvider>(),
            provider.GetService<IPublicWebFetcher>(),
            provider.GetService<IBrowser>(),
            provider.GetRequiredService<BrowserOptions>().Enabled, provider.GetRequiredService<EmailTransportOptions>(), string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase), provider.GetService<ITenantCredentialOAuthProvider>(), provider.GetService<ISandboxReadiness>(), provider.GetService<IHttpRequestClient>()));
        services.TryAddSingleton<PromptContextBuilder>(provider =>
            new PromptContextBuilder(
                provider.GetRequiredService<IToolConfigurationGate>(),
                provider.GetService<IBrowser>()));
        services.TryAddSingleton<DefinitionDraftSyntheticOfflineLanguageModel>();
        services.TryAddSingleton<IDefinitionDraftSyntheticBehaviorEvaluator, SyntheticDefinitionDraftBehaviorEvaluator>();
        services.TryAddSingleton<AgentDefinitionDraftSyntheticEvaluationRunner>();
        services.TryAddSingleton<IInitiativeEvaluator>(provider =>
            new DefaultInitiativeEvaluator(provider.GetRequiredService<PromptContextBuilder>()));
        services.TryAddSingleton<IAgentBrain>(provider =>
            new DefaultAgentBrain(
                provider.GetRequiredService<PromptContextBuilder>(),
                provider.GetRequiredService<IInitiativeEvaluator>()));
        services.TryAddSingleton<IInterruptionClassifier, HeuristicInterruptionClassifier>();
        services.TryAddSingleton<IIdGenerator, SystemIdGenerator>();
        services.TryAddSingleton<IDiagnosticIdSource, SystemDiagnosticIdSource>();
        if (string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(new SqlitePragmaInterceptor(Math.Max(1, persistence.BusyTimeoutMs)));
            services.AddDbContextFactory<AgentCoreDbContext>((provider, options) =>
            {
                options.UseSqlite(persistence.ConnectionString);
                options.AddInterceptors(provider.GetRequiredService<SqlitePragmaInterceptor>());
            });
            services.AddSingleton<SqliteMemoryStore>(provider => new SqliteMemoryStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IMemoryStore>(provider => provider.GetRequiredService<SqliteMemoryStore>());
            services.AddSingleton<IAgentRunStore>(provider => new SqliteAgentRunStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<SqliteMemoryStore>(),
                provider.GetRequiredService<IDiagnosticIdSource>()));
            services.AddSingleton<IStructuredMemoryStore>(provider => new SqliteStructuredMemoryStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
            services.AddSingleton<IAgentInstanceStore>(provider => new SqliteAgentInstanceStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<IIdGenerator>()));
            services.AddSingleton<ITriggerStore>(provider => new SqliteTriggerStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
            services.AddSingleton<IExperienceStore, SqliteExperienceStore>();
            services.AddSingleton<IAgentDefinitionAdminStore>(provider => new SqliteAgentDefinitionAdminStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<IIdGenerator>(),
                provider.GetRequiredService<IDefinitionResourceContentStore>()));
            services.AddSingleton<IDefinitionResourceContentStore>(provider =>
                new FileDefinitionResourceContentStore(persistence.DefinitionResourceRoot));
            services.AddSingleton<IAgentDefinitionResourceAdminStore>(provider =>
                new SqliteAgentDefinitionResourceAdminStore(
                    provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    provider.GetRequiredService<IDefinitionResourceContentStore>(),
                    provider.GetRequiredService<IIdGenerator>()));
            services.TryAddSingleton<IDefinitionDraftEvaluationStore>(provider =>
                new SqliteDefinitionDraftEvaluationStore(
                    provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    provider.GetRequiredService<IIdGenerator>(),
                    Math.Max(1, persistence.BusyTimeoutMs)));
            services.AddSingleton<IBrowserPrivacyStore, SqliteBrowserPrivacyStore>();
            services.AddSingleton<IAdminEventStore>(provider => new SqliteAdminEventStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<IIdGenerator>()));
            services.AddSingleton<IEmailStore, SqliteEmailStore>();
            services.AddSingleton<IEmailWatchStore, SqliteEmailWatchStore>();
            services.AddSingleton<SqliteDriveStore>();
            services.AddSingleton<IDriveOperationStore>(sp => sp.GetRequiredService<SqliteDriveStore>());
            services.AddSingleton<ISyntheticDriveStore>(sp => sp.GetRequiredService<SqliteDriveStore>());
            services.AddSingleton<SqliteCredentialStore>();
            services.AddSingleton<ICredentialStore>(p => p.GetRequiredService<SqliteCredentialStore>());
            services.AddSingleton<IAgentCredentialBindingStore>(p => p.GetRequiredService<SqliteCredentialStore>());
            services.AddSingleton<ICoreEventStore, SqliteCoreEventStore>();
            services.AddSingleton<IExternalEventStore>(provider => new SqliteExternalEventStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));

            services.TryAddSingleton<SqliteAttachmentStore>(provider => new SqliteAttachmentStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<PersistenceOptions>().AttachmentRoot));
            services.TryAddSingleton<IAttachmentStore>(p => p.GetRequiredService<SqliteAttachmentStore>());
        }
        else
        {
            services.TryAddSingleton<InMemoryCoreEventStore>();
            services.TryAddSingleton<ICoreEventStore>(p => p.GetRequiredService<InMemoryCoreEventStore>());
            services.TryAddSingleton<InMemoryMemoryStore>(p => new InMemoryMemoryStore { CoreEvents = p.GetRequiredService<InMemoryCoreEventStore>() });
            services.TryAddSingleton<IMemoryStore>(provider => provider.GetRequiredService<InMemoryMemoryStore>());
            services.TryAddSingleton<IStructuredMemoryStore, InMemoryStructuredMemoryStore>();
            services.TryAddSingleton<InMemoryAgentInstanceStore>();
            services.TryAddSingleton<IAgentInstanceStore>(provider =>
            {
                var instances = provider.GetRequiredService<InMemoryAgentInstanceStore>();
                instances.CoreEvents = provider.GetRequiredService<InMemoryCoreEventStore>();
                instances.TriggerStore = provider.GetRequiredService<InMemoryTriggerStore>();
                instances.EventStore = provider.GetRequiredService<InMemoryAdminEventStore>();
                return instances;
            });
            services.TryAddSingleton<IExperienceStore, InMemoryExperienceStore>();
            services.TryAddSingleton<InMemoryDurableState>();
            services.TryAddSingleton(provider =>
                new InMemoryTriggerStore(provider.GetRequiredService<InMemoryDurableState>(), provider.GetRequiredService<InMemoryAdminEventStore>()));
            services.TryAddSingleton<ITriggerStore>(provider => provider.GetRequiredService<InMemoryTriggerStore>());
            services.TryAddSingleton<IAgentRunStore>(provider => new InMemoryAgentRunStore(
                provider.GetRequiredService<InMemoryMemoryStore>(),
                provider.GetRequiredService<IDiagnosticIdSource>(), provider.GetRequiredService<InMemoryTriggerStore>(), provider.GetRequiredService<InMemoryAgentInstanceStore>(), provider.GetRequiredService<IEmailStore>()));
            services.TryAddSingleton<IEmailStore, InMemoryEmailStore>();
            services.TryAddSingleton<IEmailWatchStore, InMemoryEmailWatchStore>();
            services.TryAddSingleton<InMemoryDriveStore>();
            services.TryAddSingleton<IDriveOperationStore>(sp => sp.GetRequiredService<InMemoryDriveStore>());
            services.TryAddSingleton<ISyntheticDriveStore>(sp => sp.GetRequiredService<InMemoryDriveStore>());
            services.TryAddSingleton<InMemoryCredentialStore>();
            services.TryAddSingleton<ICredentialStore>(p => p.GetRequiredService<InMemoryCredentialStore>());
            services.TryAddSingleton<IAgentCredentialBindingStore>(p => p.GetRequiredService<InMemoryCredentialStore>());
            services.TryAddSingleton<IExternalEventStore, InMemoryExternalEventStore>();

            services.TryAddSingleton<InMemoryAttachmentStore>(provider => new InMemoryAttachmentStore(provider.GetRequiredService<TimeProvider>()).BindTenantAuthority(provider.GetRequiredService<InMemoryMemoryStore>()));
            services.TryAddSingleton<IAttachmentStore>(p => p.GetRequiredService<InMemoryAttachmentStore>());
            services.TryAddSingleton<InMemoryDefinitionResourceContentStore>();
            services.TryAddSingleton<IDefinitionResourceContentStore>(provider =>
                provider.GetRequiredService<InMemoryDefinitionResourceContentStore>());
            services.TryAddSingleton<InMemoryAgentDefinitionResourceAdminStore>();
            services.TryAddSingleton<IAgentDefinitionResourceAdminStore>(provider =>
                provider.GetRequiredService<InMemoryAgentDefinitionResourceAdminStore>());
            services.TryAddSingleton<IBrowserPrivacyStore, InMemoryBrowserPrivacyStore>();
            services.TryAddSingleton<InMemoryAdminEventStore>();
            services.TryAddSingleton<IAdminEventStore>(provider =>
                provider.GetRequiredService<InMemoryAdminEventStore>());
            services.TryAddSingleton<InMemoryAgentDefinitionAdminStore>();
            services.TryAddSingleton<IAgentDefinitionAdminStore>(provider =>
            {
                var admin = provider.GetRequiredService<InMemoryAgentDefinitionAdminStore>();
                admin.ResourceStore = provider.GetRequiredService<InMemoryAgentDefinitionResourceAdminStore>();
                admin.EventStore = provider.GetRequiredService<InMemoryAdminEventStore>();
                return admin;
            });
            services.TryAddSingleton<IDefinitionDraftEvaluationStore>(provider =>
                new InMemoryDefinitionDraftEvaluationStore(provider.GetRequiredService<IAgentDefinitionAdminStore>()));

        }
        services.TryAddSingleton<ICredentialProtector>(p => new LocalCredentialProtector(persistence.CredentialProtectionKeyRoot));
        services.TryAddSingleton<AgentCore.Application.Drive.DriveService>();
        services.TryAddSingleton<IAttachmentProcessor, AttachmentProcessor>();
        services.TryAddSingleton<DefinitionPublicationResourceReader>();
        services.TryAddSingleton<IAgentInstanceWorkspaceStore>(provider =>
        {
            var options = provider.GetRequiredService<PersistenceOptions>();
            return new FileAgentInstanceWorkspaceStore(options.WorkspaceRoot,
                provider.GetRequiredService<IAgentInstanceStore>(), provider.GetRequiredService<ISandboxStore>(),
                tenantAgents: provider.GetRequiredService<ITenantAgentStore>(), tenantSessions: provider.GetRequiredService<ITenantSessionStore>());
        });
        services.TryAddSingleton<ITenantWorkspaceReadStore>(provider => (ITenantWorkspaceReadStore)provider.GetRequiredService<IAgentInstanceWorkspaceStore>());
        services.TryAddSingleton<ITenantWorkspaceMutationStore>(provider => (ITenantWorkspaceMutationStore)provider.GetRequiredService<IAgentInstanceWorkspaceStore>());
        services.TryAddSingleton<ITenantWorkspaceArtifactSourceStore>(provider => (ITenantWorkspaceArtifactSourceStore)provider.GetRequiredService<IAgentInstanceWorkspaceStore>());
        services.TryAddSingleton<AgentCore.Application.Security.TenantArtifactSourceTarget>();
        services.TryAddSingleton<ITenantWorkspaceMutationObservationStore>(provider => (ITenantWorkspaceMutationObservationStore)provider.GetRequiredService<IAgentInstanceWorkspaceStore>());
        services.TryAddSingleton<AgentCore.Application.Security.TenantWorkspaceMutationService>();
        services.TryAddSingleton<AgentCore.Application.Security.TenantWorkspaceReadService>();
        services.TryAddSingleton<AdminLifecycleCoordinator>();
        services.TryAddSingleton<ISessionWorkspace>(provider =>
        {
            var options = provider.GetRequiredService<PersistenceOptions>();
            return new FileSessionWorkspace(options.WorkspaceRoot, options.TemplateRoot,
                provider.GetService<IAttachmentStore>(),
                publicationResources: provider.GetService<DefinitionPublicationResourceReader>(),
                sessions: provider.GetRequiredService<IMemoryStore>(), instances: provider.GetRequiredService<IAgentInstanceStore>(), sandboxes: provider.GetRequiredService<ISandboxStore>(),
                tenantAuthority: provider.GetRequiredService<AgentCore.Application.Security.TenantSandboxExecutionAuthority>());
        });
        services.TryAddSingleton<ITenantWorkingCleanupBytes>(provider => (ITenantWorkingCleanupBytes)provider.GetRequiredService<ISessionWorkspace>());
        services.TryAddSingleton<ITenantSandboxWorkspacePreparer>(provider => (ITenantSandboxWorkspacePreparer)provider.GetRequiredService<ISessionWorkspace>());
        if (string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<SqliteArtifactStore>(provider => new SqliteArtifactStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>(),
                persistence.ArtifactRoot));
            services.TryAddSingleton<IArtifactStore>(provider => provider.GetRequiredService<SqliteArtifactStore>());
        }
        else
        {
            services.TryAddSingleton<InMemoryArtifactStore>(provider =>
                new InMemoryArtifactStore(provider.GetRequiredService<TimeProvider>()).BindTenantAuthority(provider.GetRequiredService<InMemoryMemoryStore>()));
            services.TryAddSingleton<IArtifactStore>(provider => provider.GetRequiredService<InMemoryArtifactStore>());
        }

        services.TryAddSingleton<ITenantArtifactStore>(provider => (ITenantArtifactStore)provider.GetRequiredService<IArtifactStore>());
        services.TryAddSingleton<ITenantAttachmentCleanupStore>(p => (ITenantAttachmentCleanupStore)p.GetRequiredService<IAttachmentStore>());
        services.TryAddSingleton<AgentCore.Application.Workspaces.TenantAttachmentCleanupService>();
        services.TryAddSingleton<ITenantArtifactCleanupStore>(provider => (ITenantArtifactCleanupStore)provider.GetRequiredService<IArtifactStore>());
        services.TryAddSingleton<AgentCore.Application.Workspaces.TenantArtifactCleanupService>();
        services.TryAddSingleton<AgentCore.Application.Security.TenantArtifactService>();
        services.TryAddSingleton<IArtifactReferenceAuthorizer>(provider =>
            new SessionArtifactAuthorizer(provider.GetService<IArtifactStore>()));
        services.AddHttpClient(ModelProviderHttp.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider => ModelProviderHttp.CreateHandler(provider.GetRequiredService<AgentCore.Application.Models.ModelEndpointPolicy>()));
        services.TryAddSingleton<IModelMetadataTransport>(provider => string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)
            ? new SyntheticModelMetadataTransport()
            : new ModelMetadataTransport(provider.GetRequiredService<IHttpClientFactory>().CreateClient(ModelProviderHttp.ClientName),
                provider.GetRequiredService<ITenantCredentialStore>(), provider.GetRequiredService<ICredentialProtector>(),
                provider.GetRequiredService<AgentCore.Application.Models.ModelEndpointPolicy>()));
        services.AddHttpClient(OpenAICompatibleLanguageModel.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).WithErrorDiagnostics();
        services.AddHttpClient(OpenAICompatibleBatchSpeechRecognizer.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).WithErrorDiagnostics();
        services.TryAddSingleton(provider =>
            SpeechFactory.Create(provider, provider.GetService<SpeechProvidersOptions>()));
        services.TryAddSingleton(provider => provider.GetRequiredService<SpeechResolution>().Plan);
        services.TryAddSingleton<ISpeechRecognizer>(provider =>
            provider.GetRequiredService<SpeechResolution>().Recognizer
            ?? throw new InvalidOperationException("No backend speech recognizer is registered for the selected speech plan."));
        services.TryAddSingleton<ISpeechSynthesizer>(provider =>
            provider.GetRequiredService<SpeechResolution>().Synthesizer
            ?? throw new InvalidOperationException("No backend speech synthesizer is registered for the selected speech plan."));
        services.AddHttpClient(OpenAiSpeechSynthesizer.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).WithErrorDiagnostics();
        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<ILanguageModel>(provider => new ScriptedLanguageModel());
        services.TryAddSingleton<IApprovedKnowledgeCatalog>(provider =>
            new FileApprovedKnowledgeCatalog(agentDirectory));
        services.TryAddSingleton<IRoleKnowledgeContentResolver>(provider =>
            new DefinitionBoundKnowledgeContentResolver(
                provider.GetRequiredService<IApprovedKnowledgeCatalog>(),
                provider.GetRequiredService<IBuiltInAgentDefinitionStore>(),
                provider.GetRequiredService<IAgentDefinitionAdminStore>(),
                provider.GetRequiredService<DefinitionPublicationResourceReader>()));
        services.TryAddSingleton<RoleKnowledgeService>();
        services.TryAddSingleton(provider =>
            new FileAgentDefinitionStore(agentDirectory, provider.GetRequiredService<ProviderAliasSet>()));
        services.TryAddSingleton<IBuiltInAgentDefinitionStore>(provider =>
            provider.GetRequiredService<FileAgentDefinitionStore>());
        services.TryAddSingleton<IBuiltInDefinitionTemplateSource>(provider => new FileBuiltInDefinitionTemplateSource(
            provider.GetRequiredService<IBuiltInAgentDefinitionStore>(), agentDirectory, provider.GetRequiredService<PersistenceOptions>().TemplateRoot));
        services.TryAddSingleton<IAgentDefinitionStore>(provider =>
            provider.GetRequiredService<IBuiltInAgentDefinitionStore>());
        services.TryAddSingleton(provider =>
        {
            var speech = provider.GetRequiredService<SpeechResolution>();
            return new VoiceAvailability
            {
                Plan = speech.Plan,
                LocaleSupport = speech.LocaleSupport
            };
        });
        services.TryAddSingleton(interaction ?? new InteractionPolicy());
        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<ITriggerCommandAuthorizer>(static provider =>
            {
                var configured = provider.GetService<IConfiguration>()?["Synthetic:AutomationAuthorizationDecision"];
                if (configured is null) return new SyntheticTriggerCommandAuthorizer();
                if (!Enum.TryParse<TriggerCommandAuthorizationDecision>(configured, out var verdict) || !Enum.IsDefined(verdict))
                    throw new ArgumentException("Synthetic Automation authorization fixture decision is invalid.");
                return new SyntheticTriggerCommandAuthorizer(verdict);
            });
        }
        else
        {
            services.TryAddSingleton<ITriggerCommandAuthorizer, TenantTriggerCommandAuthorizer>();
        }
        services.TryAddSingleton<IEventFilterEvaluator, AgentCore.Infrastructure.Events.RestrictedEventFilter>();
        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<IUserTurnCapabilityValidator, UserTurnCapabilityValidator>();

        WebSearchProviderRegistration.AddPublicWeb(services, profile);
        EmailProviderRegistration.AddEmail(services);
        AgentCore.Infrastructure.Drive.DriveRegistration.AddDrive(services);
        AgentCore.Infrastructure.Credentials.TenantOAuthRegistration.Add(services, profile);
        services.TryAddSingleton(browser.PrivacyAuthority());
        services.TryAddSingleton<BrowserPrivacyService>();
        services.TryAddSingleton(sp => new NativePlaywrightBrowser(
            sp.GetRequiredService<BrowserOptions>(),
            sp.GetService<ILoggerFactory>(), timeProvider: sp.GetRequiredService<TimeProvider>(),
            privacy: sp.GetRequiredService<BrowserPrivacyService>(), diagnostics: sp.GetRequiredService<IDiagnosticIdSource>()));
        services.TryAddSingleton<IBrowser>(sp => sp.GetRequiredService<NativePlaywrightBrowser>());
        services.TryAddSingleton<ITenantBrowserStateReader>(sp => (ITenantBrowserStateReader)sp.GetRequiredService<IBrowser>());
        services.TryAddSingleton<IBrowserLease>(sp => sp.GetRequiredService<NativePlaywrightBrowser>());
        services.TryAddSingleton<IBrowserStateReader>(sp => sp.GetRequiredService<NativePlaywrightBrowser>());
        services.AddHostedService(sp => sp.GetRequiredService<NativePlaywrightBrowser>());
        services.TryAddSingleton<InstanceAutomationPolicy>();
        services.TryAddSingleton<SandboxPolicyEnforcement>();
        services.TryAddSingleton<InheritedDefinitionResourceCatalog>();
        services.TryAddSingleton<IOfficeProcessor, AgentCore.Infrastructure.Office.OfficeProcessor>();
        services.TryAddSingleton<SessionToolExecutor>(provider => new SessionToolExecutor(
            configurationGate: provider.GetRequiredService<IToolConfigurationGate>(),
            browser: provider.GetService<IBrowser>(),
            sandboxService: provider.GetRequiredService<ISandboxService>(),
            sandboxStore: provider.GetRequiredService<ISandboxStore>(),
            tenantAuthorization: provider.GetRequiredService<AgentCore.Application.Security.TenantToolAuthorization>(),
            logger: provider.GetService<ILogger<SessionToolExecutor>>()));
        services.TryAddSingleton(provider =>
        {
            var speech = provider.GetRequiredService<SpeechResolution>();
            return new SessionRuntimeFactory(
                provider.GetRequiredService<ILanguageModelResolver>(),
                provider.GetRequiredService<IAgentBrain>(),
                provider.GetRequiredService<IIdGenerator>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<IInterruptionClassifier>(),
                provider.GetRequiredService<InteractionPolicy>(),
                speech.Recognizer, speech.Synthesizer,
                provider.GetRequiredService<VoiceAvailability>(),
                provider.GetRequiredService<SessionToolExecutor>(),
                provider.GetRequiredService<AgentCore.Application.Security.TenantRunContinuationService>(),
                provider.GetRequiredService<AgentCore.Application.Security.TenantArtifactReferenceService>(),
                provider.GetRequiredService<AgentCore.Application.Security.TenantAttachmentRuntimeContextService>(),
                provider.GetRequiredService<AgentCore.Application.Security.TenantRunProfileService>(),
                provider.GetRequiredService<AgentCore.Application.Security.TenantMemoryService>(),
                provider.GetRequiredService<AgentCore.Application.Security.TenantExperienceService>(),
                string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase) ? provider.GetRequiredService<ILanguageModel>() : null,
                string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase) ? provider.GetRequiredService<IModelCatalog>() : null,
                provider.GetRequiredService<IDiagnosticIdSource>());
        });
        return services;
    }
}
