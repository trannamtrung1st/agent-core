using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Security;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionLiveDelivery
{
    private sealed record CommandRecord(string Fingerprint, long Sequence, CommandAck Ack, Guid? Run);
    public bool IsTenantConnection(string connection) => _connections.ContainsKey(connection);

    public Task<CommandAck> ReceiveAsync(string connection, ClientCommand<ResponseReceiptPayload> command, ITenantDeliveryReceiptControl control, CancellationToken ct)
        => ExecuteAsync(connection, command, "response.received", _ => null, (_, _, _) => Task.CompletedTask, async (subscription, _, token) =>
        {
            if (command.Payload is not { } body || body.BlockIds is { Length: > 0 }
                || !Guid.TryParse(command.ResponseId, out var response) || !subscription.Delivery.TryGetValue(response, out var delivered)
                || body.TextEndExclusive < delivered.ReceivedEnd || body.TextEndExclusive > delivered.PublishedEnd || body.TextEndExclusive < 0)
                return Reject(command.EventId, "InvalidReceipt", "A text receipt must acknowledge a delivered prefix on this attachment.", "Validation");
            var recipient = await access.AuthorizeRecipientAsync(subscription.Tenant, subscription.Session, subscription.Human, token).ConfigureAwait(false);
            await control.ReceiveAsync(new(recipient.Owner, subscription.Human, recipient.SecurityRevision, delivered.EntryId,
                delivered.EntrySequence, response, delivered.PublishedEnd, body.TextEndExclusive), token).ConfigureAwait(false);
            subscription.Delivery.AddOrUpdate(response, delivered with { ReceivedEnd = body.TextEndExclusive },
                (_, old) => old with { ReceivedEnd = Math.Max(old.ReceivedEnd, body.TextEndExclusive) });
            return new() { EventId = command.EventId, Accepted = true };
        }, ct);

    public Task<CommandAck> SendTextAsync(string connection, ClientCommand<UserTextPayload> command, TenantRunService service, CancellationToken ct)
        => ExecuteAsync(connection, command, "user.text", _ => null, (_, _, _) => Task.CompletedTask, async (subscription, _, token) =>
        {
            var body = command.Payload;
            if (body is null || body.ExpectedRevision is not >= 1 || body.ExpectedSessionRevision is not >= 1
                || body.Parts is { Length: > 0 } || body.AttachmentIds is { Length: > 0 } || body.Behavior is not null)
                return Reject(command.EventId, "ValidationError", "Tenant text requires revision witnesses and plain text.", "Validation");
            var result = await service.SubmitTextAsync(subscription.Tenant, subscription.Session, subscription.Human, Guid.Parse(command.EventId),
                body.Text, body.ExpectedRevision.Value, body.ExpectedSessionRevision.Value, token).ConfigureAwait(false);
            return new() { EventId = command.EventId, Accepted = true, Result = new() {
                ["created"] = result.Created, ["runId"] = result.RunId.ToString("D"), ["entryId"] = result.EntryId.ToString("D"),
                ["responseId"] = result.ResponseId.ToString("D"), ["activationId"] = result.ActivationId.ToString("D"),
                ["rootActivationId"] = result.RootActivationId.ToString("D"), ["sessionRevision"] = result.SessionRevision, ["securityRevision"] = result.SecurityRevision } };
        }, ct);

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
        Func<Subscription, Guid?, CancellationToken, Task> authorize, Func<Subscription, Guid?, CancellationToken, Task<CommandAck>> action, CancellationToken ct)
    {
        var envelope = SessionHost.Validate(command, attach: false, expectedType: type);
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
                return prior.Ack;
            }
            if (command.Sequence != subscription.LastCommand + 1)
                return Reject(command.EventId, "StaleCommand", "Commands must use the next attachment sequence.", "Protocol");
            var target = resolve(subscription); await authorize(subscription, target, ct).ConfigureAwait(false);
            CommandAck ack;
            // Never hold the publication gate while a command waits on the Runtime mailbox: cancellation
            // itself publishes its interruption through that gate before acknowledging its durable commit.
            try { ack = await action(subscription, target, ct).ConfigureAwait(false); }
            catch (AccessDeniedException ex) { ack = Reject(command.EventId, ex.ReasonCode, "This command is not authorized.", "Authorization"); }
            catch (SecurityConflictException ex) { ack = Reject(command.EventId, ex.ReasonCode, "State changed. Reload before trying again."); }
            catch (AgentCoreException ex) when (ex.Code is "ValidationError" or "Conflict" or "NotFound")
            { ack = Reject(command.EventId, ex.ValidationCode ?? ex.Code, "The command could not be admitted.", ex.Code == "ValidationError" ? "Validation" : "Session"); }
            subscription.LastCommand = command.Sequence;
            subscription.Dedupe.Add(eventId, new(fingerprint, command.Sequence, ack, target)); subscription.DedupeOrder.Enqueue(eventId);
            while (subscription.DedupeOrder.Count > 128) subscription.Dedupe.Remove(subscription.DedupeOrder.Dequeue());
            return ack;
        }
        catch (AccessDeniedException ex) { return Reject(command.EventId, ex.ReasonCode, "This command is not authorized.", "Authorization"); }
        catch (SecurityConflictException ex) { return Reject(command.EventId, ex.ReasonCode, "State changed. Reload before trying again."); }
        finally { connection.Commands.Release(); }
    }
    private static CommandAck Reject(string id, string reason, string message, string category = "Session") => new() { EventId = id, Accepted = false,
        Error = new() { Category = category, Code = reason, Message = message, Fatal = false } };
}
