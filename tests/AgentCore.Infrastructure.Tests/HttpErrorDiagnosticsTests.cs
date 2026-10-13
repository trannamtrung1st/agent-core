using System.Net;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Http;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed class HttpErrorDiagnosticsTests
{
    [Theory]
    [InlineData("{\"error\":{\"code\":\"insufficient_quota\",\"type\":\"invalid_request_error\",\"message\":\"private prompt\"}}", "insufficient_quota", "unknown", "parsed")]
    [InlineData("{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked\"}", "invalid_grant", "credential_expired", "parsed")]
    [InlineData("{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"rateLimitExceeded\"}]}}", "403", "ratelimitexceeded", "parsed")]
    [InlineData("{\"error\":{\"message\":\"Insufficient credits: user@example.com sk-private-key\"}}", "", "insufficient_credit", "parsed")]
    [InlineData("{\"error\":{\"code\":\"sk-private-key\",\"type\":\"private prompt\"}}", "", "unknown", "parsed")]
    [InlineData("<html>private upstream error</html>", "", "unknown", "invalid_json")]
    [InlineData("[]", "", "unknown", "unsupported")]
    [InlineData("", "", "unknown", "empty")]
    public void Parses_only_safe_error_metadata(string body, string code, string reason, string state)
    {
        var parsed = HttpErrorDiagnostics.Parse(Encoding.UTF8.GetBytes(body));
        Assert.Equal(code, parsed.Code);
        Assert.Equal(reason, parsed.Reason);
        Assert.Equal(state, parsed.State);
        Assert.DoesNotContain("private", parsed.ToString());
    }

    [Theory]
    [InlineData(true, 429, 1)]
    [InlineData(false, 429, 0)]
    [InlineData(true, 200, 0)]
    public async Task Factory_pipeline_preserves_response_and_respects_configuration(bool enabled, int status, int warnings)
    {
        var logs = new Logs();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:HttpErrorResponses:Enabled"] = enabled.ToString()
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IDiagnosticIdSource, SystemDiagnosticIdSource>();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddHttpErrorDiagnostics();
        var body = "{\"error\":{\"code\":\"insufficient_quota\",\"message\":\"private echoed prompt\"}}";
        var content = new CountingContent(Encoding.UTF8.GetBytes(body));
        services.AddHttpClient("fixture").WithErrorDiagnostics().ConfigurePrimaryHttpMessageHandler(() => new ResponseHandler(status, content));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("fixture");
        using var response = await client.GetAsync("https://private.example/path?token=secret", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(enabled && status >= 400, content.Reads > 0);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(warnings, logs.Warnings.Count);
        if (warnings > 0)
        {
            Assert.Contains("insufficient_quota", logs.Warnings[0]);
            Assert.DoesNotContain("private", logs.Warnings[0]);
            Assert.DoesNotContain("secret", logs.Warnings[0]);
        }
    }

    [Fact]
    public async Task Oversized_chunked_error_is_bounded_and_replayed_in_full()
    {
        var body = Encoding.UTF8.GetBytes(new string('x', HttpErrorDiagnostics.MaxBodyBytes * 3));
        var content = new CountingContent(body);
        var logs = new Logs();
        using var handler = new HttpErrorDiagnosticsHandler(new(new(), logs, new SystemDiagnosticIdSource()), "fixture")
        { InnerHandler = new ResponseHandler(502, content) };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://example.test", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpErrorDiagnostics.MaxBodyBytes + 1, content.BytesRead);
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("too_large", Assert.Single(logs.Warnings));
    }

    [Theory]
    [InlineData("openai-compatible-llm")]
    [InlineData("openai-compatible-batch-stt")]
    [InlineData("openai-speech-tts")]
    [InlineData("BraveWebSearch")]
    [InlineData("GmailEmail")]
    [InlineData("GoogleEmailOAuth")]
    public async Task All_production_named_clients_use_the_shared_handler(string name)
    {
        var logs = new Logs();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
        services.AddAgentCoreInfrastructure(Path.Combine(directory!.FullName, "agents"), "Synthetic");
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new ResponseHandler(401,
            new StringContent("{\"error\":{\"code\":\"invalid_api_key\"}}", Encoding.UTF8, "application/json")));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var response = await client.GetAsync("https://example.test", HttpCompletionOption.ResponseHeadersRead);
        Assert.Contains("invalid_api_key", Assert.Single(logs.Warnings));
        Assert.Contains(name, logs.Warnings[0]);
    }

    [Fact]
    public async Task Diagnostic_deadline_returns_the_original_error_and_replays_its_prefix()
    {
        var logs = new Logs();
        var content = new StallingContent();
        using var handler = new HttpErrorDiagnosticsHandler(new(new(), logs, new SystemDiagnosticIdSource()), "fixture")
        { InnerHandler = new ResponseHandler(503, content) };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://example.test", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("timeout", Assert.Single(logs.Warnings));
        using var stream = await response.Content.ReadAsStreamAsync();
        var bytes = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(bytes));
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_and_disposes_the_response()
    {
        var logs = new Logs();
        var content = new StallingContent();
        using var handler = new HttpErrorDiagnosticsHandler(new(new(), logs, new SystemDiagnosticIdSource()), "fixture")
        { InnerHandler = new ResponseHandler(503, content) };
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var send = client.GetAsync("https://example.test", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await content.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.True(content.Disposed);
        Assert.Empty(logs.Warnings);
    }

    [Fact]
    public async Task Disabled_switch_prevents_legacy_model_body_reads_even_with_local_message_opt_in()
    {
        var logs = new Logs();
        var content = new CountingContent(Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"private prompt\"}}"));
        using var handler = new HttpErrorDiagnosticsHandler(new(new() { Enabled = false }, logs, new SystemDiagnosticIdSource()), "fixture")
        { InnerHandler = new ResponseHandler(400, content) };
        using var http = new HttpClient(handler);
        var model = new OpenAICompatibleLanguageModel(http, new() { BaseUrl = "https://example.test/v1/", DefaultModel = "fixture", LogProviderErrorMessages = true });
        await foreach (var item in model.GenerateAsync(new(Guid.NewGuid(), [new(ModelRole.User, "private prompt")])))
            Assert.Equal(ProviderErrorCode.InvalidRequest, Assert.IsType<ModelFailed>(item).Failure.Code);
        Assert.Equal(0, content.Reads);
        Assert.Empty(logs.Warnings);
    }

    [Fact]
    public async Task Model_reuses_timed_out_inspection_instead_of_reading_the_error_again()
    {
        var logs = new Logs();
        var content = new StallingContent { RejectRepeatedRead = true };
        using var handler = new HttpErrorDiagnosticsHandler(new(new(), logs, new SystemDiagnosticIdSource()), "fixture")
        { InnerHandler = new ResponseHandler(400, content) };
        using var http = new HttpClient(handler);
        var model = new OpenAICompatibleLanguageModel(http, new() { BaseUrl = "https://example.test/v1/", DefaultModel = "fixture", LogProviderErrorMessages = true });
        await foreach (var item in model.GenerateAsync(new(Guid.NewGuid(), [new(ModelRole.User, "fixture")])))
            Assert.Equal(ProviderErrorCode.InvalidRequest, Assert.IsType<ModelFailed>(item).Failure.Code);
        Assert.Equal(2, content.Reads);
        Assert.Contains("timeout", Assert.Single(logs.Warnings));
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"error\": []}")]
    [InlineData("<html>upstream error</html>")]
    public async Task Malformed_legacy_error_envelopes_still_return_normalized_failure(string body)
    {
        using var http = new HttpClient(new ResponseHandler(400, new StringContent(body)));
        var model = new OpenAICompatibleLanguageModel(http, new() { BaseUrl = "https://example.test/v1/", DefaultModel = "fixture" });
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(new(Guid.NewGuid(), [new(ModelRole.User, "fixture")]))) events.Add(item);
        Assert.Equal(ProviderErrorCode.InvalidRequest, Assert.IsType<ModelFailed>(Assert.Single(events)).Failure.Code);
    }

    [Fact]
    public async Task Real_loopback_http_error_is_logged_and_normalized_by_the_model_adapter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapPost("/v1/chat/completions", async context =>
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = "7";
            await context.Response.WriteAsJsonAsync(new { error = new { code = "rate_limit_exceeded", message = "Too many requests: private prompt" } });
        });
        await app.StartAsync();
        try
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var logs = new Logs();
            var services = new ServiceCollection();
            services.AddSingleton<IDiagnosticIdSource, SystemDiagnosticIdSource>();
            services.AddLogging(b => b.AddProvider(logs));
            services.AddHttpErrorDiagnostics();
            services.AddHttpClient(OpenAICompatibleLanguageModel.HttpClientName).WithErrorDiagnostics();
            using var provider = services.BuildServiceProvider();
            using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAICompatibleLanguageModel.HttpClientName);
            var model = new OpenAICompatibleLanguageModel(http, new() { BaseUrl = url + "/v1/", DefaultModel = "fixture" });
            var events = new List<ModelGenerationEvent>();
            await foreach (var item in model.GenerateAsync(new(Guid.NewGuid(), [new(ModelRole.User, "private prompt")]))) events.Add(item);
            var failure = Assert.IsType<ModelFailed>(Assert.Single(events));
            Assert.Equal(ProviderErrorCode.RateLimited, failure.Failure.Code);
            Assert.Equal(TimeSpan.FromSeconds(7), failure.Failure.RetryAfter);
            Assert.Contains("rate_limit_exceeded", Assert.Single(logs.Warnings));
            Assert.DoesNotContain("private prompt", failure.Failure.SafeMessage);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ResponseHandler(int status, HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content, RequestMessage = request });
    }

    private sealed class StallingContent : HttpContent
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public int Reads;
        public bool RejectRepeatedRead;
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new StallingStream(this));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        private sealed class StallingStream(StallingContent owner) : MemoryStream
        {
            private bool first = true;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.Reads++;
                if (owner.RejectRepeatedRead && owner.Reads > 2) throw new IOException("Error stream was read again after its diagnostic deadline.");
                if (first) { first = false; buffer.Span[0] = (byte)'{'; return 1; }
                owner.Blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }
    }

    private sealed class CountingContent : HttpContent
    {
        private readonly byte[] bytes;
        public int Reads;
        public int BytesRead;
        public CountingContent(byte[] bytes) { this.bytes = bytes; Headers.ContentType = new("application/json"); }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new CountingStream(this, bytes));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
        private sealed class CountingStream(CountingContent owner, byte[] bytes) : MemoryStream(bytes)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.Reads++;
                var read = Read(buffer.Span);
                owner.BytesRead += read;
                return ValueTask.FromResult(read);
            }
        }
    }

    private sealed class Logs : ILoggerProvider, ILogger<HttpErrorDiagnostics>
    {
        public List<string> Warnings { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception)); }
    }
}
