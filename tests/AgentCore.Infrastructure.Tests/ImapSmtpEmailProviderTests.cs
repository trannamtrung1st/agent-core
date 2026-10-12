using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Email;
using MailKit;

namespace AgentCore.Infrastructure.Tests;

public sealed class ImapSmtpEmailProviderTests
{
    [Theory]
    [InlineData(false, EmailSendOutcome.Sent)]
    [InlineData(true, EmailSendOutcome.Indeterminate)]
    public async Task SMTP_exact_snapshot_Bcc_envelope_only_and_lost_acceptance_is_uncertain(bool drop, EmailSendOutcome expected)
    {
        await using var server = new FakeMailServer(smtp: true, dropAfterData: drop);
        var account = Context();
        var provider = Provider(server, account);
        var draft = new EmailDraftSnapshot("test", ["to@example.test"], ["cc@example.test"], ["hidden@example.test"], "Hello", "Exact body", account.AccountId, "mail", account.Profile.EmailAddress);
        var sent = await provider.SendDraftAsync(new("test", draft));
        Assert.Equal(expected, sent.Outcome);
        await server.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("RCPT TO:<hidden@example.test>", server.Commands);
        Assert.Contains("From: sender@example.test", server.Message);
        Assert.Contains("Exact body", server.Message);
        Assert.DoesNotContain("hidden@example.test", server.Message);
        Assert.DoesNotContain("Bcc:", server.Message);
        if (!drop) Assert.NotNull(sent.ProviderMessageId);
    }
    [Fact]
    public async Task IMAP_search_opaque_identity_PEEK_read_and_UIDVALIDITY_fence()
    {
        var account = Context();
        await using var search = new FakeMailServer(smtp: false);
        var result = await Provider(search, account).SearchAsync(new("Hello", 5));
        await search.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        var id = Assert.Single(result.Results).MessageId;
        Assert.StartsWith($"imap-{account.AccountId:N}:123:7", id);
        Assert.Contains(search.Commands, c => c.Contains("UID SEARCH", StringComparison.Ordinal));
        await using var read = new FakeMailServer(smtp: false);
        var message = await Provider(read, account).ReadAsync(new(id));
        await read.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Exact body", message.Body.Trim());
        Assert.Contains(read.Commands, c => c.Contains("BODY.PEEK[]", StringComparison.Ordinal));
        Assert.DoesNotContain(read.Commands, c => c.Contains("STORE", StringComparison.Ordinal));
        await using var stale = new FakeMailServer(smtp: false, validity: 124);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => Provider(stale, account).ReadAsync(new(id)).AsTask());
        Assert.DoesNotContain(stale.Commands, c => c.Contains("FETCH", StringComparison.Ordinal));
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => Provider(stale, Context()).ReadAsync(new(id)).AsTask());
    }
    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    public async Task IMAP_recent_inbox_without_UIDNEXT_uses_bounded_sequence_fallback(int count)
    {
        await using var server = new FakeMailServer(smtp: false, omitUidNext: true, count: count);
        var result = await Provider(server, Context()).SearchAsync(new("", 10));
        await server.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(result.Results);
        Assert.Equal(count > 500, result.Truncated);
        Assert.Contains(server.Commands, c => c == (count == 1 ? "FETCH 1 UID" : "FETCH 101:600 UID"));
        Assert.Contains(server.Commands, c => c.Contains(count == 1 ? "UID SEARCH UID 7" : "UID SEARCH UID 6007", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, c => c.Contains("429496", StringComparison.Ordinal));
    }
    [Fact]
    public async Task TLS_certificate_rejection_and_SMTP_test_never_dispatches()
    {
        await using var denied = new FakeMailServer(smtp: true);
        await Assert.ThrowsAnyAsync<Exception>(() => Provider(denied, Context(), trust: false).TestAsync(true, default).AsTask());
        await using var tested = new FakeMailServer(smtp: true);
        await Provider(tested, Context()).TestAsync(true, default);
        await tested.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(tested.Commands, c => c.StartsWith("MAIL FROM", StringComparison.Ordinal));
        Assert.Empty(tested.Message);
    }
    [Fact]
    public async Task Required_STARTTLS_cannot_downgrade_or_resolve_a_password()
    {
        await using var server = new FakeMailServer(smtp: true, plaintext: true);
        var account = Context();
        account = account with { Profile = account.Profile with { SmtpEndpoint = new("smtp.gmail.com", 587, MailTlsMode.StartTls) } };
        var resolutions = 0;
        var provider = new ImapSmtpEmailProvider(account, new(), new(new()), new(4), (_, _) => { resolutions++; return ValueTask.FromResult("fixture-password"); })
        {
            SocketFactory = async ct => { var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); await socket.ConnectAsync(server.Endpoint, ct); return socket; }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => provider.TestAsync(true, default).AsTask());
        Assert.Equal(0, resolutions); Assert.Empty(server.Message);
    }
    private static EmailAccountContext Context() => new(Guid.NewGuid(), Guid.NewGuid(), "mail", "Test", 1, Guid.NewGuid(), 1, EmailAccountProfile.Gmail("sender@example.test"));
    private static ImapSmtpEmailProvider Provider(FakeMailServer server, EmailAccountContext account, bool trust = true) => new(account,
        new() { SmtpEnabled = true, TimeoutSeconds = 3 }, new(new()), new(4), (_, _) => ValueTask.FromResult("fixture-password"))
    {
        SocketFactory = async ct => { var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); await socket.ConnectAsync(server.Endpoint, ct); return socket; },
        ConfigureClient = client => { if (trust) client.ServerCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == server.Certificate.GetCertHashString(); }
    };
    private sealed class FakeMailServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        public X509Certificate2 Certificate { get; }
        public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;
        public List<string> Commands { get; } = []; public string Message { get; private set; } = "";
        public Task Completed { get; }
        public FakeMailServer(bool smtp, bool dropAfterData = false, uint validity = 123, bool plaintext = false, bool omitUidNext = false, int count = 1)
        {
            using var key = RSA.Create(2048); var request = new CertificateRequest("CN=fixture-mail.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            listener.Start(); Completed = Run(smtp, dropAfterData, validity, plaintext, omitUidNext, count);
        }
        private async Task Run(bool smtp, bool drop, uint validity, bool plaintext, bool omitUidNext, int count)
        {
            var messageUid = count > 1 ? 6007 : 7;
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                using Stream stream = plaintext ? socket.GetStream() : new SslStream(socket.GetStream());
                if (stream is SslStream tls) await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, stop.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync(smtp ? "220 fixture ESMTP" : "* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR] fixture");
                while (await reader.ReadLineAsync(stop.Token) is { } line)
                {
                    if (smtp)
                    {
                        if (!line.StartsWith("AUTH", StringComparison.Ordinal)) Commands.Add(line);
                        if (line.StartsWith("EHLO", StringComparison.Ordinal)) await writer.WriteAsync("250-fixture\r\n250-AUTH PLAIN\r\n250 SIZE 1048576\r\n");
                        else if (line.StartsWith("AUTH", StringComparison.Ordinal)) await writer.WriteLineAsync("235 Authenticated");
                        else if (line == "DATA")
                        {
                            await writer.WriteLineAsync("354 Send message"); var body = new StringBuilder();
                            while (await reader.ReadLineAsync(stop.Token) is { } row && row != ".") body.AppendLine(row);
                            Message = body.ToString(); if (drop) return;
                            await writer.WriteLineAsync("250 2.0.0 accepted");
                        }
                        else await writer.WriteLineAsync("250 OK");
                    }
                    else
                    {
                        var split = line.IndexOf(' '); var tag = line[..split]; var command = line[(split + 1)..];
                        if (!command.StartsWith("AUTHENTICATE", StringComparison.Ordinal) && !command.StartsWith("LOGIN", StringComparison.Ordinal)) Commands.Add(command);
                        if (command == "CAPABILITY") await writer.WriteLineAsync("* CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR");
                        else if (command.StartsWith("AUTHENTICATE", StringComparison.Ordinal) || command.StartsWith("LOGIN", StringComparison.Ordinal)) { await writer.WriteLineAsync(tag + " OK [CAPABILITY IMAP4rev1] Authenticated"); continue; }
                        else if (command.StartsWith("LIST", StringComparison.Ordinal)) await writer.WriteLineAsync("* LIST (\\Inbox) \"/\" \"INBOX\"");
                        else if (command.StartsWith("EXAMINE", StringComparison.Ordinal)) await writer.WriteAsync($"* {count} EXISTS\r\n* 0 RECENT\r\n* FLAGS (\\Seen)\r\n* OK [UIDVALIDITY {validity}] valid\r\n" + (omitUidNext ? "" : "* OK [UIDNEXT 8] next\r\n"));
                        else if (command.StartsWith("FETCH ", StringComparison.Ordinal)) await writer.WriteLineAsync($"* {count} FETCH (UID {messageUid})");
                        else if (command.StartsWith("UID SEARCH", StringComparison.Ordinal)) await writer.WriteLineAsync(command.Contains("429496", StringComparison.Ordinal) ? "* SEARCH" : $"* SEARCH {messageUid}");
                        else if (command.Contains("BODY.PEEK", StringComparison.Ordinal))
                        {
                            var mime = "From: sender@example.test\r\nTo: to@example.test\r\nSubject: Hello\r\nDate: Mon, 12 Oct 2026 00:00:00 +0000\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nExact body\r\n";
                            await writer.WriteAsync($"* 1 FETCH (UID 7 BODY[]<0> {{{Encoding.ASCII.GetByteCount(mime)}}}\r\n{mime})\r\n");
                        }
                        else if (command.StartsWith("UID FETCH", StringComparison.Ordinal)) await writer.WriteLineAsync($"* {count} FETCH (UID {messageUid} INTERNALDATE \"12-Oct-2026 00:00:00 +0000\" ENVELOPE (\"Mon, 12 Oct 2026 00:00:00 +0000\" \"Hello\" ((NIL NIL \"sender\" \"example.test\")) NIL NIL ((NIL NIL \"to\" \"example.test\")) NIL NIL NIL \"<fixture@example.test>\"))");
                        await writer.WriteLineAsync(tag + " OK " + (command.StartsWith("EXAMINE", StringComparison.Ordinal) ? "[READ-ONLY] " : "") + "done");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await Completed; Certificate.Dispose(); stop.Dispose(); }
    }
}
