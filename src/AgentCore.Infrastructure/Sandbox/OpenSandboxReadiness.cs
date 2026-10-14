using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Sandbox;

public sealed class OpenSandboxReadiness(OpenSandboxOptions options, ISandboxStore store, ISandboxImageAuthority authority) : ISandboxReadiness
{
    private volatile bool _ready;
    private volatile bool _profilesAvailable;
    public bool IsAvailable => options.Enabled && _ready && _profilesAvailable;
    public string State => !options.Enabled ? "Disabled" : _ready ? "Ready" : "Unavailable";
    public string SafeMessage => !options.Enabled ? "Enable OpenSandbox in host configuration to use sandbox capabilities."
        : _ready && !_profilesAvailable ? "Private OpenSandbox is reachable. Register and verify an approved sandbox profile before using commands."
        : IsAvailable ? "Trusted local OpenSandbox is authenticated and reachable."
        : "Check the private server authentication and Docker readiness. Core remains available.";
    public async ValueTask RefreshAsync(CancellationToken ct)
    {
        if (!options.Enabled) { _ready = false; return; }
        try
        {
            var uri = options.Origin();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri,"/sandboxes?pageSize=1"));
            request.Headers.Add("OPEN-SANDBOX-API-KEY",options.ApiKey);
            using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if (!response.IsSuccessStatusCode) { _ready = false; return; }
            _ready = true;
            _profilesAvailable = false;
            foreach (var profile in await store.ListProfilesAsync(ct))
                if ((await authority.InspectAsync(profile, ct)).Eligible) { _profilesAvailable = true; break; }
        }
        catch { _ready = false; /* Never expose upstream exception bodies, connection options or credentials. */ }
    }
}
