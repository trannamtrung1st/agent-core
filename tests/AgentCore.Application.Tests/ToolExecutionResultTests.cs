using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class ToolExecutionResultTests
{
    [Fact]
    public void TextByteCount_uses_text_only()
    {
        var image = new ModelImageContent("image/png", new byte[64 * 1024], "large.png");
        var result = new ToolExecutionResult("""{"ok":true}""", [image]);
        Assert.Equal(Encoding.UTF8.GetByteCount("""{"ok":true}"""), ToolOutputBudget.TextByteCount(result));
    }

    [Fact]
    public void Null_or_empty_parts_do_not_change_text_budget()
    {
        var textOnly = ToolExecutionResult.FromText("""{"a":1}""");
        var emptyParts = new ToolExecutionResult("""{"a":1}""", []);
        Assert.Equal(ToolOutputBudget.TextByteCount(textOnly), ToolOutputBudget.TextByteCount(emptyParts));
        Assert.Null(textOnly.Parts);
        Assert.Empty(emptyParts.Parts!);
    }
}
