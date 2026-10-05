import type { WorkItem } from "../../services/api";

export function runStatusLabel(value: string | null | undefined) {
  return ({ queued: "Queued", running: "Running", needsApproval: "Needs approval", WaitingForApproval: "Needs approval",
    retrying: "Retrying", WaitingToRetry: "Retrying", completed: "Completed", failed: "Failed", cancelled: "Cancelled" } as Record<string, string>)[value ?? ""] ?? value ?? "Not yet";
}

export function thoughtOutcomeLabel(value: string | null | undefined) {
  return ({ NoAction: "No action", ActionCompleted: "Action completed", AttentionRequested: "Needs attention",
    ApprovalPending: "Needs approval", WaitingToRetry: "Retrying" } as Record<string, string>)[value ?? ""] ?? value ?? "Not yet";
}
export function runOriginLabel(origin: string) {
  return ({ "Scheduled reminder": "Schedule", "Thought activation": "Thought", "Order placed": "Event",
    "Application event": "Event" } as Record<string, string>)[origin] ?? origin;
}
export type AutomationSelection = { kind: "schedule" | "thought"; registrationId: string; request: number };
export type RunSource = Omit<AutomationSelection, "request"> | { kind: "event"; registrationId: string } | { kind: "retrospection"; workItemId: string };
export function runSource(item: WorkItem): RunSource | null {
  if (item.origin === "Retrospection" && item.workItemId) return { kind: "retrospection", workItemId: item.workItemId };
  const registrationId = item.registrationId ?? item.triggerRegistrationId;
  if (!registrationId) return null;
  if (item.origin === "Scheduled reminder") return { kind: "schedule", registrationId };
  if (item.origin === "Thought activation") return { kind: "thought", registrationId };
  if (["Order placed", "Application event"].includes(item.origin)) return { kind: "event", registrationId };
  return null;
}
