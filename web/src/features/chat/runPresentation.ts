import type { BackgroundSession } from "../../services/api";

export function runStatusLabel(value: string | null | undefined) {
  return ({ queued: "Queued", running: "Running", needsApproval: "Needs approval", WaitingForApproval: "Needs approval",
    waitingForSignal: "Waiting", WaitingForSignal: "Waiting", retrying: "Retrying", WaitingToRetry: "Retrying", completed: "Completed", failed: "Failed", cancelled: "Cancelled" } as Record<string, string>)[value ?? ""] ?? value ?? "Not yet";
}

export function runOutcomeLabel(value: string | null | undefined) {
  return ({ NoAction: "No action", Response: "Response", Result: "Result", NeedsAttention: "Needs attention" } as Record<string, string>)[value ?? ""] ?? value ?? "Not yet";
}
export function runOriginLabel(origin: string) {
  return ({ ImmediateBackground: "Immediate task", AutomationOccurrence: "Automation", SourceOccurrence: "Application event", ManualBackground: "Manual task", UserChat: "Chat" } as Record<string, string>)[origin] ?? origin;
}

export function runActivationLabel(kind: string) {
  return ({ UserTurn: "Chat turn", Initiative: "Initiative", ScheduledWork: "Scheduled task", ApplicationEvent: "Application event", CoreEvent: "Core Event",
    ImmediateBackground: "Immediate task", ManualBackground: "Manual task", BackgroundCompleted: "Completion report" } as Record<string, string>)[kind] ?? kind;
}

/** Localize the legacy one-shot ISO summary without rewriting other trigger kinds. */
export function runTriggerLabel(summary: string) {
  const match = /^Once · (\d{4}-\d{2}-\d{2}T.+)$/.exec(summary);
  if (!match) return summary;
  const date = new Date(match[1]);
  return Number.isNaN(date.getTime()) ? summary : `Once · ${date.toLocaleString(undefined, {
    year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit", timeZoneName: "short"
  })}`;
}
export type AutomationSelection = { kind: "automation"; automationId: string; request: number };
export type RunSource = Omit<AutomationSelection, "request"> | { kind: "experience"; agentRunId: string };
export function runSource(item: BackgroundSession): RunSource | null {
  if (item.origin.automationId) return { kind: "automation", automationId: item.origin.automationId };
  return null;
}
