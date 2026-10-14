using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using OpenSandbox.Models;

namespace AgentCore.Infrastructure.Sandbox;

/// <summary>Revalidate remote manifest access even when OpenSandbox finds the image in the daemon cache.</summary>
internal static class RegistryAuthorizationProbe
{
    internal static async Task CheckAsync(OpenSandboxOptions options, OciImageReference image, ImageAuth? auth, CancellationToken ct)
    {
        try
        {
            var insecure = options.InsecureRegistryHosts.Contains(image.Registry!, StringComparer.Ordinal);
            var origin = new Uri((insecure ? "http://" : "https://") + image.Registry);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            var basic = auth is null ? null : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.Username + ":" + auth.Password)));
            var manifest = new Uri(origin, "/v2/" + image.Repository + "/manifests/sha256:" + image.Digest);
            async Task<HttpResponseMessage> Head(AuthenticationHeaderValue? authorization)
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, manifest);
                request.Headers.Authorization = authorization;
                request.Headers.Accept.ParseAdd("application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json");
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            }
            using var response = await Head(basic);
            if (response.IsSuccessStatusCode) return;
            var challenge = response.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
            if (response.StatusCode != HttpStatusCode.Unauthorized || challenge?.Parameter is not { Length: <=4096 } parameters) throw new InvalidOperationException();
            var fields = Regex.Matches(parameters, "([a-z]+)=\"([^\"]*)\"").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            if (!fields.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var tokenUri)
                || tokenUri.UserInfo.Length != 0 || tokenUri.Query.Length != 0 || tokenUri.Fragment.Length != 0
                || tokenUri.Scheme != "https" && !(insecure && tokenUri.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority))
                || tokenUri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority)
                    && !options.AllowedRegistryTokenOrigins.Contains(tokenUri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal)) throw new InvalidOperationException();
            var query = "scope=" + Uri.EscapeDataString("repository:" + image.Repository + ":pull")
                + (fields.TryGetValue("service", out var service) ? "&service=" + Uri.EscapeDataString(service) : "");
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenUri + "?" + query);
            tokenRequest.Headers.Authorization = basic;
            using var tokenResponse = await client.SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            tokenResponse.EnsureSuccessStatusCode();
            await using var stream = await tokenResponse.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            { if (bytes.Length + count > 65536) throw new InvalidOperationException(); bytes.Write(buffer,0,count); }
            using var json = JsonDocument.Parse(bytes.ToArray());
            var token = json.RootElement.TryGetProperty("token", out var value) ? value.GetString() : json.RootElement.GetProperty("access_token").GetString();
            using var authorized = await Head(new("Bearer", token));
            authorized.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw AgentCoreErrors.Forbidden("Registry manifest authorization failed. Check the credential, digest, registry availability and host token-service policy, then verify again."); }
    }
}
