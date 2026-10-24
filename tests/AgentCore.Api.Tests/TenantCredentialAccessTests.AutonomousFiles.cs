using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private const string AutonomousFileBody = "Independent task bytes: thử nghiệm.";
    private sealed class AutonomousFileModel(bool review, bool conversation = false) : ILanguageModel
    {
        public ModelCapabilities Capabilities => new ScriptedLanguageModel().Capabilities;
        public readonly ConcurrentQueue<ModelRequest> Requests = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ExpectedRefusal;
        public string WriteTool = ToolCatalog.WorkspaceWrite;
        public string WriteArguments = JsonSerializer.Serialize(new { path = "/home/independent.txt", content = AutonomousFileBody });
        public string ReadTool = ToolCatalog.WorkspaceRead;
        public string ReadArguments = """{"path":"/home/independent.txt"}""";
        public string PublishArguments = """{"path":"/home/independent.txt","displayName":"independent.txt","contentType":"text/plain"}""";
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            if (conversation) Assert.DoesNotContain(request.Tools!, t => t.Name == ToolCatalog.WorkComplete);
            else Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.WorkComplete);
            Assert.DoesNotContain(request.Tools!, t => t.Name is ToolCatalog.BackgroundStart or ToolCatalog.BrowserFillCredential);
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool && m.ToolCallId?.StartsWith("independent-stage-", StringComparison.Ordinal) == true).ToArray();
            var refused = results.Any(m => m.Text.Contains("\"error\":", StringComparison.Ordinal) || m.Text.Contains("\"decision\":\"denied\"", StringComparison.Ordinal));
            if (refused)
            {
                yield return new ModelToolCallEvent(new("independent-finish", ToolCatalog.WorkComplete, """{"outcome":"NeedsAttention","summary":"Independent file operation was refused.","attentionRequired":true}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            for (var stage = 0; stage < 3; stage++)
            {
                var call = "independent-stage-" + stage; if (results.Any(m => m.ToolCallId == call)) continue;
                var tool = stage == 0 ? WriteTool : stage == 1 ? ReadTool : ToolCatalog.ArtifactsCreateFromWorkspace;
                var arguments = stage == 0 ? WriteArguments : stage == 1 ? ReadArguments : PublishArguments;
                Assert.Contains(request.Tools!, t => t.Name == tool);
                if (review && stage != 1)
                {
                    arguments = stage == 0 ? JsonSerializer.Serialize(new { operation = "mutate_workspace", mutation = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) } })
                        : JsonSerializer.Serialize(new { operation = "publish_workspace_artifact", artifact = JsonSerializer.Deserialize<JsonElement>(arguments) });
                    tool = ToolCatalog.RequestAuthorization;
                }
                yield return new ModelToolCallEvent(new(call, tool, arguments)); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Assert.False(ExpectedRefusal, "An expected independent authority refusal was bypassed.");
            if (conversation)
            {
                yield return new ModelSemanticResponseReady(new("Independent exact files completed.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
            else
            {
                yield return new ModelToolCallEvent(new("independent-finish", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Independent exact files completed.","attentionRequired":false}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
        }
    }
    public static TheoryData<bool, string, string> AutonomousFileCases
    {
        get
        {
            var cases = new TheoryData<bool, string, string>();
            foreach (var sqlite in new[] { false, true }) foreach (var origin in new[] { "manual", "scheduled" })
            foreach (var mode in new[] { "allowed", "review", "grant", "reject", "missing-read", "revoked-write" }) cases.Add(sqlite, origin, mode);
            return cases;
        }
    }
    [Theory(Timeout = 120000)] [MemberData(nameof(AutonomousFileCases))]
    public Task Independent_Automation_files_use_actual_executor_exact_consent_and_same_task_successors(bool sqlite, string origin, string mode)
        => AutonomousFileFlow(sqlite, origin, mode);

    [Theory(Timeout = 120000)] [InlineData("scheduled", "review")] [InlineData("manual", "review")] [InlineData("scheduled", "grant")]
    public Task Independent_Automation_file_consent_reopens_in_an_actual_SQLite_host_without_task_or_effect_replay(string origin, string mode)
        => AutonomousFileFlow(true, origin, mode, restart: true);

    [Theory(Timeout = 120000)] [InlineData(false, "scheduled", "review", "docx")] [InlineData(true, "scheduled", "review", "docx")]
    [InlineData(true, "manual", "allowed", "xlsx")] [InlineData(false, "scheduled", "review", "xlsx")]
    public Task Independent_Automation_Office_files_preserve_binary_sources_and_exact_consent(bool sqlite, string origin, string mode, string kind)
        => AutonomousFileFlow(sqlite, origin, mode, kind: kind);

    [Theory(Timeout = 120000)] [InlineData(false, "manual", false)] [InlineData(true, "manual", false)]
    [InlineData(false, "scheduled", false)] [InlineData(true, "scheduled", false)]
    [InlineData(true, "manual", true)] [InlineData(true, "scheduled", true)]
    public Task Existing_conversation_Automation_file_Review_keeps_empty_input_successors_and_durable_exact_sources(bool sqlite, string origin, bool restart) =>
        AutonomousFileFlow(sqlite, origin, "review", restart, conversation: true);

    private async Task AutonomousFileFlow(bool sqlite, string origin, string mode, bool restart = false, string kind = "text", bool conversation = false)
    {
        var reviewed = mode is "review" or "grant" or "reject";
        var model = new AutonomousFileModel(reviewed, conversation) { ExpectedRefusal = mode is "reject" or "missing-read" or "revoked-write" };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var diagnostics = new AccountReadDiagnostics();
        var safeDiagnostics = new DiagnosticLineLoggerProvider(LogLevel.Warning);
        var root = restart ? Path.Combine(Path.GetTempPath(), "independent-consent-reopen-" + Guid.NewGuid().ToString("N")) : null;
        void Configure(IServiceCollection services) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(diagnostics); services.AddLogging(logging => logging.AddProvider(safeDiagnostics)); }
        await using var first = new OAuthFactory(root, sqlite: sqlite, preserve: restart, execute: true, model: model, servicesOverride: Configure);
        var factory = first; OAuthFactory? recreated = null; HttpClient? reopenedOwner = null;
        using var firstOwner = ConnectedClient(factory); var owner = firstOwner; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        try
        {
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var draft = await Json(await owner.PostAsJsonAsync(api + "/agent-definitions", new { name = "Independent exact task", expectedRevision = await Revision(owner, tenant) }));
        var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        var baseline = (await factory.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 32))!;
        candidate["triggerPolicy"] = JsonSerializer.SerializeToNode(baseline.TriggerPolicy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (kind is "docx" or "xlsx")
        {
            var path = "/home/independent." + kind;
            model.WriteTool = kind == "docx" ? ToolCatalog.OfficeDocumentCreate : ToolCatalog.OfficeSpreadsheetCreate;
            model.WriteArguments = kind == "docx" ? JsonSerializer.Serialize(new { path, title = "Independent exact document", sections = new[] { new { heading = "Observed", paragraphs = new[] { AutonomousFileBody } } } })
                : JsonSerializer.Serialize(new { path, sheets = new[] { new { name = "Owned", rows = new[] { new[] { new { type = "text", text = AutonomousFileBody } } } } } });
            model.ReadTool = kind == "docx" ? ToolCatalog.OfficeDocumentInspect : ToolCatalog.OfficeSpreadsheetInspect;
            model.ReadArguments = JsonSerializer.Serialize(new { path }); model.PublishArguments = JsonSerializer.Serialize(new { path, displayName = "independent." + kind });
        }
        var capabilities = new[] { model.ReadTool, model.WriteTool, ToolCatalog.ArtifactsCreateFromWorkspace };
        candidate["environment"] = JsonSerializer.SerializeToNode(new { harness = Array.Empty<string>(), knowledgeSources = Array.Empty<string>(), toolAllowlist = Array.Empty<string>(),
            capabilities = new { mode = "Selected", resolvedCapabilities = capabilities }, projection = new { alwaysCapabilities = capabilities } });
        var draftId = draft.GetProperty("draftId").GetGuid();
        var saved = await Json(await owner.PutAsJsonAsync(api + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 }));
        Assert.True(saved.TryGetProperty("revision", out _), saved.GetRawText());
        await Json(await owner.PostAsJsonAsync(api + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = saved.GetProperty("revision").GetInt64() }));
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var permissions = new List<string> { "automations.run", "sessions.create", "agent-runs.create", "agents.read", "sessions.read", "artifacts.read" };
        if (mode != "missing-read") permissions.Add("workspaces.read");
        if (mode != "grant") permissions.AddRange(["workspaces.write", "artifacts.create"]);
        if (conversation) permissions.Add("agents.chat");
        var role = await access.SaveRoleAsync(human, tenant, null, "Independent task executor", permissions, await Revision(owner, tenant));
        var assignment = await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        if (mode is "review" or "reject")
            foreach (var permission in new[] { "workspaces.write", "artifacts.create" })
            {
                using var policy = await owner.PutAsJsonAsync(api + "/operation-policies/" + permission, new { mode = "always", independentReview = origin == "scheduled", expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 });
                Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
            }
        Guid? targetSessionId = conversation ? (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, mode = "text", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid() : null;
        var route = api + $"/agents/{instance}/automations";
        var automation = (await Json(await owner.PostAsJsonAsync(route, new { enabled = true, name = "Independent exact files", instructions = "Write, read and publish exact owned task files.", targetSessionId, schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(30).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, requiresTools = true }))).GetProperty("automationId").GetGuid();
        Guid initialId;
        if (origin == "manual")
        {
            var admitted = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 });
            Assert.True(admitted.StatusCode == HttpStatusCode.Created, await admitted.Content.ReadAsStringAsync());
            initialId = (await Json(admitted)).GetProperty("runId").GetGuid();
        }
        else
        {
            clock.Advance(TimeSpan.FromMinutes(31)); await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
            initialId = Assert.Single((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items).AgentRunId;
        }
        using (var entering = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            while (!model.Entered.Task.IsCompleted)
            {
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, entering.Token);
                await Task.Delay(25, entering.Token);
            }
        }
        if (mode == "revoked-write")
        {
            await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
            var remaining = await access.SaveRoleAsync(human, tenant, null, "Current independent reader", permissions.Where(p => p is not ("workspaces.write" or "artifacts.create")).ToArray(), await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, remaining.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        }
        model.Release.TrySetResult(); var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
        var decided = new HashSet<Guid>(); AgentRun final; var initial = (await runs.FindTenantRunAsync(tenant, initialId))!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
            var values = new List<AgentRun>(); foreach (var id in await runs.ListTenantRunIdsAsync(tenant, initial.SessionId, deadline.Token)) values.Add((await runs.FindTenantRunAsync(tenant, id, deadline.Token))!);
            final = values.SingleOrDefault(r => !r.IsTerminal) ?? values.Single(r => r.Result?.OutcomeKind != AgentRunOutcomeKind.Continued);
            if (final.Status == AgentRunStatus.WaitingForApproval)
            {
                var pending = (await approvals.FindApprovalAsync(tenant, final.ApprovalRequestId!.Value))!;
                if (restart && recreated is null)
                {
                    await first.DisposeAsync();
                    var previous = model;
                    model = new AutonomousFileModel(reviewed, conversation) { WriteTool = previous.WriteTool, WriteArguments = previous.WriteArguments,
                        ReadTool = previous.ReadTool, ReadArguments = previous.ReadArguments, PublishArguments = previous.PublishArguments };
                    model.Release.TrySetResult();
                    recreated = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: Configure); factory = recreated;
                    reopenedOwner = ConnectedClient(factory); owner = reopenedOwner; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    runs = factory.Services.GetRequiredService<ITenantRunStore>(); approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
                    var retained = (await approvals.FindApprovalAsync(tenant, pending.RequestId))!;
                    Assert.Equal(JsonSerializer.Serialize(pending), JsonSerializer.Serialize(retained));
                    Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(tenant, pending.RunId))!.Status);
                    continue;
                }
                Assert.True(decided.Add(pending.RequestId)); Assert.Equal(origin == "scheduled" ? (Guid?)null : human, pending.OriginalInitiatorPrincipalId);
                Assert.Equal(mode == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, pending.Kind);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{pending.RequestId}"));
                var decision = await owner.PostAsJsonAsync(api + $"/approvals/{pending.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = pending.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = pending.Revision });
                Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync());
            }
            if (final.IsTerminal && final.Result?.OutcomeKind != AgentRunOutcomeKind.Continued) break;
            await Task.Delay(25, deadline.Token);
        }
        Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status} {final.Failure?.Code}: {string.Join("\n", diagnostics.Failures)}");
        Assert.Equal(initial.ActivationId, final.Admission.ExecutionContext!.RootActivationId); Assert.Null(final.Owner.ProfileIdOrNull);
        Assert.Equal(origin == "scheduled" ? (Guid?)null : human, final.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
        var artifacts = await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(tenant, initial.SessionId, null, null, 100);
        if (model.ExpectedRefusal) Assert.Empty(artifacts.Items);
        else
        {
            Assert.True(artifacts.Items.Count == 1,
                $"Expected one owned Artifact. Status={final.Status} Outcome={final.Result?.OutcomeKind} FailureCode={final.Failure?.Code} DiagnosticId={final.Failure?.DiagnosticId} "
                + $"ArtifactCount={artifacts.Items.Count} ModelRequests={model.Requests.Count} Decisions={decided.Count}; "
                + AutonomousFileStageDiagnostics(model) + "; diagnostics=" + AutonomousFileSafeErrors(safeDiagnostics));
            var artifact = Assert.Single(artifacts.Items); Assert.Equal(final.AgentRunId, artifact.AgentRunId);
            var bytes = await owner.GetByteArrayAsync(api + $"/sessions/{initial.SessionId}/artifacts/{artifact.ArtifactId}/content");
            var path = "/home/independent." + (kind == "text" ? "txt" : kind);
            Assert.Equal(await owner.GetByteArrayAsync(api + $"/agents/{instance}/workspace/content?path={Uri.EscapeDataString(path)}"), bytes);
            if (kind == "text") Assert.Equal(AutonomousFileBody, Encoding.UTF8.GetString(bytes));
            Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("Independent task bytes", StringComparison.Ordinal));
        }
        if (conversation)
        {
            Assert.Equal(targetSessionId, final.SessionId); Assert.Equal(AgentRunOutputContract.ConversationResponse, final.Admission.OutputContract);
            Assert.Empty(final.Admission.ApprovalResume!.SourceInputEntryIds);
            var retained = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, final.SessionId))!;
            Assert.Equal(SessionOriginKind.UserChat, retained.Snapshot.Origin.Kind); Assert.Equal(human, retained.Owner.CreatedByPrincipalId);
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{final.SessionId}/messages"))).GetProperty("items").EnumerateArray().ToArray();
            Assert.DoesNotContain(history, row => row.GetProperty("role").GetString()!.Equals("user", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(history, row => row.GetProperty("text").GetString() == "Independent exact files completed.");
        }
        Assert.Equal(reviewed ? mode == "reject" ? 1 : 2 : 0, decided.Count); Assert.Equal(0, factory.Outbound);
        }
        finally
        {
            reopenedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync();
            if (restart) { await first.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    private static string AutonomousFileStageDiagnostics(AutonomousFileModel model)
    {
        var results = model.Requests.ToArray().SelectMany(request => request.Messages).Where(message => message.Role == ModelRole.Tool).ToArray();
        var stages = new List<string>();
        for (var stage = 0; stage < 3; stage++)
        {
            var message = results.LastOrDefault(result => result.ToolCallId == "independent-stage-" + stage);
            if (message is null) { stages.Add($"Stage{stage}=missing"); continue; }
            try
            {
                using var document = JsonDocument.Parse(message.Text);
                var root = document.RootElement; var fields = new List<string>();
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "error", "reasonCode", "decision", "status", "approvalStatus", "outcome", "observationUnavailableReason" })
                    {
                        if (!root.TryGetProperty(key, out var value)) continue;
                        var code = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        fields.Add(key + "=" + (code is { Length: > 0 and <= 96 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? code : value.ValueKind.ToString()));
                    }
                    foreach (var key in new[] { "applied", "readAuthorized" })
                        if (root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) fields.Add(key + "=" + value.GetBoolean());
                    foreach (var key in new[] { "requestId", "operationId", "diagnosticId" })
                        if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id)) fields.Add(key + "=" + id.ToString("D"));
                }
                stages.Add($"Stage{stage}[shape={root.ValueKind};{string.Join(',', fields)}]");
            }
            catch (JsonException) { stages.Add($"Stage{stage}=invalid_json"); }
        }
        return string.Join(" | ", stages);
    }

    private static string AutonomousFileSafeErrors(DiagnosticLineLoggerProvider diagnostics)
    {
        lock (diagnostics.Lines)
            return string.Join(" | ", diagnostics.Errors.Where(error => error.GetType().Assembly == typeof(DiagnosticLog).Assembly
                && error.GetType().DeclaringType?.FullName == "AgentCore.Application.Observability.DiagnosticExceptionSnapshot"
                && error.GetType().Name is "SafeException" or "SafeAggregateException").TakeLast(4)
                .Select(error => error.ToString()).Select(text => text[..Math.Min(1024, text.Length)]));
    }
}
