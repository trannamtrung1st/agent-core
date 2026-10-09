using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Xunit.Abstractions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class CheckpointBudgetMeasurementTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(48)] [InlineData(96)] [InlineData(144)]
    public void Measure_native_workflow_compaction_without_dropping_pending_or_effect_receipts(int steps)
    {
        var messages = new List<ModelMessage>();
        for (var i = 0; i < steps - 1; i++)
        {
            var tool = i % 2 == 0 ? ToolCatalog.BrowserSnapshot : ToolCatalog.BrowserClick;
            messages.Add(new(ModelRole.Assistant, "", ToolCalls: [new("call-" + i, tool, tool == ToolCatalog.BrowserSnapshot ? "{}" : JsonSerializer.Serialize(new { target = new { by = "role", value = "tab", name = "Attribute" } }))]));
            messages.Add(new(ModelRole.Tool, JsonSerializer.Serialize(new { status = "ok", untrustedBrowserContent = true,
                url = "https://fixture.test/asset", title = "Pump 002", content = "Attribute " + i + "\n" + new string('a', 6000) + "\nVerified measurement tail " + i,
                targets = Array.Empty<object>(), effectAttempted = tool == ToolCatalog.BrowserClick, effectConfirmedBySdk = tool == ToolCatalog.BrowserClick, applicationOutcomeVerified = false }),
                Name: tool, ToolCallId: "call-" + i));
        }
        var pending = new ModelToolCall("pending", ToolCatalog.BrowserHover, "{ \"target\": {\"by\":\"role\",\"value\":\"button\",\"name\":\"Account\"} }");
        messages.Add(new(ModelRole.Assistant, "", ToolCalls: [pending]));
        var before = Encoding.UTF8.GetByteCount(AgentRunToolCallCheckpoint.Write(messages));
        BrowserSnapshotCompaction.Compact(messages);
        var after = Encoding.UTF8.GetByteCount(AgentRunToolCallCheckpoint.Write(messages));
        output.WriteLine($"steps={steps}; raw={before}; compacted={after}; args={messages.Sum(m => (m.ToolCalls ?? []).Sum(c => Encoding.UTF8.GetByteCount(c.ArgumentsJson)))}");
        Assert.True(after < before / 4);
        var required = after + AgentRunToolCallCheckpoint.CompletionReserve(TriggerKind.UserTurn) + AgentRunToolCallCheckpoint.FinishRequiredReserve() + 8192;
        output.WriteLine($"requiredWithCleanupAndFinalization={required}; systemCap={AgentRunLimits.MaxCheckpointBytes}");
        Assert.True(required <= AgentRunLimits.MaxCheckpointBytes); Assert.Equal(pending, Assert.Single(AgentRunToolCallCheckpoint.PendingCalls(messages)));
        Assert.Equal(steps / 2 - 1, messages.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserClick && m.Text.Contains("\"effectConfirmedBySdk\":true")));
    }
}
