using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Models;

namespace AgentCore.Infrastructure.Providers;

public sealed class ModelMetadataTransport(HttpClient http, ITenantCredentialStore credentials, ICredentialProtector protector,
    ModelEndpointPolicy endpoints) : IModelMetadataTransport
{
    private const int MaxPages = 10;
    private const int MaxPageBytes = 2 * 1024 * 1024;
    public async ValueTask<ModelMetadataResult> ReadAsync(ProviderConnection connection, ModelCredentialWitness credential,
        bool discover, Func<CancellationToken, ValueTask> authorizeDispatch, CancellationToken ct = default)
    {
        try
        {
            endpoints.Validate(connection.Kind, connection.Endpoint, connection.NoCredential);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeout.Token; var models = new Dictionary<string, ModelDiscoveryEntry>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                await authorizeDispatch(token);
                using var request = new HttpRequestMessage(HttpMethod.Get, ListingUri(connection, cursor));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Options.Set(ModelProviderHttp.DispatchAuthority, authorizeDispatch);
                request.Options.Set(ModelProviderHttp.NoCredential, connection.NoCredential);
                var secret = await ReadSecret(connection, credential, token);
                if (secret is not null)
                {
                    if (connection.Kind == ModelProviderKind.Anthropic)
                    { request.Headers.Add("x-api-key", secret); request.Headers.Add("anthropic-version", "2023-06-01"); }
                    else if (connection.Kind == ModelProviderKind.Google) request.Headers.Add("x-goog-api-key", secret);
                    else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                }
                await authorizeDispatch(token);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                await authorizeDispatch(token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Failure("authentication_failed");
                if (!response.IsSuccessStatusCode) return Failure(); // Includes redirect/unsupported discovery. Never echo response bodies.
                using var document = JsonDocument.Parse(await ReadBounded(response, token));
                var root = document.RootElement; if (root.ValueKind != JsonValueKind.Object) return Failure();
                if (secret is not null && ContainsSecret(root, secret)) return Failure();
                var arrayName = connection.Kind == ModelProviderKind.Google ? "models" : "data";
                if (!root.TryGetProperty(arrayName, out var values) || values.ValueKind != JsonValueKind.Array) return Failure();
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Object) return Failure();
                    var idName = connection.Kind == ModelProviderKind.Google ? "name" : "id";
                    if (!value.TryGetProperty(idName, out var identity) || identity.ValueKind != JsonValueKind.String) return Failure();
                    var id = identity.GetString()!;
                    if (connection.Kind == ModelProviderKind.Google && id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..];
                    id = ModelManagementRules.ExternalId(id);
                    string? label = null;
                    foreach (var name in new[] { "display_name", "displayName", "name" })
                        if (value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 and <= 120 } text && !text.Any(char.IsControl))
                        { label = text; break; }
                    if (!models.TryAdd(id, new(id, label))) return Failure();
                    if (models.Count > ModelDiscoveryReconciliation.MaxModels) return Failure();
                }
                if (!discover) return new(true, "metadata_available", []);
                cursor = NextCursor(connection.Kind, root);
                if (cursor is null) return new(true, "metadata_available", Array.AsReadOnly(models.Values.ToArray()));
                if (cursor.Length is < 1 or > 1024 || cursor.Any(char.IsControl) || !cursors.Add(cursor)) return Failure();
            }
            return Failure(); // Never publish an incomplete page set.
        }
        catch (HttpRequestException) { return Failure(); }
        catch (IOException) { return Failure(); }
        catch (JsonException) { return Failure(); }
        catch (ArgumentException) { return Failure(); }
        catch (FormatException) { return Failure(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failure(); }
    }
    private async ValueTask<string?> ReadSecret(ProviderConnection connection, ModelCredentialWitness witness, CancellationToken ct)
    {
        var value = connection.CredentialId is { } id ? await credentials.FindTenantCredentialAsync(connection.TenantId, id, ct) : null;
        TenantModelRules.RequireCredential(connection, value);
        if (witness != new ModelCredentialWitness(connection.CredentialId, value?.Value.Revision, value?.Value.SecretRevision))
            throw new SecurityConflictException("model_credential_changed");
        return value is null ? null : protector.Unprotect(value.Value.CredentialId, value.Value.ProtectionVersion, value.Value.ProtectedPayload);
    }
    private static Uri ListingUri(ProviderConnection connection, string? cursor)
    {
        var suffix = connection.Kind switch
        {
            ModelProviderKind.Anthropic => "models?limit=1000" + (cursor is null ? "" : "&after_id=" + Uri.EscapeDataString(cursor)),
            ModelProviderKind.Google => "models?pageSize=1000" + (cursor is null ? "" : "&pageToken=" + Uri.EscapeDataString(cursor)),
            _ => "models" + (cursor is null ? "" : "?after=" + Uri.EscapeDataString(cursor))
        };
        return new(new Uri(connection.Endpoint), suffix);
    }
    private static string? NextCursor(ModelProviderKind kind, JsonElement root)
    {
        if (kind == ModelProviderKind.Google)
        {
            if (!root.TryGetProperty("nextPageToken", out var token) || token.ValueKind == JsonValueKind.Null) return null;
            return token.ValueKind == JsonValueKind.String ? token.GetString() : throw new JsonException("Invalid pagination token.");
        }
        if (!root.TryGetProperty("has_more", out var more) || more.ValueKind == JsonValueKind.False) return null;
        if (more.ValueKind != JsonValueKind.True) throw new JsonException("Invalid pagination indicator.");
        return root.TryGetProperty("last_id", out var last) && last.ValueKind == JsonValueKind.String ? last.GetString() : throw new JsonException("Incomplete model pagination.");
    }
    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxPageBytes) throw new JsonException("Model metadata exceeds bounds.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, ct); if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaxPageBytes) throw new JsonException("Model metadata exceeds bounds.");
            buffer.Write(bytes, 0, read);
        }
    }
    private static ModelMetadataResult Failure(string result = "metadata_unavailable") => new(false, result, []);
    private static bool ContainsSecret(JsonElement value, string secret) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Contains(secret, StringComparison.Ordinal) == true,
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsSecret(item, secret)),
        JsonValueKind.Object => value.EnumerateObject().Any(property => property.Name.Contains(secret, StringComparison.Ordinal) || ContainsSecret(property.Value, secret)),
        _ => false
    };
}
