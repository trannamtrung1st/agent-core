using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class BackgroundCompletionProjectionTests
{
    [Theory]
    [InlineData("\"\\")]
    [InlineData("漢😀")]
    [InlineData("\u0001\n")]
    public void Completion_projection_bounds_both_json_layers_and_omits_foreign_artifacts(string content)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var activation = new Activation(Guid.NewGuid(), sessionId, ActivationKind.ManualBackground, [Guid.NewGuid()], null,
            null, null, null, "projection:test", now);
        var run = AgentRun.Create(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()),
            new(activation, "assistant", 1, new AgentIdentity("Alex", "Assistant", "Help", "Calm"), Guid.NewGuid()),
            new("synthetic", "synthetic", "synthetic", null), 3, now).TakeClaim(Guid.NewGuid(), now, now.AddMinutes(5));
        var text = string.Concat(Enumerable.Repeat(content, 500));
        run = run.Complete(run.Revision, run.Claim!.Generation, text.Replace("\u0001", "x"), now, attentionRequired: true, outcomeEntryId: Guid.NewGuid());
        var artifact = new ArtifactRecord(Guid.NewGuid(), sessionId, text, text, 20, "not-projected", null, "/private/not-projected", now);
        var foreign = artifact with { ArtifactId = Guid.NewGuid(), SessionId = Guid.NewGuid() };
        var evidence = BackgroundCompletionProjection.Build(run, text + text, [artifact, foreign, artifact, artifact, artifact]);
        Assert.InRange(Encoding.UTF8.GetByteCount(evidence), 1, AgentRunLimits.MaxEvidenceBytes);
        var input = JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(evidence)!;
        Assert.Equal(TriggerKind.BackgroundCompleted, input.TriggerKind);
        using var projection = JsonDocument.Parse(input.Text!);
        Assert.Equal(sessionId, projection.RootElement.GetProperty("childSessionId").GetGuid());
        Assert.Equal(run.AgentRunId, projection.RootElement.GetProperty("initialAgentRunId").GetGuid());
        Assert.True(projection.RootElement.GetProperty("attentionRequired").GetBoolean());
        Assert.Equal(3, projection.RootElement.GetProperty("artifacts").GetArrayLength());
        Assert.DoesNotContain(foreign.ArtifactId.ToString(), evidence);
        Assert.DoesNotContain("not-projected", evidence);
    }
}
