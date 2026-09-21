using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class GmailEmailProviderTests
{
    [Fact]
    public async Task Create_and_get_draft_preserve_bcc()
    {
        var handler = new GmailScriptedHandler();
        var provider = CreateProvider(handler);
        var created = await provider.CreateDraftAsync(
            new EmailCreateDraftRequest(
                ["to@example.test"],
                ["cc@example.test"],
                ["hidden@example.test"],
                "Exact subject",
                "Exact body"));
        Assert.Equal("draft-1", created.Draft.DraftId);

        var snapshot = await provider.GetDraftAsync("draft-1");
        Assert.NotNull(snapshot);
        Assert.Contains("to@example.test", snapshot!.To, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("cc@example.test", snapshot.Cc, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("hidden@example.test", snapshot.Bcc, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Exact subject", snapshot.Subject);
        Assert.Equal("Exact body", snapshot.Body);

        var parsedRaw = GmailMime.ParseDraft("draft-1", GmailMime.DecodeBase64Url(handler.StoredRaw!));
        Assert.Contains("hidden@example.test", parsedRaw.Bcc, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_draft_rejects_header_injection_before_http()
    {
        var handler = new GmailScriptedHandler();
        var provider = CreateProvider(handler);
        var ex = await Assert.ThrowsAsync<AgentCoreException>(() => provider.CreateDraftAsync(
            new EmailCreateDraftRequest(
                ["to@example.test"],
                [],
                [],
                "Hello\nBcc: evil@example.test",
                "Body")).AsTask());
        Assert.Equal("ValidationError", ex.Code);
        Assert.Equal(0, handler.DraftPostCount);
        Assert.Equal(0, handler.TokenPostCount);
    }

    [Fact]
    public async Task Create_draft_rejects_crlf_in_recipient()
    {
        var handler = new GmailScriptedHandler();
        var provider = CreateProvider(handler);
        await Assert.ThrowsAsync<AgentCoreException>(() => provider.CreateDraftAsync(
            new EmailCreateDraftRequest(
                ["to@example.test\r\nBcc: evil@example.test"],
                [],
                [],
                "Hello",
                "Body")).AsTask());
        Assert.Equal(0, handler.DraftPostCount);
    }

    [Fact]
    public async Task Get_draft_without_raw_rfc822_returns_null()
    {
        var handler = new GmailScriptedHandler { OmitRawOnGet = true };
        var provider = CreateProvider(handler);
        await provider.CreateDraftAsync(
            new EmailCreateDraftRequest(
                ["to@example.test"],
                [],
                ["hidden@example.test"],
                "Subject",
                "Body"));
        Assert.Null(await provider.GetDraftAsync("draft-1"));
    }

    [Fact]
    public async Task Send_posts_to_drafts_send_with_approved_raw_mime()
    {
        var handler = new GmailScriptedHandler();
        var provider = CreateProvider(handler);
        var approved = new EmailDraftSnapshot(
            "draft-1",
            ["to@example.test"],
            ["cc@example.test"],
            ["hidden@example.test"],
            "Exact subject",
            "Exact body");
        var result = await provider.SendDraftAsync(new EmailSendDraftRequest("draft-1", approved));

        Assert.Equal(EmailSendOutcome.Sent, result.Outcome);
        Assert.Equal("/gmail/v1/users/me/drafts/send", handler.LastSendPath);
        Assert.NotNull(handler.LastSendBody);
        using var document = JsonDocument.Parse(handler.LastSendBody!);
        Assert.Equal("draft-1", document.RootElement.GetProperty("id").GetString());
        var sentRaw = document.RootElement.GetProperty("message").GetProperty("raw").GetString();
        Assert.NotNull(sentRaw);
        var parsed = GmailMime.ParseDraft("draft-1", GmailMime.DecodeBase64Url(sentRaw));
        Assert.Contains("to@example.test", parsed.To, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("cc@example.test", parsed.Cc, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("hidden@example.test", parsed.Bcc, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Exact subject", parsed.Subject);
        Assert.Equal("Exact body", parsed.Body);
        Assert.Equal(0, handler.LegacyDraftIdSendCount);
    }

    [Fact]
    public async Task Send_cancellation_after_dispatch_is_indeterminate()
    {
        var handler = new GmailScriptedHandler { HangSend = true };
        var provider = CreateProvider(handler);
        using var cts = new CancellationTokenSource();
        var approved = HarnessApprovedDraft();
        var sendTask = provider.SendDraftAsync(new EmailSendDraftRequest("draft-1", approved), cts.Token).AsTask();
        await handler.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        var result = await sendTask;
        Assert.Equal(EmailSendOutcome.Indeterminate, result.Outcome);
        Assert.Equal("cancelled", result.ErrorCode);
        Assert.Equal("/gmail/v1/users/me/drafts/send", handler.LastSendPath);
    }

    [Fact]
    public async Task Send_4xx_is_definite_failure()
    {
        var handler = new GmailScriptedHandler { SendStatus = HttpStatusCode.BadRequest };
        var provider = CreateProvider(handler);
        var result = await provider.SendDraftAsync(new EmailSendDraftRequest("draft-1", HarnessApprovedDraft()));
        Assert.Equal(EmailSendOutcome.Failed, result.Outcome);
        Assert.Equal("/gmail/v1/users/me/drafts/send", handler.LastSendPath);
    }

    [Fact]
    public void Mime_roundtrip_does_not_turn_subject_newline_into_bcc_header()
    {
        Assert.Throws<AgentCoreException>(() => GmailMime.BuildRawMessage(
            ["to@example.test"],
            [],
            [],
            "Hello\nBcc: evil@example.test",
            "Body"));
    }

    private static EmailDraftSnapshot HarnessApprovedDraft() =>
        new(
            "draft-1",
            ["to@example.test"],
            ["cc@example.test"],
            ["hidden@example.test"],
            "Exact subject",
            "Exact body");

    private static GmailEmailProvider CreateProvider(GmailScriptedHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gmail:ClientId"] = "client",
                ["Gmail:ClientSecret"] = "secret",
                ["Gmail:RefreshToken"] = "refresh"
            })
            .Build();
        return new GmailEmailProvider(
            configuration,
            new StubHttpClientFactory(handler),
            NullLogger<GmailEmailProvider>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private sealed class GmailScriptedHandler : HttpMessageHandler
    {
        public string? StoredRaw { get; private set; }
        public int TokenPostCount { get; private set; }
        public int DraftPostCount { get; private set; }
        public int LegacyDraftIdSendCount { get; private set; }
        public string? LastSendPath { get; private set; }
        public string? LastSendBody { get; private set; }
        public bool HangSend { get; init; }
        public bool OmitRawOnGet { get; init; }
        public HttpStatusCode SendStatus { get; init; } = HttpStatusCode.OK;
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/token", StringComparison.Ordinal))
            {
                TokenPostCount++;
                return Json("""{"access_token":"ya29.test-token"}""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/drafts", StringComparison.Ordinal))
            {
                DraftPostCount++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                StoredRaw = document.RootElement.GetProperty("message").GetProperty("raw").GetString();
                return Json("""{"id":"draft-1"}""");
            }

            if (request.Method == HttpMethod.Get && path.Contains("/drafts/", StringComparison.Ordinal))
            {
                object payload = OmitRawOnGet
                    ? new
                    {
                        id = "draft-1",
                        message = new
                        {
                            payload = new
                            {
                                headers = new[]
                                {
                                    new { name = "To", value = "to@example.test" },
                                    new { name = "Subject", value = "Subject" }
                                }
                            }
                        }
                    }
                    : new { id = "draft-1", message = new { raw = StoredRaw } };
                return Json(JsonSerializer.Serialize(payload));
            }

            if (request.Method == HttpMethod.Post
                && path.StartsWith("/gmail/v1/users/me/drafts/", StringComparison.Ordinal)
                && path.EndsWith("/send", StringComparison.Ordinal)
                && !string.Equals(path, "/gmail/v1/users/me/drafts/send", StringComparison.Ordinal))
            {
                LegacyDraftIdSendCount++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Post
                && string.Equals(path, "/gmail/v1/users/me/drafts/send", StringComparison.Ordinal))
            {
                LastSendPath = path;
                LastSendBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                SendStarted.TrySetResult();
                if (HangSend)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                return new HttpResponseMessage(SendStatus)
                {
                    Content = new StringContent("""{"id":"msg-1"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                }
            };
    }
}
