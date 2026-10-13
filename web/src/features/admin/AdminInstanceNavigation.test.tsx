import { useEffect, useState } from 'react';
import { AdminShell } from './AdminShell';
import { adminInstancePath, parseAppRoute } from '../../app/appRoute';
import type { InstancePage } from './adminNavigation';
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App } from "antd";
import { describe, expect, it, vi } from "vitest";
import type { AdminEffectiveConfiguration } from "../../services/adminApi";
import { InstanceDetail } from "./AdminApp";
vi.mock("./InstanceContinuitySection", () => ({
  IdentityMaintenanceSection: () => <div>Identity maintenance permission</div>,
  ExperienceSection: ({ selection }: { selection?: { agentRunId: string } }) => <div>Experience controls {selection?.agentRunId}</div>,
}));
vi.mock("./InstanceRunsSection", () => ({
  InstanceRunsSection: ({ open, detailsOnly, selectedAgentRunId, onSource, onRun }: {
    open: boolean; detailsOnly?: boolean; selectedAgentRunId?: string;
    onSource?: (source: { kind: string; automationId: string }) => void;
    onRun?: (id: string) => void;
  }) => !open ? null : detailsOnly ? <div role="dialog" aria-label="Run details">Run history {selectedAgentRunId}<button onClick={() => onSource?.({ kind: "automation", automationId: "90000000-0000-4000-8000-000000000010" })}>Back to thought fixture</button><button onClick={() => onSource?.({ kind: "automation", automationId: "90000000-0000-4000-8000-000000000011" })}>Back to event fixture</button></div> : <div>Run history<button onClick={() => onRun?.("event-run")}>Inspect run fixture</button></div>
}));
vi.mock("./InstanceActivitySection", () => ({ InstanceSessionsSection: () => <div>Session activity</div> }));
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
function Fixture({ archived = false, start = 'memory' }: { archived?: boolean; start?: InstancePage }) {
  const [page, setPage] = useState(start);
  useEffect(() => {
    window.history.replaceState(null, '', adminInstancePath(config.instanceId, start));
    const pop = () => { const route = parseAppRoute(window.location.pathname); if (route.area === 'admin' && route.view === 'instance') setPage(route.page); };
    window.addEventListener('popstate', pop); return () => window.removeEventListener('popstate', pop);
  }, [start]);
  return <App><AdminShell route={{ area: 'admin', view: 'instance', instanceId: config.instanceId, page }} ownerName="Alex"><InstanceDetail instanceId={config.instanceId} page={page} instances={{ kind: 'ready', data: [] }} effective={{ kind: 'ready', data: { ...config, instanceLifecycle: archived ? 'Archived' : 'Active' } }} onBack={vi.fn()} onRetryEffective={vi.fn()} onInstanceChanged={vi.fn()} onInstanceDeleted={vi.fn()} /></AdminShell></App>;
}
async function go(name: string) { await act(async () => fireEvent.click(within(screen.getByRole('navigation', { name: 'Admin navigation' })).getByRole('link', { name }))); }
describe('Managed Instance canonical navigation', () => {
  it.each(['automations', 'unattended'] as const)('preserves an archived exact Run requested through %s', async page => {
    const runId = '90000000-0000-4000-8000-000000000020';
    window.history.replaceState(null, '', `${adminInstancePath(config.instanceId, page)}?run=${runId}`);
    render(<App><InstanceDetail instanceId={config.instanceId} page={page} instances={{ kind: 'ready', data: [] }} effective={{ kind: 'ready', data: { ...config, instanceLifecycle: 'Archived' } }} onBack={vi.fn()} onRetryEffective={vi.fn()} onInstanceChanged={vi.fn()} onInstanceDeleted={vi.fn()} /></App>);
    await waitFor(() => expect(window.location.pathname).toBe(adminInstancePath(config.instanceId, 'runs')));
    expect(new URLSearchParams(window.location.search).get('run')).toBe(runId);
    expect(within(await screen.findByRole('dialog', { name: 'Run details' })).getByText(`Run history ${runId}`)).toBeVisible();
  });
  it('keeps owner search results separate when the Instance is named Global Admin', async () => {
    render(<App><AdminShell route={{ area: 'admin', view: 'instance', instanceId: config.instanceId, page: 'overview' }} ownerName="Global Admin"><div>Instance overview</div></AdminShell></App>);
    fireEvent.click(screen.getByRole('button', { name: 'Search Admin' }));
    const search = await screen.findByRole('dialog', { name: 'Search Admin' });
    const owner = within(search).getByRole('region', { name: 'Instance · Global Admin destinations' });
    const global = within(search).getByRole('region', { name: 'Global Admin destinations' });
    expect(within(owner).getByRole('link', { name: /^Models/ })).toHaveAttribute('href', adminInstancePath(config.instanceId, 'models'));
    expect(within(global).queryByRole('link', { name: /^Models/ })).not.toBeInTheDocument();
    expect(within(global).getByRole('link', { name: /Credentials/ })).toHaveAttribute('href', '/admin/connections/credentials');
    fireEvent.change(within(search).getByRole('textbox', { name: 'Search Admin destinations' }), { target: { value: 'reasoning' } });
    await waitFor(() => expect(within(search).getByRole('region', { name: 'Instance · Global Admin destinations' })).toBeVisible());
    expect(within(search).queryByRole('region', { name: 'Global Admin destinations' })).not.toBeInTheDocument();
  });
  it('retains exact independent Run and Automation selections in both directions', async () => {
    render(<Fixture />);
    expect(screen.getByText('Memory controls')).toBeVisible();
    expect(screen.queryByRole('tab', { name: 'Continuity' })).not.toBeInTheDocument();
    await go('Experience'); expect(screen.getByText('Experience controls')).toBeVisible();
    await go('Automations'); expect(screen.getByText('Automation controls')).toBeVisible();
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Inspect automation fixture' })));
    expect(within(screen.getByRole('dialog', { name: 'Run details' })).getByText(/Run history automation-run/)).toBeVisible();
    expect(window.location.pathname).toBe(adminInstancePath(config.instanceId, 'automations'));
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Back to thought fixture' })));
    expect(screen.getByText(/Automation controls 90000000-0000-4000-8000-000000000010/)).toBeVisible();
    await go('Sessions'); expect(screen.getByText('Session activity')).toBeVisible();
    await go('Runs'); await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Inspect run fixture' })));
    expect(within(screen.getByRole('dialog', { name: 'Run details' })).getByText(/Run history event-run/)).toBeVisible();
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Back to event fixture' })));
    expect(window.location.pathname).toBe(adminInstancePath(config.instanceId, 'automations'));
    expect(screen.getByText(/Automation controls 90000000-0000-4000-8000-000000000011/)).toBeVisible();
  });
  it('preserves archived read-only Run inspection and blocks active-only Experience', async () => {
    render(<Fixture archived start="runs" />);
    expect(screen.getByText('Run history')).toBeVisible();
    await go('Experience');
    expect(screen.getByText('Unarchive from Lifecycle to inspect Experience.')).toBeVisible();
    expect(screen.queryByText('Experience controls')).not.toBeInTheDocument();
  });
});
