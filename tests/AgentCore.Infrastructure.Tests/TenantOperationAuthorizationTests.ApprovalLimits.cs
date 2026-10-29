using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, "tenant")] [InlineData(true, "tenant")]
    [InlineData(false, "executor")] [InlineData(true, "executor")]
    [InlineData(false, "requester")] [InlineData(true, "requester")]
    public async Task Independent_approval_storm_limits_are_scoped_and_atomic_without_relying_on_operation_or_root_dedupe(bool sqlite, string dimension)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var agents = new List<TenantAgent> { setup.Agent }; var humans = new List<TenantSessionInitiator> { setup.Owner };
        var agentCount = dimension == "tenant" ? 5 : dimension == "requester" ? 2 : 1;
        var requesterCount = dimension == "tenant" ? 5 : dimension == "executor" ? 2 : 1;
        for (var i = 1; i < agentCount; i++) agents.Add(await f.Agents.CreateAsync(setup.Owner.PrincipalId, setup.Tenant, "examiner", 1, await Revision()));
        for (var i = 1; i < requesterCount; i++) humans.Add(await User(f, setup, $"requester{i}@example.test", ["agents.read", "agents.chat", "sessions.create", "sessions.read"]));
        var sessions = new Dictionary<(int, int), (Guid Id, long Revision)>();
        var limit = dimension == "tenant" ? TenantApprovalLimits.Tenant : dimension == "executor" ? TenantApprovalLimits.Executor : TenantApprovalLimits.Requester;
        var service = ApprovalService(f); var requests = new List<TenantApprovalRequest>();
        for (var i = 0; i <= limit; i++)
        {
            var ai = i % agentCount; var hi = (i / agentCount) % requesterCount;
            var agent = agents[ai]; var human = humans[hi];
            if (!sessions.TryGetValue((ai, hi), out var session))
            {
                var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(setup.Tenant, agent.Instance.InstanceId))!;
                await GrantExactSpaceRead(f, setup, agent.PrincipalId, space.SpaceId);
                await GrantExactSpaceRead(f, setup, human.PrincipalId, space.SpaceId);
                var created = await f.Sessions.CreateAsync(setup.Tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
                session = (created.Owner.SessionId, 1);
            }
            var admission = await f.RunService().SubmitTextAsync(setup.Tenant, session.Id, human, f.Ids.NewId(), "Independent requested change", await Revision(), session.Revision);
            sessions[(ai, hi)] = (session.Id, session.Revision + 1);
            var revision = await Revision();
            var result = await service.PreparePersonaAsync(new(setup.Tenant, admission.RunId), agent.Instance.Persona with { Name = "Distinct requested action " + i }, 1);
            if (i < limit) { Assert.Equal("approval_pending", result.ReasonCode); requests.Add(result.Request!); }
            else
            {
                Assert.Equal(OperationDecisionKind.Denied, result.Decision); Assert.Equal("approval_rate_limited", result.ReasonCode); Assert.Null(result.Request);
                Assert.Equal(revision, await Revision());
                var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, admission.RunId))!;
                Assert.Equal(AgentRunStatus.Queued, run.Status); Assert.Equal(1, run.Revision); Assert.Null(run.ApprovalRequestId);
            }
        }
        Assert.Equal(limit, requests.Count);
        // Duplicate resolution precedes quotas, including at the exact limit.
        var first = requests[0]; var before = await Revision();
        var duplicate = await service.PreparePersonaAsync(new(setup.Tenant, first.RunId), first.Operation.Persona!, 1);
        Assert.Equal("already_pending", duplicate.ReasonCode); Assert.Equal(first.RequestId, duplicate.Request!.RequestId); Assert.Equal(before, await Revision());
    }
}
