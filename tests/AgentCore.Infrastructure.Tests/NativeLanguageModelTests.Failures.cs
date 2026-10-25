using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class NativeLanguageModelTests
{
    [Theory]
    [InlineData(ModelProviderKind.Anthropic, ModelToolChoice.Required)] [InlineData(ModelProviderKind.Anthropic, ModelToolChoice.Named)]
    [InlineData(ModelProviderKind.Google, ModelToolChoice.Required)] [InlineData(ModelProviderKind.Google, ModelToolChoice.Named)]
    public async Task Native_supported_forced_choices_retain_the_requested_wire_mode(ModelProviderKind kind, ModelToolChoice choice)
    {
        using var f = new NativeFixture(kind, ToolRound(kind));
        var route = kind == ModelProviderKind.Anthropic ? f.Route with { ExternalModelId = "claude-haiku-5-5" } : f.Route;
        var result = await Collect(f.Model(forced: true, route: route), Request() with { ToolChoice = choice, ToolChoiceName = Echo.Name });
        Assert.Equal(2, result.OfType<ModelToolCallEvent>().Count()); Assert.DoesNotContain(result, e => e is ModelFailed);
        var body = Assert.Single(f.Handler.Requests);
        Assert.Equal(kind == ModelProviderKind.Anthropic ? choice == ModelToolChoice.Named ? "tool" : "any" : "ANY",
            kind == ModelProviderKind.Anthropic ? body.GetProperty("tool_choice").GetProperty("type").GetString()
                : body.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
    }
    [Theory]
    [InlineData(ModelProviderKind.Anthropic)] [InlineData(ModelProviderKind.Google)]
    public async Task Native_truncated_tool_round_never_publishes_executable_calls(ModelProviderKind kind)
    {
        var wire = kind == ModelProviderKind.Anthropic ? ToolRound(kind).Replace(Data(new { type = "message_stop" }), "", StringComparison.Ordinal)
            : ToolRound(kind).Replace("\"finishReason\":\"STOP\"", "\"finishReason\":\"MAX_TOKENS\"", StringComparison.Ordinal);
        using var f = new NativeFixture(kind, wire);
        var result = await Collect(f.Model(), Request());
        Assert.Equal(ProviderErrorCode.InvalidResponse, Assert.Single(result.OfType<ModelFailed>()).Failure.Code);
        Assert.DoesNotContain(result, e => e is ModelToolCallEvent or ModelCompleted); Assert.Single(f.Handler.Requests);
    }
    [Theory]
    [InlineData(ModelProviderKind.Anthropic)] [InlineData(ModelProviderKind.Google)]
    public async Task Native_midstream_failure_retains_only_normalized_partial_text_and_never_retries(ModelProviderKind kind)
    {
        var wire = kind == ModelProviderKind.Anthropic
            ? Data(new { type = "message_start", message = new { role = "assistant" } }) + Data(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
                + Data(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "partial" } })
                + Data(new { type = "error", error = new { type = "rate_limit_error", message = "private-native-api-key raw provider body" } })
            : Data(new { candidates = new[] { new { content = new { role = "model", parts = new[] { new { text = "partial" } } } } } })
                + Data(new { error = new { code = 429, message = "private-native-api-key raw provider body" } });
        using var f = new NativeFixture(kind, wire);
        var result = await Collect(f.Model(), Request() with { ResponseContract = null });
        Assert.Equal("partial", Assert.Single(result.OfType<ModelTextDelta>()).Text);
        var failure = Assert.Single(result.OfType<ModelFailed>()); Assert.Equal(ProviderErrorCode.RateLimited, failure.Failure.Code);
        Assert.DoesNotContain("private-native-api-key", JsonSerializer.Serialize(result));
        Assert.DoesNotContain(result, e => e is ModelToolCallEvent or ModelCompleted); Assert.Single(f.Handler.Requests);
    }
    [Theory]
    [InlineData(401, ProviderErrorCode.Authentication)] [InlineData(429, ProviderErrorCode.RateLimited)] [InlineData(503, ProviderErrorCode.Unavailable)]
    public async Task Native_HTTP_failures_have_safe_taxonomy_and_no_repeated_POST(int status, ProviderErrorCode expected)
    {
        using var f = new NativeFixture(ModelProviderKind.Anthropic);
        f.Handler.OnSend = (_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-native-api-key raw provider prompt", Encoding.UTF8) };
            if (status == 429) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2)); return Task.FromResult(response);
        };
        var result = await Collect(f.Model(), Request()); var failure = Assert.Single(result.OfType<ModelFailed>());
        Assert.Equal(expected, failure.Failure.Code); Assert.DoesNotContain("private-native-api-key", failure.Failure.SafeMessage);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(2), failure.Failure.RetryAfter);
        Assert.Single(f.Handler.Requests); Assert.DoesNotContain(result, e => e is ModelCompleted or ModelToolCallEvent);
    }
    [Theory]
    [InlineData(ModelProviderKind.Anthropic)] [InlineData(ModelProviderKind.Google)]
    public async Task Native_setup_and_idle_deadlines_and_user_cancellation_settle_without_calls(ModelProviderKind kind)
    {
        using (var setup = new NativeFixture(kind))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            setup.Handler.OnSend = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); };
            var task = Collect(setup.Model(timeouts: new() { SetupSeconds = 3 }), Request());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); setup.Time.Advance(TimeSpan.FromSeconds(4));
            var result = await task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(ProviderErrorCode.Timeout, Assert.Single(result.OfType<ModelFailed>()).Failure.Code);
        }
        foreach (var userCancel in new[] { false, true })
        {
            using var f = new NativeFixture(kind); using var cancellation = new CancellationTokenSource(); var stream = new PendingNativeStream();
            f.Handler.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            var task = Collect(f.Model(timeouts: new() { StreamIdleSeconds = 3, TotalSeconds = 20 }), Request(), cancellation.Token);
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (userCancel) cancellation.Cancel(); else f.Time.Advance(TimeSpan.FromSeconds(4));
            var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(userCancel ? ProviderErrorCode.Cancelled : ProviderErrorCode.Timeout, Assert.Single(result.OfType<ModelFailed>()).Failure.Code);
            Assert.DoesNotContain(result, e => e is ModelCompleted or ModelToolCallEvent); Assert.Single(f.Handler.Requests);
        }
    }
    [Theory]
    [InlineData(ModelProviderKind.Anthropic)] [InlineData(ModelProviderKind.Google)]
    public async Task Native_late_authority_revocation_withholds_the_validated_tool_round(ModelProviderKind kind)
    {
        using var f = new NativeFixture(kind, ToolRound(kind)); var checks = 0;
        var result = await Collect(f.Model(authorize: _ => ++checks >= 4 ? ValueTask.FromException(new AccessDeniedException("revoked")) : ValueTask.CompletedTask), Request());
        Assert.Equal(ProviderErrorCode.Authentication, Assert.Single(result.OfType<ModelFailed>()).Failure.Code);
        Assert.DoesNotContain(result, e => e is ModelToolCallEvent or ModelCompleted);
    }
    [Theory]
    [InlineData(ModelProviderKind.Anthropic)] [InlineData(ModelProviderKind.Google)]
    public async Task Native_reasoning_maps_only_declared_levels_and_plain_text_hierarchy_is_not_silently_upgraded(ModelProviderKind kind)
    {
        using var f = new NativeFixture(kind, FinalRound(kind, Final));
        var result = await Collect(new SemanticResponseLanguageModel(f.Model()), Request() with { ReasoningEffort = "high" });
        Assert.Single(result.OfType<ModelSemanticResponseReady>());
        var body = Assert.Single(f.Handler.Requests);
        Assert.Equal(kind == ModelProviderKind.Anthropic ? "high" : "HIGH", kind == ModelProviderKind.Anthropic
            ? body.GetProperty("output_config").GetProperty("effort").GetString() : body.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        var unsupported = new ModelCapabilityProfile(true, true, ModelInstructionHierarchy.Unsupported, ModelSemanticMode.NativeStructuredOutput,
            true, false, [], null, ModelMetadataSource.Manual);
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, Assert.Single((await Collect(f.Model(profile: unsupported), Request())).OfType<ModelFailed>()).Failure.Code);
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, Assert.Single((await Collect(f.Model(), Request() with { ReasoningEffort = "unverified" })).OfType<ModelFailed>()).Failure.Code);
        if (kind == ModelProviderKind.Google)
            Assert.Equal(ProviderErrorCode.UnsupportedCapability, Assert.Single((await Collect(f.Model(profile: unsupported with
                { Hierarchy = ModelInstructionHierarchy.OrderedTrustedSections, ReasoningEfforts = ["minimal", "high"], DefaultReasoningEffort = "high" }),
                Request() with { ReasoningEffort = "minimal" })).OfType<ModelFailed>()).Failure.Code);
        Assert.Single(f.Handler.Requests);
    }
    [Theory]
    [InlineData("claude-haiku-5-5")] [InlineData("claude-sonnet-5-5")]
    public async Task Native_current_Claude_rejects_explicit_sampling_before_dispatch(string modelId)
    {
        using var f = new NativeFixture(ModelProviderKind.Anthropic);
        var result = await Collect(f.Model(route: f.Route with { ExternalModelId = modelId }), Request() with { Temperature = 0 });
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, Assert.Single(result.OfType<ModelFailed>()).Failure.Code);
        Assert.Empty(f.Handler.Requests);
    }
    private sealed class PendingNativeStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
    }
}
