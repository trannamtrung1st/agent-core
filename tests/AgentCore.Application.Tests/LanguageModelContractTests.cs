using System.Reflection;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tests;

public sealed class LanguageModelContractTests
{
    [Fact]
    public void ModelRequest_can_carry_a_response_contract()
    {
        var contract = new ModelResponseContract(SpeechWillBeUsed: true);
        var request = new ModelRequest(
            Guid.NewGuid(),
            [new ModelMessage(ModelRole.User, "Hello")],
            ResponseContract: contract);
        Assert.Same(contract, request.ResponseContract);
        Assert.True(contract.SpeechWillBeUsed);
        Assert.False(new ModelResponseContract(SpeechWillBeUsed: false).SpeechWillBeUsed);
    }

    [Fact]
    public void ModelRequest_has_no_provider_schema_fields()
    {
        var names = typeof(ModelRequest).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("ResponseFormat", names);
        Assert.DoesNotContain("JsonSchema", names);
        Assert.DoesNotContain("response_format", names);
        Assert.DoesNotContain("json_schema", names);
        Assert.Contains("ResponseContract", names);
    }

    [Fact]
    public void Application_public_types_have_no_openai_schema_field_names()
    {
        foreach (var type in typeof(ModelRequest).Assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public))
            {
                Assert.False(
                    string.Equals(member.Name, "ResponseFormat", StringComparison.Ordinal)
                    || string.Equals(member.Name, "JsonSchema", StringComparison.Ordinal)
                    || member.Name.Contains("json_schema", StringComparison.Ordinal)
                    || member.Name.Contains("response_format", StringComparison.Ordinal),
                    type.FullName + "." + member.Name);
            }
        }
    }

    [Fact]
    public void InvalidResponse_is_distinct_from_InvalidRequest()
    {
        Assert.NotEqual(ProviderErrorCode.InvalidRequest, ProviderErrorCode.InvalidResponse);
        var failed = new ModelFailed(new ProviderFailure(ProviderErrorCode.InvalidResponse, "Malformed assistant envelope."));
        Assert.Equal(ProviderErrorCode.InvalidResponse, failed.Failure.Code);
        Assert.NotEqual(ProviderErrorCode.InvalidRequest, failed.Failure.Code);
    }

    [Fact]
    public void Semantic_display_and_ready_events_exist()
    {
        var response = new ModelSemanticResponse(
            "Shown",
            new ModelSpeechProjection(ModelSpeechMode.Same, null),
            []);
        ModelGenerationEvent display = new ModelDisplayDelta("Shown");
        ModelGenerationEvent ready = new ModelSemanticResponseReady(response);
        Assert.Equal("Shown", Assert.IsType<ModelDisplayDelta>(display).Text);
        Assert.Equal("Shown", Assert.IsType<ModelSemanticResponseReady>(ready).Response.DisplayText);
    }

    [Fact]
    public async Task Null_response_contract_keeps_model_text_delta()
    {
        var model = new ContractAwareLanguageModel();
        var events = await CollectAsync(model, new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "Hi")]));
        Assert.Contains(events, item => item is ModelTextDelta);
        Assert.DoesNotContain(events, item => item is ModelDisplayDelta);
        Assert.DoesNotContain(events, item => item is ModelSemanticResponseReady);
    }

    [Fact]
    public async Task Contract_request_uses_semantic_events_instead_of_model_text_delta()
    {
        var model = new ContractAwareLanguageModel();
        var request = new ModelRequest(
            Guid.NewGuid(),
            [new ModelMessage(ModelRole.User, "Hi")],
            ResponseContract: new ModelResponseContract(SpeechWillBeUsed: false));
        var events = await CollectAsync(model, request);
        Assert.Contains(events, item => item is ModelDisplayDelta);
        Assert.Contains(events, item => item is ModelSemanticResponseReady);
        Assert.DoesNotContain(events, item => item is ModelTextDelta);
    }

    private static async Task<List<ModelGenerationEvent>> CollectAsync(ILanguageModel model, ModelRequest request)
    {
        var listed = new List<ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(request))
        {
            listed.Add(item);
        }

        return listed;
    }

    private sealed class ContractAwareLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (request.ResponseContract is null)
            {
                yield return new ModelTextDelta("raw");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            var response = new ModelSemanticResponse(
                "Shown",
                new ModelSpeechProjection(ModelSpeechMode.Same, null),
                []);
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(response);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
