import { fireEvent, render, screen, within } from "@testing-library/react";
import { App } from "antd";
import { describe, expect, it, vi } from "vitest";
import type { AdminEffectiveConfiguration } from "../../services/adminApi";
import { InstanceDetail } from "./AdminApp";
vi.mock("./InstanceContinuitySection", () => ({
  IdentityMaintenanceSection: () => <div>Identity maintenance permission</div>,
  ExperienceSection: ({ selection }: { selection?: { workItemId: string } }) => <div>Experience controls {selection?.workItemId}</div>,
  InstanceRunsSection: ({ open, detailsOnly, selectedWorkItemId, onSource, onRun }: {
    open: boolean; detailsOnly?: boolean; selectedWorkItemId?: string;
    onSource?: (source: { kind: string; automationId: string }) => void;
    onRun?: (id: string) => void;
  }) => !open ? null : detailsOnly ? <div role="dialog" aria-label="Run details">Run history {selectedWorkItemId}<button onClick={() => onSource?.({ kind: "automation", automationId: "original-thought" })}>Back to thought fixture</button><button onClick={() => onSource?.({ kind: "automation", automationId: "event-source" })}>Back to event fixture</button></div> : <div>Run history<button onClick={() => onRun?.("event-run")}>Inspect run fixture</button></div>
}));
vi.mock("./CredentialsSection", () => ({ CredentialsSection: () => null, InstanceCredentialsSection: () => <div>Credential bindings</div> }));
vi.mock("./InstanceAutomationsSection", () => ({ InstanceAutomationsSection: ({ onWork, selection }: { onWork: (id: string) => void; selection?: { automationId: string } }) => <div>Automation controls {selection?.automationId}<button onClick={() => onWork("automation-run")}>Inspect automation fixture</button></div> }));
vi.mock("./instanceMemoryAutomation", () => ({ InstanceMemoryAutomationPanel: ({ section }: { section: string }) => <div>{section === "memory" ? "Memory controls" : "Model controls"}</div> }));
const config: AdminEffectiveConfiguration = {
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
describe("Managed instance information architecture", () => {
  it("groups retained context, automation and runs and retains exact selection in both directions", () => {
    render(<App><InstanceDetail instanceId={config.instanceId} instances={{ kind: "ready", data: [] }}
      effective={{ kind: "ready", data: config }} onBack={vi.fn()} onRetryEffective={vi.fn()} onInstanceChanged={vi.fn()} onInstanceDeleted={vi.fn()} /></App>);
    for (const name of ["Identity & version", "Continuity", "Automation", "Runs", "Connections", "Effective configuration"]) expect(screen.getByRole("tab", { name })).toBeVisible();
    expect(screen.queryByRole("tab", { name: "Behavior & continuity" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Continuity" }));
    expect(screen.getByText("Memory controls")).toBeVisible();
    fireEvent.click(screen.getByRole("tab", { name: "Experience" }));
    expect(screen.getByText("Experience controls")).toBeVisible();
    fireEvent.click(screen.getByRole("tab", { name: "Automation" }));
    expect(screen.getByText("Automation controls")).toBeVisible();
    fireEvent.click(screen.getByRole("tab", { name: "Automations" }));
    fireEvent.click(screen.getByRole("button", { name: "Inspect automation fixture" }));
    // Current Runs baseline opens shared details without switching the source tab.
    expect(screen.getByRole("tab", { name: "Automation" })).toHaveAttribute("aria-selected", "true");
    expect(within(screen.getByRole("dialog", { name: "Run details" })).getByText(/Run history automation-run/)).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Back to thought fixture" }));
    expect(screen.getByRole("tab", { name: "Automation" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "Automations" })).toHaveAttribute("aria-selected", "true");
    expect(within(screen.getByRole("tabpanel", { name: "Automations" })).getByText(/original-thought/)).toBeVisible();
    fireEvent.click(screen.getByRole("tab", { name: "Runs" }));
    fireEvent.click(screen.getByRole("button", { name: "Inspect run fixture" }));
    expect(within(screen.getByRole("dialog", { name: "Run details" })).getByText(/Run history event-run/)).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Back to event fixture" }));
    expect(screen.getByRole("tab", { name: "Automations" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByText(/Automation controls event-source/)).toBeVisible();
  });
  it("keeps inactive instances inside the existing active-instance API boundary", () => {
    render(<App><InstanceDetail instanceId={config.instanceId} instances={{ kind: "ready", data: [] }}
      effective={{ kind: "ready", data: { ...config, instanceLifecycle: "Archived" } }} onBack={vi.fn()} onRetryEffective={vi.fn()} onInstanceChanged={vi.fn()} onInstanceDeleted={vi.fn()} /></App>);
    expect(screen.queryByRole("tab", { name: "Automation" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Runs" }));
    expect(screen.getByText("Runs are available when this instance is active")).toBeVisible();
    expect(screen.queryByText(/Run history/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Continuity" }));
    fireEvent.click(screen.getByRole("tab", { name: "Experience" }));
    expect(screen.getByText("Experience is available when this instance is active")).toBeVisible();
  });

});
