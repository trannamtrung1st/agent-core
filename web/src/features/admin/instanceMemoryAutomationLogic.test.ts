import { describe, expect, it } from "vitest";
import {
  formatAutomationNextRun,
  isMemoryScopePermitted,
  isMemorySelectionReady,
  memoryResetConfirmTitle
} from "./instanceMemoryAutomationLogic";

const policy = {
  sessionMemory: true,
  identityUserPromotion: true,
  identityUserRetrieval: true,
  userPromotion: false,
  userRetrieval: false
};

describe("instanceMemoryAutomationLogic", () => {
  it("blocks User scope when userRetrieval is disabled", () => {
    expect(isMemoryScopePermitted(policy, "User")).toBe(false);
    expect(isMemoryScopePermitted(policy, "IdentityUser")).toBe(true);
  });

  it("does not gate Session scope on the current instance memory policy", () => {
    expect(isMemoryScopePermitted({ ...policy, sessionMemory: false }, "Session")).toBe(true);
  });

  it("requires session id for Session scope readiness", () => {
    expect(isMemorySelectionReady("Session", "")).toBe(false);
    expect(isMemorySelectionReady("Session", "abc")).toBe(true);
    expect(isMemorySelectionReady("User", "")).toBe(true);
  });

  it("uses scope-specific reset confirmation copy", () => {
    expect(memoryResetConfirmTitle("User", "inst-1", "")).toContain("all instances");
    expect(memoryResetConfirmTitle("Session", "inst-1", "sess-9")).toContain("sess-9");
    expect(memoryResetConfirmTitle("IdentityUser", "inst-1", "")).toContain("inst-1");
  });

  it("formats automation next run with timezone", () => {
    const line = formatAutomationNextRun("America/Los_Angeles", "2026-09-26T09:00:00.000Z");
    expect(line).toContain("America/Los_Angeles");
    expect(line).toContain("next");
  });
});
