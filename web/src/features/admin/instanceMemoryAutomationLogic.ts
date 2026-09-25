import type { AdminEffectiveConfiguration, AdminLearnedMemoryScope } from "../../services/adminApi";

export function isMemoryScopePermitted(
  policy: AdminEffectiveConfiguration["memoryPolicy"],
  scope: AdminLearnedMemoryScope
): boolean {
  switch (scope) {
    case "Session":
      // Session eligibility is validated server-side against the pinned session definition.
      return true;
    case "IdentityUser":
      return policy.identityUserRetrieval;
    case "User":
      return policy.userRetrieval;
  }
}

export function isMemorySelectionReady(scope: AdminLearnedMemoryScope, sessionId: string): boolean {
  if (scope === "Session") {
    return sessionId.trim().length > 0;
  }
  return true;
}

export function memoryResetConfirmTitle(
  scope: AdminLearnedMemoryScope,
  instanceId: string,
  sessionId: string
): string {
  switch (scope) {
    case "Session":
      return `Reset all active Session learned memory for session ${sessionId.trim()}?`;
    case "IdentityUser":
      return `Reset all active IdentityUser learned memory for this managed instance (${instanceId})?`;
    case "User":
      return "Reset all active User-wide learned memory for the trusted local profile (all instances)?";
  }
}

export function formatAutomationNextRun(timeZoneId: string, nextOccurrenceAtUtc: string | null): string {
  if (!nextOccurrenceAtUtc) {
    return `${timeZoneId} · no upcoming run`;
  }
  const when = new Date(nextOccurrenceAtUtc);
  const formatted = Number.isNaN(when.getTime())
    ? nextOccurrenceAtUtc
    : when.toLocaleString(undefined, { timeZone: "UTC", dateStyle: "medium", timeStyle: "short" });
  return `${timeZoneId} · next ${formatted} UTC`;
}
