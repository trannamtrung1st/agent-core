using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class HarnessManagementTests
{
    [Fact]
    public async Task Managed_preparation_owner_approval_promotion_freeze_and_stale_grants()
    {
        await using var factory = new AgentCoreApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 7);
        var id = instance.InstanceId;
        var prefix = $"/api/v2/admin/agent-instances/{id}/harness";
        var configured = await client.PutAsJsonAsync(prefix + "/policy", new HarnessPolicyRequest(instance.Revision, "Managed",
            ["KnowledgeResources", "Skills", "ToolSelection"], ["knowledge:support-order-policy"], ["web.fetch"]));
        configured.EnsureSuccessStatusCode();
        var review = (await configured.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        var prepared = await client.PostAsJsonAsync(prefix + "/prepare", new HarnessStartRequest(review.InstanceRevision, "Prepare an operations review harness."));
        prepared.EnsureSuccessStatusCode();
        var started = (await prepared.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        prepared = await client.PostAsJsonAsync(prefix + "/continue", new HarnessRunRequest(started.Preparation!.PreparationId));
        var body = await prepared.Content.ReadAsStringAsync();
        Assert.True(prepared.IsSuccessStatusCode, body);
        review = (await prepared.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        Assert.True(review.Preparation!.Status == "AwaitingApproval", body);
        Assert.Contains(review.Knowledge, k => k.Identity == "preparation-reference");
        Assert.Contains(review.Skills, s => s.Id == "operations.review");
        Assert.Equal(7, review.ActiveVersion);
        var approval = Assert.Single(review.Preparation.Approvals);
        var wrongHash = await client.PostAsJsonAsync(prefix + $"/approvals/{approval.ApprovalId}", new HarnessApprovalRequest(review.InstanceRevision, "changed", true));
        Assert.Equal(HttpStatusCode.Conflict, wrongHash.StatusCode);
        var decision = await client.PostAsJsonAsync(prefix + $"/approvals/{approval.ApprovalId}", new HarnessApprovalRequest(review.InstanceRevision, approval.ActionHash, true));
        decision.EnsureSuccessStatusCode();
        var continued = await client.PostAsJsonAsync(prefix + "/continue", new HarnessRunRequest(review.Preparation.PreparationId));
        Assert.True(continued.IsSuccessStatusCode, await continued.Content.ReadAsStringAsync());
        review = (await continued.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        Assert.Equal("Ready", review.Preparation!.Status);
        Assert.Contains(review.Preparation.Evidence, e => e.Actor == "Agent" && e.Limitation is not null);
        Assert.Contains(review.Preparation.Evidence, e => e.Actor == "Core" && e.Check == "Candidate knowledge readback" && e.Status == "Verified");
        Assert.Contains(review.Preparation.Evidence, e => e.Actor == "Core" && e.Check == "Candidate Skill activation" && e.Status == "Verified");
        Assert.Contains(review.Preparation.Evidence, e => e.Actor == "Agent" && e.Check == "Safe representative Skill sample" && e.Observed.Contains("owner attention"));
        var promoted = await client.PostAsJsonAsync(prefix + "/publish-adopt", new HarnessPromotionRequest(review.InstanceRevision, review.DraftRevision!.Value));
        Assert.True(promoted.IsSuccessStatusCode, await promoted.Content.ReadAsStringAsync());
        review = (await promoted.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        Assert.True(review.ActiveVersion > 7);
        var cancelledPublication = await client.PostAsJsonAsync(prefix + "/cancel", new HarnessRevisionRequest(review.InstanceRevision));
        Assert.Equal(HttpStatusCode.Conflict, cancelledPublication.StatusCode);
        var preserved = await client.GetFromJsonAsync<HarnessReviewResponse>(prefix);
        Assert.Equal("Published", preserved!.Preparation!.Status);
        Assert.Equal(review.InstanceRevision, preserved.InstanceRevision);
        Assert.Equal(review.ActiveVersion, preserved.ActiveVersion);
        var freeze = await client.PutAsJsonAsync(prefix + "/policy", new HarnessPolicyRequest(review.InstanceRevision, "Disabled", [], [], [], Frozen: true));
        freeze.EnsureSuccessStatusCode();
        var frozen = (await freeze.Content.ReadFromJsonAsync<HarnessReviewResponse>())!;
        Assert.Equal(review.ActiveVersion, frozen.ActiveVersion);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.RequestOperationAsync(id,
            Guid.Parse(review.Preparation!.PreparationId), new("instructions.update", review.DraftRevision!.Value, Content: "stale edit")));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(prefix + "/prepare", new HarnessStartRequest(frozen.InstanceRevision, "Try again"))).StatusCode);
        Assert.NotEmpty(await factory.Services.GetRequiredService<IAdminEventStore>().ListAsync(new(TargetId: id.ToString("D"))));
    }
}
