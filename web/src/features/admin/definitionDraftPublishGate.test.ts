import { describe, expect, it } from "vitest";
import { computePublishEligibility } from "./definitionDraftPublishGate";

const draftId = "019944af-00d1-7000-8000-000000000099";
const otherDraftId = "019944af-00d1-7000-8000-000000000088";

const validation = {
  draftId,
  draftRevision: 3,
  configurationFingerprint: "fp-3",
  hasBlockingFindings: false,
  findings: []
};

const diff = {
  draftId,
  draftRevision: 3,
  baselineKind: "ForkBuiltIn",
  baselineVersion: 1,
  sections: []
};

const requiredScenario = {
  scenarioId: "tool-offered",
  scenarioVersion: 1,
  title: "Tool offered",
  prompt: "check",
  requirementLevel: "Required" as const,
  checkType: "ToolOffered" as const,
  toolName: "knowledge.retrieve",
  updatedAt: "2026-09-25T12:00:00Z"
};

const passingResult = {
  draftId,
  draftRevision: 3,
  configurationFingerprint: "fp-3",
  scenarioId: "tool-offered",
  scenarioVersion: 1,
  runtimeKind: "Synthetic",
  passed: true,
  findings: [],
  recordedAt: "2026-09-25T12:01:00Z"
};

describe("computePublishEligibility", () => {
  it("blocks when draft is dirty", () => {
    const result = computePublishEligibility({
      dirty: true,
      draftId,
      draftRevision: 3,
      evidenceLoadStatus: "ready",
      validation,
      diff,
      scenarios: [],
      results: []
    });
    expect(result.eligible).toBe(false);
    expect(result.blockers[0]).toMatch(/Save draft/);
  });

  it("blocks when validation belongs to another draft", () => {
    const result = computePublishEligibility({
      dirty: false,
      draftId,
      draftRevision: 3,
      evidenceLoadStatus: "ready",
      validation: { ...validation, draftId: otherDraftId },
      diff,
      scenarios: [],
      results: []
    });
    expect(result.eligible).toBe(false);
    expect(result.blockers.some((item) => item.includes("validation"))).toBe(true);
  });

  it("blocks when diff is missing or stale", () => {
    const result = computePublishEligibility({
      dirty: false,
      draftId,
      draftRevision: 3,
      evidenceLoadStatus: "ready",
      validation,
      diff: { ...diff, draftRevision: 2 },
      scenarios: [],
      results: []
    });
    expect(result.eligible).toBe(false);
    expect(result.blockers.some((item) => item.includes("diff"))).toBe(true);
  });

  it("blocks when required eval evidence is missing", () => {
    const result = computePublishEligibility({
      dirty: false,
      draftId,
      draftRevision: 3,
      evidenceLoadStatus: "ready",
      validation,
      diff,
      scenarios: [requiredScenario],
      results: []
    });
    expect(result.eligible).toBe(false);
    expect(result.blockers.some((item) => item.includes("Required evaluation"))).toBe(true);
  });

  it("blocks while evaluation evidence is loading", () => {
    const result = computePublishEligibility({
      dirty: false,
      draftId,
      draftRevision: 3,
      evidenceLoadStatus: "loading",
      validation,
      diff,
      scenarios: [],
      results: []
    });
    expect(result.eligible).toBe(false);
    expect(result.blockers.some((item) => item.includes("not ready"))).toBe(true);
  });

  it("blocks when evaluation evidence loading failed", () => {
    const result = computePublishEligibility({
      dirty: false,
      draftId,
      draftRevision: 3,
      evidenceLoadStatus: "failed",
      validation,
      diff,
      scenarios: [],
      results: []
    });
    expect(result.eligible).toBe(false);
    expect(result.blockers.some((item) => item.includes("could not be loaded"))).toBe(true);
  });

  it("allows publish when validation, diff, and required eval evidence match draft revision", () => {
    const result = computePublishEligibility({
      dirty: false,
      draftId,
      draftRevision: 3,
      evidenceLoadStatus: "ready",
      validation,
      diff,
      scenarios: [requiredScenario],
      results: [passingResult]
    });
    expect(result.eligible).toBe(true);
    expect(result.blockers).toEqual([]);
  });
});
