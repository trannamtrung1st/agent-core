using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class RunContextCompactionTests
{
    public const string Summary = """{"objective":"Finish the original request","completed":"Read evidence; outcomes still require exact receipts","decisions":"Use verified sources","pending":"Verify remaining work","constraints":"Keep user constraints","next":"Continue","uncertainty":"Summary is not proof"}""";
    private static ModelMessage[] Messages(int rounds = 6) => Enumerable.Range(0, rounds).SelectMany(i => new ModelMessage[] {
        new(ModelRole.Assistant, "", ToolCalls: [new("call-" + i, "email.send", "{\"draftId\":\"exact-draft-" + i + "\"}", "opaque-" + i)]),
        new(ModelRole.Tool, "receipt-" + i + ":" + new string('x', 16000), ToolCallId: "call-" + i, Name: "email.send") }).ToArray();

    [Fact]
    public void Large_tool_arguments_trigger_model_input_pressure_before_checkpoint_pressure()
    {
        var messages = Messages(3);
        messages[0] = messages[0] with { ToolCalls = [new("call-0", "email.send",
            System.Text.Json.JsonSerializer.Serialize(new { body = new string('x', 100000) }))] };
        var request = ContextAssembler.Create(Guid.NewGuid(), new EffectiveContextSnapshot([
            ContextSection.Core(CoreProcedure.OperatingContract)
        ], purpose: ContextPurpose.Platform), messages);
        Assert.True(request.ContextManifest!.Sections.Sum(s => s.EstimatedTokens) < 24000);
        var projected = RunContextCompaction.EstimateInputTokens(request);
        Assert.True(projected > 24000);
        Assert.Equal(2, RunContextCompaction.SelectBoundary(messages, null, true, projected * 4, checkpointBytes: 1000));
    }

    [Fact]
    public void Stored_compaction_is_lossless_for_proof_readers_but_model_sees_summary_and_recent_closed_batches()
    {
        var messages = Messages();
        var boundary = RunContextCompaction.SelectBoundary(messages, null, true, 120000, 190000);
        Assert.Equal(8, boundary);
        var state = new ToolLoopExecutionState(TranscriptId: Guid.NewGuid(), Compaction: new(1, Summary, boundary, 1, 30000, 9000), CompactionAttempts: 1);
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write(messages, executionState: state), 6, 2000, 50000, 5000);
        Assert.True(Encoding.UTF8.GetByteCount(checkpoint.PayloadJson) < 40000);
        Assert.DoesNotContain(new string('x', 16000), JsonNode.Parse(checkpoint.PayloadJson)!["ArchivedMessages"]!.GetValue<string>());
        Assert.True(AgentRunToolCallCheckpoint.TryRead(checkpoint, out var restored));
        Assert.Equal(messages.Select(m => m.Text), restored!.Select(m => m.Text));
        Assert.Equal(messages.SelectMany(m => m.ToolCalls ?? []), restored!.SelectMany(m => m.ToolCalls ?? []));
        Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(restored!));
        Assert.Equal(6, AgentRunToolCallCheckpoint.NormalizeResumedStepCount(checkpoint.StepCount, restored!));
        var context = new EffectiveContextSnapshot([ContextSection.Core(CoreProcedure.OperatingContract),
            ContextSection.Runtime(ContextSectionId.AgentIdentity, ContextSourceKind.Definition, "Identity"),
            ContextSection.Runtime(ContextSectionId.AgentInstructions, ContextSourceKind.Definition, "Mandatory policy")]);
        var original = ContextAssembler.Create(state.TranscriptId!.Value, context, [new(ModelRole.User, "Original task")]);
        var projected = RunContextCompaction.Project(original, original.Interaction!, restored!, state.Compaction);
        Assert.Equal(original.ResponseId, projected.ResponseId);
        Assert.Equal("Mandatory policy", projected.Context!.Text(ContextSectionId.AgentInstructions));
        Assert.Equal(ContextSemantics.UntrustedData, projected.Context.Sections.Single(s => s.Id == ContextSectionId.RunSummary).Semantics);
        Assert.Equal(["call-4", "call-5"], projected.Interaction!.Where(m => m.Role == ModelRole.Tool).Select(m => m.ToolCallId));
        Assert.Single(projected.Interaction!, m => m.Text == "Original task");
        Assert.Contains(restored!, m => m.ToolCallId == "call-0" && m.Text.StartsWith("receipt-0:"));
    }

    [Fact]
    public void Compacted_browser_operation_hashes_remain_exact_recovery_anchors()
    {
        var messages = Messages();
        var call = new ModelToolCall("exact-browser", "browser.click", "{\"target\":\"verified-reference\"}");
        messages[0] = new(ModelRole.Assistant, "", ToolCalls: [call]);
        messages[1] = new(ModelRole.Tool, "{\"status\":\"ok\",\"effectConfirmedBySdk\":true}", ToolCallId: call.Id, Name: call.Name);
        var state = new ToolLoopExecutionState(Compaction: new(1, Summary, 8, 1, 30000, 9000));
        var payload = AgentRunToolCallCheckpoint.Write(messages, executionState: state);
        var hash = AgentRunActionHash.Compute(call.Name, System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(call.ArgumentsJson));
        Assert.True(AgentRunActionHash.MatchesBrowserInteraction(payload, hash));
        Assert.False(AgentRunActionHash.MatchesBrowserInteraction(payload, new string('f', 64)));
        var checkpoint = new AgentRunCheckpoint(payload, 6, 0, 50000);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(checkpoint, out var proof));
        Assert.Equal(call, proof![0].ToolCalls![0]);
        Assert.Equal(messages[1].Text, proof[1].Text);
    }

    [Fact]
    public void Run_summary_survives_optional_history_pressure_without_becoming_instruction()
    {
        var context = new EffectiveContextSnapshot([ContextSection.Core(CoreProcedure.OperatingContract),
            ContextSection.Evidence(ContextSectionId.History, ContextSourceKind.Session, new string('x', ContextAssembler.MaxOptionalEvidenceCharacters)),
            ContextSection.Evidence(ContextSectionId.RunSummary, ContextSourceKind.AgentRun, Summary)], purpose: ContextPurpose.Platform);
        var request = ContextAssembler.Create(Guid.NewGuid(), context);
        Assert.Contains(request.Messages, m => m.Text.Contains(Summary));
        Assert.True(request.ContextManifest!.Sections.Single(s => s.Id == "History").Omitted);
        Assert.False(request.ContextManifest.Sections.Single(s => s.Id == "RunSummary").Omitted);
        Assert.All(request.Messages.Where(m => m.Text.Contains(Summary)), m => Assert.Equal(ModelRole.User, m.Role));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, true)]
    public void Unsafe_or_pending_round_never_compacts(bool safe, bool pending)
    {
        var messages = Messages().ToList();
        if (pending) messages.Add(new(ModelRole.Assistant, "", ToolCalls: [new("pending", "email.send", "{}")]));
        Assert.Equal(0, RunContextCompaction.SelectBoundary(messages, null, safe, 120000, 200000, force: true));
    }
    [Fact]
    public void Repeated_pressure_requires_new_growth_and_total_cap_is_preserved()
    {
        var messages = Messages();
        var state = new RunContextSummary(1, Summary, 8, 1, 30000, 9000);
        Assert.Equal(0, RunContextCompaction.SelectBoundary(messages, state, true, 120000, 190000));
        Assert.Equal(0, RunContextCompaction.SelectBoundary(messages, state with { Count = 8 }, true, 120000, 190000, true));
        Assert.Equal(0, RunContextCompaction.SelectBoundary(messages, null, true, 1000, 1000));
    }
    [Theory] [InlineData("{broken")] [InlineData("{}")] [InlineData("{\"objective\":\"invented success\"}")]
    public void Invalid_summary_cannot_be_accepted(string text) => Assert.False(RunContextCompaction.ValidSummary(text));

    [Fact]
    public void Prior_checkpoint_format_cannot_resume_or_replay_after_the_cutover()
    {
        var json = JsonNode.Parse(AgentRunToolCallCheckpoint.Write(Messages()))!;
        json["ContextFormatVersion"] = 2;
        var checkpoint = new AgentRunCheckpoint(json.ToJsonString(), 6, 0, 50000);
        Assert.False(AgentRunToolCallCheckpoint.TryRead(checkpoint, out _));
        var failure = Assert.Throws<InvalidOperationException>(() => AgentRunToolCallCheckpoint.EnsureCurrentContextContract(checkpoint));
        Assert.Contains("no tools were replayed", failure.Message);
    }

    [Fact]
    public void Corrupt_or_missing_exact_proof_is_rejected_even_when_summary_claims_success()
    {
        var state = new ToolLoopExecutionState(Compaction: new(1, Summary, 8, 1, 30000, 9000));
        var json = JsonNode.Parse(AgentRunToolCallCheckpoint.Write(Messages(), executionState: state))!;
        json["ArchivedMessagesHash"] = new string('0', 64);
        Assert.False(AgentRunToolCallCheckpoint.TryRead(new(json.ToJsonString(), 6, 0, 50000), out _));
        json["ArchivedMessages"] = null;
        Assert.False(AgentRunToolCallCheckpoint.TryRead(new(json.ToJsonString(), 6, 0, 50000), out _));
    }

    [Fact]
    public void Wait_result_after_reopen_preserves_archived_receipts_and_pending_call_identity()
    {
        var messages = Messages().Append(new ModelMessage(ModelRole.Assistant, "", ToolCalls: [new("wait", "execution.wait", "{}")] )).ToArray();
        var state = new ToolLoopExecutionState(Compaction: new(1, Summary, 8, 1, 30000, 9000));
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write(messages, executionState: state), 7, 1000, 50000);
        var resumed = AgentRunToolCallCheckpoint.AppendWaitResult(checkpoint, "wait", "{\"ready\":true}");
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed, out var restored));
        Assert.Equal(14, restored!.Count); Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(restored!));
        Assert.Contains(restored!, m => m.ToolCallId == "call-0" && m.Text.StartsWith("receipt-0:"));
        Assert.Equal(checkpoint.StepCount, resumed.StepCount);
    }
}
