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
    public async Task Tenant_approval_preparation_cannot_read_a_legacy_draft_or_create_a_legacy_preview(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var provider = new SyntheticEmailProvider();
        var draft = await provider.CreateDraftAsync(new EmailCreateDraftRequest(["private@example.test"], [], [], "Private legacy subject", "Private legacy body"), default);
        var observed = new ObservedDraftProvider(provider);
        var tools = new SessionToolExecutor(emailProvider: observed, tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f)));
        var args = JsonSerializer.SerializeToElement(new { draftId = draft.Draft.DraftId });
        var call = new ModelToolCall("send", ToolCatalog.EmailSend, args.GetRawText());
        var frame = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: setup.Agent.Instance.InstanceId,
            AgentRunId: setup.Run.AgentRunId, OwnedSessionId: setup.Run.SessionId, TenantId: setup.Tenant);
        foreach (var admission in new[] { null, frame, frame with { TenantId = null }, frame with { TenantId = Guid.NewGuid() } })
        {
            var direct = await tools.PrepareEmailSendApprovalAsync(args, default, admission, setup.Run.SessionId);
            Denied(direct.Preparation, direct.ErrorJson);
            var common = await ToolActionPreparation.PrepareApprovalAsync(tools, call, args, sessionId: setup.Run.SessionId, admission: admission);
            Denied(common.Preparation, common.ErrorJson);
            var drive = await tools.PrepareDriveApprovalAsync(new("trash", ToolCatalog.DriveTrash, "{}"), args, setup.Run.SessionId, admission, default);
            Denied(drive.Preparation, drive.ErrorJson);
            var maintenance = await tools.PrepareIdentityMaintenanceApprovalAsync(setup.Run.Admission.Configuration!.Definition,
                setup.Run.SessionId, new("forget", ToolCatalog.MemoryForget, "{}"), args, admission, default);
            Denied(maintenance.Preparation, maintenance.ErrorJson);
        }
        var management = await tools.PrepareManagementApprovalAsync(new("settings", "settings.patch", "{}"), args, frame, default);
        Denied(management.Preparation, management.ErrorJson);
        var legacyFrame = await tools.PrepareManagementApprovalAsync(new("settings", "settings.patch", "{}"), args, frame with { TenantId = null }, default);
        Denied(legacyFrame.Preparation, legacyFrame.ErrorJson);
        var automation = await tools.PrepareAutomationAsync(setup.Run.Admission.Configuration!.Definition,
            new("create", ToolCatalog.AutomationCreate, "{}"), args,
            new(null, setup.Run.SessionId, null, "Create a reminder", null, TriggerAuthorizationClassification.CurrentUserTurn, null, f.Clock.GetUtcNow()), false, default);
        Denied(automation.Request, automation.ErrorJson);
        var generic = await ToolActionPreparation.PrepareApprovalAsync(tools, new("remove", ToolCatalog.WorkspaceDelete, "{}"), args,
            sessionId: setup.Run.SessionId);
        Denied(generic.Preparation, generic.ErrorJson);
        Assert.Equal(0, observed.DraftReads);
        var legacy = await tools.PrepareEmailSendApprovalAsync(args);
        Assert.NotNull(legacy.Preparation);
        Assert.Contains("private@example.test", legacy.Preparation.Summary);
        Assert.Equal(1, observed.DraftReads);
        var unconfigured = new SessionToolExecutor(emailProvider: observed);
        var noBoundary = await unconfigured.PrepareEmailSendApprovalAsync(args, admission: frame);
        Denied(noBoundary.Preparation, noBoundary.ErrorJson);
        Assert.Equal(1, observed.DraftReads);
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
