using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Fact]
    public async Task Local_effect_receipt_schema_binds_exact_operation_hash_executor_and_root_without_weakening_the_saved_result()
    {
        await using var f = await Fixture.Create(true); var setup = await EffectSetup(f); var request = setup.Request;
        var receipt = await Effects(f).ExecutePersonaAsync(new(request.TenantId, setup.Run.AgentRunId), request.RequestId,
            request.Operation.OperationId, setup.Generation, request.Operation.ActionHash);
        foreach (var boundary in new[] { "hash", "executor", "root" })
        {
            await using var db = await f.Contexts!.CreateDbContextAsync();
            var row = await db.Set<TenantOperationReceiptRecord>().SingleAsync();
            var forged = boundary switch { "hash" => row with { ActionHash = new string('0', 64) },
                "executor" => row with { ExecutorPrincipalId = setup.Environment.Owner.PrincipalId }, _ => row with { RootActivationId = f.Ids.NewId().ToString("D") } };
            db.Entry(row).CurrentValues.SetValues(forged);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(787, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
        }
        Assert.Equal(receipt, await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId));
        Assert.Equal(request.Operation.Persona, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId))!.Instance.Persona);
    }
}
