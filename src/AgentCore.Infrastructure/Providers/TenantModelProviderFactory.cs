using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Providers.Native;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Infrastructure.Providers;

public sealed class TenantModelProviderFactory(IHttpClientFactory clients, ITenantModelRouteAuthority routes,
    ITenantCredentialStore credentials, ICredentialProtector protector, ModelEndpointPolicy endpoints,
    IIdGenerator ids, TimeProvider time) : IModelProviderFactory
{
    private IHttpClientFactory Clients { get; } = clients;
    private ITenantModelRouteAuthority Routes { get; } = routes;
    private ITenantCredentialStore Credentials { get; } = credentials;
    private ICredentialProtector Protector { get; } = protector;
    private ModelEndpointPolicy Endpoints { get; } = endpoints;
    private IIdGenerator Ids { get; } = ids;
    private TimeProvider Time { get; } = time;
    public ILanguageModel Create(ModelRoutePin route, ModelCapabilityProfile capabilities, ModelInvocation invocation, ModelPurpose purpose)
    {
        if (invocation is null || invocation.Route != route || invocation.Context.TenantId != route.TenantId) throw new AccessDeniedException("model_route_scope_mismatch");
        capabilities = ModelManagementRules.Freeze(capabilities);
        return new SemanticResponseLanguageModel(new BoundModel(this, route, capabilities, invocation, purpose),
            preferResponseFunction: capabilities.SemanticMode == ModelSemanticMode.ResponseFunction);
    }
    private sealed class BoundModel(TenantModelProviderFactory owner, ModelRoutePin route, ModelCapabilityProfile profile,
        ModelInvocation invocation, ModelPurpose purpose) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, profile.Vision, profile.Tools, profile.StructuredOutput);
        public ModelInferenceTransport? InferenceTransport => route.Transport switch
        {
            ModelTransport.ChatCompletions => ModelInferenceTransport.ChatCompletions,
            ModelTransport.Responses => ModelInferenceTransport.Responses,
            ModelTransport.AnthropicMessages => ModelInferenceTransport.AnthropicMessages,
            ModelTransport.GeminiContent => ModelInferenceTransport.GeminiContent,
            _ => null
        };
        private async ValueTask<ModelRouteSnapshot> Current(CancellationToken ct)
        {
            var current = await owner.Routes.RequireAsync(route, invocation, purpose, ct).ConfigureAwait(false);
            if (JsonSerializer.Serialize(current.Offering.Capabilities) != JsonSerializer.Serialize(profile))
                throw new AgentCoreException("ModelRouteStale", "The admitted model capability profile changed.", 409);
            owner.Endpoints.Validate(current.Connection.Kind, current.Connection.Endpoint, current.Connection.NoCredential);
            return current;
        }
        private async ValueTask Authorize(CancellationToken ct) => _ = await Current(ct).ConfigureAwait(false);
        private async ValueTask<string?> Secret(CancellationToken ct)
        {
            var current = await Current(ct).ConfigureAwait(false);
            var credential = current.Connection.CredentialId is { } id ? await owner.Credentials.FindTenantCredentialAsync(route.TenantId, id, ct).ConfigureAwait(false) : null;
            TenantModelRules.RequireCredential(current.Connection, credential);
            if (current.Credential != new ModelCredentialWitness(current.Connection.CredentialId, credential?.Value.Revision, credential?.Value.SecretRevision))
                throw new SecurityConflictException("model_credential_changed");
            var value = credential is null ? null : owner.Protector.Unprotect(credential.Value.CredentialId, credential.Value.ProtectionVersion, credential.Value.ProtectedPayload);
            if (value is not null) TenantModelRules.ApiKey(value);
            await Authorize(ct).ConfigureAwait(false); return value;
        }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await using var iterator = GenerateCore(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                var more = false; ModelFailed? failure = null;
                try { more = await iterator.MoveNextAsync(); }
                catch (Exception error) when (error is AccessDeniedException or SecurityConflictException or AgentCoreException
                    or JsonException or HttpRequestException or IOException or OperationCanceledException or ArgumentException or InvalidOperationException)
                {
                    failure = error switch
                    {
                        AccessDeniedException or SecurityConflictException => new(new(ProviderErrorCode.Authentication, "The admitted model route is no longer authorized.")),
                        AgentCoreException { Code: "ModelCapabilityUnsupported" } => new(new(ProviderErrorCode.UnsupportedCapability, "The model does not support the required Core contract.")),
                        AgentCoreException { Code: "ModelRouteStale" } => new(new(ProviderErrorCode.InvalidRequest, "The admitted model route changed. Start a new Run with a current model.")),
                        OperationCanceledException => new(new(cancellationToken.IsCancellationRequested ? ProviderErrorCode.Cancelled : ProviderErrorCode.Timeout, "The model request was cancelled or timed out.")),
                        JsonException or ArgumentException => new(new(ProviderErrorCode.InvalidRequest, "The model route or continuation is invalid.")),
                        _ => new(new(ProviderErrorCode.Unavailable, "The admitted model connection is unavailable."))
                    };
                }
                if (failure is not null) { yield return failure; yield break; }
                if (!more) yield break;
                yield return iterator.Current;
            }
        }
        private async IAsyncEnumerable<ModelGenerationEvent> GenerateCore(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var current = await Current(ct).ConfigureAwait(false);
            if (!Enum.IsDefined(request.ToolChoice) || request.ToolChoice == ModelToolChoice.Named && request.Tools?.Any(t => t.Name == request.ToolChoiceName) != true)
                throw new ArgumentException("Explicit tool choice must identify an offered tool.");
            if (request.ToolChoice != ModelToolChoice.Auto && !profile.ForcedToolChoice
                || request.ReasoningEffort is { } effort && !profile.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
                throw new AgentCoreException("ModelCapabilityUnsupported", "The model does not support the requested inference controls.", 409);
            using var http = owner.Clients.CreateClient(ModelProviderHttp.ClientName);
            ILanguageModel model;
            if (route.Kind is ModelProviderKind.Anthropic or ModelProviderKind.Google)
                model = new NativeLanguageModel(http, route, current.Connection.Endpoint, profile, owner.Protector, owner.Ids, owner.Time, Secret, Authorize);
            else
            {
                var continuation = new NativeModelContinuation(route, owner.Protector);
                var options = new LanguageModelProviderOptions
                {
                    Adapter = route.Kind.ToString(), BaseUrl = current.Connection.Endpoint, DefaultModel = route.ExternalModelId,
                    ApiKey = await Secret(ct).ConfigureAwait(false), Transport = InferenceTransport!.Value,
                    Tools = profile.Tools, Vision = profile.Vision, StructuredOutput = profile.StructuredOutput,
                    DeveloperRoleSupport = profile.Hierarchy == ModelInstructionHierarchy.NativeDeveloper,
                    ReasoningObjectWire = route.Kind == ModelProviderKind.OpenRouter,
                    ProtectContinuation = continuation.EncodeOpaque, UnprotectContinuation = continuation.DecodeOpaque,
                    PrepareDispatch = async (message, token) =>
                    {
                        await Authorize(token).ConfigureAwait(false);
                        message.Options.Set(ModelProviderHttp.DispatchAuthority, Authorize);
                        message.Options.Set(ModelProviderHttp.NoCredential, current.Connection.NoCredential);
                    }
                };
                model = new OpenAICompatibleLanguageModel(http, options, owner.Time);
            }
            var calls = new List<ModelToolCall>();
            await foreach (var item in model.GenerateAsync(request, ct).ConfigureAwait(false))
            {
                if (item is ModelToolCallEvent call)
                {
                    if (calls.Count == 64) throw new JsonException("Model tool round exceeds its bound.");
                    calls.Add(call.Call); continue;
                }
                if (item is ModelCompleted completed)
                {
                    await Authorize(ct).ConfigureAwait(false);
                    if (calls.Count > 0 && completed.Reason != ModelStopReason.ToolCalls) throw new JsonException("Model tool round is incomplete.");
                    if (request.ToolChoice != ModelToolChoice.Auto && (calls.Count == 0 || request.ToolChoice == ModelToolChoice.Named && calls.Any(c => c.Name != request.ToolChoiceName)))
                        throw new JsonException("The model did not honor the requested tool choice.");
                    foreach (var pending in calls) yield return new ModelToolCallEvent(pending);
                    yield return completed; yield break;
                }
                yield return item;
                if (item is ModelFailed) yield break;
            }
            yield return new ModelFailed(new(ProviderErrorCode.InvalidResponse, "The model stream ended without a validated terminal response."));
        }
    }
}
