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
  uploadAdminDraftResourceContent: vi.fn(),
  upsertAdminDraftResource: vi.fn(),
  removeAdminDraftResource: vi.fn()
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
  listAdminInstances,
  removeAdminDraftResource,
  publishAdminDefinitionDraft,
  updateAdminDefinitionDraft
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
    fireEvent.click(screen.getByRole("button", { name: "Publish…" }));
    await waitFor(() => {
      expect(updateAdminDefinitionDraft).toHaveBeenCalledWith(
        draftId,
        2,
        expect.objectContaining({ systemInstructions: "Visible publish body" })
      );
    });
    expect(publishAdminDefinitionDraft).toHaveBeenCalledWith(draftId, 3);
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
