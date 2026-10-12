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
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReferenceWords = new(StringComparer.OrdinalIgnoreCase)
    { "the", "a", "an", "my", "to", "for", "automation", "schedule", "reminder", "job", "preset", "again", "please", "every", "named", "called" };

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
        var seconds = ReadCadence(text);
        var ids = Id.Matches(text).Select(m => Guid.Parse(m.Value)).Distinct().ToArray();
        if (ids.Length > 0)
        {
            if (ids.Length != 1) throw new TriggerScheduleCommandException("ambiguous_target", "Name one Automation to change; multiple IDs were supplied.");
            if (ids[0] != selectedId) throw new TriggerScheduleCommandException("target_mismatch", "The selected Automation does not match the user's ID.");
            await RequireCadenceAsync(service, owner, selectedId, seconds, ct).ConfigureAwait(false);
            return;
        }
        var match = Command.Match(text);
        if (!match.Success) throw new TriggerScheduleCommandException("ambiguous_target", "Ask which existing Automation the user wants to change.");
        var target = Cadence.Replace(match.Groups["target"].Value, " ");
        var terms = TargetWords(target);
        var generic = terms.Length == 1 && terms[0] is "this" or "that" or "it";
        if (generic && HasReferent(context.ScheduleContext))
        {
            var referent = context.ScheduleContext!;
            if (referent.AutomationId != selectedId) throw new TriggerScheduleCommandException("target_mismatch", "Use the trusted Automation referent, not another ID.");
            await RequireCadenceAsync(service, owner, selectedId, seconds, ct).ConfigureAwait(false);
            return;
        }
        if (generic && !Regex.IsMatch(target, @"\b(automation|schedule|reminder)\b", RegexOptions.IgnoreCase))
            throw new TriggerScheduleCommandException("ambiguous_target", "No trusted referent identifies this Automation. Ask which one.");
        var rows = await service.ListAsync(owner, null, ct).ConfigureAwait(false);
        var candidates = rows.Where(a => (generic || terms.Length == 0 && seconds is not null || terms.Length > 0 && MatchesDescription(a, terms))
            && MatchesCadence(a, seconds)).ToArray();
        if (candidates.Length == 0) throw new TriggerScheduleCommandException("not_found", "No owned Automation matches this request. Do not create a replacement.");
        if (candidates.Length > 1) throw new TriggerScheduleCommandException("ambiguous_target", "Several owned Automations match. Ask the user which one; do not pick the first.");
        if (candidates[0].AutomationId != selectedId) throw new TriggerScheduleCommandException("target_mismatch", "The selected Automation does not match the unique user target.");
    }

    private static string[] TargetWords(string target)
    {
        // A closed vocabulary of reference wording, not fuzzy similarity or task synonyms.
        target = Regex.Replace(target, @"\b(?:to|for)\s+me\b|\bcore\s+event\b", " ", RegexOptions.IgnoreCase);
        return Words.Matches(target.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => !ReferenceWords.Contains(w)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool MatchesDescription(Automation automation, string[] terms) => ContainsWords(automation.Name, terms)
        || Regex.Split(automation.Instructions, @"[.!?;\r\n]+")
            .Any(clause => !Regex.IsMatch(clause, @"\b(?:don['’]?t|do not|never|not)\b", RegexOptions.IgnoreCase)
                && ContainsWords(clause, terms));

    private static bool ContainsWords(string value, string[] terms)
    {
        var words = Words.Matches(value.ToLowerInvariant()).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        return terms.All(words.Contains);
    }

    private static bool MatchesCadence(Automation automation, int? seconds) => seconds is null
        || automation.Triggers.Any(t => t.Configuration is ScheduleTrigger { Schedule: FixedIntervalSchedule f } && f.IntervalSeconds == seconds);

    private static async Task RequireCadenceAsync(IAutomationService service, TriggerOwner owner, Guid id, int? seconds, CancellationToken ct)
    {
        if (seconds is null) return;
        var automation = await service.GetAsync(owner, id, ct).ConfigureAwait(false)
            ?? throw new TriggerScheduleCommandException("not_found", "Automation was not found.");
        if (!MatchesCadence(automation, seconds))
            throw new TriggerScheduleCommandException("target_mismatch", "The selected Automation does not match the user's cadence.");
    }

    private static int? ReadCadence(string text)
    {
        int? seconds = null;
        foreach (Match cadence in Cadence.Matches(text))
        {
            if (!int.TryParse(cadence.Groups["n"].Value, out var amount) || amount < 1)
                throw new TriggerScheduleCommandException("validation", "Automation cadence is invalid.");
            var unit = cadence.Groups["unit"].Value.ToLowerInvariant();
            var value = (long)amount * (unit.StartsWith('h') ? 3600 : unit.StartsWith('m') ? 60 : 1);
            if (value > int.MaxValue)
                throw new TriggerScheduleCommandException("validation", "Automation cadence is invalid.");
            if (seconds is not null && seconds != value)
                throw new TriggerScheduleCommandException("ambiguous_target", "The request supplies conflicting cadences. Ask which one.");
            seconds = (int)value;
        }
        return seconds;
    }
}
