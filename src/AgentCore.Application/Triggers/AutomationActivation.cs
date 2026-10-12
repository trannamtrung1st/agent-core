using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

internal static class AutomationActivation
{
    private static readonly Regex Command = new(
        @"^(?:please\s+|i want you to\s+)?(?<verb>enable|activate|resume|unpause|turn\s+on|disable|deactivate|pause|stop|turn\s+off)\s+(?<target>.+?)[.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Id = new(@"\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase);
    private static readonly Regex Cadence = new(@"\b(?<n>\d+)\s*(?<unit>seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h)\b", RegexOptions.IgnoreCase);

    internal static bool IsActivationDirective(string text) => Command.IsMatch(text);
    internal static bool HasReferent(ScheduleConversationContext? context) => context is not null
        && context.AutomationId != Guid.Empty && !string.IsNullOrWhiteSpace(context.Instructions)
        && context.Status is AutomationStatus.Active or AutomationStatus.Disabled or AutomationStatus.SuspendedPolicy;

    internal static bool Matches(string text, bool enabled, ScheduleConversationContext? context = null)
    {
        if (HeuristicTriggerCommandAuthorizer.IsClearlyNonAuthorizing(text, TriggerCommandAction.Enable)
            || Regex.IsMatch(text, @"\b(if|unless|maybe|perhaps|should|would|could)\b", RegexOptions.IgnoreCase)) return false;
        var match = Command.Match(text);
        if (!match.Success) return false;
        var activating = Regex.IsMatch(match.Groups["verb"].Value, @"^(enable|activate|resume|unpause|turn\s+on)$", RegexOptions.IgnoreCase);
        return activating == enabled && (Id.IsMatch(text) || HasReferent(context)
            || Regex.IsMatch(match.Groups["target"].Value, @"\b(automation|schedule|reminder|core event|preset)\b", RegexOptions.IgnoreCase));
    }

    internal static async Task RequireTargetAsync(IAutomationService service, TriggerOwner owner,
        TriggerCommandContext context, Guid selectedId, CancellationToken ct)
    {
        var text = HeuristicTriggerCommandAuthorizer.NormalizeTurn(context.CurrentUserText ?? "");
        if (Regex.IsMatch(text, @"\b(if|unless|maybe|perhaps|should|would|could)\b", RegexOptions.IgnoreCase))
            throw new TriggerScheduleCommandException("ambiguous_target", "Clarify the conditional activation request before changing enabled state.");
        var ids = Id.Matches(text).Select(m => Guid.Parse(m.Value)).Distinct().ToArray();
        if (ids.Length > 0)
        {
            if (ids.Length != 1) throw new TriggerScheduleCommandException("ambiguous_target", "Name one Automation to change; multiple IDs were supplied.");
            if (ids[0] != selectedId) throw new TriggerScheduleCommandException("target_mismatch", "The selected Automation does not match the user's ID.");
            return;
        }
        var match = Command.Match(text);
        if (!match.Success) throw new TriggerScheduleCommandException("ambiguous_target", "Ask which existing Automation the user wants to change.");
        var target = match.Groups["target"].Value.Trim();
        var generic = Regex.IsMatch(target, @"^(?:the\s+)?(?:this|that|it)(?:\s+(?:automation|schedule|reminder))?$", RegexOptions.IgnoreCase);
        if (generic && HasReferent(context.ScheduleContext))
        {
            var referent = context.ScheduleContext!;
            if (referent.AutomationId != selectedId) throw new TriggerScheduleCommandException("target_mismatch", "Use the trusted Automation referent, not another ID.");
            return;
        }
        if (generic && !Regex.IsMatch(target, @"\b(automation|schedule|reminder)\b", RegexOptions.IgnoreCase))
            throw new TriggerScheduleCommandException("ambiguous_target", "No trusted referent identifies this Automation. Ask which one.");
        int? seconds = null;
        var cadence = Cadence.Match(target);
        if (cadence.Success)
        {
            if (!int.TryParse(cadence.Groups["n"].Value, out var amount))
                throw new TriggerScheduleCommandException("validation", "Automation cadence is invalid.");
            var unit = cadence.Groups["unit"].Value.ToLowerInvariant();
            var value = (long)amount * (unit.StartsWith('h') ? 3600 : unit.StartsWith('m') ? 60 : 1);
            if (value > int.MaxValue) throw new TriggerScheduleCommandException("validation", "Automation cadence is invalid.");
            seconds = (int)value;
            target = Cadence.Replace(target, " ");
        }
        target = Regex.Replace(target.Trim(), @"^(?:the|my|this|that)\s+|\b(?:automation|schedule|reminder)$", "", RegexOptions.IgnoreCase).Trim(' ', '"', '\'');
        target = Regex.Replace(target, @"^(?:core event|preset)\s+|\s+preset$", "", RegexOptions.IgnoreCase).Trim();
        var rows = await service.ListAsync(owner, null, ct).ConfigureAwait(false);
        var candidates = rows.Where(a => (generic || target.Length == 0 && seconds is not null || target.Length > 0 && ContainsPhrase(a.Name, target))
            && (seconds is null || a.Triggers.Any(t => t.Configuration is ScheduleTrigger { Schedule: FixedIntervalSchedule f } && f.IntervalSeconds == seconds))).ToArray();
        if (candidates.Length == 0) throw new TriggerScheduleCommandException("not_found", "No owned Automation matches this request. Do not create a replacement.");
        if (candidates.Length > 1) throw new TriggerScheduleCommandException("ambiguous_target", "Several owned Automations match. Ask the user which one; do not pick the first.");
        if (candidates[0].AutomationId != selectedId) throw new TriggerScheduleCommandException("target_mismatch", "The selected Automation does not match the unique user target.");
    }

    private static bool ContainsPhrase(string value, string phrase) =>
        Regex.IsMatch(value, @"(?<!\w)" + Regex.Escape(phrase) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
