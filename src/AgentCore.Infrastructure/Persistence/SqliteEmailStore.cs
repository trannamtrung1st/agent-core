using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteEmailStore(IDbContextFactory<AgentCoreDbContext> contexts) : IEmailStore
{
    public async ValueTask CreateSelectionAsync(EmailAccountSelection request, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.Set<EmailAccountSelectionRecord>().Add(new() { RequestId = request.RequestId.ToString("D"), SelectionJson = JsonSerializer.Serialize(request), Status = request.Status });
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 }) { }
    }
    public async ValueTask<EmailAccountSelection?> GetSelectionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<EmailAccountSelectionRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.RequestId == id.ToString("D"), ct);
        return row is null ? null : JsonSerializer.Deserialize<EmailAccountSelection>(row.SelectionJson);
    }
    public async ValueTask<bool> UpdateSelectionAsync(EmailAccountSelection request, string expectedStatus, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(request);
        return await db.Set<EmailAccountSelectionRecord>().Where(r => r.RequestId == request.RequestId.ToString("D") && r.Status == expectedStatus)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, request.Status).SetProperty(r => r.SelectionJson, json), ct) == 1;
    }
    public async ValueTask CreateDraftAsync(StoredEmailDraft draft, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.Set<EmailDraftRecord>().Add(new() { DraftId = draft.Draft.DraftId, AgentInstanceId = draft.AgentInstanceId.ToString("D"),
            SessionId = draft.SessionId.ToString("D"), DraftJson = JsonSerializer.Serialize(draft.Draft), CreatedAtUtc = draft.CreatedAtUtc.ToUnixTimeMilliseconds() });
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Draft already exists."); }
    }
    public async ValueTask<StoredEmailDraft?> GetDraftAsync(string id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<EmailDraftRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.DraftId == id, ct);
        return row is null ? null : new(Guid.Parse(row.AgentInstanceId), Guid.Parse(row.SessionId),
            JsonSerializer.Deserialize<EmailDraftSnapshot>(row.DraftJson)!, DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc));
    }
    public async ValueTask<bool> TryClaimSendAsync(EmailSendOperation operation, CancellationToken ct = default)
    {
        var draft = await GetDraftAsync(operation.DraftId, ct);
        if (draft is null || draft.Draft.AccountId != operation.AccountId || draft.Draft.Revision != operation.DraftRevision)
            throw AgentCoreErrors.Conflict("Send identity does not match the durable draft.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.Set<EmailSendOperationRecord>().Add(new() { DraftId = operation.DraftId, AccountId = operation.AccountId.ToString("D"),
            DraftRevision = operation.DraftRevision, ApprovalId = operation.ApprovalId.ToString("D"), ActionHash = operation.ActionHash,
            ClaimedAtUtc = operation.ClaimedAtUtc.ToUnixTimeMilliseconds() });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 }) { return false; }
    }
    public async ValueTask<EmailSendOperation?> GetSendAsync(string id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<EmailSendOperationRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.DraftId == id, ct);
        return row is null ? null : new(row.DraftId, Guid.Parse(row.AccountId), row.DraftRevision, Guid.Parse(row.ApprovalId), row.ActionHash,
            row.ResultJson is null ? null : JsonSerializer.Deserialize<EmailSendResult>(row.ResultJson), DateTimeOffset.FromUnixTimeMilliseconds(row.ClaimedAtUtc));
    }
    public async ValueTask CompleteSendAsync(string id, EmailSendResult result, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(result);
        var changed = await db.Set<EmailSendOperationRecord>().Where(r => r.DraftId == id && r.ResultJson == null)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.ResultJson, json), ct);
        if (changed == 0 && (await GetSendAsync(id, ct))?.Result != result) throw AgentCoreErrors.Conflict("Send receipt is immutable or missing.");
    }
}
