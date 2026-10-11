using System.Net.Mail;

namespace AgentCore.Domain.Credentials;

public enum EmailProviderPreset { Gmail, Custom }
public enum MailTlsMode { ImplicitTls, StartTls }
public sealed record MailEndpoint(string Host, int Port, MailTlsMode TlsMode);
public sealed record EmailAccountProfile(EmailProviderPreset Preset, string EmailAddress,
    string ImapUsername, string SmtpUsername, MailEndpoint ImapEndpoint, MailEndpoint SmtpEndpoint)
{
    public static EmailAccountProfile Gmail(string address) => new(EmailProviderPreset.Gmail, address, address, address,
        new("imap.gmail.com", 993, MailTlsMode.ImplicitTls), new("smtp.gmail.com", 465, MailTlsMode.ImplicitTls));

    public EmailAccountProfile Validate()
    {
        if (!Enum.IsDefined(Preset) || EmailAddress is null || EmailAddress.Length > 254
            || EmailAddress.Any(char.IsControl) || !MailAddress.TryCreate(EmailAddress, out var address)
            || address.Address != EmailAddress || !EmailAddress.Contains('@'))
            throw new ArgumentException("Use one plain sender email address.");
        static string Username(string value) => value is { Length: > 0 and <= 254 } && !value.Any(char.IsControl)
            ? value : throw new ArgumentException("Mailbox username must contain 1–254 safe characters.");
        static MailEndpoint Endpoint(MailEndpoint endpoint)
        {
            if (endpoint is null || endpoint.Host is null || endpoint.Host.Length is < 1 or > 253
                || Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns || endpoint.Host.Any(char.IsControl)
                || endpoint.Host.EndsWith('.') || endpoint.Port is < 1 or > 65535 || !Enum.IsDefined(endpoint.TlsMode))
                throw new ArgumentException("Use a DNS hostname, valid port and required TLS mode.");
            return endpoint with { Host = endpoint.Host.ToLowerInvariant() };
        }
        var result = this with { ImapUsername = Username(ImapUsername), SmtpUsername = Username(SmtpUsername),
            ImapEndpoint = Endpoint(ImapEndpoint), SmtpEndpoint = Endpoint(SmtpEndpoint) };
        if (Preset == EmailProviderPreset.Gmail && (result.ImapEndpoint != Gmail(EmailAddress).ImapEndpoint
            || result.SmtpEndpoint != Gmail(EmailAddress).SmtpEndpoint && result.SmtpEndpoint != new MailEndpoint("smtp.gmail.com", 587, MailTlsMode.StartTls)
            || ImapUsername != EmailAddress || SmtpUsername != EmailAddress))
            throw new ArgumentException("Gmail uses fixed TLS endpoints and the account address as username.");
        return result;
    }
}
