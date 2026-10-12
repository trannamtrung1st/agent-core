using AgentCore.Application.Ports;
using AgentCore.Application.Testing;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class ProviderTransportDiagnosticsTests
{
    [Fact]
    public async Task Disabled_adapter_loggers_leave_failure_ids_for_the_runtime_to_assign()
    {
        using var http = new HttpClient(new FailingHandler());
        var model = new OpenAICompatibleLanguageModel(http,
            new LanguageModelProviderOptions { BaseUrl = "https://example.test/v1/", ApiKey = "private-key", DefaultModel = "fixture" },
            logger: NullLogger<OpenAICompatibleLanguageModel>.Instance);
        await foreach (var item in model.GenerateAsync(new(Guid.NewGuid(), [new(ModelRole.User, "private")])))
            Assert.Null(Assert.IsType<ModelFailed>(item).DiagnosticId);
        var speech = new OpenAiSpeechSynthesizer(http,
            new SpeechSynthesisProviderOptions { BaseUrl = "https://example.test/v1/", ApiKey = "private-key" }, NullLogger<OpenAiSpeechSynthesizer>.Instance);
        await foreach (var item in speech.SynthesizeAsync(new(Guid.NewGuid(), 0, 0, "private", "alloy", 1, CanonicalAudio.Format)))
            Assert.Null(Assert.IsType<SpeechSynthesisFailed>(item).DiagnosticId);
    }

    [Fact]
    public async Task Model_transport_failure_keeps_safe_result_and_exception_diagnostics()
    {
        var logs = new DiagnosticLogCapture<OpenAICompatibleLanguageModel>();
        using var http = new HttpClient(new FailingHandler());
        var model = new OpenAICompatibleLanguageModel(http,
            new LanguageModelProviderOptions { BaseUrl = "https://example.test/v1/", ApiKey = "private-key", DefaultModel = "fixture" }, logger: logs);
        var responseId = Guid.NewGuid();
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(new ModelRequest(responseId, [new(ModelRole.User, "private-prompt")]))) events.Add(item);
        var failure = Assert.IsType<ModelFailed>(Assert.Single(events));
        Assert.Equal(ProviderErrorCode.Unavailable, failure.Failure.Code);
        AssertDiagnostic(logs.Entries.Where(e => e.Level == LogLevel.Warning), "llm.setup", failure.DiagnosticId);
    }

    [Fact]
    public async Task Speech_transport_failure_keeps_safe_result_and_exception_diagnostics()
    {
        var logs = new DiagnosticLogCapture<OpenAiSpeechSynthesizer>();
        using var http = new HttpClient(new FailingHandler());
        var speech = new OpenAiSpeechSynthesizer(http,
            new SpeechSynthesisProviderOptions { BaseUrl = "https://example.test/v1/", ApiKey = "private-key" }, logs);
        var events = new List<SpeechSynthesisEvent>();
        await foreach (var item in speech.SynthesizeAsync(new(Guid.NewGuid(), 0, 0, "private-speech", "alloy", 1, CanonicalAudio.Format))) events.Add(item);
        var failure = Assert.IsType<SpeechSynthesisFailed>(Assert.Single(events));
        Assert.Equal(ProviderErrorCode.Unavailable, failure.Failure.Code);
        AssertDiagnostic(logs.Entries, "tts.setup", failure.DiagnosticId);
    }

    private static void AssertDiagnostic(IEnumerable<DiagnosticLogEntry> entries, string operation, Guid? diagnosticId)
    {
        var entry = Assert.Single(entries);
        Assert.Equal(operation, entry.Properties["Operation"]);
        Assert.Equal(nameof(ProviderErrorCode.Unavailable), entry.Properties["ErrorCode"]);
        Assert.Equal("provider", entry.Properties["ErrorCategory"]);
        Assert.NotNull(diagnosticId);
        Assert.Equal(diagnosticId, entry.Properties["DiagnosticId"]);
        Assert.Contains(nameof(HttpRequestException), entry.Exception!.ToString());
        Assert.DoesNotContain("private", entry.Exception.ToString());
        Assert.DoesNotContain("private", entry.Message);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("private-prompt private-speech private-key https://example.test/?token=private-token");
    }
}
