using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private static object ExperienceReviewCommand(JsonElement preview) => new {
        sourceKind = preview.GetProperty("sourceKind").GetString(), sourceId = preview.GetProperty("sourceId").GetGuid(),
        expectedRevision = preview.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = preview.GetProperty("instanceRevision").GetInt64(),
        sourceFingerprint = preview.GetProperty("sourceFingerprint").GetString() };
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_manual_review_actual_HTTP_provider_and_background_worker_keep_the_original_User(bool sqlite)
    {
        var model = new ExperienceModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, agent) = await OwnedMemoryAgent(owner, enabled: false); await MemoryExecutor(owner, tenant, agent, operations: true); var revision = await ExperienceOptIn(owner, tenant, agent, true);
        var source = await MemoryRequest(factory, owner, tenant, agent, instanceRevision: revision); model.Source = source.Run.AgentRunId;
        var api = $"/api/v2/tenants/{tenant}/agents/{agent}/experience";
        var preview = await Json(await owner.GetAsync(api + $"/review-source?sourceKind=AgentRun&sourceId={model.Source}"));
        Assert.False(preview.TryGetProperty("userPrincipalId", out _)); Assert.False(preview.TryGetProperty("accountId", out _)); Assert.False(preview.TryGetProperty("sourceWitnessJson", out _));
        var first = await Json(await owner.PostAsJsonAsync(api + "/checkpoints", ExperienceReviewCommand(preview))); Assert.True(first.GetProperty("created").GetBoolean());
        var id = first.GetProperty("runId").GetGuid(); var session = first.GetProperty("sessionId").GetGuid();
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); var completed = await ProfileTerminal(factory, tenant, id);
        Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code} {completed.Failure?.Summary}");
        Assert.Equal(AgentRunOutputContract.BackgroundOutcome, completed.Admission.OutputContract); Assert.Equal(ExecutionOrigin.UserMessage, completed.Admission.ExecutionContext!.Origin);
        Assert.Equal(session, completed.SessionId); Assert.NotNull(completed.Admission.ExecutionContext.OriginalInitiatorPrincipalId); Assert.Null(completed.Owner.ProfileIdOrNull);
        var background = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{agent}/activity/sessions/{session}/background"));
        Assert.DoesNotContain("selectedSource", background.GetProperty("initialRun").GetProperty("objective").GetString());
        Assert.Contains("Retain at most one", background.GetProperty("initialRun").GetProperty("objective").GetString());
        Assert.Equal(id, background.GetProperty("initialRun").GetProperty("runId").GetGuid()); Assert.Equal("completed", background.GetProperty("initialRun").GetProperty("status").GetString());
        var fact = Assert.Single((await Json(await owner.GetAsync(api))).GetProperty("items").EnumerateArray()); Assert.Equal("AgentUser", fact.GetProperty("scope").GetString()); Assert.Equal(source.Run.AgentRunId, fact.GetProperty("sourceId").GetGuid());
        Assert.Equal("PRIVATE_EXPERIENCE_EVIDENCE", fact.GetProperty("content").GetProperty("goal").GetString());
        var calls = model.Requests.Count; preview = await Json(await owner.GetAsync(api + $"/review-source?sourceKind=AgentRun&sourceId={model.Source}"));
        var duplicate = await Json(await owner.PostAsJsonAsync(api + "/checkpoints", ExperienceReviewCommand(preview))); Assert.False(duplicate.GetProperty("created").GetBoolean()); Assert.Equal(id, duplicate.GetProperty("runId").GetGuid());
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); Assert.Equal(calls, model.Requests.Count);
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_manual_review_HTTP_is_closed_and_requires_an_unchanged_inspected_source(bool sqlite)
    {
        var model = new ExperienceModel(); await using var factory = new RuntimeStateFactory(sqlite, model: model);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, agent) = await OwnedMemoryAgent(owner, enabled: false); await MemoryExecutor(owner, tenant, agent, operations: true); var revision = await ExperienceOptIn(owner, tenant, agent, true);
        var source = await MemoryRequest(factory, owner, tenant, agent, instanceRevision: revision); var api = $"/api/v2/tenants/{tenant}/agents/{agent}/experience";
        var query = $"sourceKind=AgentRun&sourceId={source.Run.AgentRunId}"; var preview = await Json(await owner.GetAsync(api + "/review-source?" + query));
        foreach (var bad in new[] { query + "&sourceKind=AgentRun", query + "&userId=caller", "sourceKind=Consolidation&sourceId=" + source.Run.AgentRunId, "sourceKind=Session&sourceId=invalid", "sourceKind=AgentRun" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(api + "/review-source?" + bad)).StatusCode);
        foreach (var body in new[] { "null", "[]", "{}", JsonSerializer.Serialize(ExperienceReviewCommand(preview)).TrimEnd('}') + ",\"userId\":\"caller\"}", JsonSerializer.Serialize(ExperienceReviewCommand(preview)).TrimEnd('}') + ",\"sourceKind\":\"AgentRun\"}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync(api + "/checkpoints", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
        var wrong = JsonSerializer.SerializeToNode(ExperienceReviewCommand(preview))!.AsObject(); wrong["sourceFingerprint"] = new string('0', 64);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + "/checkpoints", wrong)).StatusCode);
        var metadata = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{source.Session}"));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v2/tenants/{tenant}/sessions/{source.Session}?expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision={metadata.GetProperty("revision").GetInt64()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(api + "/checkpoints", ExperienceReviewCommand(preview))).StatusCode);
        Assert.Empty((await Json(await owner.GetAsync(api))).GetProperty("items").EnumerateArray());
    }
}
