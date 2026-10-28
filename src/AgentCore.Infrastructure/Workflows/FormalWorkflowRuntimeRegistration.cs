using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using Elsa.Extensions;
using Elsa.Features;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure.Workflows;

internal sealed record FormalWorkflowRuntimeStorage(bool Durable);

public static class FormalWorkflowRuntimeRegistration
{
    internal static IServiceCollection AddFormalWorkflowRuntime(this IServiceCollection services, PersistenceOptions options)
    {
        if (!options.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) && !options.Provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Formal Workflows supports only the configured SQLite or InMemory store.");
        services.TryAddSingleton(new FormalWorkflowRuntimeStorage(options.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)));
        services.TryAddSingleton(new SqlitePragmaInterceptor(Math.Max(1, options.BusyTimeoutMs)));
        services.AddElsa(elsa => {
            elsa.Configure<ElsaFeature>(feature => feature.DisableAutomaticActivityRegistration = true);
            elsa.AddActivity<CoreWorkflowStep>();
            elsa.AddActivity<Flowchart>();
            if (options.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                elsa.UseWorkflowManagement(management => management.UseEntityFrameworkCore(ef => {
                    ef.UseSqlite(options.ConnectionString, new ElsaDbContextOptions { MigrationsHistoryTableName = "__FormalWorkflowManagementMigrations" }); ef.RunMigrations = false;
                    var configure = ef.DbContextOptionsBuilder;
                    ef.DbContextOptionsBuilder = (sp, builder) => { configure(sp, builder); builder.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()); };
                }));
                elsa.UseWorkflowRuntime(runtime => runtime.UseEntityFrameworkCore(ef => {
                    ef.UseSqlite(options.ConnectionString, new ElsaDbContextOptions { MigrationsHistoryTableName = "__FormalWorkflowRuntimeMigrations" }); ef.RunMigrations = false;
                    var configure = ef.DbContextOptionsBuilder;
                    ef.DbContextOptionsBuilder = (sp, builder) => { configure(sp, builder); builder.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()); };
                }));
            }
        });
        services.RemoveAll<IWorkflowMaterializer>();
        services.AddScoped<IWorkflowMaterializer, CoreWorkflowMaterializer>();
        services.TryAddSingleton<IFormalWorkflowRuntime, ElsaFormalWorkflowRuntime>();
        return services;
    }

    public static async Task InitializeFormalWorkflowRuntimeAsync(this IServiceProvider services, bool freshDatabase, CancellationToken ct = default)
    {
        var storage = services.GetRequiredService<FormalWorkflowRuntimeStorage>();
        if (storage.Durable && freshDatabase) await services.MigrateFormalWorkflowRuntimeAsync(ct);
        if (!await services.GetRequiredService<IFormalWorkflowRuntime>().IsReadyAsync(ct))
            throw new InvalidOperationException(FormalWorkflowCutover.Recovery);
        await services.PopulateFormalWorkflowActivitiesAsync(ct);
    }

    // Explicit offline deployment operation. Never call migration from workflow start or a request handler.
    internal static async Task MigrateFormalWorkflowRuntimeAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope(); var sp = scope.ServiceProvider;
        await using (var management = await sp.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>().CreateDbContextAsync(ct))
            await management.Database.MigrateAsync(ct);
        await using (var runtime = await sp.GetRequiredService<IDbContextFactory<RuntimeElsaDbContext>>().CreateDbContextAsync(ct))
            await runtime.Database.MigrateAsync(ct);
    }
    internal static async Task PopulateFormalWorkflowActivitiesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync(ct);
    }
}
