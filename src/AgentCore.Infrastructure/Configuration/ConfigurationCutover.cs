using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.Configuration;

/// <summary>Reject retired host inputs before resolving services or opening persisted state.</summary>
public static class ConfigurationCutover
{
    private static readonly (string Prefix, string Replacement)[] Retired =
    [
        ("GoogleEmailOAuth", "GoogleOAuth (reconnect accounts whose client binding cannot be proven)"),
        ("GoogleDriveOAuth", "GoogleOAuth (reconnect accounts whose client binding cannot be proven)"),
        ("GOOGLE_EMAIL_OAUTH_", "GoogleOAuth"),
        ("GOOGLE_DRIVE_OAUTH_", "GoogleOAuth"),
        ("Providers:SpeechRecognizers", "Providers:Speech:Recognition"),
        ("Providers:SpeechSynthesizers", "Providers:Speech:Synthesis"),
        ("FilePreview:Limits:CsvBytes", "FilePreview:Limits:DelimitedBytes"),
        ("GOOGLE_OAUTH_", "GoogleOAuth:* (translate orchestration inputs with scripts/dev-env.py or scripts/compose.py)"),
        ("AGENTCORE_DRIVE_ENABLED", "GoogleDrive:Enabled (translate at the launcher/Compose boundary)")
    ];

    public static void Validate(IConfiguration configuration)
    {
        foreach (var entry in configuration.AsEnumerable())
        {
            if (entry.Value is null) continue;
            foreach (var (prefix, replacement) in Retired)
            {
                if (!entry.Key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    && !entry.Key.StartsWith(prefix.EndsWith('_') ? prefix : prefix + ":", StringComparison.OrdinalIgnoreCase)) continue;
                throw new InvalidOperationException($"Retired or untranslated configuration key {entry.Key}. Use {replacement}. Values are redacted.");
            }
        }
    }
}
