namespace AgentCore.Application.Triggers;

public sealed class TriggerScheduleCommandException : Exception
{
    public TriggerScheduleCommandException(
        string errorCode,
        string message,
        ScheduleDraftContext? draft = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Draft = draft;
    }

    public string ErrorCode { get; }

    public ScheduleDraftContext? Draft { get; }
}
