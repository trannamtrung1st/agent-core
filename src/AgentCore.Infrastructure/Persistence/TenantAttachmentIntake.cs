using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal static class TenantAttachmentIntake
{
    internal static async ValueTask<TenantAttachmentUpload> Read(TenantSessionRoot owner, Guid id, Guid request, Guid actor,
        string name, string type, Stream content, TimeProvider time, CancellationToken ct)
    {
        IntakeResult intake;
        try { intake = await AttachmentStreamIntake.ReadAsync(content, 0, ct).ConfigureAwait(false); }
        catch (AgentCoreException error) when (error.Code == "ValidationError" && error.Message == "Attachment exceeds 25 MiB.")
        { throw TenantAttachmentRules.Quota(); }
        await using (intake.Bytes.ConfigureAwait(false))
        {
            var inspect = AttachmentClassification.Inspect(intake.Prefix, intake.Suffix, intake.Length, type, name, true);
            if (!inspect.Accepted) throw AgentCoreErrors.Validation(inspect.Rejection ?? "Attachment was rejected.");
            var bytes = intake.Bytes.ToArray();
            return new(TenantAttachmentRules.Pending(owner, id, request, actor, name, inspect.ContentType, inspect.Readable, bytes, time.GetUtcNow()), bytes);
        }
    }
}
