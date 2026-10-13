namespace AgentCore.Infrastructure.Email;

internal static class EmailMessageReferences
{
    internal static string GmailPrefix(Guid id) => $"gmail-{id:N}:";
    internal static string Gmail(Guid id, string messageId) => GmailPrefix(id) + messageId;
    internal static string Imap(Guid id, uint validity, uint uid) => $"imap-{id:N}:{validity}:{uid}";
}
