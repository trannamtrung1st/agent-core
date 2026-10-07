import type { WorkItem } from "../../services/api";

export function runStatusLabel(value: string | null | undefined) {
  return ({ queued: "Queued", running: "Running", needsApproval: "Needs approval", WaitingForApproval: "Needs approval",
    retrying: "Retrying", WaitingToRetry: "Retrying", completed: "Completed", failed: "Failed", cancelled: "Cancelled" } as Record<string, string>)[value ?? ""] ?? value ?? "Not yet";
}

export function runOutcomeLabel(value: string | null | undefined) {
  return ({ NoAction: "No action", ActionCompleted: "Action completed", AttentionRequested: "Needs attention",
    ApprovalPending: "Needs approval", WaitingToRetry: "Retrying" } as Record<string, string>)[value ?? ""] ?? value ?? "Not yet";
}
export function runOriginLabel(origin: string) {
  return origin;
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
export type RunSource = Omit<AutomationSelection, "request"> | { kind: "experience"; workItemId: string };
export function runSource(item: WorkItem): RunSource | null {
  if (item.automationId) return { kind: "automation", automationId: item.automationId };
  if (item.origin === "Automation · Manual" && item.workItemId) return { kind: "experience", workItemId: item.workItemId };
  return null;
}
