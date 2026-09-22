using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class HttpRequestToolTests
{
    [Fact]
    public void Prepare_rejects_credentials_and_private_hosts_before_approval()
    {
        var client = new RecordingHttpClient();
        var executor = new SessionToolExecutor(httpRequestClient: client);
        using var credential = JsonDocument.Parse(
            """{"method":"GET","url":"https://example.com/items","headers":{"Authorization":"Bearer secret"}}""");
        var prepared = executor.PrepareHttpRequestApproval(credential.RootElement);
        Assert.Null(prepared.Preparation);
        Assert.Contains("forbidden_header", prepared.ErrorJson, StringComparison.Ordinal);

        using var local = JsonDocument.Parse("""{"method":"GET","url":"http://127.0.0.1/latest"}""");
        var blocked = executor.PrepareHttpRequestApproval(local.RootElement);
        Assert.Null(blocked.Preparation);
        Assert.Contains("forbidden_host", blocked.ErrorJson, StringComparison.Ordinal);
        Assert.Null(client.Last);
    }

    [Fact]
    public void Prepare_rejects_malformed_content_type_before_approval()
    {
        var client = new RecordingHttpClient();
        var executor = new SessionToolExecutor(httpRequestClient: client);
        using var malformed = JsonDocument.Parse(
            """
            {"method":"POST","url":"https://example.com/items","headers":{"Content-Type":"application/json; charset=\"utf-8"},"body":"{}"}
            """);
        var prepared = executor.PrepareHttpRequestApproval(malformed.RootElement);
        Assert.Null(prepared.Preparation);
        Assert.Contains("\"error\":\"invalid\"", prepared.ErrorJson, StringComparison.Ordinal);
        Assert.Null(client.Last);
    }

    [Fact]
    public async Task Approved_post_is_bound_to_method_url_headers_and_body_hash()
    {
        var client = new RecordingHttpClient();
        var executor = new SessionToolExecutor(httpRequestClient: client);
        var definition = HttpTools();
        using var args = JsonDocument.Parse(
            """
            {"method":"post","url":"https://example.com/items","headers":{"Content-Type":"application/json","Accept":"application/json"},"body":"{\"name\":\"sample\"}"}
            """);
        var prepared = executor.PrepareHttpRequestApproval(args.RootElement);
        Assert.NotNull(prepared.Preparation);
        Assert.Contains("POST https://example.com/items", prepared.Preparation!.Summary, StringComparison.Ordinal);
        Assert.Equal("POST", prepared.Preparation.Details["Method"]);
        var grant = new ToolApprovalGrant(
            Guid.CreateVersion7(),
            ToolCatalog.HttpRequest,
            prepared.Preparation.ActionHash,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());
        var json = (await executor.ExecuteAsync(
            definition,
            Guid.CreateVersion7(),
            new ModelToolCall("c1", ToolCatalog.HttpRequest, args.RootElement.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: grant)).Text;
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("untrusted").GetBoolean());
        Assert.Equal(201, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("created", document.RootElement.GetProperty("body").GetString());
        Assert.NotNull(client.Last);
        Assert.Equal("POST", client.Last!.Method);
        Assert.StartsWith("https://example.com/items", client.Last.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.False(client.Last.FollowRedirects);
        Assert.Equal("""{"name":"sample"}""", Encoding.UTF8.GetString(client.Last.Body));

        var stale = grant with { ActionHash = new string('a', 64) };
        var rejected = (await executor.ExecuteAsync(
            definition,
            Guid.CreateVersion7(),
            new ModelToolCall("c2", ToolCatalog.HttpRequest, args.RootElement.GetRawText()),
            ToolLimits.MaxOutputBytes,
            approvalGrant: stale)).Text;
        Assert.Contains("stale_approval", rejected, StringComparison.Ordinal);
    }

    private static AgentDefinition HttpTools() => new(
        1,
        "http-tools",
        1,
        new AgentIdentity("H", "HTTP", "Test.", "Neutral"),
        ["Test"],
        "Test",
        new BehaviorPolicy("answerNewTurn", true, true),
        new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(false, 10000, 30000, 1, []),
        new VoiceConfiguration(false, "default", 1.0),
        new ProviderPreferences("primary-llm", null, null),
        new Dictionary<string, string>(),
        new RoleEnvironment(ToolAllowlist: [ToolCatalog.HttpRequest]));

    private sealed class RecordingHttpClient : IHttpRequestClient
    {
        public HttpToolRequest? Last { get; private set; }

        public ValueTask<HttpToolResponse> SendAsync(HttpToolRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return ValueTask.FromResult(new HttpToolResponse(
                201,
                request.Url.ToString(),
                "application/json",
                "created",
                false,
                true,
                null,
                null,
                null));
        }
    }
}
