import { act, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { AdminApp } from "./AdminApp";

vi.mock("../../services/adminApi", () => ({
  listAdminDefinitions: vi.fn().mockResolvedValue([
    {
      definitionId: "examiner",
      version: 1,
      source: "builtIn",
      status: "published",
      displayName: "Examiner"
    }
  ]),
  listAdminInstances: vi.fn().mockResolvedValue([
    {
      instanceId: "019944af-00d1-7000-8000-000000000001",
      definitionId: "examiner",
      activeVersion: 1,
      lifecycle: "Active",
      compatibility: true,
      personaName: "Examiner",
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z"
    }
  ]),
  getAdminEffectiveConfig: vi.fn().mockResolvedValue({
    definitionSource: "builtIn",
    definitionId: "examiner",
    definitionVersion: 1,
    definitionStatus: "published",
    instanceId: "019944af-00d1-7000-8000-000000000001",
    instanceLifecycle: "Active",
    compatibility: true,
    persona: { name: "Examiner", role: "role", description: "desc", tone: "tone" },
    providerPreferences: {
      languageModel: "primary-llm",
      speechRecognizer: null,
      speechSynthesizer: null,
      interruptionClassifier: "heuristic"
    },
    modelDefaults: null,
    effectiveToolAllowlist: ["workspace.read"],
    knowledgeSources: [],
    memoryPolicy: {
      sessionMemory: false,
      identityUserPromotion: false,
      identityUserRetrieval: false,
      userPromotion: false,
      userRetrieval: false
    },
    triggerPolicy: null,
    durableExecutionEligibility: {
      instanceActive: true,
      definitionResolved: true,
      triggerPolicyEnabled: false,
      canAcceptNewTriggeredWork: false
    }
  })
}));

describe("AdminApp", () => {
  it("renders definition and instance inventory", async () => {
    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    await waitFor(() => {
      expect(screen.getByText(/Examiner \(examiner v1\)/)).toBeInTheDocument();
    });
    expect(screen.getByText(/Compatibility \/ legacy instance/)).toBeInTheDocument();
  });
});
