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
        LanguageModelProviderOptions? languageModel = null,
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
        services.TryAddSingleton<SandboxImageAuthority>();
        services.TryAddSingleton<ISandboxImageAuthority>(provider => provider.GetRequiredService<SandboxImageAuthority>());
        services.TryAddSingleton<ISandboxStore>(provider => string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
            ? new SqliteSandboxStore(provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()) : new InMemorySandboxStore(provider.GetRequiredService<InMemoryAdminEventStore>()));
        services.TryAddSingleton<OpenSandboxService>();
        services.TryAddSingleton<ISandboxService>(provider => provider.GetRequiredService<OpenSandboxService>());
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
        services.TryAddSingleton<IModelCatalog>(provider =>
            ModelCatalogFactory.Create(profile, languageModel, provider.GetService<IConfiguration>()));
        services.TryAddSingleton<ILanguageModelResolver>(provider =>
            new LanguageModelResolver(
                provider,
                profile,
                languageModel ?? new LanguageModelProviderOptions { Adapter = "Scripted" },
                provider.GetRequiredService<IModelCatalog>()));
        services.TryAddSingleton<IToolConfigurationGate>(provider => new ToolConfigurationGate(
            provider.GetService<IWebSearchProvider>(),
            provider.GetService<IPublicWebFetcher>(),
            provider.GetService<IEmailProvider>(),
            provider.GetService<IBrowser>(),
            provider.GetRequiredService<BrowserOptions>().Enabled, provider.GetService<IEmailProviderFactory>(), provider.GetService<IGoogleDriveProviderFactory>(), provider.GetService<ISandboxReadiness>()));
        services.TryAddSingleton<PromptContextBuilder>(provider =>
            new PromptContextBuilder(
                provider.GetRequiredService<IToolConfigurationGate>(),
                provider.GetService<IBrowser>()));
        services.TryAddSingleton<DefinitionDraftSyntheticOfflineLanguageModel>();
        services.TryAddSingleton<IDefinitionDraftSyntheticBehaviorEvaluator, SyntheticDefinitionDraftBehaviorEvaluator>();
        services.TryAddSingleton<AgentDefinitionDraftSyntheticEvaluationRunner>();
        services.TryAddSingleton<IInitiativeEvaluator>(provider =>
            new DefaultInitiativeEvaluator(
                provider.GetRequiredService<PromptContextBuilder>(),
                LanguageModelFactory.Create(provider, profile, languageModel)));
        services.TryAddSingleton<IAgentBrain>(provider =>
            new DefaultAgentBrain(
                provider.GetRequiredService<PromptContextBuilder>(),
                provider.GetRequiredService<IInitiativeEvaluator>()));
        services.TryAddSingleton<AgentCore.Application.Experience.ExperienceService>();
        services.TryAddSingleton<AgentCore.Application.Continuity.ContinuityService>();
        services.TryAddSingleton<AgentCore.Application.Continuity.IdentityMaintenanceService>();
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
            services.AddSingleton<IAdminLifecycleDeletion>(provider => new SqliteAdminLifecycleDeletion(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<IIdGenerator>(),
                provider.GetRequiredService<IAgentInstanceWorkspaceStore>(), provider.GetService<IBrowser>()));
            services.TryAddSingleton<IAdminP7eHistoryMutator>(provider =>
                new SqliteAdminP7eHistoryMutator(
                    provider.GetRequiredService<AdminMemoryService>(),
                    provider.GetRequiredService<AdminAutomationService>(),
                    provider.GetRequiredService<ILocalUserProfileService>(),
                    provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    provider.GetRequiredService<IIdGenerator>(),
                    provider.GetRequiredService<TimeProvider>()));
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
            services.TryAddSingleton<IOwnerCapabilityStore, SqliteOwnerCapabilityStore>();
            services.TryAddSingleton<IAttachmentStore>(provider => new SqliteAttachmentStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>(),
                persistence.AttachmentRoot));
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
            services.TryAddSingleton<IOwnerCapabilityStore, InMemoryOwnerCapabilityStore>();
            services.TryAddSingleton<IAttachmentStore>(provider =>
                new InMemoryAttachmentStore(provider.GetRequiredService<TimeProvider>()));
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
            services.AddSingleton<IAdminLifecycleDeletion>(provider =>
            {
                _ = provider.GetRequiredService<IAgentDefinitionAdminStore>();
                _ = provider.GetRequiredService<IDefinitionDraftEvaluationStore>();
                return new InMemoryAdminLifecycleDeletion(
                    provider.GetRequiredService<InMemoryAgentInstanceStore>(),
                    provider.GetRequiredService<InMemoryMemoryStore>(),
                    (InMemoryStructuredMemoryStore)provider.GetRequiredService<IStructuredMemoryStore>(),
                    provider.GetRequiredService<InMemoryTriggerStore>(),
                    provider.GetRequiredService<InMemoryAgentDefinitionAdminStore>(),
                    provider.GetRequiredService<InMemoryAdminEventStore>(),
                    (InMemoryExperienceStore)provider.GetRequiredService<IExperienceStore>(),
                    provider.GetRequiredService<IAgentInstanceWorkspaceStore>(), provider.GetRequiredService<IAgentCredentialBindingStore>(), provider.GetService<IBrowser>(), provider.GetRequiredService<InMemoryCoreEventStore>(), provider.GetRequiredService<IDriveOperationStore>());
            });
            services.TryAddSingleton<IAdminP7eHistoryMutator>(provider =>
                new InMemoryAdminP7eHistoryMutator(
                    provider.GetRequiredService<AdminMemoryService>(),
                    provider.GetRequiredService<InMemoryAdminEventStore>(),
                    (InMemoryStructuredMemoryStore)provider.GetRequiredService<IStructuredMemoryStore>(),
                    provider.GetRequiredService<AdminAutomationService>(),
                    provider.GetRequiredService<InMemoryDurableState>()));
        }
        services.TryAddSingleton<ICredentialProtector>(p => new LocalCredentialProtector(persistence.CredentialProtectionKeyRoot));
        services.TryAddSingleton<AgentCore.Application.Drive.DriveService>();
        services.TryAddSingleton<CredentialService>();
        services.TryAddSingleton<ICredentialResolver>(p => p.GetRequiredService<CredentialService>());
        services.TryAddSingleton<AgentBrowserProfileService>();
        services.TryAddSingleton<IAttachmentProcessor, AttachmentProcessor>();
        services.TryAddSingleton<DefinitionPublicationResourceReader>();
        services.TryAddSingleton<IAgentInstanceWorkspaceStore>(provider =>
        {
            var options = provider.GetRequiredService<PersistenceOptions>();
            return new FileAgentInstanceWorkspaceStore(options.WorkspaceRoot,
                provider.GetRequiredService<IAgentInstanceStore>(), provider.GetRequiredService<ISandboxStore>());
        });
        services.TryAddSingleton<AdminLifecycleCoordinator>();
        services.TryAddSingleton<AgentInstanceWorkspaceService>();
        services.TryAddSingleton<AgentCore.Application.Workspaces.SessionWorkingCleanupService>();
        services.TryAddSingleton<ISessionWorkspace>(provider =>
        {
            var options = provider.GetRequiredService<PersistenceOptions>();
            return new FileSessionWorkspace(options.WorkspaceRoot, options.TemplateRoot,
                provider.GetService<IAttachmentStore>(),
                publicationResources: provider.GetService<DefinitionPublicationResourceReader>(),
                sessions: provider.GetRequiredService<IMemoryStore>(), instances: provider.GetRequiredService<IAgentInstanceStore>(), sandboxes: provider.GetRequiredService<ISandboxStore>());
        });
        if (string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IArtifactStore>(provider => new SqliteArtifactStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>(),
                persistence.ArtifactRoot));
        }
        else
        {
            services.TryAddSingleton<IArtifactStore>(provider =>
                new InMemoryArtifactStore(provider.GetRequiredService<TimeProvider>()));
        }

        services.TryAddSingleton<IArtifactReferenceAuthorizer>(provider =>
            new SessionArtifactAuthorizer(provider.GetService<IArtifactStore>()));
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
        services.TryAddSingleton<ILanguageModel>(provider =>
            LanguageModelFactory.Create(provider, profile, languageModel));
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
            new CompositeAgentDefinitionStore(
                provider.GetRequiredService<FileAgentDefinitionStore>(),
                provider.GetRequiredService<IAgentDefinitionAdminStore>()));
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
        services.TryAddSingleton<ILocalUserProfileService, LocalUserProfileService>();
        services.TryAddSingleton<IStructuredMemoryService, StructuredMemoryService>();
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
            services.TryAddSingleton<ModelTriggerCommandAuthorizer>(static provider =>
            {
                var selection = SessionModelBinder.PinDefault(provider.GetRequiredService<IModelCatalog>());
                var descriptor = provider.GetRequiredService<IModelCatalog>().Get(selection.CatalogKey)!;
                // Use the least expensive declared effort for this internal purpose;
                // keep the pinned route and ordinary conversation defaults unchanged.
                if (descriptor.Reasoning)
                {
                    var effort = new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" }
                        .FirstOrDefault(candidate => descriptor.SupportedReasoningEfforts.Contains(candidate, StringComparer.OrdinalIgnoreCase));
                    if (effort is not null) selection = selection with { ReasoningEffort = effort };
                }
                return new ModelTriggerCommandAuthorizer(
                    provider.GetRequiredService<ILanguageModelResolver>().Resolve(selection, ModelPurpose.TriggerAuthorization),
                    selection.ReasoningEffort,
                    provider.GetService<ILogger<ModelTriggerCommandAuthorizer>>(), descriptor, provider.GetRequiredService<TimeProvider>());
            });
            services.TryAddSingleton<ITriggerCommandAuthorizer>(static provider =>
                provider.GetRequiredService<ModelTriggerCommandAuthorizer>());
        }
        services.TryAddSingleton<IAutomationService, AutomationService>();
        services.TryAddSingleton<AdminAutomationAuthoringService>();
        services.TryAddSingleton<TriggerScheduler>();
        services.TryAddSingleton<ITriggerAdmissionGuard, TriggerAdmissionGuard>();
        services.TryAddSingleton<ITriggerPolicyRecoveryService, TriggerPolicyRecoveryService>();
        services.TryAddSingleton<ITriggerInstancePolicyReconciliationService, TriggerInstancePolicyReconciliationService>();
        services.TryAddSingleton<IDurableApplicationEventIngress, DurableOrderEventIngress>();
        services.TryAddSingleton<IEventFilterEvaluator, AgentCore.Infrastructure.Events.RestrictedEventFilter>();
        services.TryAddSingleton<ExternalEventIngress>();
        services.TryAddSingleton<CoreEventDispatcher>();
        services.TryAddSingleton<AutomationPresetCatalog>();
        services.TryAddSingleton<WebhookEventService>();
        services.TryAddSingleton<UnifiedEventCatalog>();
        services.TryAddSingleton<TriggerOccurrenceRouter>();
        services.TryAddSingleton<IAgentInstanceService>(provider => new AgentInstanceService(
            provider.GetRequiredService<IAgentInstanceStore>(),
            provider.GetRequiredService<IAgentDefinitionStore>(),
            provider.GetRequiredService<IIdGenerator>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ITriggerInstancePolicyReconciliationService>()));
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<IUserTurnCapabilityValidator, UserTurnCapabilityValidator>();
        services.TryAddSingleton<IOwnerCapabilityService, OwnerCapabilityService>();
        WebSearchProviderRegistration.AddPublicWeb(services, profile);
        EmailProviderRegistration.AddEmail(services, profile);
        AgentCore.Infrastructure.Drive.DriveRegistration.AddDrive(services, profile);
        AgentCore.Infrastructure.Credentials.TenantOAuthRegistration.Add(services, profile);
        services.TryAddSingleton(browser.PrivacyAuthority());
        services.TryAddSingleton<BrowserPrivacyService>();
        services.TryAddSingleton(sp => new NativePlaywrightBrowser(
            sp.GetRequiredService<BrowserOptions>(),
            sp.GetService<ILoggerFactory>(), timeProvider: sp.GetRequiredService<TimeProvider>(),
            privacy: sp.GetRequiredService<BrowserPrivacyService>(), diagnostics: sp.GetRequiredService<IDiagnosticIdSource>()));
        services.TryAddSingleton<IBrowser>(sp => sp.GetRequiredService<NativePlaywrightBrowser>());
        services.TryAddSingleton<IBrowserLease>(sp => sp.GetRequiredService<NativePlaywrightBrowser>());
        services.TryAddSingleton<IBrowserStateReader>(sp => sp.GetRequiredService<NativePlaywrightBrowser>());
        services.AddHostedService(sp => sp.GetRequiredService<NativePlaywrightBrowser>());
        services.TryAddSingleton<AgentInstanceSkillService>();
        services.TryAddSingleton<InstanceAutomationPolicy>();
        services.TryAddSingleton<AgentInstanceSettingsService>();
        services.TryAddSingleton<IStateIsolationConfigurationStore>(p => p.GetRequiredService<IAgentInstanceStore>() is IStateIsolationConfigurationStore sqlite
            ? sqlite : new InMemoryStateIsolationConfigurationStore((InMemoryAgentInstanceStore)p.GetRequiredService<IAgentInstanceStore>(), (InMemoryExperienceStore)p.GetRequiredService<IExperienceStore>()));
        services.TryAddSingleton<SandboxPolicyEnforcement>();
        services.TryAddSingleton<StateIsolationConfigurationService>();
        services.TryAddSingleton<InheritedDefinitionResourceCatalog>();
        services.TryAddSingleton<AgentInstanceResourceService>();
        services.TryAddSingleton<AgentCore.Application.Execution.AgentRunConfigurationResolver>();
        services.TryAddSingleton<IOfficeProcessor, AgentCore.Infrastructure.Office.OfficeProcessor>();
        services.TryAddSingleton<SessionToolExecutor>(provider => new SessionToolExecutor(
            provider.GetService<RoleKnowledgeService>(),
            provider.GetService<IAttachmentStore>(),
            provider.GetService<IAttachmentProcessor>(),
            provider.GetService<ISessionWorkspace>(),
            provider.GetService<IArtifactStore>(),
            provider.GetService<IWebSearchProvider>(),
            provider.GetService<IPublicWebFetcher>(),
            provider.GetService<IEmailProvider>(),
            provider.GetService<IHttpRequestClient>(),
            provider.GetRequiredService<IToolConfigurationGate>(),
            provider.GetRequiredService<IAutomationService>(),
            provider.GetRequiredService<ITriggerCommandAuthorizer>(),
            provider.GetRequiredService<IAgentInstanceStore>(),
            provider.GetRequiredService<IAgentDefinitionStore>(),
            provider.GetRequiredService<IMemoryStore>(),
            provider.GetService<IBrowser>(),
            provider.GetService<IAgentDefinitionResourceAdminStore>(),
            () => provider.GetRequiredService<HarnessManagementService>(),
            provider.GetRequiredService<AgentCore.Application.Experience.ExperienceService>(),
            provider.GetRequiredService<AgentCore.Application.Continuity.ContinuityService>(),
            provider.GetRequiredService<AgentCore.Application.Continuity.IdentityMaintenanceService>(),
            provider.GetRequiredService<AgentInstanceWorkspaceService>(),
            provider.GetRequiredService<CredentialService>(),
            provider.GetRequiredService<AdminAutomationAuthoringService>(),
            provider.GetRequiredService<AgentInstanceSkillService>(),
            provider.GetRequiredService<ICoreEventStore>(),
            provider.GetRequiredService<InheritedDefinitionResourceCatalog>(),
            provider.GetService<SelfManagementService>(), provider.GetService<AgentInstanceSettingsService>(), provider.GetService<AgentInstanceResourceService>(), provider.GetService<IAgentCredentialBindingStore>(), provider.GetService<AdminAgentInstanceService>(), provider.GetService<AgentCore.Application.Email.EmailAccountService>(), provider.GetService<IEmailStore>(), provider.GetService<IAgentRunStore>(), provider.GetService<ILogger<SessionToolExecutor>>(), provider.GetRequiredService<IDiagnosticIdSource>(), provider.GetRequiredService<IOfficeProcessor>(), provider.GetRequiredService<AgentCore.Application.Drive.DriveService>(), provider.GetRequiredService<ISandboxService>(), provider.GetRequiredService<ISandboxStore>(), provider.GetRequiredService<ISandboxImageAuthority>(), provider.GetRequiredService<ISandboxReadiness>(), provider.GetRequiredService<AgentCore.Application.Workspaces.SessionWorkingCleanupService>(), () => provider.GetService<RuntimeStateService>(), tenantAuthorization: provider.GetRequiredService<AgentCore.Application.Security.TenantToolAuthorization>()));
        services.TryAddSingleton(provider =>
        {
            var speech = provider.GetRequiredService<SpeechResolution>();
            return new SessionRuntimeFactory(
                provider.GetRequiredService<ILanguageModel>(),
                provider.GetRequiredService<IAgentBrain>(),
                provider.GetRequiredService<IMemoryStore>(),
                provider.GetRequiredService<IIdGenerator>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<IInterruptionClassifier>(),
                provider.GetRequiredService<InteractionPolicy>(),
                speech.Recognizer,
                speech.Synthesizer,
                provider.GetRequiredService<VoiceAvailability>(),
                provider.GetRequiredService<IAttachmentStore>(),
                provider.GetRequiredService<IAttachmentProcessor>(),
                provider.GetRequiredService<IArtifactReferenceAuthorizer>(),
                provider.GetRequiredService<SessionToolExecutor>(),
                provider.GetRequiredService<IAgentRunStore>(),
                provider.GetRequiredService<ILanguageModelResolver>(),
                provider.GetRequiredService<IModelCatalog>(),
                provider.GetRequiredService<IUserTurnCapabilityValidator>(),
                provider.GetRequiredService<IStructuredMemoryService>(),
                provider.GetRequiredService<IDiagnosticIdSource>(),
                provider.GetService<IBrowserLease>(), provider.GetRequiredService<IAgentRunAuthority>(),
                composer: provider.GetRequiredService<AgentCore.Application.Composer.ComposerReferenceService>(),
                tenantContinuations: provider.GetRequiredService<AgentCore.Application.Security.TenantRunContinuationService>());
        });
        services.TryAddSingleton<AgentCore.Application.Composer.ComposerReferenceService>();
        services.TryAddSingleton<IAgentRunAuthority, AgentCore.Application.Execution.AgentRunAuthority>();
        services.TryAddSingleton<AgentCore.Application.Execution.BackgroundOccurrenceIntake>();
        services.TryAddSingleton<AgentCore.Application.Execution.BackgroundCompletionReporter>();
        services.TryAddSingleton<AgentCore.Application.Execution.AgentRunCoordinator>();
        return services;
    }
}
