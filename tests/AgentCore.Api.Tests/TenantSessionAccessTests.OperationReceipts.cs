using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Application.Context;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class ApprovalExecutionFactory : AgentCoreApiFactory
    {
        protected override bool EnableTenantRunExecution => true;
        public bool Enabled;
        public ApprovalRecordingModel Model { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ILanguageModel>(Model));
                services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new ApprovalExecutionGate(this,
                    new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(),
                        p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
            });
        }
    }
    private sealed class ApprovalExecutionGate(ApprovalExecutionFactory fixture, ITenantRunExecution inner) : ITenantRunExecution
    {
        public ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default) =>
            Volatile.Read(ref fixture.Enabled) ? inner.ExecuteRunnableAsync(limit, ct) : ValueTask.FromResult(0);
    }
    private sealed class ApprovalRecordingModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel _inner = new();
        public AgentCore.Domain.Definitions.AgentIdentity? ProposedPersona { get; set; }
        public ModelToolCall? ProposedBrowserRead { get; set; }
        public int BrowserReadRepeats { get; set; } = 1;
        public string? ProposedBrowserCommand { get; set; }
        public string? ProposedMetadataRead { get; set; }
        public string? ProposedPreferences { get; set; }
        public string? ProposedArtifact { get; set; }
        public bool EmitArtifactReference { get; set; }
        public string? ProposedArtifactAuthorization { get; set; }
        public string? ProposedPreferenceAuthorization { get; set; }
        public TaskCompletionSource? ApprovalResponseGate { get; set; }
        public TaskCompletionSource ApprovalResponseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public Func<ModelRequest, CancellationToken, Task>? BeforeGenerate { get; set; }
        public ModelCapabilities Capabilities => _inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (BeforeGenerate is { } beforeGenerate) await beforeGenerate(request, cancellationToken);
            if (ApprovalResponseGate is { } gate && request.Context?.Sections.Any(s => s.Id == ContextSectionId.ApprovalOutcome) == true)
            { ApprovalResponseEntered.TrySetResult(); await gate.Task.WaitAsync(cancellationToken); }
            if (ProposedBrowserRead is { } browserRead && request.Messages.Count(m => m.Role == ModelRole.Tool && m.Name == browserRead.Name) is var reads && reads < BrowserReadRepeats
                && request.Tools?.Any(t => t.Name == browserRead.Name) == true)
            {
                yield return new ModelToolCallEvent(browserRead with { Id = "direct-browser-read-" + reads });
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (ProposedMetadataRead is { } metadataRead && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization)
                && request.Tools?.Any(t => t.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("inspect_credential_metadata")) == true)
            {
                yield return new ModelToolCallEvent(new("reviewed-metadata-read", AgentCore.Application.Tools.ToolCatalog.RequestAuthorization, metadataRead));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (ProposedBrowserCommand is { } browserCommand && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization)
                && request.Tools?.Any(t => t.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("run_browser_command")) == true)
            {
                yield return new ModelToolCallEvent(new("reviewed-browser-command", AgentCore.Application.Tools.ToolCatalog.RequestAuthorization, browserCommand));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (ProposedPreferenceAuthorization is { } authorizedPreferences && request.Context?.Sections.All(s => s.Id != ContextSectionId.ApprovalOutcome) == true
                && request.Tools?.Any(t => t.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization) == true)
            {
                using var parameters = System.Text.Json.JsonDocument.Parse(authorizedPreferences);
                yield return new ModelToolCallEvent(new("durable-preference-request", AgentCore.Application.Tools.ToolCatalog.RequestAuthorization,
                    System.Text.Json.JsonSerializer.Serialize(new { operation = "update_preferences", preferences = parameters.RootElement })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (ProposedArtifactAuthorization is { } publication && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization)
                && request.Tools?.Any(t => t.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("publish_artifact")) == true)
            {
                yield return new ModelToolCallEvent(new("reviewed-artifact", AgentCore.Application.Tools.ToolCatalog.RequestAuthorization, publication));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (EmitArtifactReference && (ProposedArtifactAuthorization is not null || ProposedArtifact is not null))
            {
                var result = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == (ProposedArtifactAuthorization is null
                    ? AgentCore.Application.Tools.ToolCatalog.ArtifactsCreate : AgentCore.Application.Tools.ToolCatalog.RequestAuthorization));
                if (result is not null)
                {
                    using var parsed = System.Text.Json.JsonDocument.Parse(result.Text);
                    if (parsed.RootElement.TryGetProperty("operationId", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.TryGetGuid(out var id)
                        && parsed.RootElement.TryGetProperty("applied", out var applied) && applied.ValueKind == System.Text.Json.JsonValueKind.True)
                    {
                        yield return new ModelSemanticResponseReady(new("The file was published.", new(ModelSpeechMode.None, null), [new(ModelResponseBlockKind.ArtifactReference, ArtifactId: id.ToString("N").ToUpperInvariant())]));
                        yield return new ModelCompleted(ModelStopReason.Completed); yield break;
                    }
                }
            }
            if (ProposedArtifactAuthorization is not null)
            {
                yield return new ModelTextDelta("The saved file publication decision was processed. Its receipt records the outcome.");
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (ProposedArtifact is { } artifact && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == AgentCore.Application.Tools.ToolCatalog.ArtifactsCreate)
                && request.Tools?.Any(t => t.Name == AgentCore.Application.Tools.ToolCatalog.ArtifactsCreate) == true)
            {
                yield return new ModelToolCallEvent(new("direct-artifact", AgentCore.Application.Tools.ToolCatalog.ArtifactsCreate, artifact));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (ProposedArtifact is not null)
            {
                yield return new ModelTextDelta("Artifact publication was processed; its receipt records the outcome.");
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (ProposedPreferences is { } preferences && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == AgentCore.Application.Tools.TenantPreferenceToolContract.Name)
                && request.Tools?.Any(t => t.Name == AgentCore.Application.Tools.TenantPreferenceToolContract.Name) == true)
            {
                yield return new ModelToolCallEvent(new("direct-preferences", AgentCore.Application.Tools.TenantPreferenceToolContract.Name, preferences));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (ProposedPersona is { } proposed && request.Context?.Sections.All(s => s.Id != ContextSectionId.ApprovalOutcome) == true
                && request.Tools?.Any(t => t.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization) == true)
            {
                yield return new ModelToolCallEvent(new("durable-persona-request", AgentCore.Application.Tools.ToolCatalog.RequestAuthorization,
                    System.Text.Json.JsonSerializer.Serialize(new { operation = "update_persona", persona = proposed }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in _inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, false)] [InlineData(true, true)]
    public async Task Canonical_approval_HTTP_projects_exact_receipt_after_trusted_local_execution_without_exposing_grant_authority(bool approve, bool freeze)
    {
        await using var factory = new ApprovalExecutionFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var policy = await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } } });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
        var id = session.GetProperty("sessionId").GetGuid();
        var admission = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{id}/messages", new { messageId = Guid.NewGuid(), text = "Save an exact approved persona", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
        var runId = admission.GetProperty("runId").GetGuid(); var agentStore = factory.Services.GetRequiredService<ITenantAgentStore>();
        var current = (await agentStore.FindTenantAgentAsync(tenant, instance))!;
        var approvals = factory.Services.GetRequiredService<TenantApprovalService>();
        var request = (await approvals.PreparePersonaAsync(new(tenant, runId), current.Instance.Persona with { Name = "Verified receipt persona" }, 1)).Request!;
        var path = api + $"/approvals/{request.RequestId}";
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/decision", new { decision = approve ? "approve" : "reject", actionHash = request.Operation.ActionHash,
            expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
        await approvals.DispatchPendingAsync();
        if (freeze)
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new
            {
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2, frozen = true,
                overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } }
            })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
        // Release the actual hosted worker only after preparation/decision. No manual claim or direct effect call substitutes for execution.
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        var execution = new TenantRunExecution(factory.Services.GetRequiredService<ITenantRuntimeStore>(), runs,
            factory.Services.GetRequiredService<ITenantRunDispatcher>(), factory.Services.GetRequiredService<IIdGenerator>(), factory.Services.GetRequiredService<TimeProvider>());
        Volatile.Write(ref factory.Enabled, true);
        AgentRun? finished = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            finished = await runs.FindTenantRunAsync(tenant, runId);
            if (finished?.IsTerminal == true) break;
            await Task.Delay(25, timeout.Token);
        }
        Assert.NotNull(finished); Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}: {finished.Failure?.Summary}");
        Assert.Equal(1, finished.AttemptCount); Assert.NotNull(finished.Result!.OutcomeEntryId);
        var result = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
        if (approve) Assert.NotNull(result); else Assert.Null(result);
        Assert.Equal(0, await execution.ExecuteRunnableAsync(8));
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(path)).StatusCode);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var read = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
        var view = await Json(read); Assert.Equal(approve ? "approved" : "rejected", view.GetProperty("status").GetString());
        Assert.Equal(principal, view.GetProperty("decision").GetProperty("principalId").GetGuid());
        if (approve)
        {
            Assert.Equal(freeze ? "denied" : "completed", view.GetProperty("operationResult").GetProperty("outcome").GetString());
            Assert.Equal(result!.OperationId, view.GetProperty("operationResult").GetProperty("operationId").GetGuid());
        }
        else Assert.Equal(System.Text.Json.JsonValueKind.Null, view.GetProperty("operationResult").ValueKind);
        Assert.DoesNotContain("grantId", view.GetRawText()); Assert.DoesNotContain("effectIdempotencyKey", view.GetRawText());
        var actual = (await agentStore.FindTenantAgentAsync(tenant, instance))!;
        Assert.Equal(approve && !freeze ? "Verified receipt persona" : current.Instance.Persona.Name, actual.Instance.Persona.Name);
        var grant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId);
        if (approve) Assert.Equal(freeze ? OperationGrantStatus.Revoked : OperationGrantStatus.Consumed, grant!.Status); else Assert.Null(grant);
        Assert.Equal(approve ? 1 : 0, (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Count(a => a.Action == "operations.persona.apply"));
        Assert.Null(finished.Owner.ProfileIdOrNull);
        var evidence = factory.Model.Requests.SelectMany(r => r.Context?.Sections ?? []).Where(s => s.Id == ContextSectionId.ApprovalOutcome).ToArray();
        Assert.NotEmpty(evidence);
        Assert.All(evidence, section =>
        {
            Assert.Equal(ContextLayer.Evidence, section.Layer); Assert.Equal(ModelRole.User, section.Message.Role);
            Assert.Contains(request.Operation.OperationId.ToString("D"), section.Message.Text);
            Assert.Contains(freeze ? "management_unavailable" : approve ? "operation_completed" : "approval_rejected", section.Message.Text);
            Assert.DoesNotContain("grantId", section.Message.Text); Assert.DoesNotContain("actionHash", section.Message.Text);
        });
        var list = await Json(await owner.GetAsync(api + "/approvals"));
        if (approve) Assert.Equal(freeze ? "denied" : "completed", list.GetProperty("items")[0].GetProperty("operationResult").GetProperty("outcome").GetString());
    }
}
