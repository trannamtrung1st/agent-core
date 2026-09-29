namespace AgentCore.Application.Ports;

public interface IDiagnosticIdSource
{
    Guid NewId();
}

internal sealed class FallbackDiagnosticIdSource : IDiagnosticIdSource
{
    public static readonly FallbackDiagnosticIdSource Instance = new();

    private FallbackDiagnosticIdSource()
    {
    }

    public Guid NewId() => Guid.NewGuid();
}
