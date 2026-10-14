using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Sandbox;

public sealed class OpenSandboxReadiness(OpenSandboxOptions options, ISandboxStore store) : ISandboxReadiness
{
    private volatile bool _ready;
    private volatile bool _profilesAvailable;
    public bool IsAvailable => options.Enabled && _ready && _profilesAvailable;
    public string State => !options.Enabled ? "Disabled" : IsAvailable ? "Ready" : "Unavailable";
    public string SafeMessage => !options.Enabled ? "Enable OpenSandbox in host configuration to use sandbox capabilities."
        : _ready && !_profilesAvailable ? "Private OpenSandbox is reachable. Add or enable an approved sandbox profile before using commands."
        : IsAvailable ? "Trusted local OpenSandbox is authenticated and reachable."
        : "Check the private server, API key, pinned image and Docker readiness. Core remains available.";
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
            if (await store.GetProfileAsync("general-workbench",ct:ct) is null && options.WorkbenchImage is { Length: > 0 } image)
            {
                var profile = new SandboxProfile("general-workbench",1,"General Workbench","Offline Python, Node, data analysis and Office generation.",true,image);
                options.ValidateProfile(profile); await store.SaveProfileAsync(profile,0,ct);
            }
            _ready = true;
            _profilesAvailable = (await store.ListProfilesAsync(ct)).Any(p => p.Enabled);
        }
        catch { _ready = false; /* Never expose upstream exception bodies, connection options or credentials. */ }
    }
}
