using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.AspNetCore.DataProtection;

namespace AgentCore.Infrastructure.Credentials;

public sealed class LocalCredentialProtector : ICredentialProtector
{
    private readonly Lazy<IDataProtectionProvider> _provider;
    public LocalCredentialProtector(string keyRoot)
    {
        _provider = new(() =>
        {
            var root = Directory.CreateDirectory(Path.GetFullPath(keyRoot));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return DataProtectionProvider.Create(root, builder => builder.SetApplicationName("AgentCore.Credentials"));
        });
    }
    private IDataProtector For(Guid id, int version) => _provider.Value.CreateProtector("AgentCore.SystemCredential", version.ToString(System.Globalization.CultureInfo.InvariantCulture), id.ToString("D"));
    public string Protect(Guid credentialId, int version, string value) => Run(() => For(credentialId, version).Protect(value));
    public string Unprotect(Guid credentialId, int version, string payload) => Run(() => For(credentialId, version).Unprotect(payload));
    private static string Run(Func<string> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException)
        { throw new AgentCoreException("credential_unavailable", "Credential protection is unavailable.", 503); }
    }
}
