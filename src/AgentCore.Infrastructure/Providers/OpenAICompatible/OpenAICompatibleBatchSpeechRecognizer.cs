using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAI;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

public sealed class OpenAICompatibleBatchSpeechRecognizer : ISpeechRecognizer, ISpeechLocaleSupport
{
    public const string HttpClientName = "openai-compatible-batch-stt";

    public static string WhisperLanguageHint(string locale) =>
        SpeechLocaleCompatibility.PrimarySubtag(locale);

    private readonly HttpClient _http;
    private readonly SpeechRecognitionProviderOptions _options;

    public OpenAICompatibleBatchSpeechRecognizer(HttpClient http, SpeechRecognitionProviderOptions options)
    {
        _http = http;
        _options = options;
        Capabilities = new RecognitionCapabilities(false, false, false, true);
    }

    public RecognitionCapabilities Capabilities { get; }

    public bool CanRecognize(string locale) => !string.IsNullOrWhiteSpace(locale);

    public bool CanSynthesize(string locale) => false;

    public ValueTask<ISpeechRecognitionSession> OpenAsync(
        RecognitionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ISpeechRecognitionSession>(new Session(_http, _options, options.Language));
    }

    private sealed class Session : ISpeechRecognitionSession
    {
        private readonly HttpClient _http;
        private readonly SpeechRecognitionProviderOptions _options;
        private readonly string _language;
        private readonly Channel<SpeechRecognitionEvent> _events = Channel.CreateUnbounded<SpeechRecognitionEvent>();
        private readonly List<byte> _buffer = [];

        public Session(HttpClient http, SpeechRecognitionProviderOptions options, string language)
        {
            _http = http;
            _options = options;
            _language = language;
        }

        public ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            _buffer.AddRange(frame.Data.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask ObserveBoundaryAsync(
            Guid utteranceId,
            SpeechBoundary boundary,
            CancellationToken cancellationToken = default)
        {
            if (boundary != SpeechBoundary.Ended)
            {
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(_options.DefaultModel ?? "whisper-1"), "model");
            content.Add(new StringContent(WhisperLanguageHint(_language)), "language");
            content.Add(new StringContent("json"), "response_format");
            var wav = WavPcm.WrapPcm16Mono24k(_buffer.ToArray());
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(file, "file", "utterance.wav");
            using var request = new HttpRequestMessage(HttpMethod.Post, Join(_options.BaseUrl)) { Content = content };
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            _buffer.Clear();
            if (!response.IsSuccessStatusCode)
            {
                _ = _events.Writer.TryWrite(
                    new RecognitionFailed(
                        utteranceId,
                        new ProviderFailure(ProviderErrorCode.Unavailable, "Batch transcription failed.")));
                return;
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var text = doc.RootElement.TryGetProperty("text", out var node) ? node.GetString() ?? string.Empty : string.Empty;
            _ = _events.Writer.TryWrite(new SpeechFinal(utteranceId, text, null));
        }

        public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default)
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
            _events.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            _buffer.Clear();
            return ValueTask.CompletedTask;
        }

        private static Uri Join(string? baseUrl)
        {
            var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1/" : baseUrl.TrimEnd('/') + "/";
            return new Uri(new Uri(root), "audio/transcriptions");
        }
    }
}

public static class WavPcm
{
    public static byte[] WrapPcm16Mono24k(ReadOnlySpan<byte> pcm)
    {
        var data = pcm.Length;
        var bytes = new byte[44 + data];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes.AsSpan(0, 4));
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), 36 + data);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes.AsSpan(8, 4));
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes.AsSpan(12, 4));
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20, 2), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22, 2), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 4), 24000);
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 4), 24000 * 2);
        BitConverter.TryWriteBytes(bytes.AsSpan(32, 2), (short)2);
        BitConverter.TryWriteBytes(bytes.AsSpan(34, 2), (short)16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes.AsSpan(36, 4));
        BitConverter.TryWriteBytes(bytes.AsSpan(40, 4), data);
        pcm.CopyTo(bytes.AsSpan(44));
        return bytes;
    }
}
