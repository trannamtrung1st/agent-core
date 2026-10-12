using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Email;
using MimeKit;

namespace AgentCore.Infrastructure.Tests;

public sealed class GmailEmailProviderTests
{
    [Theory]
    [InlineData(200, EmailSendOutcome.Sent)]
    [InlineData(400, EmailSendOutcome.Failed)]
    [InlineData(503, EmailSendOutcome.Indeterminate)]
    [InlineData(408, EmailSendOutcome.Indeterminate)]
    public async Task Messages_send_uses_exact_Core_snapshot_verified_From_and_Bcc(int status, EmailSendOutcome outcome)
    {
        var handler = new Transport { Status = (HttpStatusCode)status }; var account = Account(); var provider = Provider(handler, account);
        var draft = Draft(account); await provider.PrepareSendAsync(default);
        var result = await provider.SendDraftAsync(new(draft.DraftId, draft)); Assert.Equal(outcome, result.Outcome);
        Assert.Equal("/gmail/v1/users/me/messages/send", handler.Path);
        using var json = JsonDocument.Parse(handler.Body!); Assert.Single(json.RootElement.EnumerateObject());
        var bytes = GmailMime.DecodeBase64Url(json.RootElement.GetProperty("raw").GetString()!);
        using var stream = new MemoryStream(bytes); using var message = MimeMessage.Load(stream);
        Assert.Equal("verified@example.test", Assert.Single(message.From.Mailboxes).Address);
        Assert.Equal("hidden@example.test", Assert.Single(message.Bcc.Mailboxes).Address);
        Assert.Equal("to@example.test", Assert.Single(message.To.Mailboxes).Address); Assert.Equal("cc@example.test", Assert.Single(message.Cc.Mailboxes).Address);
        Assert.Equal("Exact subject", message.Subject); Assert.Equal("Exact body", message.TextBody!.TrimEnd('\r', '\n'));
        Assert.Equal(1, handler.Sends); Assert.DoesNotContain("drafts", handler.Path!);
    }
    [Fact]
    public async Task Lost_send_response_is_uncertain_and_never_retried_by_adapter()
    {
        var handler = new Transport { Hang = true }; var account = Account(); var provider = Provider(handler, account); using var ct = new CancellationTokenSource();
        var draft = Draft(account); var sending = provider.SendDraftAsync(new(draft.DraftId, draft), ct.Token).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await ct.CancelAsync();
        Assert.Equal(EmailSendOutcome.Indeterminate, (await sending).Outcome); Assert.Equal(1, handler.Sends);
    }
    [Theory]
    [InlineData("sender")]
    [InlineData("account")]
    [InlineData("epoch")]
    [InlineData("id")]
    [InlineData("subject")]
    [InlineData("recipient")]
    public async Task Forged_draft_and_header_injection_fail_before_HTTP(string change)
    {
        var handler = new Transport(); var account = Account(); var provider = Provider(handler, account); var draft = Draft(account);
        draft = change switch { "sender" => draft with { Sender = "other@example.test" }, "account" => draft with { AccountId = Guid.NewGuid() }, "epoch" => draft with { AuthEpoch = 2 },
            "subject" => draft with { Subject = "Hello\nBcc: evil@example.test" }, "recipient" => draft with { To = ["to@example.test\r\nBcc: evil@example.test"] }, _ => draft };
        await Assert.ThrowsAsync<AgentCoreException>(() => provider.SendDraftAsync(new(change == "id" ? "wrong" : draft.DraftId, draft)).AsTask()); Assert.Equal(0, handler.Sends);
    }
    [Fact]
    public async Task Read_search_are_bounded_account_scoped_recent_inbox_and_plain_text_only()
    {
        var handler = new Transport(); var account = Account(); var provider = Provider(handler, account);
        var found = await provider.SearchAsync(new("", 2)); Assert.Single(found.Results); Assert.Contains("q=in%3Ainbox", handler.SearchUri!);
        Assert.Equal("Subject", found.Results[0].Subject);
        var read = await provider.ReadAsync(new(found.Results[0].MessageId)); Assert.Equal("Plain body", read.Body); Assert.DoesNotContain("script", read.Body);
        var foreign = Provider(handler, Account()); await Assert.ThrowsAsync<AgentCoreException>(() => foreign.ReadAsync(new(read.MessageId)).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateDraftAsync(new(["to@example.test"], [], [], "subject", "body")).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetDraftAsync("draft").AsTask());
    }
    [Fact]
    public async Task Safe_read_retries_one_transient_failure_with_bounds()
    {
        var handler = new Transport { FailFirstList = true }; var provider = Provider(handler, Account());
        Assert.Single((await provider.SearchAsync(new("", 1))).Results); Assert.Equal(2, handler.Lists);
    }
    [Fact]
    public void Mime_roundtrip_does_not_turn_subject_newline_into_bcc_header() => Assert.Throws<AgentCoreException>(() => GmailMime.BuildRawMessage(["to@example.test"], [], [], "Hello\nBcc: evil@example.test", "Body"));
    private static EmailAccountContext Account() => new(Guid.NewGuid(), Guid.NewGuid(), "gmail", "Gmail", 2, Guid.NewGuid(), 1,
        EmailAccountProfile.Gmail("verified@example.test") with { AuthEpoch = 1, ConnectionStatus = EmailConnectionStatus.Connected, GoogleSubject = "verified-sub", GrantedScopes = [EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope] });
    private static EmailDraftSnapshot Draft(EmailAccountContext a) => new("core-draft-1", ["to@example.test"], ["cc@example.test"], ["hidden@example.test"], "Exact subject", "Exact body", a.AccountId, a.AccountRef, a.Profile.EmailAddress, AccountRevision: a.AccountRevision, AuthEpoch: 1);
    private static GmailEmailProvider Provider(Transport handler, EmailAccountContext account) => new(account, handler, _ => ValueTask.FromResult("protected-access"), _ => ValueTask.CompletedTask, () => true, TimeProvider.System);
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public bool FailFirstList; public int Lists;
        public HttpStatusCode Status = HttpStatusCode.OK; public bool Hang; public string? Path, Body, SearchUri; public int Sends;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("protected-access", request.Headers.Authorization.Parameter);
            Path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post) { Sends++; Body = await request.Content!.ReadAsStringAsync(ct); Started.TrySetResult(); if (Hang) await Task.Delay(Timeout.Infinite, ct); return Json("{\"id\":\"accepted-id\"}", Status); }
            if (Path.EndsWith("/messages")) { Lists++; if (FailFirstList && Lists == 1) return Json("{}", HttpStatusCode.ServiceUnavailable); SearchUri = request.RequestUri.ToString(); return Json("{\"messages\":[{\"id\":\"m1\"}]}"); }
            return Json(JsonSerializer.Serialize(new { id = "m1", threadId = "t1", payload = new { mimeType = "multipart/alternative", headers = new[] { new { name = "Subject", value = "Subject" }, new { name = "From", value = "sender@example.test" } },
                parts = new[] { new { mimeType = "text/plain", body = new { data = GmailMime.EncodeBase64Url(Encoding.UTF8.GetBytes("Plain body")) } }, new { mimeType = "text/html", body = new { data = GmailMime.EncodeBase64Url(Encoding.UTF8.GetBytes("<script>unsafe</script>")) } } } } }));
        }
        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
