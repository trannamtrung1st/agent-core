using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantPngArtifactScriptTests
{
    [Fact]
    public void Refused_approval_continuation_stops_without_replaying_the_command()
    {
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, "Continue the exact saved authorization operation."),
            new(ModelRole.Tool, "{\"outcome\":\"denied\"}", ToolCallId: "png-proof-0", Name: ToolCatalog.RequestAuthorization)]);
        var events = TenantPngArtifactScript.Generate(request)!;
        Assert.DoesNotContain(events, e => e is ModelToolCallEvent);
        Assert.Contains(events, e => e is ModelTextDelta text && text.Text.Contains("No command was replayed", StringComparison.Ordinal));
    }

    [Fact]
    public void Completed_command_continuation_publishes_only_the_observed_working_source()
    {
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, "Continue the exact saved authorization operation."),
            new(ModelRole.Tool, "{\"outcome\":\"completed\",\"sandbox\":{\"exitCode\":0}}", ToolCallId: "png-proof-0", Name: ToolCatalog.RequestAuthorization)],
            Tools: [new(ToolCatalog.RequestAuthorization, "Exact tenant authorization", "{}")]);
        var call = Assert.Single(TenantPngArtifactScript.Generate(request)!.OfType<ModelToolCallEvent>()).Call;
        Assert.Equal(ToolCatalog.RequestAuthorization, call.Name);
        Assert.Contains("publish_workspace_artifact", call.ArgumentsJson); Assert.Contains("/working/plot.png", call.ArgumentsJson);
        Assert.DoesNotContain("run_sandbox_command", call.ArgumentsJson);
    }
}
