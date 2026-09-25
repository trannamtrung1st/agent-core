import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { OwnerCapabilityError } from "../../services/api";
import { AdminApp, EffectiveConfigView } from "./AdminApp";
import type { AdminEffectiveConfiguration } from "../../services/adminApi";

const sampleEffective: AdminEffectiveConfiguration = {
  definitionSource: "builtIn",
  definitionId: "examiner",
  definitionVersion: 1,
  definitionStatus: "published",
  instanceId: "019944af-00d1-7000-8000-000000000001",
  instanceLifecycle: "Active",
  compatibility: true,
  persona: { name: "Alex", role: "Examiner", description: "Practice speaking.", tone: "Supportive" },
  providerPreferences: {
    languageModel: "primary-llm",
    speechRecognizer: "primary-stt",
    speechSynthesizer: "primary-tts",
    interruptionClassifier: "heuristic"
  },
  effectiveModel: {
    catalogKey: "scripted-alpha",
    displayName: "Scripted Alpha",
    selectionSource: "systemDefault",
    reasoningEffort: "medium",
    modelId: "scripted-alpha"
  },
  effectiveToolAllowlist: ["workspace.read"],
  harnessReferences: ["fixture-a"],
  workspaceTemplateId: "template-1",
  knowledgeSources: [{ identity: "policy", title: "Policy", citation: "policy@demo" }],
  memoryPolicy: {
    sessionMemory: true,
    identityUserPromotion: false,
    identityUserRetrieval: true,
    userPromotion: false,
    userRetrieval: false
  },
  triggerPolicy: {
    enabled: true,
    allowUserScheduling: true,
    allowOneShot: true,
    allowDaily: false,
    allowWeekly: false,
    allowIndefiniteRecurrence: false,
    maxActiveRegistrations: 4,
    oneShotHorizonDays: 30,
    minRecurrenceDays: 1,
    allowedSourceKinds: ["schedule"],
    allowFixedInterval: false,
    minFixedIntervalSeconds: 60
  },
  durableExecutionEligibility: {
    instanceActive: true,
    definitionResolved: true,
    triggerPolicyEnabled: true,
    allowsScheduleSource: true,
    allowsApplicationEventSource: false,
    canAcceptNewTriggeredWork: true
  }
};

vi.mock("../../services/adminApi", () => ({
  listAdminDefinitions: vi.fn(),
  listAdminInstances: vi.fn(),
  getAdminEffectiveConfig: vi.fn()
}));

import { getAdminEffectiveConfig, listAdminDefinitions, listAdminInstances } from "../../services/adminApi";

const instanceId = "019944af-00d1-7000-8000-000000000001";

describe("AdminApp", () => {
  it("renders definition and instance inventory", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([
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
    ]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    await waitFor(() => {
      expect(screen.getByText(/Examiner \(examiner v1\)/)).toBeInTheDocument();
    });
    expect(screen.getByText(/Compatibility \/ legacy instance/)).toBeInTheDocument();
  });

  it("keeps definitions when instances fail and offers retry", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner"
      }
    ]);
    vi.mocked(listAdminInstances).mockRejectedValueOnce(new Error("Instances unavailable"));
    vi.mocked(listAdminInstances).mockResolvedValueOnce([]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    await waitFor(() => {
      expect(screen.getByText(/Examiner \(examiner v1\)/)).toBeInTheDocument();
    });
    expect(screen.getByText(/Instances unavailable/)).toBeInTheDocument();
    fireEvent.click(within(screen.getByLabelText("Instances")).getByRole("button", { name: "Retry" }));
    await waitFor(() => {
      expect(screen.getByText("No instances yet. Start a chat to create compatibility instances.")).toBeInTheDocument();
    });
  });

  it("shows unauthorized recovery for owner capability errors", async () => {
    vi.mocked(listAdminDefinitions).mockRejectedValue(new OwnerCapabilityError());
    vi.mocked(listAdminInstances).mockResolvedValue([]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    await waitFor(() => {
      expect(screen.getByText(/Owner capability is missing or invalid/)).toBeInTheDocument();
    });
  });

  it("renders effective configuration fields", () => {
    render(<EffectiveConfigView config={sampleEffective} />);
    const identity = screen.getByLabelText("Instance identity");
    expect(within(identity).getByText("Compatibility / legacy")).toBeInTheDocument();
    expect(within(identity).getByText("Definition status")).toBeInTheDocument();
    expect(screen.getByText("Practice speaking.")).toBeInTheDocument();
    expect(screen.getByText("heuristic")).toBeInTheDocument();
    expect(screen.getAllByText("scripted-alpha").length).toBeGreaterThan(0);
    expect(screen.getByText(/max registrations 4/)).toBeInTheDocument();
  });

  it("shows instance identity from effective config when inventory is unavailable", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockRejectedValue(new Error("Instances unavailable"));
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(sampleEffective);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "instance", instanceId }} />);
    });

    await waitFor(() => {
      expect(screen.getByText("Practice speaking.")).toBeInTheDocument();
    });
    expect(within(screen.getByLabelText("Instance identity")).getByText("Compatibility / legacy")).toBeInTheDocument();
    expect(screen.getAllByText("Active").length).toBeGreaterThan(0);
    expect(screen.getByText("Practice speaking.")).toBeInTheDocument();
  });
});
