import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { DefinitionDraftPublishGatePanel } from "./definitionDraftPublishGatePanel";

vi.mock("../../services/adminApi", () => ({
  listAdminDefinitionEvaluationScenarios: vi.fn(),
  listAdminDefinitionEvaluationResults: vi.fn(),
  validateAdminDefinitionDraft: vi.fn(),
  getAdminDefinitionDraftDiff: vi.fn(),
  getAdminDefinitionDraft: vi.fn(),
  upsertAdminDefinitionEvaluationScenario: vi.fn(),
  runAdminDefinitionEvaluationScenario: vi.fn()
}));

import {
  listAdminDefinitionEvaluationResults,
  listAdminDefinitionEvaluationScenarios
} from "../../services/adminApi";

const draft = {
  draftId: "019944af-00d1-7000-8000-000000000099",
  definitionId: "examiner",
  revision: 2,
  sourceKind: "ForkBuiltIn",
  sourceVersion: 1,
  candidate: {},
  createdAt: "2026-09-25T12:00:00Z",
  updatedAt: "2026-09-25T12:00:00Z"
};

describe("DefinitionDraftPublishGatePanel evidence loading", () => {
  it("keeps publish blocked while evaluation evidence is still loading", async () => {
    vi.mocked(listAdminDefinitionEvaluationScenarios).mockImplementation(
      () =>
        new Promise((resolve) => {
          setTimeout(() => resolve([]), 200);
        })
    );
    vi.mocked(listAdminDefinitionEvaluationResults).mockResolvedValue([]);

    const onEligibilityChange = vi.fn();
    render(
      <DefinitionDraftPublishGatePanel
        activeDraft={draft}
        dirty={false}
        busy={false}
        toolNames={["knowledge.retrieve"]}
        onDraftRevisionChange={vi.fn()}
        onError={vi.fn()}
        onEligibilityChange={onEligibilityChange}
      />
    );

    await waitFor(() => {
      expect(onEligibilityChange).toHaveBeenCalledWith(false);
    });
    expect(screen.getByText(/Evaluation evidence is not ready/i)).toBeInTheDocument();

    await waitFor(() => {
      expect(onEligibilityChange).toHaveBeenLastCalledWith(false);
    });
  });

  it("keeps publish blocked when evaluation evidence loading fails", async () => {
    vi.mocked(listAdminDefinitionEvaluationScenarios).mockRejectedValue(new Error("network down"));
    vi.mocked(listAdminDefinitionEvaluationResults).mockResolvedValue([]);

    const onEligibilityChange = vi.fn();
    render(
      <DefinitionDraftPublishGatePanel
        activeDraft={draft}
        dirty={false}
        busy={false}
        toolNames={["knowledge.retrieve"]}
        onDraftRevisionChange={vi.fn()}
        onError={vi.fn()}
        onEligibilityChange={onEligibilityChange}
      />
    );

    await waitFor(() => {
      expect(onEligibilityChange).toHaveBeenCalledWith(false);
    });
    expect(screen.getByText(/could not be loaded/i)).toBeInTheDocument();
  });
});
