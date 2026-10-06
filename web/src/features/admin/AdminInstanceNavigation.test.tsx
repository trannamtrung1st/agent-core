import { fireEvent, render, screen, within } from "@testing-library/react";
import { App } from "antd";
import { describe, expect, it, vi } from "vitest";
import type { AdminEffectiveConfiguration } from "../../services/adminApi";
import { InstanceDetail } from "./AdminApp";
vi.mock("./InstanceContinuitySection", () => ({
  IdentityMaintenanceSection: () => <div>Identity maintenance permission</div>,
  ExperienceSection: ({ selection }: { selection?: { workItemId: string } }) => <div>Experience controls {selection?.workItemId}</div>,
  ThoughtSection: ({ onWork, selection }: { onWork: (id: string) => void; selection?: { registrationId: string } }) => <div>Thought controls {selection?.registrationId}<button onClick={() => onWork("thought-run")}>Inspect thought fixture</button></div>,
  InstanceRunsSection: ({ selectedWorkItemId, onSource }: { selectedWorkItemId?: string; onSource: (source: { kind: string; registrationId: string }) => void }) => <div>Run history {selectedWorkItemId}<button onClick={() => onSource({ kind: "thought", registrationId: "original-thought" })}>Back to thought fixture</button><button onClick={() => onSource({ kind: "event", registrationId: "event-source" })}>Back to event fixture</button></div>
}));
vi.mock("./EventSubscriptionsSection", () => ({ EventSubscriptionsSection: ({ selection }: { selection?: { registrationId: string } }) => <div>Event controls {selection?.registrationId}</div> }));
vi.mock("./ApplicationConnectionSection", () => ({ ApplicationConnectionSection: () => null }));
vi.mock("./InstanceSchedulesSection", () => ({ InstanceSchedulesSection: () => <div>Schedule controls</div> }));
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
  compatibility: false,
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
    expect(screen.getByText("Schedule controls")).toBeVisible();
    fireEvent.click(screen.getByRole("tab", { name: "Thoughts" }));
    fireEvent.click(screen.getByRole("button", { name: "Inspect thought fixture" }));
    expect(screen.getByRole("tab", { name: "Runs" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByText(/Run history thought-run/)).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Back to thought fixture" }));
    expect(screen.getByRole("tab", { name: "Automation" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "Thoughts" })).toHaveAttribute("aria-selected", "true");
    expect(within(screen.getByRole("tabpanel", { name: "Thoughts" })).getByText(/original-thought/)).toBeVisible();
    fireEvent.click(screen.getByRole("tab", { name: "Runs" }));
    fireEvent.click(screen.getByRole("button", { name: "Back to event fixture" }));
    expect(screen.getByRole("tab", { name: "Connections" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByText(/Event controls event-source/)).toBeVisible();
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
