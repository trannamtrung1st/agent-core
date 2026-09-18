using AgentCore.Application.Sessions;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class SpeechFactoryTests
{
    [Fact]
    public void Synthetic_resolves_backend_adapters_and_server_audio()
    {
        using var provider = Build(new SpeechProvidersOptions());
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.IsType<SyntheticSpeechRecognizer>(speech.Recognizer);
        Assert.IsType<SyntheticSpeechSynthesizer>(speech.Synthesizer);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.OutputTransport);
        Assert.True(speech.Plan.RecognitionResolvable);
        Assert.True(speech.Plan.SynthesisResolvable);
        Assert.IsType<SyntheticSpeechRecognizer>(provider.GetRequiredService<ISpeechRecognizer>());
        Assert.IsType<SyntheticSpeechSynthesizer>(provider.GetRequiredService<ISpeechSynthesizer>());
    }

    [Fact]
    public void Browser_is_client_transport_without_backend_ports()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "Browser" },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "Browser" }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.Null(speech.Recognizer);
        Assert.Null(speech.Synthesizer);
        Assert.Equal(SpeechTransport.ClientTranscript, speech.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ClientSpeech, speech.Plan.OutputTransport);
        Assert.True(speech.Plan.RecognitionResolvable);
        Assert.True(speech.Plan.SynthesisResolvable);
        Assert.Null(speech.Plan.RecognitionCapabilities);
        Assert.Null(speech.Plan.SynthesisCapabilities);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISpeechRecognizer>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISpeechSynthesizer>());
    }

    [Fact]
    public void Deferred_openai_realtime_stt_is_not_selectable_or_replaced()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "OpenAI", ApiKey = "present" },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "Synthetic" }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.Null(speech.Recognizer);
        Assert.IsNotType<OpenAiSpeechRecognizer>(speech.Recognizer);
        Assert.False(speech.Recognizer is SyntheticSpeechRecognizer);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.InputTransport);
        Assert.False(speech.Plan.RecognitionResolvable);
        Assert.IsType<SyntheticSpeechSynthesizer>(speech.Synthesizer);
    }

    [Fact]
    public void Hosted_batch_stt_and_openai_tts_resolve_with_keys_without_claiming_realtime()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "OpenAICompatibleBatch", ApiKey = "present" },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "OpenAI", ApiKey = "present" }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.IsType<OpenAICompatibleBatchSpeechRecognizer>(speech.Recognizer);
        Assert.IsType<OpenAiSpeechSynthesizer>(speech.Synthesizer);
        Assert.False(speech.Recognizer is SyntheticSpeechRecognizer);
        Assert.False(speech.Recognizer is OpenAiSpeechRecognizer);
        Assert.False(speech.Synthesizer is SyntheticSpeechSynthesizer);
        Assert.True(speech.Plan.RecognitionResolvable);
        Assert.True(speech.Plan.SynthesisResolvable);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.OutputTransport);
        Assert.False(speech.Plan.RecognitionCapabilities?.StreamingAudio);
        Assert.False(speech.Plan.RecognitionCapabilities?.PartialTranscripts);
        Assert.False(speech.Plan.RecognitionCapabilities?.SpeechBoundaryEvents);
        Assert.True(speech.Plan.RecognitionCapabilities?.Cancellation);
        Assert.True(speech.Plan.SynthesisCapabilities?.StreamingAudio);
        Assert.True(speech.Plan.SynthesisCapabilities?.Cancellation);
        Assert.IsType<OpenAICompatibleBatchSpeechRecognizer>(provider.GetRequiredService<ISpeechRecognizer>());
        Assert.IsType<OpenAiSpeechSynthesizer>(provider.GetRequiredService<ISpeechSynthesizer>());
    }

    [Fact]
    public void Hosted_batch_stt_without_key_is_not_resolvable_and_not_synthetic()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "OpenAICompatibleBatch", ApiKey = "  " },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "Browser" }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.Null(speech.Recognizer);
        Assert.False(speech.Recognizer is OpenAICompatibleBatchSpeechRecognizer);
        Assert.False(speech.Recognizer is SyntheticSpeechRecognizer);
        Assert.False(speech.Plan.RecognitionResolvable);
        Assert.True(speech.Plan.SynthesisResolvable);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.InputTransport);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISpeechRecognizer>());
    }

    [Fact]
    public void OpenAI_tts_without_key_is_not_resolvable_and_not_synthetic()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "Synthetic" },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "OpenAI", ApiKey = "  " }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.IsType<SyntheticSpeechRecognizer>(speech.Recognizer);
        Assert.Null(speech.Synthesizer);
        Assert.False(speech.Synthesizer is SyntheticSpeechSynthesizer);
        Assert.False(speech.Synthesizer is OpenAiSpeechSynthesizer);
        Assert.True(speech.Plan.RecognitionResolvable);
        Assert.False(speech.Plan.SynthesisResolvable);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.OutputTransport);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISpeechSynthesizer>());
    }

    [Fact]
    public void Mixed_browser_stt_and_openai_tts_with_key_resolves_openai_synthesizer()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "Browser" },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "OpenAI", ApiKey = "present" }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.Null(speech.Recognizer);
        Assert.IsType<OpenAiSpeechSynthesizer>(speech.Synthesizer);
        Assert.Equal(SpeechTransport.ClientTranscript, speech.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.OutputTransport);
        Assert.True(speech.Plan.RecognitionResolvable);
        Assert.True(speech.Plan.SynthesisResolvable);
        Assert.False(speech.Synthesizer is SyntheticSpeechSynthesizer);
    }

    [Fact]
    public void Mixed_batch_stt_and_browser_tts_with_key_resolves_batch_recognizer()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "OpenAICompatibleBatch", ApiKey = "present" },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "Browser" }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.IsType<OpenAICompatibleBatchSpeechRecognizer>(speech.Recognizer);
        Assert.Null(speech.Synthesizer);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ClientSpeech, speech.Plan.OutputTransport);
        Assert.True(speech.Plan.RecognitionResolvable);
        Assert.True(speech.Plan.SynthesisResolvable);
        Assert.False(speech.Plan.RecognitionCapabilities?.PartialTranscripts);
        Assert.False(speech.Recognizer is OpenAiSpeechRecognizer);
        Assert.False(speech.Recognizer is SyntheticSpeechRecognizer);
    }

    [Fact]
    public void Section37_combinations_resolve_with_dummy_keys_without_synthetic_fallback()
    {
        var cases = new (string Recognition, string Synthesis, string Input, string Output, Type? Recognizer, Type? Synthesizer)[]
        {
            ("Synthetic", "Synthetic", SpeechTransport.ServerAudio, SpeechTransport.ServerAudio, typeof(SyntheticSpeechRecognizer), typeof(SyntheticSpeechSynthesizer)),
            ("Browser", "Browser", SpeechTransport.ClientTranscript, SpeechTransport.ClientSpeech, null, null),
            ("Browser", "OpenAI", SpeechTransport.ClientTranscript, SpeechTransport.ServerAudio, null, typeof(OpenAiSpeechSynthesizer)),
            ("OpenAICompatibleBatch", "Browser", SpeechTransport.ServerAudio, SpeechTransport.ClientSpeech, typeof(OpenAICompatibleBatchSpeechRecognizer), null),
            ("OpenAICompatibleBatch", "OpenAI", SpeechTransport.ServerAudio, SpeechTransport.ServerAudio, typeof(OpenAICompatibleBatchSpeechRecognizer), typeof(OpenAiSpeechSynthesizer))
        };

        foreach (var item in cases)
        {
            using var provider = Build(new SpeechProvidersOptions
            {
                Recognition = new SpeechRecognitionProviderOptions { Adapter = item.Recognition, ApiKey = "not-a-live-key" },
                Synthesis = new SpeechSynthesisProviderOptions { Adapter = item.Synthesis, ApiKey = "not-a-live-key" }
            });
            var speech = provider.GetRequiredService<SpeechResolution>();
            Assert.Equal(item.Input, speech.Plan.InputTransport);
            Assert.Equal(item.Output, speech.Plan.OutputTransport);
            Assert.True(speech.Plan.RecognitionResolvable);
            Assert.True(speech.Plan.SynthesisResolvable);
            Assert.False(speech.Recognizer is OpenAiSpeechRecognizer);
            if (item.Recognizer is null)
            {
                Assert.Null(speech.Recognizer);
            }
            else
            {
                Assert.IsType(item.Recognizer, speech.Recognizer);
            }

            if (item.Synthesizer is null)
            {
                Assert.Null(speech.Synthesizer);
            }
            else
            {
                Assert.IsType(item.Synthesizer, speech.Synthesizer);
            }
        }
    }

    [Theory]
    [InlineData("Synthetic", "Synthetic", SpeechTransport.ServerAudio, SpeechTransport.ServerAudio, true, true, true, true)]
    [InlineData("Browser", "Browser", SpeechTransport.ClientTranscript, SpeechTransport.ClientSpeech, false, false, true, true)]
    public void Required_mixed_combinations_resolve_without_paid_clients_or_synthetic_fallback(
        string recognitionAdapter,
        string synthesisAdapter,
        string inputTransport,
        string outputTransport,
        bool recognitionPort,
        bool synthesisPort,
        bool recognitionResolvable,
        bool synthesisResolvable)
    {
        foreach (var key in new[] { null, "not-a-live-key" })
        {
            using var provider = Build(new SpeechProvidersOptions
            {
                Recognition = new SpeechRecognitionProviderOptions { Adapter = recognitionAdapter, ApiKey = key },
                Synthesis = new SpeechSynthesisProviderOptions { Adapter = synthesisAdapter, ApiKey = key }
            });
            var speech = provider.GetRequiredService<SpeechResolution>();
            Assert.Equal(inputTransport, speech.Plan.InputTransport);
            Assert.Equal(outputTransport, speech.Plan.OutputTransport);
            Assert.Equal(recognitionResolvable, speech.Plan.RecognitionResolvable);
            Assert.Equal(synthesisResolvable, speech.Plan.SynthesisResolvable);
            Assert.Equal(recognitionPort, speech.Recognizer is not null);
            Assert.Equal(synthesisPort, speech.Synthesizer is not null);
            Assert.False(speech.Recognizer is OpenAiSpeechRecognizer);
            Assert.False(speech.Recognizer is OpenAICompatibleBatchSpeechRecognizer);
            Assert.False(speech.Synthesizer is OpenAiSpeechSynthesizer);
            if (recognitionPort)
            {
                Assert.IsType<SyntheticSpeechRecognizer>(speech.Recognizer);
            }

            if (synthesisPort)
            {
                Assert.IsType<SyntheticSpeechSynthesizer>(speech.Synthesizer);
            }
        }
    }

    [Fact]
    public void OpenAI_stt_and_tts_with_key_keeps_realtime_stt_gated()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "OpenAI", ApiKey = "not-a-live-key" },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "OpenAI", ApiKey = "not-a-live-key" }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.Null(speech.Recognizer);
        Assert.IsType<OpenAiSpeechSynthesizer>(speech.Synthesizer);
        Assert.False(speech.Plan.RecognitionResolvable);
        Assert.True(speech.Plan.SynthesisResolvable);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ServerAudio, speech.Plan.OutputTransport);
        Assert.False(speech.Recognizer is OpenAiSpeechRecognizer);
        Assert.False(speech.Recognizer is OpenAICompatibleBatchSpeechRecognizer);
        Assert.False(speech.Synthesizer is SyntheticSpeechSynthesizer);
    }

    [Fact]
    public void Unknown_adapter_is_not_silently_synthetic()
    {
        using var provider = Build(new SpeechProvidersOptions
        {
            Recognition = new SpeechRecognitionProviderOptions { Adapter = "NotAThing" },
            Synthesis = new SpeechSynthesisProviderOptions { Adapter = "Local" }
        });
        var speech = provider.GetRequiredService<SpeechResolution>();
        Assert.Null(speech.Recognizer);
        Assert.Null(speech.Synthesizer);
        Assert.False(speech.Plan.RecognitionResolvable);
        Assert.False(speech.Plan.SynthesisResolvable);
    }

    private static ServiceProvider Build(SpeechProvidersOptions speech)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic", new LanguageModelProviderOptions { Adapter = "Scripted" }, speech: speech);
        return services.BuildServiceProvider();
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return Path.Combine(dir.FullName, "agents");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
