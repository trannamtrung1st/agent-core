using System.Net;
using System.Net.Sockets;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Email;

public sealed class EmailTransportOptions
{
    public bool Enabled { get; set; } = true;
    public bool SmtpEnabled { get; set; } = true;
    public string[] AllowedCustomEndpoints { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 20;
}
public sealed class MailTransportPolicy(EmailTransportOptions options)
{
    public void ValidateEndpoint(EmailAccountProfile profile, bool smtp)
    {
        profile.Validate();
        var endpoint = smtp ? profile.SmtpEndpoint : profile.ImapEndpoint;
        if (profile.Preset == EmailProviderPreset.Custom && !options.AllowedCustomEndpoints.Contains($"{endpoint.Host}:{endpoint.Port}", StringComparer.OrdinalIgnoreCase))
            throw AgentCoreErrors.Forbidden("mail_endpoint_denied: Operator allowlist must authorize this endpoint.");
        if (!PublicNetworkPolicy.IsAllowedHostName(endpoint.Host)) throw AgentCoreErrors.Forbidden("mail_endpoint_denied: Public mail hostname is required.");
    }
    public static void ValidateAddresses(IPAddress[] addresses)
    {
        if (addresses.Length == 0 || addresses.Any(a => !PublicNetworkPolicy.IsAllowed(a)))
            throw AgentCoreErrors.Forbidden("mail_endpoint_denied: DNS resolved to a prohibited address.");
    }
    internal async ValueTask<Socket> ConnectAsync(EmailAccountProfile profile, bool smtp, CancellationToken ct)
    {
        ValidateEndpoint(profile, smtp);
        var endpoint = smtp ? profile.SmtpEndpoint : profile.ImapEndpoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        ValidateAddresses(addresses);
        // Connect to the checked address itself. MailKit receives the original hostname for SNI/certificate validation.
        var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(new IPEndPoint(addresses[0], endpoint.Port), ct); return socket; }
        catch { socket.Dispose(); throw; }
    }
}
