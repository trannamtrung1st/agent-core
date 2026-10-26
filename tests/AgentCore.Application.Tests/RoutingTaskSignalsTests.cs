using AgentCore.Application.Models;

namespace AgentCore.Application.Tests;

public sealed class RoutingTaskSignalsTests
{
    [Fact]
    public void Equal_length_tasks_get_distinct_meaningful_hints_without_sending_text()
    {
        var greeting = RoutingTaskSignals.Project("Greet the team.", "text");
        var debug = RoutingTaskSignals.Project("Debug the code.", "text");
        Assert.Equal(greeting["inputCharacters"], debug["inputCharacters"]);
        Assert.Equal("light", greeting["reasoningDemand"]); Assert.Equal("substantial", debug["reasoningDemand"]);
        Assert.Equal("greeting", greeting["taskKind"]); Assert.Equal("analysis", debug["taskKind"]);
    }

    [Theory]
    [InlineData("Chứng minh thuật toán và phân tích trường hợp biên.", "analysis", "substantial")]
    [InlineData("Saluda al equipo.", "greeting", "light")]
    [InlineData("Démontrez la terminaison de cet algorithme.", "analysis", "substantial")]
    [InlineData("Write a Python function.", "code", "moderate")]
    [InlineData("An unspecified task.", "general", "unknown")]
    public void Text_and_unattended_admission_share_the_same_task_projection(string text, string kind, string demand)
    {
        foreach (var mode in new[] { "text", "unattended" })
        {
            var hints = RoutingTaskSignals.Project(text, mode);
            Assert.Equal(kind, hints["taskKind"]); Assert.Equal(demand, hints["reasoningDemand"]);
        }
    }

    [Fact]
    public void Names_credentials_urls_and_code_are_never_projected_and_inspection_is_bounded()
    {
        const string privateTask = "Debug this code for alice@example.test; token=sk-test-private-fixture; https://private.example.test/?key=secret; void SecretFunction() {}";
        var values = string.Join('|', RoutingTaskSignals.Project(privateTask, "text").Values);
        Assert.DoesNotContain("alice", values); Assert.DoesNotContain("sk-test", values); Assert.DoesNotContain("SecretFunction", values); Assert.DoesNotContain("https", values);
        var bounded = RoutingTaskSignals.Project(new string('x', 4096) + " prove a theorem", "text");
        Assert.Equal("unknown", bounded["reasoningDemand"]);
        Assert.InRange(bounded.Count, 1, 16);
    }
}
