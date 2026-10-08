import { describe, expect, it } from "vitest";
import { runOriginLabel, runStatusLabel, runOutcomeLabel, runTriggerLabel, runSource } from "./runPresentation";
import type { BackgroundSession } from "../../services/api";
describe("Run presentation", () => {
  it("formats one-shot ISO summaries while retaining unknown or invalid trigger text", () => {
    const formatted = runTriggerLabel("Once · 2026-10-07T17:40:15.2280000+00:00");
    expect(formatted).toMatch(/^Once · /);
    expect(formatted).toContain("2026");
    expect(formatted).not.toContain("T17:40:15");
    expect(runTriggerLabel("Daily at 09:00")).toBe("Daily at 09:00");
    expect(runTriggerLabel("Once · 2026-99-99Tinvalid")).toBe("Once · 2026-99-99Tinvalid");
  });
  it.each([["WaitingForApproval", "Needs approval"], ["needsApproval", "Needs approval"], ["WaitingToRetry", "Retrying"], ["retrying", "Retrying"], ["completed", "Completed"]])("uses the same run label for %s", (value, label) => expect(runStatusLabel(value)).toBe(label));
  it.each([["NoAction", "No action"], ["Result", "Result"], ["Response", "Response"], ["NeedsAttention", "Needs attention"]])("displays %s as %s", (value, label) => expect(runOutcomeLabel(value)).toBe(label));
  it("keeps unknown future labels readable and does not invent source configuration", () => {
    expect(runOutcomeLabel("Future outcome")).toBe("Future outcome");
    expect(runOriginLabel("Automation · Event")).toBe("Automation · Event");
    expect(runSource({ origin: { kind: "AutomationOccurrence", automationId: "event-source" } } as BackgroundSession)).toEqual({ kind: "automation", automationId: "event-source" });
    expect(runSource({ origin: { kind: "ImmediateBackground" } } as BackgroundSession)).toBeNull();
  });
});
