using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Composer;

public sealed record ComposerChoice(string Id, string Label, string Description, string Category,
    string? SkillKey = null, UserResourceReference? Reference = null, string? UnavailableReason = null);
public sealed record ComposerChoicePage(IReadOnlyList<ComposerChoice> Items, string? NextCursor);

/// <summary>Read-only, typed adapters over native owners. Picker metadata carries no read grant.</summary>
public sealed class ComposerReferenceService(IAgentInstanceStore instances, AgentRunConfigurationResolver configurations,
    IMemoryStore sessions, IAgentInstanceWorkspaceStore home, IArtifactStore artifacts, IAgentRunStore runs, TimeProvider time)
{
    public const int MaxReferenceCharacters = 4000;
    public const int MaxAggregateCharacters = 12000;
    public const int MaxBodyBytes = 64 * 1024;
    private static string Label(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Take(200).Aggregate(new StringBuilder(), (b, c) => b.Append(c)).ToString();
    private async ValueTask ActiveAsync(Guid instanceId, CancellationToken ct)
    {
        var instance = await instances.FindAsync(instanceId, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance is unavailable.");
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Choose an active Agent Instance.");
    }

    public static IReadOnlyList<string> ActiveSkills(AgentDefinition definition, IReadOnlyList<EffectiveSkill> catalog, IEnumerable<string> explicitKeys)
    {
        var requested = explicitKeys.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in requested)
        {
            if (!catalog.Any(s => s.Key == key)) throw AgentCoreErrors.Validation($"Selected Skill {key} is unavailable. Remove or select it again before retrying.");
        }
        var active = catalog.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).Concat(requested).Distinct(StringComparer.Ordinal).ToArray();
        try { SkillPolicy.ValidateActive(catalog, active); }
        catch (ArgumentException) { throw AgentCoreErrors.Validation("Selected Skills and Always Skills exceed the 8000-character procedure budget. Remove a Skill and retry."); }
        if (requested.Length > 0) BrowserContractCutover.EnsureCurrent(definition, catalog, active);
        return active;
    }

    public async ValueTask<IReadOnlyList<UserMessagePart>?> ValidateInputAsync(SessionSnapshot snapshot, IReadOnlyList<UserMessagePart>? parts, CancellationToken ct)
    {
        if (parts is null) return null;
        await ActiveAsync(snapshot.AgentInstanceId, ct);
        var resolved = await configurations.ResolveAsync(snapshot.AgentInstanceId, ct);
        ActiveSkills(resolved.Configuration.Definition, resolved.Skills, UserMessageContent.ExplicitSkills(parts));
        var owner = new AgentRunOwner(snapshot.AgentInstanceId, snapshot.ProfileId ?? throw AgentCoreErrors.Forbidden("Session owner is unavailable."));
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in parts.Where(p => p.Kind == "reference").Select(p => p.Reference!).Distinct())
        {
            var evidence = await ResolveAsync(owner, reference, false, ct);
            if (evidence.Status is "forbidden" or "unavailable" or "stale")
                throw AgentCoreErrors.Validation($"Referenced {reference.Kind} is {evidence.Status}. Remove it or select its current version.");
            labels[reference.Locator] = evidence.Label;
        }
        return UserMessageContent.Normalize(parts.Select(p => p.Kind switch
        { "invocation" => p with { Label = resolved.Skills.Single(s => s.Key == p.SkillKey).Name },
          "reference" => p with { Label = labels[p.Reference!.Locator] }, _ => p }).ToArray());
    }

    public async ValueTask<ComposerRunInput> PinAsync(AgentRunOwner owner, IReadOnlyList<ConversationEntry> users,
        AgentDefinition definition, IReadOnlyList<EffectiveSkill> catalog, CancellationToken ct)
    {
        var explicitKeys = users.SelectMany(u => UserMessageContent.ExplicitSkills(u.Parts)).Distinct(StringComparer.Ordinal).ToArray();
        var evidence = new List<ComposerReferenceEvidence>();
        string? error = null;
        try { await ActiveAsync(owner.AgentInstanceId, ct); ActiveSkills(definition, catalog, explicitKeys); }
        catch (AgentCoreException exception) { error = exception.Message; }
        var remaining = MaxAggregateCharacters;
        foreach (var reference in users.SelectMany(u => u.Parts ?? []).Where(p => p.Kind == "reference").Select(p => p.Reference!).DistinctBy(r => r.Locator))
        {
            var resolved = await ResolveAsync(owner, reference, true, ct);
            if (resolved.Status is "forbidden" or "unavailable" or "stale") error ??= $"Referenced {reference.Kind} is {resolved.Status}. Edit the selection and retry; the original input remains in history.";
            if (resolved.Text.Length > remaining) resolved = resolved with { Text = resolved.Text[..remaining], Truncated = true };
            remaining -= resolved.Text.Length;
            evidence.Add(resolved);
        }
        return new(explicitKeys, Array.AsReadOnly(evidence.ToArray()), time.GetUtcNow(), error);
    }

    private async ValueTask<SessionSnapshot> OwnedSessionAsync(AgentRunOwner owner, Guid id, CancellationToken ct)
    {
        var session = await sessions.LoadMetadataAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Reference is unavailable.");
        if (session.AgentInstanceId != owner.AgentInstanceId || session.ProfileId != owner.ProfileId || session.ArchivedAt is not null || session.DurablyDeletedAt is not null)
            throw AgentCoreErrors.Forbidden("Reference is outside this active Instance and owner scope.");
        return session;
    }

    public async ValueTask<ComposerReferenceEvidence> ResolveAsync(AgentRunOwner owner, UserResourceReference reference, bool includeContent, CancellationToken ct)
    {
        reference.Validate();
        try
        {
            string label, text = ""; long? revision = null; string? hash = null; var truncated = false; var status = "valid";
            switch (reference.Kind)
            {
                case "homeFile":
                    if (reference.AgentInstanceId != owner.AgentInstanceId) throw AgentCoreErrors.Forbidden("Reference is outside this Instance.");
                    var file = await home.InspectAsync(owner.AgentInstanceId, reference.ItemId!.Value, includeContent ? MaxBodyBytes : 0, ct);
                    if (file.Item.Directory) throw AgentCoreErrors.Validation("Select a file, not a directory.");
                    label = file.Item.LogicalPath; revision = file.Item.Revision; hash = file.Item.Sha256Hex;
                    if (includeContent) (text, status, truncated) = file.Item.ByteSize > MaxBodyBytes ? ("", "metadataOnly", true) : ReadText(file.Bytes, file.Item.ContentType);
                    break;
                case "session":
                case "backgroundSession":
                    var session = await OwnedSessionAsync(owner, reference.SessionId!.Value, ct);
                    if (reference.Kind == "backgroundSession" && !session.Surfaces.HasFlag(SessionSurface.BackgroundWork)
                        || reference.Kind == "session" && session.Surfaces.HasFlag(SessionSurface.BackgroundWork)) throw AgentCoreErrors.Validation("Reference category does not match its Session.");
                    label = session.Title; revision = session.Revision;
                    if (includeContent)
                    {
                        if (reference.Kind == "backgroundSession" && session.Origin.InitialBackgroundAgentRunId is { } originalId)
                        {
                            var original = await runs.GetAsync(owner, originalId, ct);
                            var originalInputs = await sessions.ReadHistoryAsync(session.SessionId, 0, 20, ct);
                            var task = string.Join("\n", originalInputs.Where(e => original?.Admission.Activation.SourceEntryIds.Contains(e.EntryId) == true).Select(e => e.Text));
                            text = $"Original task: {task}\nStatus: {original?.Status}\nOriginal result: {original?.Result?.Text ?? original?.Failure?.Summary ?? "No completed result."}";
                        }
                        else
                        {
                            var page = await sessions.ReadHistoryPageAsync(session.SessionId, null, null, 20, ct);
                            text = string.Join("\n", (page?.Items ?? []).Where(e => e.IsPromptTurn).Select(e => $"{e.Role}: {PublicHistory.FromEntry(e).Text}"));
                            truncated = page?.HasOlder == true;
                        }
                    }
                    break;
                case "artifact":
                    await OwnedSessionAsync(owner, reference.SessionId!.Value, ct);
                    var artifact = await artifacts.GetAsync(reference.SessionId.Value, reference.ArtifactId!.Value, ct) ?? throw AgentCoreErrors.NotFound("Reference is unavailable.");
                    label = artifact.DisplayName; hash = artifact.Sha256Hex;
                    if (includeContent)
                    {
                        if (artifact.ByteSize > MaxBodyBytes) { status = "metadataOnly"; truncated = true; }
                        else { await using var stream = await artifacts.OpenContentAsync(artifact.SessionId, artifact.ArtifactId, ct); using var memory = new MemoryStream(); await stream.CopyToAsync(memory, ct); (text, status, truncated) = ReadText(memory.ToArray(), artifact.ContentType); }
                    }
                    break;
                case "agentRun":
                    await OwnedSessionAsync(owner, reference.SessionId!.Value, ct);
                    var run = await runs.GetAsync(owner, reference.AgentRunId!.Value, ct) ?? throw AgentCoreErrors.NotFound("Reference is unavailable.");
                    if (run.SessionId != reference.SessionId) throw AgentCoreErrors.Forbidden("Run does not belong to this Session.");
                    label = $"{run.Admission.Activation.Kind} · {run.AgentRunId.ToString("D")[..8]}"; revision = run.Revision;
                    if (includeContent) text = $"Status: {run.Status}\nOutcome: {run.Result?.Text ?? run.Failure?.Summary ?? "No completed outcome."}";
                    break;
                case "skill":
                    if (reference.AgentInstanceId != owner.AgentInstanceId) throw AgentCoreErrors.Forbidden("Reference is outside this Instance.");
                    var configuration = await configurations.ResolveAsync(owner.AgentInstanceId, ct);
                    var skill = configuration.Skills.SingleOrDefault(s => s.Key == reference.SkillKey) ?? throw AgentCoreErrors.NotFound("Reference is unavailable.");
                    label = skill.Name; hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skill.Procedure))).ToLowerInvariant();
                    if (includeContent) text = $"Description: {skill.Description}\nProcedure (reference only, not activated): {skill.Procedure}";
                    break;
                default: throw AgentCoreErrors.Validation("Unknown reference kind.");
            }
            if (reference.SelectedRevision is { } selected && selected != revision) return new(reference, Label(label), "stale", "", revision, hash);
            if (text.Length > MaxReferenceCharacters) { text = text[..MaxReferenceCharacters]; truncated = true; }
            return new(reference, Label(label), status, text, revision, hash, truncated);
        }
        catch (AgentCoreException exception) { return new(reference, "Referenced " + reference.Kind, exception.Code == "Forbidden" ? "forbidden" : "unavailable", ""); }
    }

    private sealed record NestedCursor(string? SessionCursor, Guid? ItemCursor);

    private static (string Text, string Status, bool Truncated) ReadText(byte[] bytes, string contentType)
    {
        if (bytes.Length > MaxBodyBytes) return ("", "metadataOnly", true);
        if (!(contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || contentType is "application/json" or "application/xml")) return ("", "metadataOnly", false);
        try { return (new UTF8Encoding(false, true).GetString(bytes), "valid", false); }
        catch (DecoderFallbackException) { return ("", "metadataOnly", false); }
    }

    public async ValueTask<ComposerChoicePage> DiscoverAsync(AgentRunOwner owner, string category, string? search, string? cursor, CancellationToken ct)
    {
        await ActiveAsync(owner.AgentInstanceId, ct);
        if ((search?.Length ?? 0) > 200 || (cursor?.Length ?? 0) > 1024) throw AgentCoreErrors.Validation("Picker search or cursor is too long.");
        bool Matches(string name, string description) => string.IsNullOrWhiteSpace(search) || (name + " " + description).Contains(search, StringComparison.OrdinalIgnoreCase);
        var choices = new List<ComposerChoice>(); string? next = null;
        if (category is "invocation" or "skill")
        {
            var configuration = await configurations.ResolveAsync(owner.AgentInstanceId, ct);
            var catalog = configuration.Skills.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal).Where(s => Matches(s.Name, s.Description + " " + s.Key)).ToArray();
            var offset = cursor is null ? 0 : int.TryParse(cursor, out var value) && value >= 0 ? value : throw AgentCoreErrors.Validation("Invalid picker cursor.");
            foreach (var skill in catalog.Skip(offset).Take(40))
            {
                string? reason = null;
                if (category == "invocation")
                { try { ActiveSkills(configuration.Configuration.Definition, configuration.Skills, [skill.Key]); } catch (AgentCoreException exception) { reason = exception.Message; } }
                choices.Add(new(skill.Key, skill.Name, $"{skill.Key} · {skill.Description}", category,
                    category == "invocation" ? skill.Key : null,
                    category == "skill" ? new("skill", AgentInstanceId: owner.AgentInstanceId, SkillKey: skill.Key) : null, reason));
            }
            if (offset + 40 < catalog.Length) next = (offset + 40).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (category == "homeFile")
        {
            var page = await home.ListAsync(owner.AgentInstanceId, "/home", cursor, 40, ct); next = page.NextPath;
            foreach (var item in page.Items.Where(i => !i.Directory && Matches(i.LogicalPath, i.ContentType)))
                choices.Add(new(item.ItemId.ToString("D"), Label(item.LogicalPath), item.ContentType, category, Reference: new(category, AgentInstanceId: owner.AgentInstanceId, ItemId: item.ItemId, SelectedRevision: item.Revision)));
        }
        else if (category is "session" or "backgroundSession")
        {
            var page = category == "backgroundSession" ? await sessions.ListBackgroundSessionsAsync(owner, cursor, 40, false, ct) : await sessions.ListInstanceSessionsAsync(owner, cursor, 40, ct); next = page.NextCursor;
            foreach (var session in page.Items.Where(s => s.AgentInstanceId == owner.AgentInstanceId && s.ProfileId == owner.ProfileId && s.ArchivedAt is null && s.DurablyDeletedAt is null))
            {
                if (category == "session" && session.Surfaces.HasFlag(SessionSurface.BackgroundWork)) continue;
                if (Matches(session.Title, session.SessionId.ToString("D"))) choices.Add(new(session.SessionId.ToString("D"), Label(session.Title), $"{session.Status} · {session.SessionId.ToString("D")[..8]}", category, Reference: new(category, SessionId: session.SessionId)));
            }
        }
        else if (category is "artifact" or "agentRun")
        {
            NestedCursor position;
            try { position = cursor is null ? new(null, null) : JsonSerializer.Deserialize<NestedCursor>(Convert.FromBase64String(cursor)) ?? throw new FormatException(); }
            catch (Exception e) when (e is FormatException or JsonException) { throw AgentCoreErrors.Validation("Invalid picker cursor."); }
            for (var visited = 0; visited < 40; visited++)
            {
                var page = await sessions.ListInstanceSessionsAsync(owner, position.SessionCursor, 1, ct);
                var session = page.Items.FirstOrDefault();
                if (session is null) break;
                var retained = session.ArchivedAt is null && session.DurablyDeletedAt is null && session.AgentInstanceId == owner.AgentInstanceId && session.ProfileId == owner.ProfileId;
                Guid? nextItem = null;
                if (retained && category == "artifact")
                {
                    var items = await artifacts.ListPageAsync(session.SessionId, position.ItemCursor, 40, ct);
                    foreach (var item in items.Items)
                        if (Matches(item.DisplayName, session.Title)) choices.Add(new(item.ArtifactId.ToString("D"), Label(item.DisplayName), $"{Label(session.Title)} · {item.ArtifactId.ToString("D")[..8]}", category, Reference: new(category, SessionId: session.SessionId, ArtifactId: item.ArtifactId)));
                    if (items.HasMore) nextItem = items.NextCursor;
                }
                else if (retained)
                {
                    var items = await runs.ListPageAsync(owner, session.SessionId, position.ItemCursor, 40, ct);
                    foreach (var run in items.Items)
                        if (Matches(session.Title, run.Status + " " + run.AgentRunId)) choices.Add(new(run.AgentRunId.ToString("D"), $"{Label(session.Title)} · {run.AgentRunId.ToString("D")[..8]}", run.Status.ToString(), category, Reference: new(category, SessionId: session.SessionId, AgentRunId: run.AgentRunId)));
                    if (items.HasMore) nextItem = items.NextCursor;
                }
                if (nextItem is not null) position = position with { ItemCursor = nextItem };
                else if (page.NextCursor is { } nextSession) position = new(nextSession, null);
                else { next = null; break; }
                next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(position));
                if (choices.Count > 0 || nextItem is not null) break;
            }
        }
        else throw AgentCoreErrors.Validation("Unknown picker category.");
        return new(Array.AsReadOnly(choices.ToArray()), next);
    }
}
