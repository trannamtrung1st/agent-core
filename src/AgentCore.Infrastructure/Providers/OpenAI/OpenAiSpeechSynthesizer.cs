using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Audio;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;

namespace AgentCore.Infrastructure.Providers.OpenAI;

public sealed class SpeechSynthesisProviderOptions
{
    public string Adapter { get; set; } = "Synthetic";
    public string? BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; } = "tts-1";
    public string DefaultVoice { get; set; } = "alloy";
    public Dictionary<string, string> Voices { get; set; } = [];
    public Dictionary<string, string> AdditionalHeaders { get; set; } = [];
    public ProviderTimeoutOptions Timeouts { get; set; } = new();
    public Dictionary<string, bool> DisabledCapabilities { get; set; } = [];
}

public static class OpenAiSpeechPayload
{
    public static string CreateJson(string model, string input, string voice, double speed) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = input,
            ["voice"] = voice,
            ["speed"] = speed,
            ["response_format"] = "pcm"
        });
}

public sealed class OpenAiSpeechSynthesizer : ISpeechSynthesizer, ISpeechLocaleSupport
{
    public const string HttpClientName = "openai-speech-tts";

    private readonly HttpClient _http;
    private readonly SpeechSynthesisProviderOptions _options;
    private readonly Uri _speech;

    public OpenAiSpeechSynthesizer(HttpClient http, SpeechSynthesisProviderOptions options)
    {
        _http = http;
        _options = options;
        _speech = Join(options.BaseUrl);
        Capabilities = new SynthesisCapabilities(
            StreamingAudio: true,
            TimingMarks: false,
            Cancellation: true,
            VoiceSelection: true,
            SpeakingRate: true,
            SupportedFormats: [CanonicalAudio.Format]);
    }

    public SynthesisCapabilities Capabilities { get; }

    public bool CanRecognize(string locale) => false;

    public bool CanSynthesize(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || !HasLocaleVoiceMap())
        {
            return true;
        }

        return SpeechLocaleCompatibility.AnyMatch(locale, LocaleVoiceKeys());
    }

    public async IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(
        SpeechRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw AgentCoreErrors.VoiceUnavailable();
        }

        if (!TryResolveVoice(request, out var voice))
        {
            yield return new SpeechSynthesisFailed(
                new ProviderFailure(
                    ProviderErrorCode.UnsupportedCapability,
                    "No compatible speech synthesis voice is configured for this locale."));
            yield break;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _speech)
        {
            Content = new StringContent(
                OpenAiSpeechPayload.CreateJson(
                    _options.DefaultModel ?? "tts-1",
                    request.Text,
                    voice,
                    request.SpeakingRate),
                Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        HttpResponseMessage? response = null;
        SpeechSynthesisFailed? setupFailed = null;
        try
        {
            response = await _http
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            setupFailed = new SpeechSynthesisFailed(new ProviderFailure(ProviderErrorCode.Cancelled, "Synthesis cancelled."));
        }
        catch (HttpRequestException)
        {
            setupFailed = new SpeechSynthesisFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "Speech synthesis is unavailable."));
        }

        if (setupFailed is not null || response is null)
        {
            yield return setupFailed ?? new SpeechSynthesisFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "Speech synthesis is unavailable."));
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                yield return new SpeechSynthesisFailed(MapStatus(response.StatusCode));
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var pending = new List<byte>(CanonicalAudio.MaxFrameBytes);
            var buffer = new byte[4096];
            long offset = 0;
            long frameSequence = 1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    pending.Add(buffer[index]);
                    if (pending.Count < CanonicalAudio.MaxFrameBytes)
                    {
                        continue;
                    }

                    var frame = pending.ToArray();
                    pending.Clear();
                    yield return new SpeechAudio(new AudioFrame(frameSequence, offset, frame));
                    offset += PcmCodec.SampleCount(frame);
                    frameSequence++;
                }
            }

            if (pending.Count >= 2)
            {
                if (pending.Count % 2 == 1)
                {
                    pending.RemoveAt(pending.Count - 1);
                }

                var frame = pending.ToArray();
                yield return new SpeechAudio(new AudioFrame(frameSequence, offset, frame));
                offset += PcmCodec.SampleCount(frame);
            }

            yield return new SpeechSynthesisCompleted(offset);
        }
    }

    internal bool TryResolveVoice(SpeechRequest request, out string voice)
    {
        voice = "";
        if (!string.IsNullOrWhiteSpace(request.Language)
            && HasLocaleVoiceMap()
            && TryMapLocaleVoice(request.Language, out var mapped))
        {
            voice = mapped;
            return true;
        }

        if (HasLocaleVoiceMap() && !string.IsNullOrWhiteSpace(request.Language))
        {
            return false;
        }

        if (_options.Voices.TryGetValue("default", out var configuredDefault)
            && !string.IsNullOrWhiteSpace(configuredDefault)
            && string.IsNullOrWhiteSpace(request.Voice))
        {
            voice = configuredDefault;
            return true;
        }

        voice = string.IsNullOrWhiteSpace(request.Voice) ? _options.DefaultVoice : request.Voice;
        return !string.IsNullOrWhiteSpace(voice);
    }

    private bool HasLocaleVoiceMap() => LocaleVoiceKeys().Any();

    private IEnumerable<string> LocaleVoiceKeys() =>
        _options.Voices.Keys.Where(key => !string.Equals(key, "default", StringComparison.OrdinalIgnoreCase));

    private bool TryMapLocaleVoice(string locale, out string voice)
    {
        foreach (var pair in _options.Voices)
        {
            if (string.Equals(pair.Key, "default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(pair.Key, locale, StringComparison.OrdinalIgnoreCase))
            {
                voice = pair.Value;
                return !string.IsNullOrWhiteSpace(voice);
            }
        }

        foreach (var pair in _options.Voices)
        {
            if (string.Equals(pair.Key, "default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SpeechLocaleCompatibility.Matches(locale, pair.Key))
            {
                voice = pair.Value;
                return !string.IsNullOrWhiteSpace(voice);
            }
        }

        voice = "";
        return false;
    }

    private static ProviderFailure MapStatus(System.Net.HttpStatusCode status) =>
        status switch
        {
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                new ProviderFailure(ProviderErrorCode.Authentication, "Speech synthesis authentication failed."),
            System.Net.HttpStatusCode.TooManyRequests =>
                new ProviderFailure(ProviderErrorCode.RateLimited, "Speech synthesis rate limited.", TimeSpan.FromSeconds(1)),
            System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity =>
                new ProviderFailure(ProviderErrorCode.InvalidRequest, "Speech synthesis request was invalid."),
            _ => new ProviderFailure(ProviderErrorCode.Unavailable, "Speech synthesis is unavailable.")
        };

    private static Uri Join(string? baseUrl)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1/" : baseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(root, UriKind.Absolute), "audio/speech");
    }
}
