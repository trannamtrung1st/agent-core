using Microsoft.Extensions.Configuration;

namespace AgentCore.Api.Tests;

public sealed class LiveIdentityMaintenanceFactAttribute : FactAttribute
{
    internal static string? Key() => new ConfigurationBuilder().AddUserSecrets<Program>(optional: true)
        .AddEnvironmentVariables().Build()["OPENROUTER_API_KEY"];

    public LiveIdentityMaintenanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_LIVE_IDENTITY_MAINTENANCE") != "1") Skip = "Explicit AGENTCORE_LIVE_IDENTITY_MAINTENANCE=1 required; default gates remain key-free.";
        else if (string.IsNullOrWhiteSpace(Key())) Skip = "OpenRouter key is missing.";
    }
}
