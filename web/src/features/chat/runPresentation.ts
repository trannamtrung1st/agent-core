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
export type AutomationSelection = { kind: "automation"; automationId: string; request: number };
export type RunSource = Omit<AutomationSelection, "request"> | { kind: "experience"; workItemId: string };
export function runSource(item: WorkItem): RunSource | null {
  if (item.automationId) return { kind: "automation", automationId: item.automationId };
  if (item.origin === "Automation · Manual" && item.workItemId) return { kind: "experience", workItemId: item.workItemId };
  return null;
}
