using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionLiveDelivery
{
    private sealed record CommandRecord(string Fingerprint, long Sequence, CommandAck Ack, Guid? Run);

    public Task<CommandAck> ReceiveAsync(string connection, ClientCommand<ResponseReceiptPayload> command, ITenantDeliveryReceiptControl control, CancellationToken ct)
        => ExecuteAsync(connection, command, "response.received", _ => null, (_, _, _) => Task.CompletedTask, async (subscription, _, token) =>
        {
            if (command.Payload is not { } body || body.BlockIds is { Length: > 32 }
                || !Guid.TryParse(command.ResponseId, out var response) || !subscription.Delivery.TryGetValue(response, out var delivered)
                || body.TextEndExclusive < delivered.ReceivedEnd || body.TextEndExclusive > delivered.PublishedEnd || body.TextEndExclusive < 0
                || (body.BlockIds ?? []).Distinct(StringComparer.Ordinal).Count() != (body.BlockIds?.Length ?? 0)
                || (body.BlockIds ?? []).Any(id => !delivered.Blocks.ContainsKey(id)))
                return Reject(command.EventId, "InvalidReceipt", "A receipt must acknowledge text or blocks delivered on this attachment.", "Validation");
            var recipient = await access.AuthorizeRecipientAsync(subscription.Tenant, subscription.Session, subscription.Human, token).ConfigureAwait(false);
            var blocks = (body.BlockIds ?? []).Select(id => delivered.Blocks[id]).ToArray();
            foreach (var block in blocks.Where(b => b.ArtifactId is not null))
                if (!await access.CanReadArtifactReferenceAsync(subscription.Tenant, subscription.Session, subscription.Human, block.ArtifactId!.Value.ToString("D"), token).ConfigureAwait(false))
                    return Reject(command.EventId, "InvalidReceipt", "Current file access is required for this receipt.", "Authorization");
            var after = await access.AuthorizeRecipientAsync(subscription.Tenant, subscription.Session, subscription.Human, token).ConfigureAwait(false);
            if (after.SecurityRevision != recipient.SecurityRevision || after.Owner != recipient.Owner) throw new SecurityConflictException("stale_revision");
            await control.ReceiveAsync(new(recipient.Owner, subscription.Human, recipient.SecurityRevision, delivered.EntryId,
                delivered.EntrySequence, response, delivered.PublishedEnd, body.TextEndExclusive, blocks), token).ConfigureAwait(false);
            subscription.Delivery.AddOrUpdate(response, delivered with { ReceivedEnd = body.TextEndExclusive },
                (_, old) => old with { ReceivedEnd = Math.Max(old.ReceivedEnd, body.TextEndExclusive) });
            return new() { EventId = command.EventId, Accepted = true };
        }, ct);

    public Task<CommandAck> SendTextAsync(string connection, ClientCommand<UserTextPayload> command, TenantRunService service, CancellationToken ct)
        => ExecuteAsync(connection, command, "user.text", _ => null, (_, _, _) => Task.CompletedTask, async (subscription, _, token) =>
        {
            var body = command.Payload;
            if (body is null || body.ExpectedRevision is not >= 1 || body.ExpectedSessionRevision is not >= 1
                || body.Parts is { Length: > 0 } || MessageAttachmentIds(body) is not { } attachmentIds
                || body.InterruptedRunId is not null && !Guid.TryParse(body.InterruptedRunId, out var parsedInterruption))
                return Reject(command.EventId, "ValidationError", "Tenant messages require revision witnesses, exact input behavior, plain text and at most ten Attachment IDs.", "Validation");
            var interruption = TenantInputInterruptionRules.Request(body.Behavior, body.InterruptedRunId is null ? null : Guid.Parse(body.InterruptedRunId), body.ExpectedRunRevision);
            var result = await service.SubmitMessageAsync(subscription.Tenant, subscription.Session, subscription.Human, Guid.Parse(command.EventId),
                body.Text, attachmentIds, body.ExpectedRevision.Value, body.ExpectedSessionRevision.Value, token, interruption).ConfigureAwait(false);
            return new() { EventId = command.EventId, Accepted = true, Result = new() {
                ["created"] = result.Created, ["runId"] = result.RunId.ToString("D"), ["entryId"] = result.EntryId.ToString("D"),
                ["responseId"] = result.ResponseId.ToString("D"), ["activationId"] = result.ActivationId.ToString("D"),
                ["rootActivationId"] = result.RootActivationId.ToString("D"), ["sessionRevision"] = result.SessionRevision, ["securityRevision"] = result.SecurityRevision } };
        }, ct, async (subscription, token) => {
            if (command.Payload is { Behavior: "interrupt", ExpectedRunRevision: >= 1 } interrupted && Guid.TryParse(interrupted.InterruptedRunId, out var interruptedRun))
                await service.RequireMessageInterruptionAsync(subscription.Tenant, subscription.Session, subscription.Human,
                    new(interruptedRun, interrupted.ExpectedRunRevision.Value), token).ConfigureAwait(false);
            if (MessageAttachmentIds(command.Payload) is { Length: > 0 } ids)
                await service.RequireMessageAttachmentsAsync(subscription.Tenant, subscription.Session, subscription.Human, ids, token).ConfigureAwait(false);
        });

    private static Guid[]? MessageAttachmentIds(UserTextPayload? body)
    {
        if (body is null || body.AttachmentIds is { Length: > AttachmentLimits.MaxPerMessage }) return null;
        var values = new List<Guid>();
        foreach (var value in body.AttachmentIds ?? [])
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty || values.Contains(id)) return null;
            values.Add(id);
        }
        return values.ToArray();
    }

    public Task<CommandAck> CancelResponseAsync(string connection, ClientCommand<CancelResponsePayload> command, TenantRunService service,
        IamAuthorizer authorizer, CancellationToken ct)
        => ExecuteAsync(connection, command, "agent.response.cancel", subscription =>
        {
            if (!Guid.TryParse(command.ResponseId, out var response) || !subscription.KnownRuns.TryGetValue(response, out var run))
                throw new AccessDeniedException("UnknownResponse");
            return run;
        }, async (subscription, run, token) =>
        {
            await service.ReadAsync(subscription.Tenant, run!.Value, subscription.Human, token).ConfigureAwait(false);
            await authorizer.RequireAsync(subscription.Human.PrincipalId, "agent-runs.cancel",
                AccessScope.ForResource(subscription.Tenant, "agent-run", run.Value.ToString("D")), token).ConfigureAwait(false);
        }, async (subscription, run, token) =>
        {
            if (command.Payload is not { ExpectedRevision: >= 1, ExpectedRunRevision: >= 1 } body)
                return Reject(command.EventId, "ValidationError", "Cancellation requires current revision witnesses.", "Validation");
            var stopped = await service.CancelAsync(subscription.Tenant, run!.Value, subscription.Human, body.ExpectedRevision.Value,
                body.ExpectedRunRevision.Value, token).ConfigureAwait(false);
            return new() { EventId = command.EventId, Accepted = true, Result = new() { ["runId"] = stopped.Context.RunId.ToString("D"),
                ["status"] = stopped.Status.ToString().ToLowerInvariant(), ["revision"] = stopped.Revision } };
        }, ct);

    private async Task<CommandAck> ExecuteAsync(string connectionId, IRealtimeCommand command, string type, Func<Subscription, Guid?> resolve,
        Func<Subscription, Guid?, CancellationToken, Task> authorize, Func<Subscription, Guid?, CancellationToken, Task<CommandAck>> action, CancellationToken ct,
        Func<Subscription, CancellationToken, Task>? reauthorizeCached = null)
    {
        var envelope = SessionCommandValidation.Validate(command, attach: false, expectedType: type);
        if (!envelope.Accepted) return envelope;
        if (!_connections.TryGetValue(connectionId, out var connection)) return Reject(command.EventId, "StaleAttachment", "Attach again before sending a command.");
        await connection.Commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Subscription subscription;
            await connection.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                subscription = connection.Current ?? throw new AccessDeniedException("StaleAttachment");
                if (connection.Closed || !Guid.TryParse(command.AttachmentId, out var attachment) || attachment != subscription.Attachment
                    || Guid.Parse(command.SessionId) != subscription.Session) return Reject(command.EventId, "StaleAttachment", "The command belongs to a different attachment.");
            }
            finally { connection.Gate.Release(); }
            await access.AuthorizeRecipientAsync(subscription.Tenant, subscription.Session, subscription.Human, ct).ConfigureAwait(false);
            if (connection.Closed || connection.Current != subscription) return Reject(command.EventId, "StaleAttachment", "The attachment changed.");
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                command.ProtocolVersion, session = subscription.Session, command.Sequence, command.Type, command.ResponseId,
                attachment = subscription.Attachment, payload = command.Payload }))));
            var eventId = Guid.Parse(command.EventId);
            if (subscription.Dedupe.TryGetValue(eventId, out var prior))
            {
                if (prior.Sequence != command.Sequence || prior.Fingerprint != fingerprint) return Reject(command.EventId, "ProtocolError", "The event ID was already used for a different command.", "Protocol");
                await authorize(subscription, prior.Run, ct).ConfigureAwait(false);
                if (reauthorizeCached is not null) await reauthorizeCached(subscription, ct).ConfigureAwait(false);
                return prior.Ack;
            }
            if (command.Sequence != subscription.LastCommand + 1)
                return Reject(command.EventId, "StaleCommand", "Commands must use the next attachment sequence.", "Protocol");
            var target = resolve(subscription); await authorize(subscription, target, ct).ConfigureAwait(false);
            CommandAck ack;
            // Never hold the publication gate while a command waits on the Runtime mailbox: cancellation
            // itself publishes its interruption through that gate before acknowledging its durable commit.
            try { ack = await action(subscription, target, ct).ConfigureAwait(false); }
            catch (AccessDeniedException ex) { await RetireVoiceIfAccessLostAsync(subscription, ct).ConfigureAwait(false); ack = Reject(command.EventId, ex.ReasonCode, AuthorizationMessage(ex.ReasonCode), "Authorization"); }
            catch (SecurityConflictException ex) { await RetireVoiceIfAccessLostAsync(subscription, ct).ConfigureAwait(false); ack = Reject(command.EventId, ex.ReasonCode, ConflictMessage(ex.ReasonCode)); }
            catch (AgentCoreException ex) when (ex.Code == "VoiceUnavailable")
            { ack = Reject(command.EventId, ex.Code, "Voice is unavailable for this conversation. Continue in text.", "Speech"); }
            catch (AgentCoreException ex) when (ex.Code is "ValidationError" or "Conflict" or "NotFound")
            { ack = Reject(command.EventId, ex.ValidationCode ?? ex.Code, "The command could not be admitted.", ex.Code == "ValidationError" ? "Validation" : "Session"); }
            subscription.LastCommand = command.Sequence;
            subscription.Dedupe.Add(eventId, new(fingerprint, command.Sequence, ack, target)); subscription.DedupeOrder.Enqueue(eventId);
            while (subscription.DedupeOrder.Count > 128) subscription.Dedupe.Remove(subscription.DedupeOrder.Dequeue());
            return ack;
        }
        catch (AccessDeniedException ex) { if (connection.Current is { } failed) RetireVoice(failed); return Reject(command.EventId, ex.ReasonCode, AuthorizationMessage(ex.ReasonCode), "Authorization"); }
        catch (SecurityConflictException ex) { await RetireVoiceIfAccessLostAsync(connection.Current, ct).ConfigureAwait(false); return Reject(command.EventId, ex.ReasonCode, ConflictMessage(ex.ReasonCode)); }
        finally { connection.Commands.Release(); }
    }
    private static string AuthorizationMessage(string reason) => reason switch {
        "attachment_unavailable" => "The selected files are unavailable. Remove them or check current file access before trying again.",
        "interruption_unavailable" => "You can still queue messages. Permission to stop this response is required to steer.",
        _ => "This command is not authorized." };
    private static string ConflictMessage(string reason) => reason == "stale_interruption"
        ? "That response changed. Check the current response before steering again." : "State changed. Reload before trying again.";
    private static CommandAck Reject(string id, string reason, string message, string category = "Session") => new() { EventId = id, Accepted = false,
        Error = new() { Category = category, Code = reason, Message = message, Fatal = false } };
}
