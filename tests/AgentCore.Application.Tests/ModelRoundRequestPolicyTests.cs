using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class ModelRoundRequestPolicyTests
{
    private static ModelRequest Request() => new(Guid.NewGuid(), [], MaxOutputTokens: 8192,
        Tools: [ToolRegistry.Get(ToolCatalog.BrowserNavigate).ModelDefinition],
        ToolChoice: ModelToolChoice.Named, ToolChoiceName: ToolCatalog.BrowserNavigate,
        ResponseContract: new(SpeechWillBeUsed: true));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Finalization_takes_precedence_over_capacity_cleanup_and_completion(bool background)
    {
        var request = Request();
        var result = ModelRoundRequestPolicy.ForPhase(request, new(true, background, true, false, true, "runDeadline"));
        Assert.Equal(request.ResponseId, result.ResponseId);
        if (background)
        {
            Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(result.Tools!).Name);
            Assert.Equal(ModelToolChoice.Named, result.ToolChoice); Assert.Equal(ToolCatalog.WorkComplete, result.ToolChoiceName);
            Assert.Equal(4096, result.MaxOutputTokens); Assert.Null(result.ResponseContract);
        }
        else
        {
            Assert.Null(result.Tools); Assert.Equal(ModelToolChoice.Auto, result.ToolChoice); Assert.Null(result.ToolChoiceName);
            Assert.Equal(2048, result.MaxOutputTokens); Assert.Equal(request.ResponseContract, result.ResponseContract);
        }
        Assert.Equal(ToolCatalog.BrowserNavigate, Assert.Single(request.Tools!).Name);
    }

    [Fact]
    public void Repair_removes_tools_and_structured_contract_without_changing_response_identity()
    {
        var request = Request(); var repaired = ModelRoundRequestPolicy.ForRepair(request, toolsOffered: true);
        Assert.Equal(request.ResponseId, repaired.ResponseId); Assert.Null(repaired.Tools); Assert.Null(repaired.ResponseContract);
        Assert.Equal(ModelToolChoice.Auto, repaired.ToolChoice); Assert.Null(repaired.ToolChoiceName);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Capacity_limits_background_to_completion_and_interactive_to_prose(bool background)
    {
        var result = ModelRoundRequestPolicy.ForPhase(Request(), new(false, background, true, false, false, null));
        if (background) Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(result.Tools!).Name);
        else Assert.Null(result.Tools);
    }

    [Fact]
    public void Ordinary_work_keeps_request_and_completion_report_cannot_offer_tools()
    {
        var request = Request();
        Assert.Same(request, ModelRoundRequestPolicy.ForPhase(request, new(false, false, false, false, false, null)));
        Assert.Null(ModelRoundRequestPolicy.ForPhase(request, new(false, false, false, true, false, null)).Tools);
        var completed = ModelRoundRequestPolicy.ForPhase(request, new(true, false, false, false, false, null));
        Assert.Null(completed.Tools); Assert.Null(completed.ToolChoiceName); Assert.Equal(ModelToolChoice.Auto, completed.ToolChoice);
    }
}
