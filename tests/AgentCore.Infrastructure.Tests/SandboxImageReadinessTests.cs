using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Infrastructure.Tests;

public sealed class SandboxImageReadinessTests
{
    [Fact]
    public void Complete_success_is_required_before_an_image_is_ready() =>
        Assert.Null(SandboxImageReadiness.Describe("agent-core-image-readiness-v1\ncomplete\n", 0));

    [Theory]
    [InlineData("processUnavailable", "process inspection is unavailable")]
    [InlineData("scratchUnavailable", "/working is missing")]
    [InlineData("dockerSocket", "Docker socket is exposed")]

    public void Known_failures_have_actionable_safe_recovery(string reasons, string expected)
    {
        var message = SandboxImageReadiness.Describe("agent-core-image-readiness-v1\n" + reasons + "\ncomplete\n", 1);
        Assert.Contains(expected, message);
        Assert.Contains("Python and a pre-created directory are not required", message);
    }

    [Theory]
    [InlineData("private-provider-sentinel", 0)]
    [InlineData("agent-core-image-readiness-v1\nprivate-provider-sentinel\ncomplete\n", 1)]
    [InlineData("agent-core-image-readiness-v1\nprocessUnavailable\ncomplete\n", 0)]
    [InlineData("agent-core-image-readiness-v1\ncomplete\n", 1)]
    [InlineData("agent-core-image-readiness-v1\ncomplete\n", null)]
    [InlineData("agent-core-image-readiness-v1\nprocessUnavailable\nprocessUnavailable\ncomplete\n", 1)]
    [InlineData("agent-core-image-readiness-v1\n", 0)]
    public void Unknown_partial_or_inconsistent_evidence_cannot_pass_or_leak_provider_output(string output, int? exitCode)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SandboxImageReadiness.Describe(output, exitCode));
        Assert.DoesNotContain("private-provider-sentinel", error.Message);
    }
}
