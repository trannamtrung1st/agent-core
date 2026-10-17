using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Tests;

public sealed class SyntheticAuthorizationAuditTests
{
    [Theory]
    [InlineData("work-drive", true)] [InlineData("bad/reference", true)] [InlineData("work-drive", false)]
    public async Task Synthetic_credential_fixture_uses_only_an_offered_valid_reference_and_reports_saved_uncertainty(string reference, bool offered)
    {
        var model = new ScriptedLanguageModel(["fallback"]);
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, ScriptedLanguageModel.CredentialCheckMarker + " " + reference)],
            Tools: offered ? [TenantApprovalToolContract.Descriptor(false, true).ModelDefinition] : []);
        var events = new List<ModelGenerationEvent>(); await foreach (var e in model.GenerateAsync(request)) events.Add(e);
        if (!offered || reference != "work-drive") { Assert.Empty(events.OfType<ModelToolCallEvent>()); return; }
        var call = Assert.Single(events.OfType<ModelToolCallEvent>()).Call;
        Assert.Equal(reference, TenantApprovalToolContract.Parse(call).CredentialReference);
        Assert.DoesNotContain("credentialId", call.ArgumentsJson); Assert.DoesNotContain("tenant", call.ArgumentsJson); Assert.DoesNotContain("grant", call.ArgumentsJson);
        request = request with { Tools = [], Messages = [.. request.Messages, new(ModelRole.Assistant, "", ToolCalls: [call]),
            new(ModelRole.Tool, """{"outcome":"indeterminate","applied":null}""", ToolCallId: call.Id, Name: call.Name)] };
        events.Clear(); await foreach (var e in model.GenerateAsync(request)) events.Add(e);
        Assert.Empty(events.OfType<ModelToolCallEvent>()); Assert.Contains(events.OfType<ModelTextDelta>(), e => e.Text.Contains("outcome is uncertain"));
    }

    [Fact]
    public async Task Synthetic_review_fixture_requests_only_an_offered_exact_action_without_caller_authority()
    {
        var model = new ScriptedLanguageModel(["fallback"]);
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, ScriptedLanguageModel.ApprovalReviewMarker)], Tools: [TenantApprovalToolContract.Descriptor().ModelDefinition]);
        var events = new List<ModelGenerationEvent>(); await foreach (var e in model.GenerateAsync(request)) events.Add(e);
        var call = Assert.Single(events.OfType<ModelToolCallEvent>()).Call;
        var intent = TenantApprovalToolContract.Parse(call); Assert.Equal("vi", intent.Preferences!.Language);
        Assert.DoesNotContain("principal", call.ArgumentsJson); Assert.DoesNotContain("tenant", call.ArgumentsJson); Assert.DoesNotContain("grant", call.ArgumentsJson);
        events.Clear(); await foreach (var e in model.GenerateAsync(request with { Tools = [] })) events.Add(e);
        Assert.Empty(events.OfType<ModelToolCallEvent>());
    }
    [Fact]
    public async Task Synthetic_audit_fixture_emits_only_the_offered_readonly_inspection_with_a_resource_argument_and_no_caller_authority()
    {
        var resource = Guid.NewGuid(); var model = new ScriptedLanguageModel(["fallback"]);
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, $"{ScriptedLanguageModel.AuthorizationAuditMarker} agents.read {resource:D}")],
            Tools: [IamToolContracts.Descriptor(ToolCatalog.IamCheckAccess).ModelDefinition]);
        var events = new List<ModelGenerationEvent>(); await foreach (var e in model.GenerateAsync(request)) events.Add(e);
        var call = Assert.Single(events.OfType<ModelToolCallEvent>()).Call;
        Assert.Equal(ToolCatalog.IamCheckAccess, call.Name); Assert.Contains(resource.ToString("D"), call.ArgumentsJson);
        Assert.DoesNotContain("principal", call.ArgumentsJson); Assert.DoesNotContain("tenant", call.ArgumentsJson);
        var disabled = new List<ModelGenerationEvent>();
        await foreach (var e in model.GenerateAsync(request with { Tools = [] })) disabled.Add(e);
        Assert.Empty(disabled.OfType<ModelToolCallEvent>());
    }
}
