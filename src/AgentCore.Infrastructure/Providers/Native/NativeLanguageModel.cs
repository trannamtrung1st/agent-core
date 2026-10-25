using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Providers.OpenAICompatible;

namespace AgentCore.Infrastructure.Providers.Native;

// Native wire protocols implement the incumbent model port, with no agent/tool execution loop.
public sealed partial class NativeLanguageModel : ILanguageModel
{
    private readonly HttpClient http;
    private readonly ModelRoutePin route;
    private readonly ModelCapabilityProfile profile;
    private readonly NativeModelContinuation continuation;
    private readonly IIdGenerator ids;
    private readonly TimeProvider time;
    private readonly Func<CancellationToken, ValueTask<string?>> credential;
    private readonly Func<CancellationToken, ValueTask> authorize;
    private readonly Uri endpoint;
    private readonly TimeSpan setup, idle, total;

    public NativeLanguageModel(HttpClient http, ModelRoutePin route, string endpoint, ModelCapabilityProfile profile,
        ICredentialProtector continuationProtector, IIdGenerator ids, TimeProvider time,
        Func<CancellationToken, ValueTask<string?>> credential, Func<CancellationToken, ValueTask> authorize,
        ProviderTimeoutOptions? timeouts = null)
    {
        ModelManagementRules.RequireTransport(route.Kind, route.Transport);
        if (route.Kind is not (ModelProviderKind.Anthropic or ModelProviderKind.Google)) throw new ArgumentException("A native Messages or Gemini route is required.");
        this.http = http; this.route = route; this.profile = ModelManagementRules.Freeze(profile);
        this.endpoint = new Uri(endpoint); this.ids = ids; this.time = time; this.credential = credential; this.authorize = authorize;
        continuation = new(route, continuationProtector);
        timeouts ??= new(); setup = TimeSpan.FromSeconds(Math.Max(1, timeouts.SetupSeconds));
        idle = TimeSpan.FromSeconds(Math.Max(1, timeouts.StreamIdleSeconds)); total = TimeSpan.FromSeconds(Math.Max(1, timeouts.TotalSeconds));
        Capabilities = new(true, true, profile.Vision, profile.Tools, profile.StructuredOutput);
    }
    public ModelCapabilities Capabilities { get; }
    public ModelInferenceTransport? InferenceTransport => route.Kind == ModelProviderKind.Anthropic
        ? ModelInferenceTransport.AnthropicMessages : ModelInferenceTransport.GeminiContent;

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var events = GenerateCore(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ModelFailed? failure = null; bool more = false;
            try { more = await events.MoveNextAsync(); }
            catch (Exception error) when (error is NativeRequestException or JsonException or HttpRequestException or IOException
                or OperationCanceledException or SseStreamIdleTimeoutException or SseStreamTotalTimeoutException
                or AccessDeniedException or SecurityConflictException or AgentCore.Application.Sessions.AgentCoreException
                or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
            { failure = Normalize(error, cancellationToken); }
            if (failure is not null) { yield return failure; yield break; }
            if (!more) yield break;
            yield return events.Current;
        }
    }
    private async IAsyncEnumerable<ModelGenerationEvent> GenerateCore(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Validate(request);
        var body = route.Kind == ModelProviderKind.Anthropic ? AnthropicBody(request) : GeminiBody(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > 20 * 1024 * 1024) throw new NativeRequestException("Native model input exceeds the request bound.");
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var totalTimer = time.CreateTimer(_ => totalCts.Cancel(), null, total, Timeout.InfiniteTimeSpan);
        await authorize(totalCts.Token);
        var secret = await credential(totalCts.Token);
        if (string.IsNullOrEmpty(secret)) throw new NativeRequestException("The native provider credential is unavailable.");
        TenantModelRules.ApiKey(secret);
        var path = route.Kind == ModelProviderKind.Anthropic ? "messages" : "models/" + Uri.EscapeDataString(route.ExternalModelId) + ":streamGenerateContent?alt=sse";
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path));
        message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/json");
        message.Headers.Accept.ParseAdd("text/event-stream");
        message.Options.Set(ModelProviderHttp.DispatchAuthority, authorize);
        message.Options.Set(ModelProviderHttp.NoCredential, false);
        if (route.Kind == ModelProviderKind.Anthropic)
        {
            message.Headers.Add("x-api-key", secret); message.Headers.Add("anthropic-version", "2023-06-01");
        }
        else message.Headers.Add("x-goog-api-key", secret);
        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
        using var setupTimer = time.CreateTimer(_ => setupCts.Cancel(), null, setup, Timeout.InfiniteTimeSpan);
        await authorize(setupCts.Token);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, setupCts.Token);
        setupTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        await authorize(totalCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            yield return HttpFailure(response.StatusCode, response.Headers.RetryAfter?.Delta); yield break;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(totalCts.Token);
        var round = new NativeRound(this, request);
        var parser = new SseStreamParser();
        await foreach (var payload in parser.ReadDataPayloadsAsync(stream, idle, time, totalCts.Token, ct, ct))
        {
            using var document = JsonDocument.Parse(payload);
            foreach (var item in round.Apply(document.RootElement)) yield return item;
            if (round.Terminal) break;
        }
        await authorize(totalCts.Token);
        foreach (var item in round.Complete()) yield return item;
    }
    private void Validate(ModelRequest request)
    {
        if (profile.Hierarchy != ModelInstructionHierarchy.OrderedTrustedSections || profile.SemanticMode != ModelSemanticMode.NativeStructuredOutput)
            throw new NativeRequestException("This native route does not declare the required trusted instruction/output channels.", true);
        if (!Enum.IsDefined(request.ToolChoice) || request.MaxOutputTokens is < 1 or > 128000 || request.Messages.Count > 2048 || request.Tools?.Count > 256)
            throw new NativeRequestException("Native model request exceeds its supported bounds.");
        if (request.ResponseContract is not null && !Capabilities.StructuredOutput || request.Tools?.Count > 0 && !Capabilities.Tools
            || request.Messages.Any(m => m.Parts?.OfType<ModelImageContent>().Any() == true) && !Capabilities.Vision)
            throw new NativeRequestException("The selected model does not support this request's capabilities.", true);
        if (route.Kind == ModelProviderKind.Anthropic && route.ExternalModelId is "claude-haiku-5-5" or "claude-sonnet-5-5"
            && request.Temperature is not null)
            throw new NativeRequestException("The selected native model does not accept temperature.", true);
        var forced = profile.ForcedToolChoice && !(route.Kind == ModelProviderKind.Anthropic && route.ExternalModelId == "claude-sonnet-5-5");
        if (request.ToolChoice != ModelToolChoice.Auto && !forced) throw new NativeRequestException("This model does not support required or named tool choice.", true);
        if (request.ToolChoice != ModelToolChoice.Auto && request.Tools is not { Count: > 0 }
            || request.ToolChoice == ModelToolChoice.Named && !request.Tools!.Any(t => t.Name == request.ToolChoiceName))
            throw new NativeRequestException("Tool choice must identify an offered tool.");
        if (request.ReasoningEffort is { } effort && (!profile.ReasoningEfforts.Contains(effort, StringComparer.Ordinal)
            || !(route.Kind == ModelProviderKind.Anthropic ? new[] { "low", "medium", "high", "xhigh", "max" } : new[] { "minimal", "low", "medium", "high" }).Contains(effort, StringComparer.Ordinal)))
            throw new NativeRequestException("The requested reasoning effort is unsupported.", true);
        if (route.Kind == ModelProviderKind.Google && route.ExternalModelId == "gemini-3.8-flash" && request.ReasoningEffort == "minimal")
            throw new NativeRequestException("Gemini 3.8 Flash does not support minimal thinking.", true);
        var tools = request.Tools ?? [];
        if (tools.Any(t => !OpenAiCompatibleToolNames.IsWireSafe(OpenAiCompatibleToolNames.ToWireName(t.Name)))
            || tools.Select(t => OpenAiCompatibleToolNames.ToWireName(t.Name)).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new NativeRequestException("The tool names cannot be represented unambiguously.");
    }
    private static ModelFailed HttpFailure(HttpStatusCode status, TimeSpan? retry) => new(new(status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrorCode.Authentication,
        HttpStatusCode.TooManyRequests => ProviderErrorCode.RateLimited,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ProviderErrorCode.Timeout,
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound => ProviderErrorCode.InvalidRequest,
        _ => ProviderErrorCode.Unavailable
    }, "Native model request failed. Check the model connection and its declared capabilities.", retry));
    private static ModelFailed Normalize(Exception error, CancellationToken ct) => error switch
    {
        NativeRequestException request => new(new(request.Unsupported ? ProviderErrorCode.UnsupportedCapability : ProviderErrorCode.InvalidRequest, request.Message)),
        OperationCanceledException when ct.IsCancellationRequested => new(new(ProviderErrorCode.Cancelled, "Model generation was cancelled.")),
        OperationCanceledException or SseStreamIdleTimeoutException or SseStreamTotalTimeoutException => new(new(ProviderErrorCode.Timeout, "Native model generation timed out.")),
        AccessDeniedException or SecurityConflictException => new(new(ProviderErrorCode.Authentication, "The admitted model route is no longer authorized.")),
        HttpRequestException or IOException or AgentCore.Application.Sessions.AgentCoreException => new(new(ProviderErrorCode.Unavailable, "The native model connection is unavailable.")),
        _ => new(new(ProviderErrorCode.InvalidResponse, "The native provider returned an invalid or incomplete response."))
    };
}
