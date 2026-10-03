namespace AgentCore.Application.Ports;

public sealed record WorkCapture(
    Guid CaptureId,
    Guid WorkItemId,
    Guid AgentInstanceId,
    string ContentType,
    long ByteSize,
    string Sha256Hex,
    DateTimeOffset CreatedAt,
    DateTimeOffset RetainUntil);

public sealed record WorkCaptureSaveResult(WorkCapture? Capture, string? ErrorCode);

public interface IWorkCaptureStore
{
    ValueTask<WorkCaptureSaveResult> SaveAsync(
        Guid workItemId,
        Guid agentInstanceId,
        string contentType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    ValueTask ExtendRetentionAsync(
        Guid workItemId,
        DateTimeOffset terminalAt,
        CancellationToken cancellationToken = default);
}

public static class WorkCaptureRetention
{
    public static DateTimeOffset Until(DateTimeOffset created, DateTimeOffset? terminalAt)
    {
        var floor = created.AddDays(7);
        if (terminalAt is not DateTimeOffset terminal)
        {
            return floor;
        }

        var afterTerminal = terminal.AddHours(24);
        return afterTerminal > floor ? afterTerminal : floor;
    }
}
