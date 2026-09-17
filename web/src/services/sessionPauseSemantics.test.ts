import { describe, expect, it } from "vitest";
import { requiresExplicitResume } from "./sessionPauseSemantics";

describe("session pause semantics", () => {
  it("requires explicit resume for semantic pauses", () => {
    expect(requiresExplicitResume("inactivity")).toBe(true);
    expect(requiresExplicitResume("silentEvaluation")).toBe(true);
    expect(requiresExplicitResume("initiative")).toBe(true);
    expect(requiresExplicitResume("manual")).toBe(true);
    expect(requiresExplicitResume("persistence")).toBe(true);
  });

  it("keeps disconnected and recovered transport-resumable", () => {
    expect(requiresExplicitResume("disconnected")).toBe(false);
    expect(requiresExplicitResume("recovered")).toBe(false);
  });
});
