using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Infrastructure.Email;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sensitive_dispatch_cannot_read_a_legacy_draft_or_use_forged_approval_facts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var provider = new SyntheticEmailProvider();
        var draft = await provider.CreateDraftAsync(new EmailCreateDraftRequest(["private@example.test"], [], [], "Private legacy subject", "Private legacy body"), default);
        var observed = new ObservedDraftProvider(provider);
        var tools = new SessionToolExecutor(emailProvider: observed, tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f)));
        var args = JsonSerializer.SerializeToElement(new { draftId = draft.Draft.DraftId, approvalId = Guid.NewGuid(), approved = true });
        var call = new ModelToolCall("send", ToolCatalog.EmailSend, args.GetRawText());
        var frame = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: setup.Agent.Instance.InstanceId,
            AgentRunId: setup.Run.AgentRunId, OwnedSessionId: setup.Run.SessionId, TenantId: setup.Tenant);
        foreach (var admission in new[] { null, frame, frame with { TenantId = null }, frame with { TenantId = Guid.NewGuid() } })
        {
            var result = await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, call, 16000, admission: admission);
            Assert.DoesNotContain("private@example.test", result.Text);
            Assert.DoesNotContain("Private legacy", result.Text);
            Assert.Equal(0, observed.DraftReads);
            using var json = JsonDocument.Parse(result.Text);
            Assert.True(json.RootElement.TryGetProperty("error", out _));
        }
        var automation = await tools.PrepareAutomationAsync(setup.Run.Admission.Configuration!.Definition,
            new("create", ToolCatalog.AutomationCreate, "{}"), JsonSerializer.SerializeToElement(new { }),
            new(null, setup.Run.SessionId, null, "Create a reminder", null, TriggerAuthorizationClassification.CurrentUserTurn, null, f.Clock.GetUtcNow()), false, default);
        Denied(automation.Request, automation.ErrorJson);
        Assert.Equal(0, observed.DraftReads);
    }

    private static void Denied(object? preparation, string? error)
    {
        Assert.Null(preparation); Assert.NotNull(error);
        using var result = JsonDocument.Parse(error);
        Assert.Equal("forbidden", result.RootElement.GetProperty("error").GetString());
        Assert.Equal("tenant_effect_unavailable", result.RootElement.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("Private legacy", error);
    }

    private sealed class ObservedDraftProvider(IEmailProvider inner) : IEmailProvider
    {
        public int DraftReads { get; private set; }
        public bool IsAvailable => inner.IsAvailable;
        public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string id, CancellationToken ct = default)
        { DraftReads++; return inner.GetDraftAsync(id, ct); }
        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken ct = default) => inner.SearchAsync(request, ct);
        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken ct = default) => inner.ReadAsync(request, ct);
        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken ct = default) => inner.CreateDraftAsync(request, ct);
        public ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken ct = default) => inner.SendDraftAsync(request, ct);
    }
}
