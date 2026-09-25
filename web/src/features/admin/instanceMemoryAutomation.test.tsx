import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App } from "antd";
import { describe, expect, it, vi } from "vitest";
import type { AdminEffectiveConfiguration } from "../../services/adminApi";
import { InstanceMemoryAutomationPanel } from "./instanceMemoryAutomation";
import { isMemoryScopePermitted } from "./instanceMemoryAutomationLogic";

const config: AdminEffectiveConfiguration = {
  definitionSource: "builtIn",
  definitionId: "examiner",
  definitionVersion: 1,
  definitionStatus: "published",
  instanceId: "019944af-00d1-7000-8000-000000000001",
  instanceLifecycle: "Active",
  instanceRevision: 1,
  personaRevision: 1,
  compatibility: false,
  persona: { name: "Alex", role: "role", description: "desc", tone: "tone" },
  providerPreferences: {
    languageModel: "primary-llm",
    speechRecognizer: null,
    speechSynthesizer: null,
    interruptionClassifier: "heuristic"
  },
  effectiveModel: {
    catalogKey: "synthetic-default",
    displayName: "Synthetic",
    selectionSource: "default",
    reasoningEffort: null,
    modelId: "synthetic-small"
  },
  effectiveToolAllowlist: [],
  harnessReferences: [],
  workspaceTemplateId: null,
  knowledgeSources: [],
  memoryPolicy: {
    sessionMemory: true,
    identityUserPromotion: true,
    identityUserRetrieval: true,
    userPromotion: false,
    userRetrieval: false
  },
  triggerPolicy: {
    enabled: true,
    allowUserScheduling: true,
    allowOneShot: true,
    allowDaily: true,
    allowWeekly: true,
    allowIndefiniteRecurrence: true,
    maxActiveRegistrations: 32,
    oneShotHorizonDays: 365,
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
  listAdminLearnedMemory: vi.fn(),
  deleteAdminLearnedMemory: vi.fn(),
  resetAdminLearnedMemoryScope: vi.fn(),
  listAdminAutomationRegistrations: vi.fn(),
  cancelAdminAutomationRegistration: vi.fn()
}));

import {
  deleteAdminLearnedMemory,
  listAdminAutomationRegistrations,
  listAdminLearnedMemory
} from "../../services/adminApi";

const memoryRow = {
  memoryId: "019944af-00e5-7000-8000-000000000001",
  kind: "Fact" as const,
  subject: "Preference",
  content: "Dark mode",
  provenance: {
    source: "test",
    originSessionId: null,
    originMemoryId: null,
    recordedAt: "2026-09-25T00:00:00.000Z"
  },
  updatedAt: "2026-09-25T00:00:00.000Z"
};

async function confirmDeleteOnRow() {
  fireEvent.click(screen.getByRole("button", { name: "Delete" }));
  const confirmButtons = await screen.findAllByRole("button", { name: "Delete" });
  const confirm = confirmButtons.find((button) => button.classList.contains("ant-btn-primary"));
  if (!confirm) {
    throw new Error("Popconfirm Delete button not found.");
  }
  fireEvent.click(confirm);
}

function renderPanel() {
  return render(
    <App>
      <InstanceMemoryAutomationPanel config={config} />
    </App>
  );
}

async function chooseMemoryScope(label: string) {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: "Memory scope" }));
  await waitFor(() => {
    const options = document.querySelectorAll(".ant-select-item-option-content");
    const match = Array.from(options).find((node) => node.textContent === label);
    if (!match) {
      throw new Error(`Memory scope option not found: ${label}`);
    }
    fireEvent.click(match);
  });
}

describe("InstanceMemoryAutomationPanel", () => {
  it("loads identity-user memory and automation registrations", async () => {
    vi.mocked(listAdminLearnedMemory).mockResolvedValue([memoryRow]);
    vi.mocked(listAdminAutomationRegistrations).mockResolvedValue([
      {
        registrationId: "019944af-00e5-7000-8000-000000000002",
        intent: "Reminder",
        status: "active",
        scheduleKind: "oneShot",
        timeZoneId: "UTC",
        scheduleSummary: "Once tomorrow",
        nextOccurrenceAtUtc: "2026-09-26T09:00:00.000Z",
        revision: 1,
        suspensionReason: null,
        provenance: {
          authorizationOrigin: "CurrentUserTurn",
          sourceSessionId: "873f07d1-e264-4c81-a31b-7e59e940b842",
          createdAt: "2026-09-25T00:00:00.000Z",
          updatedAt: "2026-09-25T00:00:00.000Z"
        }
      }
    ]);

    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Load items" }));
    await waitFor(() => expect(screen.getByText("Dark mode")).toBeInTheDocument());
    expect(listAdminLearnedMemory).toHaveBeenCalledWith(config.instanceId, "IdentityUser", undefined);

    fireEvent.click(screen.getByRole("tab", { name: "Automation" }));
    fireEvent.click(screen.getByRole("button", { name: "Load registrations" }));
    await waitFor(() => expect(screen.getByText("Reminder")).toBeInTheDocument());
    expect(screen.getByText("CurrentUserTurn")).toBeInTheDocument();
    expect(screen.getByText(/UTC · next/i)).toBeInTheDocument();
  });

  it("ignores stale memory responses after scope change", async () => {
    let resolveSlow: (value: Awaited<ReturnType<typeof listAdminLearnedMemory>>) => void;
    const slowPromise = new Promise<Awaited<ReturnType<typeof listAdminLearnedMemory>>>((resolve) => {
      resolveSlow = resolve;
    });
    vi.mocked(listAdminLearnedMemory)
      .mockReturnValueOnce(slowPromise)
      .mockResolvedValueOnce([]);

    renderPanel();
    fireEvent.click(screen.getByRole("button", { name: "Load items" }));

    await chooseMemoryScope("Session");

    resolveSlow!([
      {
        memoryId: "019944af-00e5-7000-8000-000000000099",
        kind: "Fact",
        subject: "Stale",
        content: "Should not appear",
        provenance: {
          source: "test",
          originSessionId: null,
          originMemoryId: null,
          recordedAt: "2026-09-25T00:00:00.000Z"
        },
        updatedAt: "2026-09-25T00:00:00.000Z"
      }
    ]);

    await waitFor(() => expect(screen.queryByText("Should not appear")).not.toBeInTheDocument());
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Load items" })).not.toHaveClass("ant-btn-loading")
    );

    fireEvent.change(screen.getByRole("textbox", { name: "Session id" }), {
      target: { value: "873f07d1-e264-4c81-a31b-7e59e940b842" }
    });
    fireEvent.click(screen.getByRole("button", { name: "Load items" }));
    await waitFor(() => expect(listAdminLearnedMemory).toHaveBeenLastCalledWith(
      config.instanceId,
      "Session",
      "873f07d1-e264-4c81-a31b-7e59e940b842"
    ));
  });

  it("surfaces pinned-session policy denial from the server for Session scope", async () => {
    const deniedConfig: AdminEffectiveConfiguration = {
      ...config,
      memoryPolicy: { ...config.memoryPolicy, sessionMemory: false }
    };
    expect(isMemoryScopePermitted(deniedConfig.memoryPolicy, "Session")).toBe(true);

    vi.mocked(listAdminLearnedMemory).mockRejectedValue(
      new Error("Session learned memory is not enabled for this session.")
    );

    render(
      <App>
        <InstanceMemoryAutomationPanel config={deniedConfig} />
      </App>
    );

    await chooseMemoryScope("Session");
    fireEvent.change(screen.getByRole("textbox", { name: "Session id" }), {
      target: { value: "873f07d1-e264-4c81-a31b-7e59e940b842" }
    });
    fireEvent.click(screen.getByRole("button", { name: "Load items" }));

    await waitFor(() =>
      expect(screen.getByText(/Session learned memory is not enabled/i)).toBeInTheDocument()
    );
    expect(screen.getByRole("button", { name: "Load items" })).not.toBeDisabled();
  });

  it("disables reset when User scope is policy-denied", async () => {
    renderPanel();
    await chooseMemoryScope("User");
    await waitFor(() => expect(screen.getByRole("button", { name: "Reset scope" })).toBeDisabled());
    expect(screen.getByText(/does not allow administration for User scope/i)).toBeInTheDocument();
  });

  it("disables destructive actions while a memory list request is in flight", async () => {
    let resolveSlow: (value: Awaited<ReturnType<typeof listAdminLearnedMemory>>) => void;
    const slowPromise = new Promise<Awaited<ReturnType<typeof listAdminLearnedMemory>>>((resolve) => {
      resolveSlow = resolve;
    });
    vi.mocked(listAdminLearnedMemory).mockReturnValueOnce(slowPromise);

    renderPanel();
    fireEvent.click(screen.getByRole("button", { name: "Load items" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Reset scope" })).toBeDisabled());
    const deleteButtons = screen.queryAllByRole("button", { name: "Delete" });
    for (const button of deleteButtons) {
      expect(button).toBeDisabled();
    }

    resolveSlow!([memoryRow]);
    await waitFor(() => expect(screen.getByRole("button", { name: "Reset scope" })).not.toBeDisabled());
  });

  it("refreshes the table after delete and does not resurrect removed rows", async () => {
    let resolveStaleList: (value: Awaited<ReturnType<typeof listAdminLearnedMemory>>) => void;
    const staleListPromise = new Promise<Awaited<ReturnType<typeof listAdminLearnedMemory>>>((resolve) => {
      resolveStaleList = resolve;
    });

    vi.mocked(listAdminLearnedMemory)
      .mockResolvedValueOnce([memoryRow])
      .mockReturnValueOnce(staleListPromise)
      .mockResolvedValueOnce([]);
    vi.mocked(deleteAdminLearnedMemory).mockResolvedValue(undefined);

    renderPanel();
    fireEvent.click(screen.getByRole("button", { name: "Load items" }));
    await waitFor(() => expect(screen.getByText("Dark mode")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Load items" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Reset scope" })).toBeDisabled());

    resolveStaleList!([memoryRow]);
    await waitFor(() => expect(screen.getByText("Dark mode")).toBeInTheDocument());

    await confirmDeleteOnRow();
    await waitFor(() => expect(deleteAdminLearnedMemory).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText("Dark mode")).not.toBeInTheDocument());
  });
});
