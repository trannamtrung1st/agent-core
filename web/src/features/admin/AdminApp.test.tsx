import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OwnerCapabilityError } from "../../services/api";
import {
  AdminApp,
  InstanceManagedControls,
  EffectiveConfigView,
  PublicationResourcesSummary,
  defaultForkSourceVersion,
  formatDefinitionVersionSummary,
  formatForkSourceOptionLabel
} from "./AdminApp";
import type { AdminDefinitionPublicationResource, AdminEffectiveConfiguration } from "../../services/adminApi";

const sampleEffective: AdminEffectiveConfiguration = {
  definitionSource: "builtIn",
  definitionId: "examiner",
  definitionVersion: 1,
  definitionStatus: "published",
  instanceId: "019944af-00d1-7000-8000-000000000001",
  instanceLifecycle: "Active",
  instanceRevision: 1,
  personaRevision: 1,
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
  instanceContinuityRequest: vi.fn().mockImplementation((_id: string, path: string) => Promise.resolve(path === "maintenance" ? { agentInstanceId: _id, allowAgentConsolidation: false, revision: 0 } : path === "thoughts" ? { minimumIntervalSeconds: 3600, items: [] } : { enabled: false, settingsRevision: 0, contextBudgetCharacters: 6000, items: [] })),
  listAdminDefinitions: vi.fn(),
  listAdminInstances: vi.fn(),
  getAdminEffectiveConfig: vi.fn(),
  listAdminDefinitionDrafts: vi.fn(),
  listAdminDefinitionPublications: vi.fn(),
  getAdminDefinitionDraft: vi.fn(),
  deleteAdminDefinitionDraft: vi.fn(),
  updateAdminDefinitionDraft: vi.fn(),
  publishAdminDefinitionDraft: vi.fn(),
  createNewAdminDefinitionDraft: vi.fn(),
  createAdminAgentInstance: vi.fn(),
  forkAdminDefinitionDraft: vi.fn(),
  listAdminDraftResources: vi.fn(),
  listAdminPublicationResources: vi.fn(),
  getAdminToolRegistry: vi.fn().mockResolvedValue({ toolNames: ["workspace.read"], maxToolAllowlistEntries: 32 }),
  uploadAdminDraftResourceContent: vi.fn(),
  upsertAdminDraftResource: vi.fn(),
  bindAdminDraftResources: vi.fn(),
  removeAdminDraftResource: vi.fn(),
  updateAdminAgentInstancePersona: vi.fn(),
  updateAdminAgentInstanceLifecycle: vi.fn(),
  updateAdminAgentInstanceActiveVersion: vi.fn(),
  getHarnessReview: vi.fn().mockResolvedValue({ instanceId: "instance", instanceRevision: 1, activeVersion: 1, policyRevision: 1,
    policy: { mode: "Disabled", scopes: [], sources: [], eligibleTools: [], frozen: false }, preparation: null,
    draftRevision: null, instructions: null, skills: [], knowledge: [], selectedTools: [], diff: null, resources: [] }),
  updateHarness: vi.fn(),
  getApplicationConnection: vi.fn().mockResolvedValue(null),
  listEventSources: vi.fn().mockResolvedValue([]),
  listEventSubscriptions: vi.fn().mockResolvedValue([]),
  createEventSource: vi.fn(),
  rotateEventSource: vi.fn(),
  revokeEventSource: vi.fn(),
  createEventSubscription: vi.fn(),
  connectApplication: vi.fn(),
  reauthenticateApplication: vi.fn(),
  openApplicationBrowser: vi.fn(),
  revokeApplication: vi.fn(),
  resetApplicationProfile: vi.fn(),
  deprecateAdminDefinitionPublication: vi.fn(),
  validateAdminDefinitionDraft: vi.fn(),
  getAdminDefinitionDraftDiff: vi.fn(),
  listAdminDefinitionEvaluationScenarios: vi.fn(),
  listAdminDefinitionEvaluationResults: vi.fn(),
  listAdminAuthoringOptions: vi.fn().mockResolvedValue({
    languageModelAliases: ["primary-llm"],
    speechRecognizerAliases: ["primary-stt"],
    speechSynthesizerAliases: ["primary-tts"],
    defaultLanguageModelAlias: "primary-llm",
    defaultSpeechRecognizerAlias: "primary-stt",
    defaultSpeechSynthesizerAlias: "primary-tts",
    defaultModelKey: "scripted-alpha",
    models: [{
      key: "scripted-alpha",
      displayName: "Scripted Alpha",
      supportedReasoningEfforts: ["low", "medium", "high"],
      defaultReasoningEffort: "medium"
    }],
    interruptionClassifiers: ["heuristic"]
  }),
  deleteAdminAgentInstance: vi.fn(),
  deleteAdminDefinition: vi.fn()
}));

import * as antd from "antd";
import {
  createNewAdminDefinitionDraft,
  createAdminAgentInstance,
  forkAdminDefinitionDraft,
  getAdminDefinitionDraft,
  getAdminEffectiveConfig,
  deleteAdminDefinitionDraft,
  listAdminDefinitionDrafts,
  listAdminDefinitionPublications,
  listAdminDefinitions,
  listAdminDraftResources,
  listAdminPublicationResources,
  getAdminToolRegistry,
  listAdminInstances,
  removeAdminDraftResource,
  publishAdminDefinitionDraft,
  updateAdminDefinitionDraft,
  updateAdminAgentInstanceActiveVersion,
  deprecateAdminDefinitionPublication,
  updateAdminAgentInstanceLifecycle,
  updateAdminAgentInstancePersona,
  validateAdminDefinitionDraft,
  getAdminDefinitionDraftDiff,
  listAdminDefinitionEvaluationScenarios,
  listAdminDefinitionEvaluationResults
} from "../../services/adminApi";

const instanceId = "019944af-00d1-7000-8000-000000000001";

describe("defaultForkSourceVersion", () => {
  it("prefers the highest non-deprecated version", () => {
    expect(
      defaultForkSourceVersion([
        {
          definitionId: "examiner",
          version: 3,
          source: "durable",
          status: "deprecated",
          displayName: "v3"
        },
        {
          definitionId: "examiner",
          version: 2,
          source: "durable",
          status: "published",
          displayName: "v2"
        },
        {
          definitionId: "examiner",
          version: 1,
          source: "builtIn",
          status: "published",
          displayName: "v1"
        }
      ])
    ).toBe(2);
  });

  it("falls back to the highest version when every version is deprecated", () => {
    expect(
      defaultForkSourceVersion([
        {
          definitionId: "examiner",
          version: 2,
          source: "durable",
          status: "deprecated",
          displayName: "v2"
        },
        {
          definitionId: "examiner",
          version: 1,
          source: "builtIn",
          status: "deprecated",
          displayName: "v1"
        }
      ])
    ).toBe(2);
  });
});

describe("formatForkSourceOptionLabel", () => {
  it("includes source and status in the fork dropdown", () => {
    expect(
      formatForkSourceOptionLabel({
        definitionId: "examiner",
        version: 3,
        source: "durable",
        status: "deprecated",
        displayName: "v3"
      })
    ).toBe("v3 · Durable · Deprecated");
  });

  it("labels deprecated latest separately from latest active", () => {
    const versions = [
      {
        definitionId: "examiner",
        version: 6,
        source: "durable",
        status: "deprecated",
        displayName: "Examiner v6"
      },
      {
        definitionId: "examiner",
        version: 5,
        source: "durable",
        status: "published",
        displayName: "Examiner v5"
      }
    ];
    expect(defaultForkSourceVersion(versions)).toBe(5);
    expect(formatDefinitionVersionSummary({
      latestVersion: 6,
      latestStatus: "deprecated",
      versions
    })).toBe("Latest v6 · Deprecated · Latest active v5");
  });
});

describe("AdminApp", () => {
  let useAppSpy: ReturnType<typeof vi.spyOn> | undefined;

  afterEach(() => {
    useAppSpy?.mockRestore();
    useAppSpy = undefined;
  });
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
              personaName: "Examiner",
        createdAt: "2026-01-01T00:00:00Z",
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    await waitFor(() => {
      expect(within(screen.getByRole("region", { name: "Definitions" })).getByText("Examiner")).toBeInTheDocument();
    });
    expect(within(screen.getByRole("region", { name: "Definitions" })).getByText("examiner")).toBeInTheDocument();
    const definitionRow = screen.getByRole("button", { name: "Examiner · examiner" }).closest("tr")!;
    expect(within(definitionRow).getAllByText("v1")).toHaveLength(2);
    expect(within(definitionRow).getByText("Published")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Instances" }));
    expect(within(screen.getByRole("region", { name: "Instances" })).getByText("1 instance")).toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: "Instances" })).getByText("v1")).toBeInTheDocument();
    expect(screen.queryByText("Compatibility", { exact: true })).not.toBeInTheDocument();
  });

  it("groups immutable versions under one logical definition", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "customer-support",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Sam"
      },
      {
        definitionId: "customer-support",
        version: 2,
        source: "builtIn",
        status: "published",
        displayName: "Sam"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });

    const definitions = screen.getByRole("region", { name: "Definitions" });
    await waitFor(() => {
      expect(within(definitions).getByText("Customer Support")).toBeInTheDocument();
    });
    expect(within(definitions).getAllByRole("button", { name: "Customer Support · customer-support" })).toHaveLength(1);
    expect(within(definitions).getByText("customer-support")).toBeInTheDocument();
    expect(within(definitions).getAllByText("v2")).toHaveLength(2);
    expect(within(definitions).getByRole("columnheader", { name: "Versions" })).toBeInTheDocument();
    expect(within(definitions).getByText("2")).toBeInTheDocument();
    expect(within(definitions).getByText("1 definition")).toBeInTheDocument();
  });

  it("offers only published definitions when creating an instance", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "aaa-new-agent",
        version: 0,
        source: "draft",
        status: "draftOnly",
        displayName: "Aaa New Agent",
        draftCount: 1
      },
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner",
        draftCount: 0
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    await waitFor(() => {
      expect(screen.getByText("Draft")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("tab", { name: "Instances" }));
    fireEvent.click(screen.getByRole("button", { name: "New instance" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Examiner · examiner")).toBeInTheDocument();
    expect(within(dialog).queryByText(/aaa-new-agent/)).not.toBeInTheDocument();
    expect(within(dialog).getByText(/v1 · Built-in · Published/)).toBeInTheDocument();
  });

  it("confirms and removes a definition draft at its current revision", async () => {
    const draftId = "019944af-00d1-7000-8000-0000000000dd";
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Alex"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts)
      .mockResolvedValueOnce([
        {
          draftId,
          definitionId: "examiner",
          revision: 3,
          sourceKind: "ForkBuiltIn",
          sourceVersion: 1,
          updatedAt: "2026-01-01T00:00:00Z"
        }
      ])
      .mockResolvedValueOnce([]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(listAdminDraftResources).mockResolvedValue([]);
    vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 4,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-02T00:00:00Z",
      candidate: { systemInstructions: "Draft body", definitionId: "examiner" }
    });
    vi.mocked(deleteAdminDefinitionDraft).mockResolvedValue(undefined);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });
    fireEvent.click(await screen.findByRole("tab", { name: "Drafts" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Draft rev 3/ })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Draft rev 3/ }));
    await waitFor(() => {
      expect(screen.getByLabelText("Draft editor")).toBeInTheDocument();
    });

    fireEvent.click(within(screen.getByLabelText("Draft editor")).getByRole("button", { name: "Delete draft" }));
    expect(screen.getAllByText("Delete draft from v1?").length).toBeGreaterThan(0);
    fireEvent.click(screen.getAllByRole("button", { name: "Delete draft" }).at(-1)!);

    await waitFor(() => {
      expect(deleteAdminDefinitionDraft).toHaveBeenCalledWith(draftId, 4);
    });
    await waitFor(() => {
      expect(screen.queryByText("Existing drafts")).not.toBeInTheDocument();
    });
  // Full draft editor + confirmation rendering needs hosted-runner headroom.
  }, 60_000);

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
      expect(within(screen.getByRole("region", { name: "Definitions" })).getByText("Examiner")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("tab", { name: "Instances" }));
    expect(screen.getByText(/Instances unavailable/)).toBeInTheDocument();
    fireEvent.click(within(screen.getByRole("region", { name: "Instances" })).getByRole("button", { name: "Retry" }));
    await waitFor(() => {
      expect(screen.getByText("No instances yet.")).toBeInTheDocument();
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
    expect(within(identity).queryByText("Compatibility / legacy")).not.toBeInTheDocument();
    expect(within(identity).getByText("Definition status")).toBeInTheDocument();
    expect(screen.getByText("Practice speaking.")).toBeInTheDocument();
    expect(screen.getByText("heuristic")).toBeInTheDocument();
    expect(screen.getAllByText("scripted-alpha").length).toBeGreaterThan(0);
    expect(screen.getByText(/max registrations 4/)).toBeInTheDocument();
    const config = screen.getByText("Runtime model").closest(".admin-effective-config-grid");
    expect(config).toBeTruthy();
    expect(within(config as HTMLElement).queryByRole("button")).not.toBeInTheDocument();
    expect(within(config as HTMLElement).queryByRole("textbox")).not.toBeInTheDocument();
    expect(within(config as HTMLElement).queryByRole("combobox")).not.toBeInTheDocument();
    expect(within(config as HTMLElement).queryByRole("switch")).not.toBeInTheDocument();
  });

  it("saves visible instructions before publishing a draft", async () => {
    const draftId = "019944af-00d1-7000-8000-000000000099";
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([
      {
        draftId,
        definitionId: "examiner",
        revision: 2,
        sourceKind: "ForkBuiltIn",
        sourceVersion: 1,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(listAdminDraftResources).mockResolvedValue([]);
    vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 2,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z",
      candidate: { systemInstructions: "Stored body", definitionId: "examiner" }
    });
    vi.mocked(updateAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 3,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-02T00:00:00Z",
      candidate: { systemInstructions: "Visible publish body", definitionId: "examiner" }
    });
    vi.mocked(publishAdminDefinitionDraft).mockResolvedValue({
      definitionId: "examiner",
      version: 2,
      status: "published",
      metadataRevision: 1,
      publishedAt: "2026-01-02T00:00:00Z"
    });
    vi.mocked(validateAdminDefinitionDraft).mockResolvedValue({
      draftId,
      draftRevision: 3,
      configurationFingerprint: "fp-3",
      hasBlockingFindings: false,
      findings: []
    });
    vi.mocked(getAdminDefinitionDraftDiff).mockResolvedValue({
      draftId,
      draftRevision: 3,
      baselineKind: "ForkBuiltIn",
      baselineVersion: 1,
      sections: []
    });
    vi.mocked(listAdminDefinitionEvaluationScenarios).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionEvaluationResults).mockResolvedValue([]);
    useAppSpy = vi.spyOn(antd.App, "useApp").mockReturnValue({
      message: { success: vi.fn(), error: vi.fn(), warning: vi.fn() },
      modal: {
        confirm: (options: { onOk?: () => void | Promise<void> }) => {
          void options.onOk?.();
        }
      },
      notification: {}
    } as unknown as ReturnType<typeof antd.App.useApp>);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });
    fireEvent.click(await screen.findByRole("tab", { name: "Drafts" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Draft rev 2/ })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Draft rev 2/ }));
    await waitFor(() => {
      expect(screen.getByLabelText("System instructions")).toBeInTheDocument();
      expect(screen.getByRole("combobox", { name: "Language-model provider" })).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText("System instructions"), {
      target: { value: "Visible publish body" }
    });
    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalledWith(
        draftId,
        2,
        expect.objectContaining({ systemInstructions: "Visible publish body" })
      );
    });
    vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 3,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-02T00:00:00Z",
      candidate: { systemInstructions: "Visible publish body", definitionId: "examiner" }
    });
    fireEvent.click(screen.getByRole("tab", { name: "Test & Publish" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Run validation" })).not.toBeDisabled();
    });
    fireEvent.click(screen.getByRole("button", { name: "Run validation" }));
    await waitFor(() => {
      expect(validateAdminDefinitionDraft).toHaveBeenCalledWith(draftId);
      expect(screen.getByText("Draft is ready for final publish.")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("tab", { name: "Definition" }));
    fireEvent.click(screen.getByRole("tab", { name: "Test & Publish" }));
    expect(screen.getByText("Validation snapshot")).toBeInTheDocument();
    expect(screen.getByText("Draft is ready for final publish.")).toBeInTheDocument();
    expect(validateAdminDefinitionDraft).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("tab", { name: "Definition" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Publish…" })).not.toBeDisabled();
    });
    fireEvent.click(screen.getByRole("button", { name: "Publish…" }));
    await waitFor(() => {
      expect(publishAdminDefinitionDraft).toHaveBeenCalledWith(draftId, 3);
    });
  }, 60_000);

  it("preserves unsaved instructions after a resource mutation", async () => {
    const draftId = "019944af-00d1-7000-8000-000000000087";
    const resourceId = "019944af-00d1-7000-8000-0000000000ab";
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([
      {
        draftId,
        definitionId: "examiner",
        revision: 1,
        sourceKind: "ForkBuiltIn",
        sourceVersion: 1,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(listAdminDraftResources).mockResolvedValue([
      {
        resourceId,
        logicalPath: "knowledge/policy.md",
        kind: "Knowledge",
        mediaType: "text/plain",
        contentSha256: "abc123def456",
        byteLength: 12,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(getAdminDefinitionDraft)
      .mockResolvedValueOnce({
        draftId,
        definitionId: "examiner",
        revision: 1,
        sourceKind: "ForkBuiltIn",
        sourceVersion: 1,
        createdAt: "2026-01-01T00:00:00Z",
        updatedAt: "2026-01-01T00:00:00Z",
        candidate: { systemInstructions: "Stored body", definitionId: "examiner" }
      })
      .mockResolvedValue({
        draftId,
        definitionId: "examiner",
        revision: 2,
        sourceKind: "ForkBuiltIn",
        sourceVersion: 1,
        createdAt: "2026-01-01T00:00:00Z",
        updatedAt: "2026-01-02T00:00:00Z",
        candidate: { systemInstructions: "Stored body", definitionId: "examiner" }
      });
    vi.mocked(removeAdminDraftResource).mockResolvedValue(undefined);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });
    fireEvent.click(await screen.findByRole("tab", { name: "Drafts" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Draft rev 1/ })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Draft rev 1/ }));
    await waitFor(() => {
      expect(screen.getByLabelText("System instructions")).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText("System instructions"), {
      target: { value: "Unsaved instruction edit" }
    });
    fireEvent.click(screen.getByRole("tab", { name: "Resources" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Remove" })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => {
      expect(removeAdminDraftResource).toHaveBeenCalled();
    });
    fireEvent.click(screen.getByRole("tab", { name: "Definition" }));
    await waitFor(() => {
      expect(screen.getByLabelText("System instructions")).toHaveValue("Unsaved instruction edit");
    });
  // This journey renders both editor tabs and refreshes the resource revision.
  }, 60_000);

  it("ignores stale publication resource responses after definition changes", async () => {
    let resolveFirst: ((value: AdminDefinitionPublicationResource[]) => void) | undefined;
    const firstPending = new Promise<AdminDefinitionPublicationResource[]>((resolve) => {
      resolveFirst = resolve;
    });
    vi.mocked(listAdminPublicationResources).mockImplementation((definitionId) => {
      if (definitionId === "examiner") {
        return firstPending;
      }

      return Promise.resolve([
        {
          resourceId: "019944af-00d1-7000-8000-0000000000dd",
          logicalPath: "other/policy.md",
          kind: "Knowledge",
          mediaType: "text/plain",
          contentSha256: "fedcba987654",
          byteLength: 8
        }
      ]);
    });

    const { rerender } = render(<PublicationResourcesSummary definitionId="examiner" version={1} />);
    rerender(<PublicationResourcesSummary definitionId="customer-support" version={1} />);
    await waitFor(() => {
      expect(screen.getByText(/other\/policy\.md/)).toBeInTheDocument();
    });
    resolveFirst?.([
      {
        resourceId: "019944af-00d1-7000-8000-0000000000ee",
        logicalPath: "stale/policy.md",
        kind: "Knowledge",
        mediaType: "text/plain",
        contentSha256: "abc123def456",
        byteLength: 12
      }
    ]);
    await act(async () => {
      await Promise.resolve();
    });
    expect(screen.queryByText(/stale\/policy\.md/)).not.toBeInTheDocument();
    expect(screen.getByText(/other\/policy\.md/)).toBeInTheDocument();
  });

  it("shows publication resource load failures with retry", async () => {
    vi.mocked(listAdminPublicationResources)
      .mockRejectedValueOnce(new Error("Admin publication resources failed (503)"))
      .mockResolvedValueOnce([
        {
          resourceId: "019944af-00d1-7000-8000-0000000000cc",
          logicalPath: "knowledge/policy.md",
          kind: "Knowledge",
          mediaType: "text/plain",
          contentSha256: "abc123def4567890",
          byteLength: 12
        }
      ]);

    render(<PublicationResourcesSummary definitionId="examiner" version={2} />);
    await waitFor(() => {
      expect(screen.getByText(/Admin publication resources failed \(503\)/)).toBeInTheDocument();
    });
    expect(screen.queryByRole("button", { name: "Error details" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    await waitFor(() => {
      expect(screen.getByText(/knowledge\/policy\.md/)).toBeInTheDocument();
    });
  });

  it("shows the diagnostic id on a publication resource server failure", async () => {
    const failure = new Error("The request could not be completed.");
    failure.name = "AdminRequestError";
    Object.assign(failure, { diagnosticId: "019944af-0008-7000-8000-0000000000e1" });
    vi.mocked(listAdminPublicationResources).mockRejectedValueOnce(failure);

    render(<PublicationResourcesSummary definitionId="examiner" version={2} />);
    await waitFor(() => {
      expect(screen.getByText("The request could not be completed.")).toBeInTheDocument();
    });
    expect(screen.getByRole("button", { name: "Error details" })).toBeInTheDocument();
  });

  it("shows draft resources tab when a draft is open", async () => {
    const draftId = "019944af-00d1-7000-8000-000000000088";
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([
      {
        draftId,
        definitionId: "examiner",
        revision: 1,
        sourceKind: "ForkBuiltIn",
        sourceVersion: 1,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(getAdminToolRegistry).mockResolvedValue({ toolNames: ["workspace.read"], maxToolAllowlistEntries: 32 });
    vi.mocked(listAdminDraftResources).mockResolvedValue([
      {
        resourceId: "019944af-00d1-7000-8000-0000000000aa",
        logicalPath: "knowledge/policy.md",
        kind: "Knowledge",
        mediaType: "text/plain",
        contentSha256: "abc123def456",
        byteLength: 12,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 1,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z",
      candidate: { systemInstructions: "Body", definitionId: "examiner" }
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });
    fireEvent.click(await screen.findByRole("tab", { name: "Drafts" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Draft rev 1/ })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Draft rev 1/ }));
    await waitFor(() => {
      expect(screen.getByRole("tab", { name: "Resources" })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("tab", { name: "Resources" }));
    await waitFor(() => {
      expect(screen.getByText(/knowledge\/policy\.md/)).toBeInTheDocument();
    });
  });

  it("saves typed capabilities through the draft editor", async () => {
    const draftId = "019944af-00d1-7000-8000-0000000000bb";
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([
      {
        draftId,
        definitionId: "examiner",
        revision: 1,
        sourceKind: "ForkBuiltIn",
        sourceVersion: 1,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(listAdminDraftResources).mockResolvedValue([]);
    vi.mocked(getAdminToolRegistry).mockResolvedValue({ toolNames: ["workspace.read", "knowledge.retrieve"], maxToolAllowlistEntries: 32 });
    vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 1,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z",
      candidate: {
        systemInstructions: "Body",
        definitionId: "examiner",
        environment: { harness: ["examiner-turn-taking"], toolAllowlist: [], workspace: {} }
      }
    });
    vi.mocked(updateAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 2,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-02T00:00:00Z",
      candidate: { systemInstructions: "Body", definitionId: "examiner" }
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });
    fireEvent.click(await screen.findByRole("tab", { name: "Drafts" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Draft rev 1/ })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Draft rev 1/ }));
    await waitFor(() => {
      expect(screen.getByRole("tab", { name: "Capabilities" })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("tab", { name: "Capabilities" }));
    fireEvent.change(screen.getByLabelText("Workspace template id"), {
      target: { value: "examiner-default" }
    });
    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalled();
    });
    const candidate = vi.mocked(updateAdminDefinitionDraft).mock.calls.at(-1)?.[2] as {
      environment?: { workspace?: { templateId?: string } };
    };
    expect(candidate?.environment?.workspace?.templateId).toBe("examiner-default");
  });

  it("binds a knowledge source to a Knowledge resource path", async () => {
    const draftId = "019944af-00d1-7000-8000-0000000000bc";
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([
      {
        draftId,
        definitionId: "examiner",
        revision: 1,
        sourceKind: "ForkBuiltIn",
        sourceVersion: 1,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(listAdminDraftResources).mockResolvedValue([
      {
        resourceId: "019944af-00d1-7000-8000-0000000000bd",
        logicalPath: "knowledge/refund-policy.md",
        kind: "Knowledge",
        mediaType: "text/markdown",
        contentSha256: "abc",
        byteLength: 12,
        updatedAt: "2026-01-01T00:00:00Z"
      },
      {
        resourceId: "019944af-00d1-7000-8000-0000000000be",
        logicalPath: "references/notes.md",
        kind: "Reference",
        mediaType: "text/plain",
        contentSha256: "def",
        byteLength: 8,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 1,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z",
      candidate: {
        systemInstructions: "Body",
        definitionId: "examiner",
        environment: {
          knowledgeSources: [{ identity: "policy", title: "Policy", citation: "policy@demo" }]
        }
      }
    });
    vi.mocked(updateAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 2,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-02T00:00:00Z",
      candidate: { systemInstructions: "Body", definitionId: "examiner" }
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });
    fireEvent.click(await screen.findByRole("tab", { name: "Drafts" }));
    fireEvent.click(await screen.findByRole("button", { name: /Draft rev 1/ }));
    fireEvent.click(await screen.findByRole("tab", { name: "Capabilities" }));
    expect((await screen.findAllByText("Legacy fallback: knowledge/policy")).length).toBeGreaterThan(0);
    fireEvent.mouseDown(screen.getByLabelText("Knowledge resource 1"));
    const option = await waitFor(() => {
      const match = document.querySelector(
        '.ant-select-item-option[title="knowledge/refund-policy.md"]'
      );
      expect(match).toBeTruthy();
      return match as HTMLElement;
    });
    expect(document.querySelector('.ant-select-item-option[title="references/notes.md"]')).toBeNull();
    fireEvent.mouseDown(option);
    fireEvent.click(option);
    expect((await screen.findAllByText("Backing resource: knowledge/refund-policy.md")).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Save draft" }));
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalled();
    });
    const saved = vi.mocked(updateAdminDefinitionDraft).mock.calls.at(-1)?.[2] as {
      environment?: { knowledgeSources?: Array<{ resourcePath?: string }> };
    };
    expect(saved.environment?.knowledgeSources?.[0]?.resourcePath).toBe("knowledge/refund-policy.md");
  });

  it("retries tool registry loading from the Capabilities tab", async () => {
    const draftId = "019944af-00d1-7000-8000-0000000000cc";
    vi.mocked(getAdminToolRegistry).mockReset();
    let toolRegistryAttempts = 0;
    vi.mocked(getAdminToolRegistry).mockImplementation(async () => {
      toolRegistryAttempts += 1;
      if (toolRegistryAttempts === 1) {
        throw new Error("Registry unavailable");
      }
      return { toolNames: ["workspace.read", "knowledge.retrieve"], maxToolAllowlistEntries: 32 };
    });
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([
      {
        draftId,
        definitionId: "examiner",
        revision: 1,
        sourceKind: "ForkBuiltIn",
        sourceVersion: 1,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(listAdminDraftResources).mockResolvedValue([]);
    vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "examiner",
      revision: 1,
      sourceKind: "ForkBuiltIn",
      sourceVersion: 1,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z",
      candidate: { systemInstructions: "Body", definitionId: "examiner" }
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });
    fireEvent.click(await screen.findByRole("tab", { name: "Drafts" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Draft rev 1/ })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Draft rev 1/ }));
    await waitFor(() => {
      expect(screen.getByRole("tab", { name: "Capabilities" })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("tab", { name: "Capabilities" }));
    await waitFor(() => {
      expect(screen.getByText("Registry unavailable")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: "Retry tool registry" }));
    await waitFor(() => {
      expect(toolRegistryAttempts).toBe(2);
    });
    expect(screen.queryByText("Registry unavailable")).not.toBeInTheDocument();
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
    expect(screen.queryByText("Compatibility / legacy")).not.toBeInTheDocument();
    expect(screen.getAllByText("Active").length).toBeGreaterThan(0);
    expect(screen.getByText("Practice speaking.")).toBeInTheDocument();
  });

  it.each(["success", "failure"])("ignores a late effective configuration %s after instance navigation", async outcome => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    let resolve!: (value: AdminEffectiveConfiguration) => void;
    let reject!: (error: Error) => void;
    const delayed = new Promise<AdminEffectiveConfiguration>((yes, no) => { resolve = yes; reject = no; });
    const other = { ...sampleEffective, instanceId: "other-instance", persona: { ...sampleEffective.persona, name: "Beta" } };
    vi.mocked(getAdminEffectiveConfig).mockImplementation(id => id === instanceId ? delayed : Promise.resolve(other));
    const view = render(<AdminApp route={{ area: "admin", view: "instance", instanceId }} />);
    await waitFor(() => expect(getAdminEffectiveConfig).toHaveBeenCalledWith(instanceId));
    view.rerender(<AdminApp route={{ area: "admin", view: "instance", instanceId: other.instanceId }} />);
    expect(await screen.findByRole("heading", { name: "Beta" })).toBeInTheDocument();
    await act(async () => { if (outcome === "success") resolve(sampleEffective); else reject(new Error("Stale instance error")); });
    expect(screen.getByRole("heading", { name: "Beta" })).toBeInTheDocument();
    expect(screen.queryByText("Stale instance error")).not.toBeInTheDocument();
  });

  it("keeps persona edits after unrelated instance revisions and accepts a newer saved persona", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    const config = { ...sampleEffective };
    const props = { onUpdated: vi.fn(), onDeleted: vi.fn() };
    let view!: ReturnType<typeof render>;
    await act(async () => { view = render(<InstanceManagedControls config={config} {...props} />); });
    fireEvent.change(screen.getByLabelText("Persona name"), { target: { value: "Unsaved name" } });
    view.rerender(<InstanceManagedControls config={{ ...config, instanceRevision: 2 }} {...props} />);
    expect(screen.getByLabelText("Persona name")).toHaveValue("Unsaved name");
    view.rerender(<InstanceManagedControls config={{ ...config, instanceRevision: 3, instanceLifecycle: "Archived" }} {...props} />);
    expect(screen.getByLabelText("Persona name")).toHaveValue("Unsaved name");
    view.rerender(<InstanceManagedControls config={{ ...config, instanceRevision: 4, definitionVersion: 2 }} {...props} />);
    expect(screen.getByLabelText("Persona name")).toHaveValue("Unsaved name");
    fireEvent.click(screen.getByRole("tab", { name: "JSON" }));
    fireEvent.change(screen.getByLabelText("Persona JSON"), { target: { value: "{invalid json" } });
    view.rerender(<InstanceManagedControls config={{ ...config, instanceRevision: 5, definitionVersion: 3 }} {...props} />);
    expect(screen.getByLabelText("Persona JSON")).toHaveValue("{invalid json");
    view.rerender(<InstanceManagedControls config={{ ...config, instanceRevision: 3, personaRevision: 2,
      persona: { ...config.persona, name: "Saved name" } }} {...props} />);
    expect((screen.getByLabelText("Persona JSON") as HTMLTextAreaElement).value).toContain("Saved name");
  });

  it("saves managed instance persona from the form tab", async () => {
    const managedEffective = { ...sampleEffective };
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(managedEffective);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        status: "published",
        metadataRevision: 1,
        publishedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(updateAdminAgentInstancePersona).mockResolvedValue({
      instanceId: managedEffective.instanceId,
      definitionId: "examiner",
      activeVersion: 1,
      lifecycle: "Active",
      revision: 2,
      personaRevision: 2
    });

    await act(async () => {
      render(<antd.App><InstanceManagedControls config={managedEffective} onUpdated={vi.fn()} onDeleted={vi.fn()} /></antd.App>);
    });

    await waitFor(() => {
      expect(screen.getByLabelText("Managed instance controls")).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText("Persona role"), { target: { value: "Coach" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save persona" })); });

    await waitFor(() => {
      expect(updateAdminAgentInstancePersona).toHaveBeenCalledWith(managedEffective.instanceId, {
        expectedRevision: 1,
        expectedPersonaRevision: 1,
        name: "Alex",
        role: "Coach",
        description: "Practice speaking.",
        tone: "Supportive"
      });
    });
  });

  it("syncs persona form edits into the JSON tab before save", async () => {
    const managedEffective = { ...sampleEffective };
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(managedEffective);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);

    await act(async () => {
      render(<antd.App><InstanceManagedControls config={managedEffective} onUpdated={vi.fn()} onDeleted={vi.fn()} /></antd.App>);
    });

    await waitFor(() => {
      expect(screen.getByLabelText("Persona role")).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText("Persona role"), { target: { value: "Coach" } });
    fireEvent.click(screen.getByRole("tab", { name: "JSON" }));

    await waitFor(() => {
      const json = screen.getByLabelText("Persona JSON") as HTMLTextAreaElement;
      expect(json.value).toContain('"role": "Coach"');
    });

    fireEvent.click(screen.getByRole("tab", { name: "Form" }));
    expect(screen.getByLabelText("Persona role")).toHaveValue("Coach");
  });

  it("does not save persona JSON that includes non-persona fields", async () => {
    const managedEffective = { ...sampleEffective };
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(managedEffective);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(updateAdminAgentInstancePersona).mockClear();

    await act(async () => {
      render(<antd.App><InstanceManagedControls config={managedEffective} onUpdated={vi.fn()} onDeleted={vi.fn()} /></antd.App>);
    });

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: "JSON" })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("tab", { name: "JSON" }));
    fireEvent.change(screen.getByLabelText("Persona JSON"), {
      target: {
        value: JSON.stringify(
          {
            name: "Alex",
            role: "Examiner",
            description: "Practice speaking.",
            tone: "Supportive",
            lifecycle: "Archived",
            activeVersion: 9,
            memory: { sessionMemory: false },
            triggers: [],
            instanceRevision: 3,
            effectiveConfiguration: { definitionId: "other" }
          },
          null,
          2
        )
      }
    });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save persona" })); });

    await waitFor(() => {
      expect(screen.getAllByText(/unknown fields/).length).toBeGreaterThan(0);
    });
    expect(updateAdminAgentInstancePersona).not.toHaveBeenCalled();
  });

  it("lists built-in and durable versions for active-version changes", async () => {
    const managedEffective = { ...sampleEffective, definitionVersion: 2 };
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner v1"
      },
      {
        definitionId: "examiner",
        version: 2,
        source: "builtIn",
        status: "published",
        displayName: "Examiner v2"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(managedEffective);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 3,
        status: "published",
        metadataRevision: 1,
        publishedAt: "2026-01-01T00:00:00Z"
      }
    ]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "instance", instanceId }} />);
    });

    await waitFor(() => {
      expect(screen.getByLabelText("Target definition version")).toBeInTheDocument();
    });

    fireEvent.mouseDown(screen.getByLabelText("Target definition version"));
    await waitFor(() => {
      expect(screen.getByText("v1 (builtIn · published)")).toBeInTheDocument();
      expect(screen.getByText("v3 (published)")).toBeInTheDocument();
    });
  });

  it("confirms archive while keeping unsaved persona form edits", async () => {
    const managedEffective = { ...sampleEffective };
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(managedEffective);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(updateAdminAgentInstanceLifecycle).mockResolvedValue({
      instanceId: managedEffective.instanceId,
      definitionId: "examiner",
      activeVersion: 1,
      lifecycle: "Archived",
      revision: 2,
      personaRevision: 1
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "instance", instanceId }} />);
    });

    await waitFor(() => {
      expect(screen.getByLabelText("Persona role")).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText("Persona role"), { target: { value: "Coach" } });
    const archiveButton = screen.getByRole("button", { name: "Archive instance" });
    fireEvent.click(archiveButton);

    await waitFor(
      () => {
        expect(screen.getByText("Unsaved persona edits stay in this editor across lifecycle and version changes until you save or leave this instance.")).toBeInTheDocument();
      },
      { timeout: 10_000 }
    );

    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    expect(updateAdminAgentInstanceLifecycle).not.toHaveBeenCalled();

    fireEvent.click(archiveButton);
    const confirmDialog = screen.getAllByRole("dialog").at(-1)!;
    fireEvent.click(within(confirmDialog).getByRole("button", { name: "Archive" }));

    await waitFor(
      () => {
        expect(updateAdminAgentInstanceLifecycle).toHaveBeenCalledWith(
          managedEffective.instanceId,
          1,
          "Archived"
        );
      },
      { timeout: 10_000 }
    );
  }, 15_000);

  it("keeps built-in and durable fork sources distinct when version numbers overlap", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      { definitionId: "examiner", version: 2, source: "builtIn", status: "published", displayName: "Built-in" },
      { definitionId: "examiner", version: 2, source: "durable", status: "published", displayName: "Durable" }
    ]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(forkAdminDefinitionDraft).mockRejectedValue(new Error("Fixture fork refused"));
    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });
    fireEvent.mouseDown(await screen.findByLabelText("Base version"));
    fireEvent.click(await screen.findByText("v2 · Durable · Published"));
    fireEvent.click(screen.getByRole("button", { name: "Fork v2 (durable)" }));
    await waitFor(() => expect(forkAdminDefinitionDraft).toHaveBeenCalledWith("examiner", 2, "ForkDurable"));
    await screen.findByText("Fixture fork refused");
    fireEvent.mouseDown(screen.getByLabelText("Base version"));
    fireEvent.click(await screen.findByText("v2 · Built-in · Published"));
    fireEvent.click(screen.getByRole("button", { name: "Fork v2 (builtIn)" }));
    await waitFor(() => expect(forkAdminDefinitionDraft).toHaveBeenCalledWith("examiner", 2, "ForkBuiltIn"));
  });

  it("shows one effective published version per number when creating an instance", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      { definitionId: "examiner", version: 2, source: "durable", status: "deprecated", displayName: "Durable" },
      { definitionId: "examiner", version: 2, source: "builtIn", status: "published", displayName: "Built-in" }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    fireEvent.click(screen.getByRole("tab", { name: "Instances" }));
    fireEvent.click(await screen.findByRole("button", { name: "New instance" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("New instance", { exact: true })).toBeInTheDocument();
    fireEvent.mouseDown(within(dialog).getByLabelText("Published version"));
    await waitFor(() => {
      const options = document.querySelectorAll(".ant-select-item-option-content");
      expect([...options].map(option => option.textContent)).toEqual(["v2 · Built-in · Published"]);
    });
  });

  it("defaults fork source to the highest non-deprecated version on definition detail", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 3,
        source: "durable",
        status: "deprecated",
        displayName: "Examiner v3"
      },
      {
        definitionId: "examiner",
        version: 2,
        source: "durable",
        status: "published",
        displayName: "Examiner v2"
      }
    ]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(listAdminPublicationResources).mockResolvedValue([]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Fork v2 (durable)" })).toBeInTheDocument();
    });
    fireEvent.mouseDown(screen.getByLabelText("Base version"));
    await waitFor(() => {
      expect(screen.getByText("v2 · Durable · Published")).toBeInTheDocument();
      expect(screen.getByText("v3 · Durable · Deprecated")).toBeInTheDocument();
    });
  });

  it("deprecates an active durable publication from the definition detail view", async () => {
    let inventoryLoads = 0;
    vi.mocked(listAdminDefinitions).mockImplementation(async () => {
      inventoryLoads += 1;
      return [
        {
          definitionId: "examiner",
          version: 2,
          source: "durable",
          status: inventoryLoads > 1 ? "deprecated" : "published",
          displayName: "Examiner v2"
        }
      ];
    });
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([]);
    vi.mocked(listAdminPublicationResources).mockResolvedValue([]);
    let publicationLoads = 0;
    vi.mocked(listAdminDefinitionPublications).mockImplementation(async () => {
      publicationLoads += 1;
      return [
        {
          definitionId: "examiner",
          version: 2,
          status: publicationLoads > 1 ? "Deprecated" : "Active",
          metadataRevision: publicationLoads > 1 ? 4 : 3,
          publishedAt: "2026-01-02T00:00:00Z"
        }
      ];
    });
    vi.mocked(deprecateAdminDefinitionPublication).mockResolvedValue({
      definitionId: "examiner",
      version: 2,
      status: "Deprecated",
      metadataRevision: 4,
      publishedAt: "2026-01-02T00:00:00Z"
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Deprecate publication v2" })).toBeInTheDocument();
    });
    expect(screen.getByRole("button", { name: "Start managed chat for v2" })).toHaveTextContent("Chat");
    const versionRow = screen.getByRole("button", { name: "View v2 (durable)" }).closest("tr")!;
    expect(within(versionRow).getByText("Durable")).toBeInTheDocument();
    expect(within(versionRow).getByText("Published")).toBeInTheDocument();

    const inventoryCallsBefore = vi.mocked(listAdminDefinitions).mock.calls.length;
    fireEvent.click(screen.getByRole("button", { name: "Deprecate publication v2" }));
    const deprecateDialog = await screen.findByRole("dialog");
    expect(deprecateDialog).toHaveTextContent("Deprecate publication v2?");
    fireEvent.click(within(deprecateDialog).getByRole("button", { name: /^Deprecate$/ }));

    await waitFor(() => {
      expect(deprecateAdminDefinitionPublication).toHaveBeenCalledWith("examiner", 2, 3);
    });
    await waitFor(() => {
      expect(screen.getByText("Latest v2 · Deprecated")).toBeInTheDocument();
      expect(within(screen.getByRole("button", { name: "View v2 (durable)" }).closest("tr")!).getByText("4")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Start managed chat for v2" })).toBeDisabled();
      expect(screen.queryByRole("button", { name: "Deprecate publication v2" })).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(vi.mocked(listAdminDefinitions).mock.calls.length).toBeGreaterThan(inventoryCallsBefore);
    });
  });

  it("applies a version while retaining dirty persona JSON", async () => {
    const managedEffective = { ...sampleEffective, definitionVersion: 1 };
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 1,
        source: "builtIn",
        status: "published",
        displayName: "Examiner v1"
      },
      {
        definitionId: "examiner",
        version: 2,
        source: "builtIn",
        status: "published",
        displayName: "Examiner v2"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(managedEffective);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(updateAdminAgentInstanceActiveVersion).mockResolvedValue({
      instanceId: managedEffective.instanceId,
      definitionId: "examiner",
      activeVersion: 2,
      lifecycle: "Active",
      revision: 2,
      personaRevision: 1
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "instance", instanceId }} />);
    });

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: "JSON" })).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("tab", { name: "JSON" }));
    await waitFor(() => {
      expect(screen.getByLabelText("Persona JSON")).toBeInTheDocument();
    });
    fireEvent.change(screen.getByLabelText("Persona JSON"), {
      target: { value: '{"name":"Alex","role":"Coach","description":"Practice speaking.","tone":"Supportive"}' }
    });
    fireEvent.mouseDown(screen.getByLabelText("Target definition version"));
    await waitFor(() => {
      expect(screen.getByText("v2 (builtIn · published)")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByText("v2 (builtIn · published)"));
    fireEvent.click(screen.getByRole("button", { name: "Upgrade to v2" }));

    expect(screen.queryByRole("button", { name: "Apply anyway" })).not.toBeInTheDocument();

    await waitFor(() => {
      expect(updateAdminAgentInstanceActiveVersion).toHaveBeenCalledWith(managedEffective.instanceId, 1, 2);
    });
  });

  it("creates a new definition through the starter endpoint", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(createNewAdminDefinitionDraft).mockResolvedValue({
      draftId: "019944af-00d1-7000-8000-0000000000d1",
      definitionId: "field-guide",
      revision: 1,
      sourceKind: "New",
      sourceVersion: null,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z",
      candidate: { definitionId: "field-guide", systemInstructions: "Server starter" }
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    fireEvent.click(screen.getByRole("button", { name: "New definition" }));
    expect(screen.getByText("Lowercase letters, digits, and hyphens, up to 64 characters.")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Definition ID"), { target: { value: "Field Guide" } });
    expect(screen.getByRole("button", { name: "Create draft" })).toBeDisabled();
    expect(screen.getByLabelText("Definition ID")).toHaveAttribute("aria-invalid", "true");
    fireEvent.change(screen.getByLabelText("Definition ID"), { target: { value: "field-guide" } });
    expect(screen.getByLabelText("Definition ID")).toHaveAttribute("aria-invalid", "false");
    fireEvent.click(screen.getByRole("button", { name: "Create draft" }));

    await waitFor(() => {
      expect(createNewAdminDefinitionDraft).toHaveBeenCalledWith("field-guide");
    });
    expect(createNewAdminDefinitionDraft).toHaveBeenCalledTimes(1);
  });

  it("creates a managed instance from the latest active version without a custom persona", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 6,
        source: "durable",
        status: "deprecated",
        displayName: "Examiner v6"
      },
      {
        definitionId: "examiner",
        version: 5,
        source: "durable",
        status: "published",
        displayName: "Examiner v5"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(createAdminAgentInstance).mockResolvedValue({
      instanceId,
      definitionId: "examiner",
      activeVersion: 5,
      lifecycle: "Active",
      revision: 1,
      personaRevision: 1
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    fireEvent.click(screen.getByRole("tab", { name: "Instances" }));
    fireEvent.click(screen.getByRole("button", { name: "New instance" }));
    expect(screen.getByText("v5 · Durable · Published")).toBeInTheDocument();
    expect(screen.queryByText(/is deprecated/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByText("Harness management (optional)"));
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Authoring mode" }));
    fireEvent.click(await screen.findByText("Managed", { selector: ".ant-select-item-option-content" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Knowledge & resources" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Skills" }));
    expect(screen.getByRole("button", { name: "Create instance" })).toBeDisabled();
    await waitFor(() => expect(screen.getByText("Select at least one area.")).toBeVisible());
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Authoring mode" }));
    fireEvent.click(await screen.findByText("Manual (off)", { selector: ".ant-select-item-option-content" }));
    expect(screen.getByRole("button", { name: "Create instance" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Create instance" }));

    await waitFor(() => {
      expect(createAdminAgentInstance).toHaveBeenCalledWith("examiner", 5, null);
    });
    expect(createAdminAgentInstance).toHaveBeenCalledTimes(1);
  });

  it("warns on a deprecated version and sends a custom persona", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 6,
        source: "durable",
        status: "deprecated",
        displayName: "Examiner v6"
      },
      {
        definitionId: "examiner",
        version: 5,
        source: "durable",
        status: "published",
        displayName: "Examiner v5"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(createAdminAgentInstance).mockResolvedValue({
      instanceId,
      definitionId: "examiner",
      activeVersion: 6,
      lifecycle: "Active",
      revision: 1,
      personaRevision: 1,
      persona: { name: "Casey", role: "Guide", description: "A field guide.", tone: "Direct" }
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "home" }} />);
    });
    fireEvent.click(screen.getByRole("tab", { name: "Instances" }));
    fireEvent.click(screen.getByRole("button", { name: "New instance" }));
    fireEvent.mouseDown(screen.getByLabelText("Published version"));
    fireEvent.click(await screen.findByText("v6 · Durable · Deprecated"));
    expect(
      screen.getByText("v6 is deprecated. New instances normally use the latest active publication.")
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("radio", { name: "Custom persona" }));
    fireEvent.change(screen.getByLabelText("Persona name"), { target: { value: "Casey" } });
    fireEvent.change(screen.getByLabelText("Persona role"), { target: { value: "Guide" } });
    fireEvent.change(screen.getByLabelText("Persona description"), { target: { value: "A field guide." } });
    fireEvent.change(screen.getByLabelText("Persona tone"), { target: { value: "Direct" } });
    fireEvent.click(screen.getByRole("button", { name: "Create instance" }));

    await waitFor(() => {
      expect(createAdminAgentInstance).toHaveBeenCalledWith("examiner", 6, {
        name: "Casey",
        role: "Guide",
        description: "A field guide.",
        tone: "Direct"
      });
    });
  });

  it("opens a stored starter draft when the definition has no published version", async () => {
    const draftId = "019944af-00d1-7000-8000-0000000000d2";
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([
      {
        draftId,
        definitionId: "field-guide",
        revision: 1,
        sourceKind: "New",
        sourceVersion: null,
        updatedAt: "2026-01-01T00:00:00Z"
      }
    ]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(listAdminDraftResources).mockResolvedValue([]);
    vi.mocked(getAdminDefinitionDraft).mockResolvedValue({
      draftId,
      definitionId: "field-guide",
      revision: 1,
      sourceKind: "New",
      sourceVersion: null,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z",
      candidate: {
        definitionId: "field-guide",
        systemInstructions: "Stored starter instructions"
      }
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "field-guide" }} />);
    });

    await waitFor(() => {
      expect(screen.getByLabelText("System instructions")).toHaveValue("Stored starter instructions");
    });
    expect(getAdminDefinitionDraft).toHaveBeenCalledWith(draftId);
  });

  it("shows latest and latest active when the newest publication is deprecated", async () => {
    vi.mocked(listAdminDefinitions).mockResolvedValue([
      {
        definitionId: "examiner",
        version: 6,
        source: "durable",
        status: "deprecated",
        displayName: "Examiner v6"
      },
      {
        definitionId: "examiner",
        version: 5,
        source: "durable",
        status: "published",
        displayName: "Examiner v5"
      }
    ]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionDrafts).mockResolvedValue([]);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "definition", definitionId: "examiner" }} />);
    });

    await waitFor(() => {
      expect(screen.getByText("Latest v6 · Deprecated · Latest active v5")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Fork v5 (durable)" })).toBeInTheDocument();
    });
    expect(screen.getByText("Latest active")).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByLabelText("Base version"));
    await waitFor(() => {
      expect(screen.getByText("v6 · Durable · Deprecated")).toBeInTheDocument();
      expect(screen.getByText("v5 · Durable · Published")).toBeInTheDocument();
    });
  });
});
