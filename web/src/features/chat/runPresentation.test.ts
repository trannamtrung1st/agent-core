import { describe, expect, it } from "vitest";
import { runOriginLabel, runStatusLabel, thoughtOutcomeLabel, runSource } from "./runPresentation";
import type { WorkItem } from "../../services/api";
describe("Run presentation", () => {
  it.each([["WaitingForApproval", "Needs approval"], ["needsApproval", "Needs approval"], ["WaitingToRetry", "Retrying"], ["retrying", "Retrying"], ["completed", "Completed"]])("uses the same run label for %s", (value, label) => expect(runStatusLabel(value)).toBe(label));
  it.each([["NoAction", "No action"], ["ActionCompleted", "Action completed"], ["AttentionRequested", "Needs attention"]])("displays %s as %s", (value, label) => expect(thoughtOutcomeLabel(value)).toBe(label));
  it("keeps unknown future labels readable and does not invent source configuration", () => {
    expect(thoughtOutcomeLabel("Future outcome")).toBe("Future outcome");
    expect(runOriginLabel("Application event")).toBe("Event");
    expect(runSource({ origin: "Application event", registrationId: "event-source" } as WorkItem)).toEqual({ kind: "event", registrationId: "event-source" });
    expect(runSource({ origin: "Retrospection", workItemId: "generation" } as WorkItem)).toEqual({ kind: "retrospection", workItemId: "generation" });
    expect(runSource({ origin: "Retrospection", sourceId: "checkpoint" } as WorkItem)).toBeNull();
    expect(runSource({ origin: "Thought activation", registrationId: "thought-id" } as WorkItem)).toEqual({ kind: "thought", registrationId: "thought-id" });
  });
});
