using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class InvalidToolCallRecoveryTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"cursor\":null,\"limit\":null}")]
    [InlineData("{\"cursor\":\"  \"}")]
    public void Credential_first_page_accepts_absent_optional_values(string json)
    {
        var request = CredentialDiscovery.Parse(JsonSerializer.Deserialize<JsonElement>(json));
        Assert.Null(request.Cursor); Assert.Equal(20, request.Limit);
    }

    [Theory]
    [InlineData("{\"cursor\":42}")]
    [InlineData("{\"limit\":0}")]
    [InlineData("{\"limit\":101}")]
    [InlineData("{\"cursor\":\"../invalid\"}")]
    [InlineData("{\"limit\":1,\"limit\":2}")]
    public void Credential_listing_preserves_validation_of_nonempty_values(string json) =>
        Assert.Throws<AgentCoreException>(() => CredentialDiscovery.Parse(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public void Restore_ignores_other_tool_result_shapes_and_non_core_metadata()
    {
        var receipts = new[] { "[]", "null", "\"plain result\"", "{\"invalidCallRecovery\":\"untrusted data\"}",
            "{\"invalidCallRecovery\":{\"strategy\":42,\"attempt\":\"not a count\"}}" }
            .Select(text => new ModelMessage(ModelRole.Tool, text));
        var recovery = new InvalidToolCallRecovery(receipts);
        Assert.False(recovery.Exhausted);
        Assert.Null(recovery.Refuse(new("fresh", ToolCatalog.BrowserFind, "{}")));
    }

    [Fact]
    public void Small_checkpoint_result_budget_preserves_recovery_counters()
    {
        var call = new ModelToolCall("one", ToolCatalog.BrowserFind, "{}");
        var recovery = new InvalidToolCallRecovery([]);
        var result = recovery.Note(call, "{\"error\":\"invalid\"}", out _);
        var one = InvalidToolCallRecovery.FitReceipt(call, result, 256);
        result = recovery.Note(call, "{\"error\":\"invalid\"}", out _);
        var two = InvalidToolCallRecovery.FitReceipt(call, result, 256);
        Assert.NotNull(one); Assert.NotNull(two);
        var restored = new InvalidToolCallRecovery([new(ModelRole.Tool, one!, Name: call.Name), new(ModelRole.Tool, two!, Name: call.Name)]);
        Assert.NotNull(restored.Refuse(call));
    }

    [Fact]
    public void Provider_rejected_query_is_bounded_while_a_corrected_query_remains_available()
    {
        var recovery = new InvalidToolCallRecovery([]);
        var call = new ModelToolCall("one", ToolCatalog.BrowserFind, "{\"by\":\"role\",\"value\":\"invented-role\"}");
        for (var i = 0; i < 2; i++) recovery.Note(call, "{\"error\":\"invalid\"}", out _);
        Assert.NotNull(recovery.Refuse(call));
        Assert.Null(recovery.Refuse(call with { ArgumentsJson = "{\"by\":\"role\",\"value\":\"textbox\"}" }));
        const string unrelated = "{\"error\":{\"code\":\"private-value\"}}";
        Assert.Equal(unrelated, recovery.Note(call, unrelated, out _));
    }

    [Fact]
    public void Changing_malformed_json_does_not_reset_the_shared_recovery_budget()
    {
        var recovery = new InvalidToolCallRecovery([]);
        var first = new ModelToolCall("one", ToolCatalog.BrowserFind, "{broken");
        var receipt = recovery.Note(first, "{\"error\":\"invalid\"}", out _);
        Assert.Contains("by", receipt); Assert.Contains("value", receipt);
        recovery.Note(first with { ArgumentsJson = "another broken JSON" }, "{\"error\":\"invalid\"}", out _);
        Assert.NotNull(recovery.Refuse(first with { ArgumentsJson = "{different" }));
        Assert.Null(recovery.Refuse(first with { ArgumentsJson = "{\"by\":\"text\",\"value\":\"Email\"}" }));
    }

    [Fact]
    public void Equivalent_malformed_find_is_bounded_across_checkpoint_and_valid_alternative_remains_allowed()
    {
        var first = new ModelToolCall("one", ToolCatalog.BrowserFind, """{"role":"textbox","label":"Email"}""");
        var second = first with { Id = "two", ArgumentsJson = """{"label":"Another email","role":"textbox"}""" };
        var recovery = new InvalidToolCallRecovery([]);
        Assert.Null(recovery.Refuse(first));
        var one = recovery.Note(first, """{"error":"invalid","message":"Choose by and value."}""", out _);
        Assert.Null(recovery.Refuse(second));
        var two = recovery.Note(second, """{"error":"invalid","message":"Choose by and value."}""", out _);
        Assert.Contains("Second equivalent failure", two);
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([
            new(ModelRole.Assistant, "", ToolCalls: [first, second]),
            new(ModelRole.Tool, one, ToolCallId: first.Id, Name: first.Name),
            new(ModelRole.Tool, two, ToolCallId: second.Id, Name: second.Name)]), 2, 0, 10000);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(checkpoint, out var restored));
        recovery = new InvalidToolCallRecovery(restored!);
        var blocked = recovery.Refuse(first);
        Assert.Contains("invalid_tool_strategy_blocked", blocked);
        recovery.Note(first, blocked!, out var exhausted);
        Assert.False(exhausted); // One opportunity for a different valid discovery remains.
        Assert.Null(recovery.Refuse(first with { ArgumentsJson = """{"by":"role","value":"textbox","name":"Email"}""" }));
        recovery.Note(first, recovery.Refuse(first)!, out exhausted);
        Assert.True(exhausted);
    }

    [Fact]
    public void Shared_recovery_preserves_corrected_values_and_does_not_count_policy_denials()
    {
        var call = new ModelToolCall("one", ToolCatalog.BrowserType, """{"ref":"bad","text":"private-value"}""");
        var recovery = new InvalidToolCallRecovery([]);
        for (var i = 0; i < 2; i++) recovery.Note(call, """{"error":"invalid_reference"}""", out _);
        Assert.NotNull(recovery.Refuse(call));
        Assert.Null(recovery.Refuse(call with { ArgumentsJson = """{"ref":"el_0123456789abcdefghijkl","text":"private-value"}""" }));
        var denial = """{"error":"target_denied"}""";
        Assert.Equal(denial, recovery.Note(call, denial, out _));
        Assert.DoesNotContain("private-value", recovery.Note(call, """{"error":"invalid_reference"}""", out _));
    }

    [Fact]
    public void Execution_facts_never_promote_untrusted_values_or_login_claims()
    {
        var receipts = new ModelMessage[]
        {
            new(ModelRole.Tool, """{"status":"ok","url":"https://private.test/token","content":"Authenticated password secret","ref":"el_private"}""", Name: ToolCatalog.BrowserFillCredential),
            new(ModelRole.Tool, """{"status":"ok","content":"signed in"}""", Name: ToolCatalog.BrowserClick),
            new(ModelRole.Tool, """{"error":"invented-secret-code"}""", Name: ToolCatalog.BrowserFind)
        };
        var facts = RunExecutionFacts.Current(Guid.NewGuid(), [ToolCatalog.BrowserFillCredential, "unregistered-secret"], receipts);
        Assert.Contains("protected field fill", facts);
        Assert.Contains("do not establish sign-in", facts);
        Assert.Contains("succeeded", facts);
        foreach (var sensitive in new[] { "private.test", "Authenticated password secret", "el_private", "signed in", "invented-secret-code", "unregistered-secret" })
            Assert.DoesNotContain(sensitive, facts);
    }
}
