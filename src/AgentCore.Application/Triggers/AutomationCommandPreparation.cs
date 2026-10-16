using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Triggers;

public sealed record AutomationCommandPreparation(AutomationAuthorizationRequest? Request, string? ErrorJson = null,
    TriggerCommandAuthorizationVerdict? Verdict = null, Guid? SourceEventId = null)
{
    public bool RequiresApproval => Verdict?.Decision is TriggerCommandAuthorizationDecision.ClassifierUnavailable or TriggerCommandAuthorizationDecision.ClassifierInvalidOutput;
}

public static partial class TriggerScheduleCommands
{
    internal const string ApprovalSummary = "Review exact Automation change";

    internal static Dictionary<string, string> ApprovalDetails(AutomationAuthorizationRequest request) => new()
    {
        ["Reason"] = "Semantic verification could not complete, or this change requires approval.",
        ["Exact action"] = JsonSerializer.Serialize(request.ProposedAction, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
    };

    public static async ValueTask<AutomationCommandPreparation> PrepareAsync(AgentDefinition definition, IAutomationService? registrations,
        string tool, JsonElement args, TriggerCommandContext? context, CancellationToken ct)
    {
        try
        {
            var action = TriggerAuthorization.ActionForTool(tool);
            if (action == TriggerCommandAction.None || context?.Owner is not TriggerOwner owner || context.SessionId == Guid.Empty)
                return Failed("forbidden", "A trusted Automation owner and Session are required.");
            if (action != TriggerCommandAction.List && (context.Classification != TriggerAuthorizationClassification.CurrentUserTurn
                || string.IsNullOrWhiteSpace(context.CurrentUserText)))
                return Failed("forbidden", "Only a current user turn or its exact UI approval can authorize an Automation mutation.");
            if (registrations is null) return Failed("unavailable", "Automation services are unavailable.");
            if (action == TriggerCommandAction.List) return new(null);
            Automation? current = null;
            if (action != TriggerCommandAction.Create)
            {
                current = await registrations.GetAsync(owner, RequireId(args), ct).ConfigureAwait(false)
                    ?? throw AgentCoreErrors.NotFound("Owned Automation was not found.");
                if (RequireRevision(args) != current.Revision) throw AgentCoreErrors.Conflict("Automation revision changed. Inspect it and propose a fresh action.");
            }
            if (args.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("enabled must be a boolean.");
            JsonElement? resolvedSchedule = null;
            JsonElement? resolvedTriggers = null;
            string? resolvedInstructions = null;
            if (action == TriggerCommandAction.Create || action == TriggerCommandAction.Update)
            {
                if (args.TryGetProperty("triggers", out _))
                    resolvedTriggers = JsonSerializer.SerializeToElement(ReadTriggerCollection(args).Select(t =>
                    {
                        if (t.Configuration is ScheduleTrigger timing) TriggerScheduleCalculator.RequireZone(TimeZoneOf(timing.Schedule));
                        return new { t.Enabled, t.Revision, Configuration = JsonSerializer.SerializeToElement(t.Configuration, t.Configuration.GetType()) };
                    }));
                else if (args.TryGetProperty("presetId", out var presetId))
                {
                    var preset = AutomationPresetCatalog.Templates.SingleOrDefault(p => p.PresetId == presetId.GetString())
                        ?? throw new ArgumentException("Preset is unavailable.");
                    resolvedInstructions = preset.Instructions;
                    resolvedTriggers = JsonSerializer.SerializeToElement(preset);
                    if (preset.TriggerKind == "schedule")
                    {
                        var presetSchedule = new WeeklySchedule(1, [DayOfWeek.Monday], new TimeOnly(9, 0), "UTC");
                        resolvedSchedule = JsonSerializer.SerializeToElement(presetSchedule);
                    }
                }
                else if (!args.TryGetProperty("eventId", out _) && !args.TryGetProperty("coreEventKey", out _)
                    && (action == TriggerCommandAction.Create || HasScheduleFields(args)))
                {
                    var policy = RequirePolicy(definition, "prepare", requireUserScheduling: true);
                    TriggerSchedule schedule;
                    if (current?.Schedule is FixedIntervalSchedule interval && !IsExplicitCalendarKindChange(args))
                        (schedule, _) = ApplyFixedIntervalUpdate(interval, args, context, policy);
                    else if (current?.Schedule is OneShotSchedule || HasOneShotFields(args))
                        (schedule, _) = ResolveOneShot(args, context, policy);
                    else schedule = ResolveRecurring(args, context, policy);
                    TriggerScheduleCalculator.RequireZone(TimeZoneOf(schedule));
                    resolvedSchedule = JsonSerializer.SerializeToElement(schedule, schedule.GetType());
                }
                if (args.TryGetProperty("instructions", out _) || action == TriggerCommandAction.Create && resolvedInstructions is null)
                    resolvedInstructions = RequireInstructions(args);
            }
            var target = action is TriggerCommandAction.Create or TriggerCommandAction.Update ? ChatTarget(args, context, current) : current?.ExecutionTarget;
            var delivery = action is TriggerCommandAction.Create or TriggerCommandAction.Update ? ChatDelivery(args, context, current) : current?.CompletionDelivery;
            bool? proposedEnabled = action switch
            {
                TriggerCommandAction.Enable => true,
                TriggerCommandAction.Disable => false,
                TriggerCommandAction.Create or TriggerCommandAction.Update => args.TryGetProperty("enabled", out var active)
                    ? active.GetBoolean() : current is not null ? current.Status == AutomationStatus.Active
                        : !args.TryGetProperty("presetId", out _) && !args.TryGetProperty("triggers", out _),
                _ => null
            };
            var exact = JsonSerializer.SerializeToElement(new { tool, owner.AgentInstanceId, owner.ProfileId, context.SessionId, context.SourceEventId,
                arguments = args, current = current is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(Projection(current)),
                resolvedSchedule, resolvedTriggers, resolvedInstructions, executionTarget = target, completionDelivery = delivery,
                proposedEnabled, presetRequiresTools = args.TryGetProperty("presetId", out _) ? true : (bool?)null,
                configuredTimeZone = context.ProfileTimeZoneId });
            if (exact.GetRawText().Length > 64000) return Failed("validation", "Automation action exceeds the verification bound.");
            var request = new AutomationAuthorizationRequest(context.CurrentUserText!, action, tool, exact,
                ToolActionHash.Compute(tool, exact), context.ScheduleContext, context.ScheduleDraft, context.ConversationEvidenceJson);
            if (System.Text.Encoding.UTF8.GetByteCount(args.GetRawText()) > AgentRunLimits.MaxAutomationPreparedActionBytes
                || JsonSerializer.Serialize(new { summary = ApprovalSummary, details = ApprovalDetails(request) }).Length > AgentRunLimits.MaxAutomationPreviewCharacters)
                return Failed("validation", "Automation action exceeds the complete approval preview bound. Propose a smaller action.");
            return new(request, SourceEventId: context.SourceEventId);
        }
        catch (TriggerScheduleCommandException ex)
        { return new(null, Error(ex.ErrorCode, ex.Message, ex.Draft is { } draft ? draft with { ArgumentsJson = args.GetRawText() } : null)); }
        catch (TriggerTimeZoneUnavailableException)
        { return Failed("schedule_validation_failed", "Timezone is unavailable. Ask the user for a different timezone."); }
        catch (AgentCoreException ex) { return Failed(ex.StatusCode == 409 ? "conflict" : ex.StatusCode == 404 ? "not_found" : ex.StatusCode == 400 ? "schedule_validation_failed" : "forbidden", ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Failed("schedule_validation_failed", ex is KeyNotFoundException ? "Required structured schedule fields are missing." : ex.Message); }
    }

    private static AutomationCommandPreparation Failed(string code, string message) => new(null, Error(code, message));

    internal static ToolExecutionResult AuthorizationFailure(TriggerCommandAuthorizationVerdict verdict) => new(JsonSerializer.Serialize(new
    {
        error = verdict.Decision switch
        {
            TriggerCommandAuthorizationDecision.Deny => "authorization_denied",
            TriggerCommandAuthorizationDecision.Ambiguous => "authorization_ambiguous",
            TriggerCommandAuthorizationDecision.ClassifierUnavailable => "authorization_classifier_unavailable",
            _ => "authorization_classifier_invalid_output"
        },
        classifierFailureReason = verdict.FailureReason?.ToString(),
        message = verdict.Decision is TriggerCommandAuthorizationDecision.Deny or TriggerCommandAuthorizationDecision.Ambiguous
            ? "The exact proposed action is not authorized. Clarify the requested action or missing details; nothing was saved."
            : "Semantic verification could not complete. Nothing was saved. Review the exact proposed action in the approval UI; do not rephrase the request, change arguments or repeatedly call the verifier."
    }));
}
