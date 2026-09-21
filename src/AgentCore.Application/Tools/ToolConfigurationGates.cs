namespace AgentCore.Application.Tools;

public static class ToolConfigurationGates
{
    public static IToolConfigurationGate Unconfigured { get; } = new DelegatingToolConfigurationGate(toolName =>
        toolName != ToolCatalog.WebSearch
        && toolName is not (
            ToolCatalog.EmailSearch
            or ToolCatalog.EmailRead
            or ToolCatalog.EmailCreateDraft
            or ToolCatalog.EmailSend));

    public static IToolConfigurationGate AllowAll { get; } = new DelegatingToolConfigurationGate(_ => true);

    public static IToolConfigurationGate From(Func<string, bool> isConfigured) =>
        new DelegatingToolConfigurationGate(isConfigured);
}

public sealed class DelegatingToolConfigurationGate(Func<string, bool> isConfigured) : IToolConfigurationGate
{
    public bool IsConfigured(string toolName) => isConfigured(toolName);
}
