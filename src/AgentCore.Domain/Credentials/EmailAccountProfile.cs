using System.Net.Mail;

namespace AgentCore.Domain.Credentials;

public enum EmailProviderPreset { Gmail, Custom }
public enum EmailAuthMethod { Password, GoogleOAuth2 }
public enum EmailConnectionStatus { NotConnected, Connected, ReconnectRequired, Disconnected }
public enum MailTlsMode { ImplicitTls, StartTls }
public sealed record MailEndpoint(string Host, int Port, MailTlsMode TlsMode);
public sealed record EmailAccountProfile(EmailProviderPreset Preset, string EmailAddress,
    string ImapUsername, string SmtpUsername, MailEndpoint? ImapEndpoint, MailEndpoint? SmtpEndpoint,
    EmailAuthMethod AuthMethod = EmailAuthMethod.Password, string? GoogleSubject = null,
    IReadOnlyList<string>? GrantedScopes = null, EmailConnectionStatus ConnectionStatus = EmailConnectionStatus.NotConnected,
    long AuthEpoch = 0, DateTimeOffset? ConnectedAtUtc = null, bool Synthetic = false)
{
    public const string ReadScope = "https://www.googleapis.com/auth/gmail.readonly";
    public const string SendScope = "https://www.googleapis.com/auth/gmail.send";
    public bool IsUsable => AuthMethod == EmailAuthMethod.Password || ConnectionStatus == EmailConnectionStatus.Connected;
    public bool CanSend => IsUsable && (AuthMethod == EmailAuthMethod.Password || GrantedScopes?.Contains(SendScope) == true);
    public static EmailAccountProfile Gmail(string address = "") => new(EmailProviderPreset.Gmail, address, "", "", null, null, EmailAuthMethod.GoogleOAuth2);
    public static EmailAccountProfile Custom(string address) => new(EmailProviderPreset.Custom, address, address, address,
        new("mail.example.test", 993, MailTlsMode.ImplicitTls), new("mail.example.test", 465, MailTlsMode.ImplicitTls));

    public EmailAccountProfile Validate()
    {
        if (!Enum.IsDefined(AuthMethod) || !Enum.IsDefined(ConnectionStatus) || AuthEpoch < 0)
            throw new ArgumentException("Invalid email authorization state.");
        if (Preset == EmailProviderPreset.Gmail)
        {
            if (AuthMethod != EmailAuthMethod.GoogleOAuth2 || ImapEndpoint is not null || SmtpEndpoint is not null
                || ImapUsername != "" || SmtpUsername != "") throw new ArgumentException("Gmail requires Google OAuth; mail endpoints and passwords are not supported.");
            if (ConnectionStatus == EmailConnectionStatus.Connected && (string.IsNullOrWhiteSpace(GoogleSubject)
                || GrantedScopes?.Contains(ReadScope) != true || AuthEpoch < 1)) throw new ArgumentException("Connected Gmail requires a verified identity and read scope.");
            if (EmailAddress == "" && ConnectionStatus != EmailConnectionStatus.Connected) return this;
        }
        else if (AuthMethod != EmailAuthMethod.Password || GoogleSubject is not null || GrantedScopes is not null)
            throw new ArgumentException("Custom mail requires Password authentication.");
        if (!Enum.IsDefined(Preset) || EmailAddress is null || EmailAddress.Length > 254
            || EmailAddress.Any(char.IsControl) || !MailAddress.TryCreate(EmailAddress, out var address)
            || address.Address != EmailAddress || !EmailAddress.Contains('@'))
            throw new ArgumentException("Use one plain sender email address.");
        static string Username(string value) => value is { Length: > 0 and <= 254 } && !value.Any(char.IsControl)
            ? value : throw new ArgumentException("Mailbox username must contain 1–254 safe characters.");
        static MailEndpoint Endpoint(MailEndpoint? endpoint)
        {
            if (endpoint is null || endpoint.Host is null || endpoint.Host.Length is < 1 or > 253
                || Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns || endpoint.Host.Any(char.IsControl)
                || endpoint.Host.EndsWith('.') || endpoint.Port is < 1 or > 65535 || !Enum.IsDefined(endpoint.TlsMode))
                throw new ArgumentException("Use a DNS hostname, valid port and required TLS mode.");
            return endpoint with { Host = endpoint.Host.ToLowerInvariant() };
        }
        if (Preset == EmailProviderPreset.Gmail) return this;
        var result = this with { ImapUsername = Username(ImapUsername), SmtpUsername = Username(SmtpUsername),
            ImapEndpoint = Endpoint(ImapEndpoint), SmtpEndpoint = Endpoint(SmtpEndpoint) };
        return result;
    }
}
