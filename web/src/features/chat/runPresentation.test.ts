import { describe, expect, it } from "vitest";
import { runOriginLabel, runStatusLabel, runOutcomeLabel, runSource } from "./runPresentation";
import type { WorkItem } from "../../services/api";
describe("Run presentation", () => {
  it.each([["WaitingForApproval", "Needs approval"], ["needsApproval", "Needs approval"], ["WaitingToRetry", "Retrying"], ["retrying", "Retrying"], ["completed", "Completed"]])("uses the same run label for %s", (value, label) => expect(runStatusLabel(value)).toBe(label));
  it.each([["NoAction", "No action"], ["ActionCompleted", "Action completed"], ["AttentionRequested", "Needs attention"]])("displays %s as %s", (value, label) => expect(runOutcomeLabel(value)).toBe(label));
  it("keeps unknown future labels readable and does not invent source configuration", () => {
    expect(runOutcomeLabel("Future outcome")).toBe("Future outcome");
    expect(runOriginLabel("Automation · Event")).toBe("Automation · Event");
    expect(runSource({ origin: "Application event", automationId: "event-source" } as WorkItem)).toEqual({ kind: "automation", automationId: "event-source" });
    expect(runSource({ origin: "Automation · Manual", workItemId: "generation" } as WorkItem)).toEqual({ kind: "experience", workItemId: "generation" });
    expect(runSource({ origin: "Automation · Manual", sourceId: "checkpoint" } as WorkItem)).toBeNull();
    expect(runSource({ origin: "Thought activation", automationId: "thought-id" } as WorkItem)).toEqual({ kind: "automation", automationId: "thought-id" });
  });
});
