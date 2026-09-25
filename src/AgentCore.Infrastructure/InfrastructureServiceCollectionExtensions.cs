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
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.PublicWeb;
using AgentCore.Infrastructure.Tools;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
        SpeechProvidersOptions? speech = null)
    {
        persistence ??= new PersistenceOptions();
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
            provider.GetService<IEmailProvider>()));
        services.TryAddSingleton<PromptContextBuilder>(provider =>
            new PromptContextBuilder(provider.GetRequiredService<IToolConfigurationGate>()));
        services.TryAddSingleton<IInitiativeEvaluator>(provider =>
            new DefaultInitiativeEvaluator(
                provider.GetRequiredService<PromptContextBuilder>(),
                LanguageModelFactory.Create(provider, profile, languageModel)));
        services.TryAddSingleton<IAgentBrain>(provider =>
            new DefaultAgentBrain(
                provider.GetRequiredService<PromptContextBuilder>(),
                provider.GetRequiredService<IInitiativeEvaluator>()));
        services.TryAddSingleton<DurableWorkContextFactory>();
        services.TryAddSingleton<WorkCancellationRegistry>();
        services.TryAddSingleton<DurableReminderExecutor>();
        services.TryAddSingleton<DurableWorkIntake>();
        services.TryAddSingleton<IInterruptionClassifier, HeuristicInterruptionClassifier>();
        services.TryAddSingleton<IIdGenerator, SystemIdGenerator>();
        if (string.Equals(persistence.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(new SqlitePragmaInterceptor(Math.Max(1, persistence.BusyTimeoutMs)));
            services.AddDbContextFactory<AgentCoreDbContext>((provider, options) =>
            {
                options.UseSqlite(persistence.ConnectionString);
                options.AddInterceptors(provider.GetRequiredService<SqlitePragmaInterceptor>());
            });
            services.AddSingleton<IMemoryStore>(provider => new SqliteMemoryStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IStructuredMemoryStore>(provider => new SqliteStructuredMemoryStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
            services.AddSingleton<IAgentInstanceStore>(provider => new SqliteAgentInstanceStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
            services.AddSingleton<ITriggerStore>(provider => new SqliteTriggerStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
            services.AddSingleton<IWorkItemStore>(provider => new SqliteWorkItemStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
            services.AddSingleton<IDurableWorkHandoff>(provider => new SqliteDurableWorkHandoff(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));
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
                    provider.GetRequiredService<IIdGenerator>()));
            services.TryAddSingleton<IOwnerCapabilityStore, SqliteOwnerCapabilityStore>();
            services.TryAddSingleton<IAttachmentStore>(provider => new SqliteAttachmentStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>(),
                persistence.AttachmentRoot));
        }
        else
        {
            services.TryAddSingleton<IMemoryStore, InMemoryMemoryStore>();
            services.TryAddSingleton<IStructuredMemoryStore, InMemoryStructuredMemoryStore>();
            services.TryAddSingleton<IAgentInstanceStore, InMemoryAgentInstanceStore>();
            services.TryAddSingleton<InMemoryDurableState>();
            services.TryAddSingleton<ITriggerStore>(provider =>
                new InMemoryTriggerStore(provider.GetRequiredService<InMemoryDurableState>()));
            services.TryAddSingleton<IWorkItemStore>(provider =>
                new InMemoryWorkItemStore(provider.GetRequiredService<InMemoryDurableState>()));
            services.TryAddSingleton<IDurableWorkHandoff>(provider =>
                new InMemoryDurableWorkHandoff(provider.GetRequiredService<InMemoryDurableState>()));
            services.TryAddSingleton<IOwnerCapabilityStore, InMemoryOwnerCapabilityStore>();
            services.TryAddSingleton<IAttachmentStore>(provider =>
                new InMemoryAttachmentStore(provider.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<InMemoryDefinitionResourceContentStore>();
            services.TryAddSingleton<IDefinitionResourceContentStore>(provider =>
                provider.GetRequiredService<InMemoryDefinitionResourceContentStore>());
            services.TryAddSingleton<InMemoryAgentDefinitionResourceAdminStore>();
            services.TryAddSingleton<IAgentDefinitionResourceAdminStore>(provider =>
                provider.GetRequiredService<InMemoryAgentDefinitionResourceAdminStore>());
            services.TryAddSingleton<InMemoryAgentDefinitionAdminStore>();
            services.TryAddSingleton<IAgentDefinitionAdminStore>(provider =>
            {
                var admin = provider.GetRequiredService<InMemoryAgentDefinitionAdminStore>();
                admin.ResourceStore = provider.GetRequiredService<InMemoryAgentDefinitionResourceAdminStore>();
                return admin;
            });
            services.TryAddSingleton<IDefinitionDraftEvaluationStore>(provider =>
                new InMemoryDefinitionDraftEvaluationStore(provider.GetRequiredService<IAgentDefinitionAdminStore>()));
        }
        services.TryAddSingleton<IAttachmentProcessor, AttachmentProcessor>();
        services.TryAddSingleton<DefinitionPublicationResourceReader>();
        services.TryAddSingleton<ISessionWorkspace>(provider => new FileSessionWorkspace(
            persistence.WorkspaceRoot,
            persistence.TemplateRoot,
            provider.GetService<IAttachmentStore>(),
            publicationResources: provider.GetService<DefinitionPublicationResourceReader>()));
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
        });
        services.AddHttpClient(OpenAICompatibleBatchSpeechRecognizer.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
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
        });
        services.TryAddSingleton<ILanguageModel>(provider =>
            LanguageModelFactory.Create(provider, profile, languageModel));
        services.TryAddSingleton<IApprovedKnowledgeCatalog>(provider =>
            new FileApprovedKnowledgeCatalog(agentDirectory));
        services.TryAddSingleton<RoleKnowledgeService>();
        services.TryAddSingleton(provider =>
            new FileAgentDefinitionStore(agentDirectory, provider.GetRequiredService<ProviderAliasSet>()));
        services.TryAddSingleton<IBuiltInAgentDefinitionStore>(provider =>
            provider.GetRequiredService<FileAgentDefinitionStore>());
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
        services.TryAddSingleton<HeuristicTriggerCommandAuthorizer>();
        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<ITriggerCommandAuthorizer>(static provider =>
                provider.GetRequiredService<HeuristicTriggerCommandAuthorizer>());
        }
        else
        {
            services.TryAddSingleton<ModelTriggerCommandAuthorizer>(static provider =>
                new ModelTriggerCommandAuthorizer(
                    provider.GetRequiredService<ILanguageModel>(),
                    provider.GetRequiredService<HeuristicTriggerCommandAuthorizer>()));
            services.TryAddSingleton<ITriggerCommandAuthorizer>(static provider =>
                provider.GetRequiredService<ModelTriggerCommandAuthorizer>());
        }
        services.TryAddSingleton<ITriggerRegistrationService, TriggerRegistrationService>();
        services.TryAddSingleton<TriggerScheduler>();
        services.TryAddSingleton<ITriggerAdmissionGuard, TriggerAdmissionGuard>();
        services.TryAddSingleton<ITriggerPolicyRecoveryService, TriggerPolicyRecoveryService>();
        services.TryAddSingleton<ITriggerInstancePolicyReconciliationService, TriggerInstancePolicyReconciliationService>();
        services.TryAddSingleton<IDurableApplicationEventIngress, DurableOrderEventIngress>();
        services.TryAddSingleton<TriggerOccurrenceRouter>();
        services.TryAddSingleton<IAgentInstanceService>(provider => new AgentInstanceService(
            provider.GetRequiredService<IAgentInstanceStore>(),
            provider.GetRequiredService<IAgentDefinitionStore>(),
            provider.GetRequiredService<IMemoryStore>(),
            provider.GetRequiredService<IIdGenerator>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ITriggerInstancePolicyReconciliationService>()));
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<IUserTurnCapabilityValidator, UserTurnCapabilityValidator>();
        services.TryAddSingleton<IOwnerCapabilityService, OwnerCapabilityService>();
        WebSearchProviderRegistration.AddPublicWeb(services, profile);
        EmailProviderRegistration.AddEmail(services, profile);
        services.TryAddSingleton<SessionToolExecutor>(provider => new SessionToolExecutor(
            provider.GetService<RoleKnowledgeService>(),
            provider.GetService<IAttachmentStore>(),
            provider.GetService<IAttachmentProcessor>(),
            provider.GetService<ISessionWorkspace>(),
            provider.GetService<IArtifactStore>(),
            provider.GetService<ISandboxExecutor>(),
            provider.GetService<IWebSearchProvider>(),
            provider.GetService<IPublicWebFetcher>(),
            provider.GetService<IEmailProvider>(),
            provider.GetService<IHttpRequestClient>(),
            provider.GetRequiredService<IToolConfigurationGate>(),
            provider.GetRequiredService<ITriggerRegistrationService>(),
            provider.GetRequiredService<ITriggerCommandAuthorizer>(),
            provider.GetRequiredService<IAgentInstanceStore>(),
            provider.GetRequiredService<IAgentDefinitionStore>(),
            provider.GetRequiredService<IMemoryStore>()));
        services.TryAddSingleton<ISandboxExecutor>(provider =>
            new DockerSandboxExecutor(
                provider.GetRequiredService<ISessionWorkspace>(),
                provider.GetService<IArtifactStore>()));
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
                provider.GetRequiredService<ILanguageModelResolver>(),
                provider.GetRequiredService<IModelCatalog>(),
                provider.GetRequiredService<IUserTurnCapabilityValidator>(),
                provider.GetRequiredService<IStructuredMemoryService>());
        });
        return services;
    }
}
