using AgentCore.Application.Ports;
using AgentCore.Application.Evaluations;
using AgentCore.Domain.Evaluations;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Fact]
    public async Task Evaluation_sandbox_migration_preserves_legacy_approvals_versions_and_guards()
    {
        await using var f = await Fixture.Create(true);
        await using var db = await f.Contexts!.CreateDbContextAsync();
        var setup = await RuntimeCredential(f, true); var service = RuntimeApprovals(f);
        var legacy = (await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Preserved exact review" },
            setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var script = EvaluationRules.Freeze(new ScriptVersion(setup.Tenant, f.Ids.NewId(), 1, "Historical predicate", ScriptKind.Assertion,
            EvaluationPredicate.RunCompleted, null, null, [EvaluationHook.Manual], "", setup.Owner.PrincipalId, f.Clock.GetUtcNow()));
        var catalog = (ITenantEvaluationStore)f.Iam;
        Assert.True(await catalog.TrySaveScriptAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, script, 0,
            f.Audit(setup.Tenant, setup.Owner.PrincipalId, script.ScriptId), setup.Owner));
        async Task<Dictionary<string, string>> Guards()
        {
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type IN ('trigger','index') AND sql IS NOT NULL ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync(); var result = new Dictionary<string, string>();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0), reader.GetString(1));
            return result;
        }
        // Seed through the current store before selecting the historical schema. No
        // current-model INSERT is valid against a predecessor with fewer columns.
        var approval = await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, legacy.RequestId);
        var historical = await catalog.FindScriptAsync(setup.Tenant, script.ScriptId, 1);
        await db.GetService<IMigrator>().MigrateAsync("20261027010000_UnifiedEvaluations");
        var before = await Guards();
        await db.GetService<IMigrator>().MigrateAsync("20261027020000_EvaluationSandboxExecutions");
        var after = await Guards();
        var changed = new HashSet<string> { "TR_TenantApprovals_IndependentTask_Insert", "TR_TenantApprovals_IndependentTask_Update", "TR_TenantApprovals_Space_INSERT", "TR_TenantApprovals_Space_Workspace_INSERT" };
        foreach (var pair in before)
        {
            Assert.True(after.ContainsKey(pair.Key), pair.Key);
            if (!changed.Contains(pair.Key)) Assert.Equal(pair.Value, after[pair.Key]);
            else Assert.Contains("SandboxCommand.Evaluation", after[pair.Key]);
        }
        // Later migrations intentionally extend Task/Core guards. Verify the native
        // extension alone above, then preserve the same ordinary records through HEAD.
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(approval, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, legacy.RequestId));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(historical), System.Text.Json.JsonSerializer.Serialize(await catalog.FindScriptAsync(setup.Tenant, script.ScriptId, 1)));
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        { command.CommandText = "PRAGMA foreign_key_check"; await using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync()); }
        // A native marker cannot turn an arbitrary historical action into independent authority.
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.SandboxCommand',json_object('Evaluation',json_object()))"));
        Assert.Contains("Native evaluation approval identity is immutable", error.Message);
        // Legacy decision and its original signal still function after migration.
        await service.DecideAsync(setup.Tenant, legacy.RequestId, setup.Owner, true, legacy.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, legacy.Revision);
        Assert.Equal(1, await service.DispatchPendingAsync());
    }
}
