using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Fact]
    public async Task Canonical_pending_request_survives_embedded_approval_schema_retirement_and_resumes_once()
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f);
        var service = ApprovalService(f);
        var request = (await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId),
            setup.Agent.Instance.Persona with { Name = "Canonical migration consent" }, setup.Run.Revision)).Request!;
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261023140000_TenantConfigurationRemedyAuthoring");
            await migrator.MigrateAsync();
        }
        var reopened = new SqliteIamStore(f.Contexts!, clock: f.Clock);
        var runs = (ITenantRunStore)reopened; service = ApprovalService(f, reopened);
        var waiting = (await runs.FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Equal(request.RequestId, waiting.ApprovalRequestId);
        Assert.Equal(request, await service.ReadAsync(setup.Tenant, request.RequestId, setup.Owner));
        await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash,
            (await reopened.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, request.Revision);
        Assert.Equal(1, await service.DispatchPendingAsync());
        Assert.Equal(0, await service.DispatchPendingAsync());
        var resumed = (await runs.FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Queued, resumed.Status); Assert.Equal(request.RequestId, resumed.ApprovalRequestId);
        Assert.Equal(setup.Agent.Instance.Persona, (await ((ITenantAgentStore)reopened).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance.Persona);
    }
}
