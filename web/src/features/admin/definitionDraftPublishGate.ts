import type {
  AdminDefinitionDraftDiff,
  AdminDefinitionDraftValidation,
  AdminDefinitionEvaluationResult,
  AdminDefinitionEvaluationScenario
} from "../../services/adminApi";

export type PublishEligibility = {
  eligible: boolean;
  blockers: string[];
};

export type EvidenceLoadStatus = "idle" | "loading" | "ready" | "failed";

export function computePublishEligibility(input: {
  dirty: boolean;
  draftId: string;
  draftRevision: number;
  evidenceLoadStatus: EvidenceLoadStatus;
  validation: AdminDefinitionDraftValidation | null;
  diff: AdminDefinitionDraftDiff | null;
  scenarios: AdminDefinitionEvaluationScenario[];
  results: AdminDefinitionEvaluationResult[];
}): PublishEligibility {
  const blockers: string[] = [];
  if (input.dirty) {
    blockers.push("Save draft edits before validating or publishing.");
  }

  if (input.evidenceLoadStatus === "failed") {
    blockers.push("Evaluation evidence could not be loaded for the active draft.");
  } else if (input.evidenceLoadStatus !== "ready") {
    blockers.push("Evaluation evidence is not ready for the active draft.");
  }

  if (
    !input.validation
    || input.validation.draftId !== input.draftId
    || input.validation.draftRevision !== input.draftRevision
  ) {
    blockers.push("Run validation on the current draft revision.");
  } else if (input.validation.hasBlockingFindings) {
    blockers.push("Resolve blocking validation findings.");
  }

  if (
    !input.diff
    || input.diff.draftId !== input.draftId
    || input.diff.draftRevision !== input.draftRevision
  ) {
    blockers.push("Review the current safe diff for this draft revision.");
  }

  const fingerprint = input.validation?.configurationFingerprint ?? "";
  const revision = input.draftRevision;
  const required = input.scenarios.filter((item) => item.requirementLevel === "Required");
  for (const scenario of required) {
    const latest = latestResultForScenario(input.results, scenario.scenarioId);
    if (
      !latest
      || latest.draftId !== input.draftId
      || latest.draftRevision !== revision
      || latest.configurationFingerprint !== fingerprint
      || !latest.passed
    ) {
      blockers.push(`Required evaluation "${scenario.title}" is missing current passing evidence.`);
    }
  }

  return { eligible: blockers.length === 0, blockers };
}

function latestResultForScenario(
  results: AdminDefinitionEvaluationResult[],
  scenarioId: string
): AdminDefinitionEvaluationResult | null {
  const matches = results.filter((item) => item.scenarioId === scenarioId);
  if (matches.length === 0) {
    return null;
  }

  return matches.reduce((latest, item) =>
    item.recordedAt > latest.recordedAt ? item : latest);
}
