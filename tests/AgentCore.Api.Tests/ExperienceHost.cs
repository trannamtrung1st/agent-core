using AgentCore.Application.Execution;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Work;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

internal sealed class ExperienceHost : DurableSqliteHostFactory
{
    private readonly string db;
    private readonly TimeProvider? clock;
    private readonly IExperienceStore? experienceStore;
    private readonly Action<IServiceCollection>? configure;
    private readonly Action<Microsoft.Extensions.Configuration.IConfigurationBuilder>? configuration;
    internal ExperienceHost(string db, ILanguageModel? languageModel = null, TimeProvider? clock = null, IExperienceStore? experienceStore = null, Action<IServiceCollection>? configure = null, Action<Microsoft.Extensions.Configuration.IConfigurationBuilder>? configuration = null) : base(db, runScheduler: false, languageModel: languageModel) { this.db = db; this.clock = clock; this.experienceStore = experienceStore; this.configure = configure; this.configuration = configuration; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (configuration is not null) builder.ConfigureAppConfiguration((_, c) => configuration(c));
        builder.ConfigureTestServices(services =>
        {
            foreach (var hosted in services.Where(s => s.ImplementationType is { } t &&
                (t == typeof(AgentRunHostedService) || t == typeof(TriggerSchedulerHostedService))).ToArray()) services.Remove(hosted);
            if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
            services.RemoveAll<IStructuredMemoryStore>(); services.AddSingleton<IStructuredMemoryStore, SqliteStructuredMemoryStore>();
            services.RemoveAll<IExperienceStore>(); services.AddSingleton<IExperienceStore, SqliteExperienceStore>();
            if (experienceStore is not null) { services.RemoveAll<IExperienceStore>(); services.AddSingleton(experienceStore); }
            configure?.Invoke(services);
            services.RemoveAll<IAdminLifecycleDeletion>(); services.AddSingleton<IAdminLifecycleDeletion, SqliteAdminLifecycleDeletion>();
            services.RemoveAll<IAdminEventStore>(); services.AddSingleton<IAdminEventStore, SqliteAdminEventStore>();
            services.RemoveAll<IAgentDefinitionAdminStore>(); services.AddSingleton<IAgentDefinitionAdminStore, SqliteAgentDefinitionAdminStore>();
            services.RemoveAll<IAgentDefinitionResourceAdminStore>(); services.AddSingleton<IAgentDefinitionResourceAdminStore, SqliteAgentDefinitionResourceAdminStore>();
            services.RemoveAll<IDefinitionResourceContentStore>(); services.AddSingleton<IDefinitionResourceContentStore>(new FileDefinitionResourceContentStore(db + ".resources"));
            services.RemoveAll<IDefinitionDraftEvaluationStore>(); services.AddSingleton<IDefinitionDraftEvaluationStore, SqliteDefinitionDraftEvaluationStore>();
        });
    }
}
