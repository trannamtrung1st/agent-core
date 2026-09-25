import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { OwnerCapabilityError } from "../../services/api";
import { AdminApp, EffectiveConfigView, PublicationResourcesSummary } from "./AdminApp";
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
  getAdminEffectiveConfig: vi.fn(),
  listAdminDefinitionDrafts: vi.fn(),
  listAdminDefinitionPublications: vi.fn(),
  getAdminDefinitionDraft: vi.fn(),
  updateAdminDefinitionDraft: vi.fn(),
  publishAdminDefinitionDraft: vi.fn(),
  forkAdminDefinitionDraft: vi.fn(),
  listAdminDraftResources: vi.fn(),
  listAdminPublicationResources: vi.fn(),
  listAdminToolNames: vi.fn().mockResolvedValue(["workspace.read"]),
  uploadAdminDraftResourceContent: vi.fn(),
  upsertAdminDraftResource: vi.fn(),
  removeAdminDraftResource: vi.fn(),
  updateAdminAgentInstancePersona: vi.fn(),
  updateAdminAgentInstanceLifecycle: vi.fn(),
  updateAdminAgentInstanceActiveVersion: vi.fn(),
  validateAdminDefinitionDraft: vi.fn(),
  getAdminDefinitionDraftDiff: vi.fn(),
  listAdminDefinitionEvaluationScenarios: vi.fn(),
  listAdminDefinitionEvaluationResults: vi.fn()
}));

import * as antd from "antd";
import {
  getAdminDefinitionDraft,
  getAdminEffectiveConfig,
  listAdminDefinitionDrafts,
  listAdminDefinitionPublications,
  listAdminDefinitions,
  listAdminDraftResources,
  listAdminPublicationResources,
  listAdminToolNames,
  listAdminInstances,
  removeAdminDraftResource,
  publishAdminDefinitionDraft,
  updateAdminDefinitionDraft,
  updateAdminAgentInstanceActiveVersion,
  updateAdminAgentInstanceLifecycle,
  updateAdminAgentInstancePersona,
  validateAdminDefinitionDraft,
  getAdminDefinitionDraftDiff,
  listAdminDefinitionEvaluationScenarios,
  listAdminDefinitionEvaluationResults
} from "../../services/adminApi";

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
    vi.spyOn(antd.App, "useApp").mockReturnValue({
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
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /Draft rev 2/ })).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole("button", { name: /Draft rev 2/ }));
    await waitFor(() => {
      expect(screen.getByLabelText("System instructions")).toBeInTheDocument();
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
    });
    fireEvent.click(screen.getByRole("tab", { name: "Instructions" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Publish…" })).not.toBeDisabled();
    });
    fireEvent.click(screen.getByRole("button", { name: "Publish…" }));
    await waitFor(() => {
      expect(publishAdminDefinitionDraft).toHaveBeenCalledWith(draftId, 3);
    });
  });

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
    fireEvent.click(screen.getByRole("tab", { name: "Instructions" }));
    await waitFor(() => {
      expect(screen.getByLabelText("System instructions")).toHaveValue("Unsaved instruction edit");
    });
  });

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
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    await waitFor(() => {
      expect(screen.getByText(/knowledge\/policy\.md/)).toBeInTheDocument();
    });
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
    vi.mocked(listAdminToolNames).mockResolvedValue(["workspace.read"]);
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
    vi.mocked(listAdminToolNames).mockResolvedValue(["workspace.read", "knowledge.retrieve"]);
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

  it("retries tool registry loading from the Capabilities tab", async () => {
    const draftId = "019944af-00d1-7000-8000-0000000000cc";
    vi.mocked(listAdminToolNames).mockReset();
    let toolRegistryAttempts = 0;
    vi.mocked(listAdminToolNames).mockImplementation(async () => {
      toolRegistryAttempts += 1;
      if (toolRegistryAttempts === 1) {
        throw new Error("Registry unavailable");
      }
      return ["workspace.read", "knowledge.retrieve"];
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
    expect(within(screen.getByLabelText("Instance identity")).getByText("Compatibility / legacy")).toBeInTheDocument();
    expect(screen.getAllByText("Active").length).toBeGreaterThan(0);
    expect(screen.getByText("Practice speaking.")).toBeInTheDocument();
  });

  it("saves managed instance persona from the form tab", async () => {
    const managedEffective = { ...sampleEffective, compatibility: false };
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
      compatibility: false,
      lifecycle: "Active",
      revision: 2,
      personaRevision: 2
    });

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "instance", instanceId }} />);
    });

    await waitFor(() => {
      expect(screen.getByLabelText("Managed instance controls")).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText("Persona role"), { target: { value: "Coach" } });
    fireEvent.click(screen.getByRole("button", { name: "Save persona" }));

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
    const managedEffective = { ...sampleEffective, compatibility: false };
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(managedEffective);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);

    await act(async () => {
      render(<AdminApp route={{ area: "admin", view: "instance", instanceId }} />);
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

  it("lists built-in and durable versions for active-version changes", async () => {
    const managedEffective = { ...sampleEffective, compatibility: false, definitionVersion: 2 };
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

  it("warns before archive when persona form has unsaved edits", async () => {
    const managedEffective = { ...sampleEffective, compatibility: false };
    vi.mocked(listAdminDefinitions).mockResolvedValue([]);
    vi.mocked(listAdminInstances).mockResolvedValue([]);
    vi.mocked(getAdminEffectiveConfig).mockResolvedValue(managedEffective);
    vi.mocked(listAdminDefinitionPublications).mockResolvedValue([]);
    vi.mocked(updateAdminAgentInstanceLifecycle).mockResolvedValue({
      instanceId: managedEffective.instanceId,
      definitionId: "examiner",
      activeVersion: 1,
      compatibility: false,
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
    fireEvent.click(screen.getByRole("button", { name: "Archive instance" }));

    await waitFor(() => {
      expect(screen.getByText(/Unsaved persona changes will be discarded/)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(updateAdminAgentInstanceLifecycle).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Archive instance" }));
    const confirmButtons = screen.getAllByRole("button", { name: "Archive" });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]!);

    await waitFor(() => {
      expect(updateAdminAgentInstanceLifecycle).toHaveBeenCalledWith(
        managedEffective.instanceId,
        1,
        "Archived"
      );
    });
  });

  it("warns before apply version when JSON persona draft is dirty", async () => {
    const managedEffective = { ...sampleEffective, compatibility: false, definitionVersion: 1 };
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
      compatibility: false,
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
    fireEvent.click(screen.getByRole("button", { name: "Apply version" }));

    await waitFor(() => {
      expect(screen.getByText(/Unsaved persona changes will be discarded/)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: "Apply anyway" }));

    await waitFor(() => {
      expect(updateAdminAgentInstanceActiveVersion).toHaveBeenCalledWith(managedEffective.instanceId, 1, 2);
    });
  });
});
