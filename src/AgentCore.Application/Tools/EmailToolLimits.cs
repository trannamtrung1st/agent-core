namespace AgentCore.Application.Tools;

public static class EmailToolLimits
{
    public const int DefaultSearchLimit = 10;
    public const int MaxSearchLimit = 25;
    public const int MaxSearchQueryLength = 400;

    public const int MaxSubjectLength = 500;
    public const int MaxBodyLength = 32_000;
    public const int MaxRecipientsPerField = 20;
    public const int MaxRecipientLength = 320;

    public const int MaxPreviewRecipients = 5;
    public const int MaxPreviewSubjectLength = 120;
}
