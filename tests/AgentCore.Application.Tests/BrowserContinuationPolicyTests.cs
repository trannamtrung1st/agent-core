using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserContinuationPolicyTests
{
    [Fact]
    public void Intervention_redirect_retires_requested_and_final_origins_but_allows_other_sites_and_close()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? page = null;
        Assert.False(BrowserContinuationPolicy.Observe(ToolCatalog.BrowserNavigate,
            """{"url":"https://requested.test/path"}""",
            """{"url":"https://challenge.test/login","error":"user_intervention_required"}""", blocked, ref page));
        Assert.Equal("https://challenge.test", page);
        Assert.Equal(2, blocked.Count);
        Assert.True(BrowserContinuationPolicy.TryRefuse(new("a", ToolCatalog.BrowserNavigate,
            """{"url":"https://REQUESTED.test/again"}"""), blocked, page, out var receipt));
        Assert.Contains("user_intervention_required", receipt);
        Assert.True(BrowserContinuationPolicy.TryRefuse(new("b", ToolCatalog.BrowserClick, "{}"), blocked, page, out _));
        Assert.False(BrowserContinuationPolicy.TryRefuse(new("c", ToolCatalog.BrowserNavigate,
            """{"url":"https://other.test/"}"""), blocked, page, out _));
        Assert.False(BrowserContinuationPolicy.TryRefuse(new("d", ToolCatalog.BrowserClose, "{}"), blocked, page, out _));
        Assert.True(BrowserContinuationPolicy.Observe(ToolCatalog.BrowserClose, "{}", """{"status":"closed"}""", blocked, ref page));
        Assert.Null(page);
        Assert.Equal(2, blocked.Count); // Closing never makes the challenged sites safe to retry.
    }

    [Fact]
    public void Successful_alternative_observation_changes_current_origin_without_clearing_intervention_evidence()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://blocked.test" };
        string? page = "https://blocked.test";
        BrowserContinuationPolicy.Observe(ToolCatalog.BrowserNavigate, "{}", """{"url":"https://other.test/path"}""", blocked, ref page);
        Assert.True(BrowserContinuationPolicy.SuccessfulUnblockedResult(ToolCatalog.BrowserSnapshot, "{}", page, blocked));
        Assert.False(BrowserContinuationPolicy.SuccessfulUnblockedResult(ToolCatalog.BrowserSnapshot, "broken", page, blocked));
        Assert.False(BrowserContinuationPolicy.SuccessfulUnblockedResult(ToolCatalog.BrowserSnapshot, """{"error":"target_missing"}""", page, blocked));
        Assert.Contains("https://blocked.test", blocked);
    }

    [Fact]
    public void Blocked_and_terminal_projection_remove_interaction_without_granting_tools()
    {
        var names = new[] { ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserClick,
            ToolCatalog.BrowserClose, ToolCatalog.KnowledgeRetrieve };
        var request = new ModelRequest(Guid.NewGuid(), [], Tools: names.Select(n => ToolRegistry.Get(n).ModelDefinition).ToArray(),
            ToolChoice: ModelToolChoice.Named, ToolChoiceName: ToolCatalog.BrowserClick);
        var projected = BrowserContinuationPolicy.ProjectTools(request, pageBlocked: true, terminalOnly: false);
        Assert.Equal(new[] { ToolCatalog.BrowserNavigate, ToolCatalog.BrowserClose, ToolCatalog.KnowledgeRetrieve }, projected.Tools!.Select(t => t.Name));
        Assert.Equal(ModelToolChoice.Auto, projected.ToolChoice); Assert.Null(projected.ToolChoiceName);
        var terminal = BrowserContinuationPolicy.ProjectTools(request, pageBlocked: false, terminalOnly: true);
        Assert.Null(terminal.Tools); Assert.Null(terminal.ToolChoiceName); Assert.Equal(ModelToolChoice.Auto, terminal.ToolChoice);
        Assert.Equal(5, request.Tools!.Count);
    }
}
