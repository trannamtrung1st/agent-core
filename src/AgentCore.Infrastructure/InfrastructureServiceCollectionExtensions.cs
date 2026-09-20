using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
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
        services.TryAddSingleton<PromptContextBuilder>();
        services.TryAddSingleton<IInitiativeEvaluator>(provider =>
            new DefaultInitiativeEvaluator(
                provider.GetRequiredService<PromptContextBuilder>(),
                LanguageModelFactory.Create(provider, profile, languageModel)));
        services.TryAddSingleton<IAgentBrain>(provider =>
            new DefaultAgentBrain(
                provider.GetRequiredService<PromptContextBuilder>(),
                provider.GetRequiredService<IInitiativeEvaluator>()));
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
            services.TryAddSingleton<IOwnerCapabilityStore, SqliteOwnerCapabilityStore>();
            services.TryAddSingleton<IAttachmentStore>(provider => new SqliteAttachmentStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<TimeProvider>(),
                persistence.AttachmentRoot));
        }
        else
        {
            services.TryAddSingleton<IMemoryStore, InMemoryMemoryStore>();
            services.TryAddSingleton<IOwnerCapabilityStore, InMemoryOwnerCapabilityStore>();
            services.TryAddSingleton<IAttachmentStore>(provider =>
                new InMemoryAttachmentStore(provider.GetRequiredService<TimeProvider>()));
        }
        services.TryAddSingleton<IAttachmentProcessor, AttachmentProcessor>();
        services.TryAddSingleton<ISessionWorkspace>(provider => new FileSessionWorkspace(
            persistence.WorkspaceRoot,
            persistence.TemplateRoot,
            provider.GetService<IAttachmentStore>()));
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
        services.TryAddSingleton<IAgentDefinitionStore>(provider =>
            new FileAgentDefinitionStore(agentDirectory, provider.GetRequiredService<ProviderAliasSet>()));
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
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<IUserTurnCapabilityValidator, UserTurnCapabilityValidator>();
        services.TryAddSingleton<IOwnerCapabilityService, OwnerCapabilityService>();
        services.TryAddSingleton<SessionToolExecutor>(provider => new SessionToolExecutor(
            provider.GetService<RoleKnowledgeService>(),
            provider.GetService<IAttachmentStore>(),
            provider.GetService<IAttachmentProcessor>(),
            provider.GetService<ISessionWorkspace>(),
            provider.GetService<IArtifactStore>(),
            provider.GetService<ISandboxExecutor>()));
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
                provider.GetRequiredService<IUserTurnCapabilityValidator>());
        });
        return services;
    }
}
