using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

public sealed class OpenAICompatibleLanguageModel : ILanguageModel
{
    public const string HttpClientName = "openai-compatible-llm";

    private static readonly HashSet<string> ForbiddenHeaderNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization", "Host", "Content-Type", "Content-Length", "Transfer-Encoding"
        };

    private readonly HttpClient _http;
    private readonly LanguageModelProviderOptions _options;
    private readonly TimeProvider _time;
    private readonly GenerationCircuitBreaker _breaker;
    private readonly Uri _completions;

    public OpenAICompatibleLanguageModel(
        HttpClient http,
        LanguageModelProviderOptions options,
        TimeProvider? time = null,
        GenerationCircuitBreaker? breaker = null)
    {
        _http = http;
        _options = options;
        _time = time ?? TimeProvider.System;
        _breaker = breaker ?? new GenerationCircuitBreaker(_time);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _completions = JoinCompletions(options.BaseUrl);
        Capabilities = new ModelCapabilities(StreamingText: true, Cancellation: true);
    }

    public ModelCapabilities Capabilities { get; }

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_breaker.IsOpen)
        {
            yield return Fail(ProviderErrorCode.Unavailable, "Language model circuit is open.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(_options.DefaultModel))
        {
            yield return Fail(ProviderErrorCode.InvalidRequest, "DefaultModel is required.");
            yield break;
        }

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var totalTimer = ScheduleCancel(
            _time,
            totalCts,
            TimeSpan.FromSeconds(Math.Max(1, _options.Timeouts.TotalSeconds)));
        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
        using var setupTimer = ScheduleCancel(
            _time,
            setupCts,
            TimeSpan.FromSeconds(Math.Max(1, _options.Timeouts.SetupSeconds)));

        HttpResponseMessage? response = null;
        ModelFailed? setupFailed = null;
        try
        {
            using var httpRequest = BuildRequest(request);
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, setupCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            setupFailed = Fail(ProviderErrorCode.Cancelled, "Generation cancelled.");
        }
        catch (OperationCanceledException)
        {
            setupFailed = Fail(ProviderErrorCode.Timeout, "Language model setup timed out.");
        }
        catch (HttpRequestException)
        {
            _breaker.RecordFailure();
            setupFailed = Fail(ProviderErrorCode.Unavailable, "Language model transport failed.");
        }

        if (setupFailed is not null || response is null)
        {
            yield return setupFailed ?? Fail(ProviderErrorCode.Unavailable, "Language model transport failed.");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var failure = MapStatus(response);
                if (failure.Code is ProviderErrorCode.Unavailable or ProviderErrorCode.Timeout)
                {
                    _breaker.RecordFailure();
                }

                yield return new ModelFailed(failure);
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(totalCts.Token).ConfigureAwait(false);
            var parser = new SseStreamParser();
            ModelStopReason? stop = null;
            int? inputTokens = null;
            int? outputTokens = null;
            var sawChoice = false;
            var sawDone = false;
            var emittedText = false;
            var idle = TimeSpan.FromSeconds(Math.Max(1, _options.Timeouts.StreamIdleSeconds));

            await using var enumerator = parser.ReadDataPayloadsAsync(stream, totalCts.Token)
                .GetAsyncEnumerator(totalCts.Token);
            while (true)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
                using var idleTimer = ScheduleCancel(_time, idleCts, idle);
                bool moved;
                ModelFailed? streamFailed = null;
                try
                {
                    moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    streamFailed = Fail(ProviderErrorCode.Cancelled, "Generation cancelled.");
                    moved = false;
                }
                catch (OperationCanceledException)
                {
                    if (emittedText)
                    {
                        _breaker.RecordFailure();
                    }

                    streamFailed = Fail(
                        emittedText ? ProviderErrorCode.Unavailable : ProviderErrorCode.Timeout,
                        emittedText
                            ? "Language model stream ended unexpectedly."
                            : "Language model stream idle timeout.");
                    moved = false;
                }
                catch (InvalidOperationException)
                {
                    _breaker.RecordFailure();
                    streamFailed = Fail(ProviderErrorCode.Unavailable, "Language model stream exceeded the event size limit.");
                    moved = false;
                }

                if (streamFailed is not null)
                {
                    yield return streamFailed;
                    yield break;
                }

                if (!moved)
                {
                    break;
                }

                var payload = enumerator.Current;
                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    sawDone = true;
                    break;
                }

                ModelGenerationEvent? mapped = null;
                ModelFailed? parseFailed = null;
                try
                {
                    mapped = MapPayload(payload, ref stop, ref inputTokens, ref outputTokens, ref sawChoice);
                }
                catch (JsonException)
                {
                    _breaker.RecordFailure();
                    parseFailed = Fail(ProviderErrorCode.Unavailable, "Language model stream was malformed.");
                }

                if (parseFailed is not null)
                {
                    yield return parseFailed;
                    yield break;
                }

                switch (mapped)
                {
                    case ModelFailed failed:
                        if (failed.Failure.Code is ProviderErrorCode.Unavailable or ProviderErrorCode.Timeout)
                        {
                            _breaker.RecordFailure();
                        }

                        yield return failed;
                        yield break;
                    case ModelTextDelta delta:
                        emittedText = true;
                        yield return delta;
                        break;
                }
            }

            if (sawDone && stop is null && sawChoice)
            {
                stop = ModelStopReason.Completed;
            }

            if (stop is { } completed)
            {
                _breaker.RecordSuccess();
                yield return new ModelCompleted(completed, inputTokens, outputTokens);
                yield break;
            }

            _breaker.RecordFailure();
            yield return Fail(
                ProviderErrorCode.Unavailable,
                sawDone
                    ? "Language model stream was incomplete."
                    : "Language model stream ended without a finish marker.");
        }
    }

    private HttpRequestMessage BuildRequest(ModelRequest request)
    {
        var messages = request.Messages.Select(message => new Dictionary<string, string>
        {
            ["role"] = message.Role switch
            {
                ModelRole.System => "system",
                ModelRole.Assistant => "assistant",
                _ => "user"
            },
            ["content"] = message.Text
        }).ToArray();

        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.DefaultModel,
            ["stream"] = true,
            ["max_tokens"] = request.MaxOutputTokens,
            ["messages"] = messages
        };
        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        var json = JsonSerializer.Serialize(body);
        var message = new HttpRequestMessage(HttpMethod.Post, _completions)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        foreach (var header in _options.AdditionalHeaders)
        {
            if (ForbiddenHeaderNames.Contains(header.Key))
            {
                continue;
            }

            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return message;
    }

    private static ModelGenerationEvent? MapPayload(
        string payload,
        ref ModelStopReason? stop,
        ref int? inputTokens,
        ref int? outputTokens,
        ref bool sawChoice)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _))
        {
            return Fail(ProviderErrorCode.Unavailable, "Language model reported a stream error.");
        }

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var input))
            {
                inputTokens = input;
            }

            if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var output))
            {
                outputTokens = output;
            }
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (choices.GetArrayLength() > 1)
        {
            return Fail(ProviderErrorCode.UnsupportedCapability, "Multiple choices are not supported.");
        }

        if (choices.GetArrayLength() == 0)
        {
            return null;
        }

        sawChoice = true;
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
        {
            var reason = finish.GetString();
            if (reason is "tool_calls" or "function_call")
            {
                return Fail(ProviderErrorCode.UnsupportedCapability, "Tool calls are not supported.");
            }

            stop = reason switch
            {
                "stop" => ModelStopReason.Completed,
                "length" => ModelStopReason.LengthLimit,
                "content_filter" => ModelStopReason.ContentFiltered,
                _ => stop
            };
        }

        if (choice.TryGetProperty("delta", out var delta)
            && delta.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? string.Empty;
            if (text.Length > 0)
            {
                return new ModelTextDelta(text);
            }
        }

        return null;
    }

    private static ITimer ScheduleCancel(TimeProvider time, CancellationTokenSource cts, TimeSpan delay) =>
        time.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), cts, delay, Timeout.InfiniteTimeSpan);

    private static ModelFailed Fail(ProviderErrorCode code, string message) =>
        new(new ProviderFailure(code, message));

    private static ProviderFailure MapStatus(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter?.Delta;
        return (int)response.StatusCode switch
        {
            401 or 403 => new ProviderFailure(ProviderErrorCode.Authentication, "Language model authentication failed."),
            429 => new ProviderFailure(ProviderErrorCode.RateLimited, "Language model rate limited.", retry),
            400 or 422 => new ProviderFailure(ProviderErrorCode.InvalidRequest, "Language model rejected the request."),
            >= 500 => new ProviderFailure(ProviderErrorCode.Unavailable, "Language model is unavailable."),
            _ => new ProviderFailure(ProviderErrorCode.Unknown, "Language model request failed.")
        };
    }

    public static Uri JoinCompletions(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Language model BaseUrl is invalid.");
        }

        if (uri.Scheme is not "https" && !(uri.Scheme == "http" && (uri.IsLoopback || uri.Host == "localhost")))
        {
            throw new ArgumentException("Language model BaseUrl must be https, or http for local demo endpoints.");
        }

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
    }
}

public sealed class GenerationCircuitBreaker
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failures = [];
    private readonly Queue<DateTimeOffset> _samples = [];
    private DateTimeOffset? _openUntil;

    public GenerationCircuitBreaker(TimeProvider time) => _time = time;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                if (_openUntil is { } until && _time.GetUtcNow() < until)
                {
                    return true;
                }

                _openUntil = null;
                return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _samples.Enqueue(_time.GetUtcNow());
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _samples.Enqueue(now);
            _failures.Enqueue(now);
            var windowStart = now - TimeSpan.FromSeconds(30);
            Trim(_samples, windowStart);
            Trim(_failures, windowStart);
            if (_samples.Count >= 5 && _failures.Count * 2 >= _samples.Count)
            {
                _openUntil = now + TimeSpan.FromSeconds(15);
            }
        }
    }

    private static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset windowStart)
    {
        while (queue.Count > 0 && queue.Peek() < windowStart)
        {
            queue.Dequeue();
        }
    }
}

public static class LanguageModelAdapterFactory
{
    public static ILanguageModel Create(
        LanguageModelProviderOptions options,
        HttpMessageHandler? handler,
        TimeProvider? time = null)
    {
        if (string.Equals(options.Adapter, "Scripted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Adapter, "Synthetic", StringComparison.OrdinalIgnoreCase))
        {
            return new Synthetic.ScriptedLanguageModel();
        }

        if (!string.Equals(options.Adapter, "OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown language model adapter '{options.Adapter}'.");
        }

        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        return new OpenAICompatibleLanguageModel(http, options, time);
    }
}
