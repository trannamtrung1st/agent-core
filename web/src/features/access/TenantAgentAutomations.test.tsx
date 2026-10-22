import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantAutomationEditor, TenantAutomationPreset } from '../../services/tenantAutomations';
import { TenantAgentAutomations } from './TenantAgentAutomations';

const context = { tenant: { tenantId: 'tenant', name: 'Schedule organization', slug: 'schedules' }, principalId: 'human', permissions: [], roles: [], revision: 4 } as unknown as TenantContext;
const presets: TenantAutomationPreset[] = [
  { presetId: 'review-recent-work', presetVersion: 1, name: 'Review recent work', description: 'Review owned completed work.', instructions: 'Inspect exact sources before recording.', triggerKind: 'coreEvent', coreEventKey: 'run.completed', filterExpression: "event.data.activationKind === 'UserTurn' && event.data.outcomeKind !== 'NoAction'", dispatch: { mode: 'coalesceLatest', windowSeconds: 900 }, eligible: false, prerequisites: ['Enable Experience.'] },
  { presetId: 'consolidate-continuity', presetVersion: 1, name: 'Consolidate continuity', description: 'Review redundant owned observations.', instructions: 'Consolidate only with exact permission and approval.', triggerKind: 'schedule', coreEventKey: null, filterExpression: null, dispatch: null, eligible: false, prerequisites: ['Enable Allow agent consolidation.'] },
  { presetId: 'improve-harness', presetVersion: 1, name: 'Improve agent harness', description: 'Propose a governed improvement.', instructions: 'Inspect friction before proposing one change.', triggerKind: 'schedule', coreEventKey: null, filterExpression: null, dispatch: null, eligible: false, prerequisites: ['Enable Harness Management.'] },
];
const fixture = (): TenantAutomationEditor => ({ agent: { principalId: 'executor', instance: { instanceId: 'agent', revision: 3, activeVersion: 32, lifecycle: 'Active', persona: { name: 'Alex' } } }, securityRevision: 8, canCreate: true, canSchedule: true, total: 1, page: 0, pageSize: 10,
  policy: { allowDaily: true, allowOneShot: true, allowWeekly: true, allowFixedInterval: true, allowIndefiniteRecurrence: true }, items: [{ canUpdate: true, canCancel: true, canRun: true,
    automation: { automationId: 'schedule', name: 'Saved schedule', instructions: 'Exact saved instructions', status: 'active', revision: 6, triggerRevision: 2, occurrenceCount: 2, nextOccurrenceAtUtc: null, modelOverrideCatalogKey: null, modelOverrideReasoningEffort: null, requiresTools: true, requiresVision: false,
      executionTarget: 'backgroundSession', completionDelivery: 'none', triggers: [{ kind: 'schedule', schedule: { kind: 'daily', interval: 1, localTime: '09:00', timeZone: 'UTC', startDate: '2026-10-01', endDate: '2027-01-01', maxOccurrences: 10 } }] } }] } as TenantAutomationEditor);
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>(); let current: TenantAutomationEditor; let stale = false, denied = false, unknown = false, invalidAck = false, catalogFailed = false, mismatchedPreset = false;
const writes = () => fetchMock.mock.calls.filter(([, options]) => options?.method && options.method !== 'GET' && !String(options.body).includes('csrf'));
beforeEach(() => {
  resetIdentityCsrf(); current = { ...fixture(), presets }; stale = denied = unknown = invalidAck = catalogFailed = mismatchedPreset = false;
  fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (url.endsWith('/chat/models')) return catalogFailed ? response({ reasonCode: 'catalog_unavailable' }, 503) : response({ defaultKey: 'alpha', models: [{ key: 'alpha', displayName: 'Alpha', supportedReasoningEfforts: [] }] });
    if (url.endsWith('/events/builtin')) return catalogFailed ? response({ reasonCode: 'catalog_unavailable' }, 503) : response({ items: [{ key: 'run.completed', description: 'Run finished.' }] });
    if (url.includes('/webhooks')) return catalogFailed ? response({ reasonCode: 'catalog_unavailable' }, 503) : response({ items: [], total: 0 });
    if (options?.method && options.method !== 'GET') {
      if (unknown) throw new TypeError('Connection lost');
      if (stale) return response({ reasonCode: 'stale_revision' }, 409);
      if (invalidAck) return new Response('{', { status: 200 });
      if (options.method === 'DELETE') { current.items[0].automation.status = 'cancelled'; current.items[0].automation.nextOccurrenceAtUtc = '2027-01-01T09:00:00Z'; return response(current.items[0].automation); }
      if (url.endsWith('/run')) return response({ runId: 'admitted-run', sessionId: 'task-session' });
      const body = JSON.parse(String(options.body ?? '{}'));
      if (body.name) current.items[0].automation = { ...current.items[0].automation, ...body, executionTarget: body.targetSessionId ? 'existingSession' : 'backgroundSession', completionDelivery: body.reportToSessionId ? 'toSession' : 'none', revision: 7 };
      return response(mismatchedPreset ? { ...current.items[0].automation, presetId: 'improve-harness' } : current.items[0].automation);
    }
    if (url.includes('/automations/')) return denied ? response({ reasonCode: 'permission_denied' }, 403) : response(current);
    throw new Error(`Unexpected endpoint ${url}`);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async () => { render(<App><TenantAgentAutomations context={context} id="agent" /></App>); await screen.findByText('Saved schedule'); };
const edit = async () => { fireEvent.click(screen.getByRole('button', { name: 'Edit Automation' })); return screen.findByRole('dialog', { name: 'Saved Automation' }); };

it('saves the exact schedule, retained bounds and current parent/security witnesses through the shared editor', async () => {
  await open(); await edit(); fireEvent.change(screen.getByRole('textbox', { name: 'Automation instructions' }), { target: { value: 'Revised exact instructions' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' }));
  await waitFor(() => expect(writes()).toHaveLength(1));
  expect(writes()[0][0]).toBe('/api/v2/tenants/tenant/agents/agent/automations/schedule');
  expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ expectedRevision: 8, expectedInstanceRevision: 3, expectedAutomationRevision: 6, instructions: 'Revised exact instructions', schedule: { startDate: '2026-10-01', endDate: '2027-01-01', maxOccurrences: 10 } });
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  await waitFor(() => expect(screen.getByRole('button', { name: 'Edit Automation' })).toHaveFocus());
});
it('preserves a stale draft, blocks replay and refreshes witnesses only on explicit reload', async () => {
  stale = true; await open(); await edit(); fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'Retain stale draft' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await screen.findByText(/Access has changed since/); expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled();
  stale = false; current.securityRevision = 19; current.items[0].automation.revision = 10;
  fireEvent.click(screen.getByRole('button', { name: 'Reload current Automation' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Save Automation' })).toBeEnabled());
  expect(screen.getByRole('textbox', { name: 'Automation name' })).toHaveValue('Retain stale draft'); fireEvent.click(screen.getByRole('button', { name: 'Save Automation' }));
  await waitFor(() => expect(writes()).toHaveLength(2)); expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ expectedRevision: 19, expectedAutomationRevision: 10, name: 'Retain stale draft' });
});
it('keeps unknown saves blocked through reload and prevents a duplicate new registration', async () => {
  unknown = true; await open(); await edit(); fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'Unknown saved draft' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await screen.findByText('Outcome unknown'); fireEvent.click(screen.getByRole('button', { name: 'Reload current Automation' }));
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Automation name' })).toHaveValue('Unknown saved draft'));
  expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled(); expect(writes()).toHaveLength(1);
});
it('preserves an uncertain draft without a success claim when the acknowledgement cannot be decoded', async () => {
  invalidAck = true; await open(); await edit(); fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'Committed acknowledgement' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await screen.findByText('Outcome unknown');
  expect(screen.getByRole('textbox', { name: 'Automation name' })).toHaveValue('Committed acknowledgement');
  expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled(); expect(writes()).toHaveLength(1);
  expect(screen.queryByText('Automation saved. Reading current registrations.')).not.toBeInTheDocument();
  expect(screen.queryByText(/The change was saved, but this view/)).not.toBeInTheDocument();
});
it('exposes independent read-only hints and clears protected detail on read revocation', async () => {
  current.canCreate = false; current.items[0] = { ...current.items[0], canUpdate: false, canRun: false, canCancel: false }; await open();
  expect(screen.getByRole('button', { name: 'New Automation' })).toBeDisabled(); expect(screen.queryByRole('button', { name: 'Run now' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Inspect Automation' })); await screen.findByRole('dialog'); expect(screen.getByRole('textbox', { name: 'Automation instructions' })).toBeDisabled();
  denied = true; fireEvent.click(screen.getByRole('button', { name: 'Reload current Automation' })); await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  await waitFor(() => expect(screen.queryByText('Exact saved instructions')).not.toBeInTheDocument()); expect(writes()).toHaveLength(0);
});
it('confirms saved Run admission separately from completion and links to canonical Activity', async () => {
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Run now' })); const dialog = await screen.findByRole('dialog', { name: 'Run saved Automation now?' });
  expect(dialog).toHaveTextContent('Saved schedule'); expect(dialog).toHaveTextContent('Admission is not completion'); expect(writes()).toHaveLength(0);
  fireEvent.click(within(dialog).getByRole('button', { name: 'Run now' })); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ expectedRevision: 8, expectedInstanceRevision: 3, expectedAutomationRevision: 6 });
  expect(await screen.findByRole('link', { name: 'Open the admitted Run in Activity' })).toHaveAttribute('href', '/t/tenant/admin/agents/agent/activity/runs?run=admitted-run');
  await waitFor(() => expect(screen.getByRole('button', { name: 'Cancel Automation' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Cancel Automation' })); const cancel = await screen.findByRole('dialog', { name: 'Cancel this Automation?' });
  fireEvent.click(within(cancel).getByRole('button', { name: 'Cancel Automation' })); await waitFor(() => expect(writes()).toHaveLength(2));
  await screen.findByText('cancelled'); expect(screen.getByText('Next: Not scheduled · Occurrences: 2')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Open the admitted Run in Activity' })).toHaveAttribute('href', '/t/tenant/admin/agents/agent/activity/runs?run=admitted-run');
});
it('starts disabled, requires valid instructions and refuses enabled timing outside current policy', async () => {
  current.canSchedule = false; await open(); fireEvent.click(screen.getByRole('button', { name: 'New Automation' })); await screen.findByRole('dialog');
  fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'New task' } }); fireEvent.change(screen.getByRole('textbox', { name: 'Automation instructions' }), { target: { value: 'New saved objective' } });
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save Automation' })).toBeEnabled());
  fireEvent.click(screen.getByRole('checkbox', { name: 'Enable automatic occurrences' })); expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled();
  expect(await screen.findByText('The current Definition does not permit these triggers')).toBeVisible();
});

it('acknowledges pending admission consent without claiming a Run exists or replaying the action', async () => {
  const original = fetchMock.getMockImplementation()!;
  fetchMock.mockImplementation(async (path, options) => String(path).endsWith('/run') ? response({ runId: null, sessionId: null, approvalRequestId: 'exact-approval' }, 202) : original(path, options));
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Run now' })); const dialog = await screen.findByRole('dialog', { name: 'Run saved Automation now?' });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Run now' }));
  expect(await screen.findByRole('link', { name: 'Review the exact admission request' })).toHaveAttribute('href', '/t/tenant/admin/approvals/exact-approval');
  expect(screen.queryByRole('link', { name: 'Open the admitted Run in Activity' })).not.toBeInTheDocument();
  expect(screen.getByText('No Run has been admitted for this occurrence. Approval and admission remain separate steps.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Reload Automations' })); await waitFor(() => expect(writes()).toHaveLength(1));
});

it('authors an exact report destination through the shared picker and retains it after a stale write', async () => {
  await open(); await edit();
  fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Automation completion report' }));
  fireEvent.click(await screen.findByText('Selected conversation', { selector: '.ant-select-item-option-content' }));
  expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Enter Session ID' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Session ID' }), { target: { value: 'exact-report-conversation' } });
  stale = true; fireEvent.click(screen.getByRole('button', { name: 'Save Automation' }));
  await screen.findByText(/Access has changed since/);
  expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ reportToSessionId: 'exact-report-conversation', expectedAutomationRevision: 6 });
  expect(screen.getByRole('textbox', { name: 'Session ID' })).toHaveValue('exact-report-conversation');
  stale = false; current.securityRevision = 22;
  fireEvent.click(screen.getByRole('button', { name: 'Reload current Automation' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save Automation' })).toBeEnabled());
  expect(screen.getByText('Session ID: exact-report-conversation')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ reportToSessionId: 'exact-report-conversation', expectedRevision: 22 });
});
it('opens a configured report for editing and can explicitly remove that destination', async () => {
  current.items[0].automation.completionDelivery = 'toSession'; current.items[0].automation.reportToSessionId = 'exact-report-conversation';
  await open(); await edit();
  fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Automation completion report' }));
  fireEvent.click(await screen.findByText('None', { selector: '.ant-select-item-option-content' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ reportToSessionId: null });
});

it('uses the shared picker for execution and clears separate reports and Automation model overrides', async () => {
  current.items[0].automation.modelOverrideCatalogKey = 'alpha';
  current.items[0].automation.completionDelivery = 'toSession'; current.items[0].automation.reportToSessionId = 'prior-report';
  await open(); await edit();
  fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Automation execution destination' }));
  fireEvent.click(await screen.findByText('Selected conversation', { selector: '.ant-select-item-option-content' }));
  expect(screen.queryByRole('combobox', { name: 'Automation completion report' })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Enter Session ID' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Session ID' }), { target: { value: 'exact-execution-conversation' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ targetSessionId: 'exact-execution-conversation', reportToSessionId: null, modelKey: null, reasoningEffort: null });
});
it('opens an existing execution destination, retains a stale target draft and saves only after explicit reload', async () => {
  current.items[0].automation.executionTarget = 'existingSession'; current.items[0].automation.targetSessionId = 'saved-conversation';
  await open(); await edit();
  expect(screen.getByText('Session ID: saved-conversation')).toBeInTheDocument();
  fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'Retained selected conversation' } });
  stale = true; fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await screen.findByText(/Access has changed since/);
  stale = false; current.securityRevision = 23;
  fireEvent.click(screen.getByRole('button', { name: 'Reload current Automation' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save Automation' })).toBeEnabled());
  expect(screen.getByText('Session ID: saved-conversation')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ targetSessionId: 'saved-conversation', expectedRevision: 23 });
});

const newDraft = async () => { await open(); fireEvent.click(screen.getByRole('button', { name: 'New Automation' })); await screen.findByRole('textbox', { name: 'Automation name' }); };
const recipe = async (name: string) => {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Automation starting recipe' }));
  fireEvent.click(await screen.findByText(name, { selector: '.ant-select-item-option-content' }));
};
it('fills a finite disabled weekly recipe and acknowledges the exact creation provenance', async () => {
  await newDraft(); await recipe('Consolidate continuity');
  expect(screen.getByRole('textbox', { name: 'Automation name' })).toHaveValue('Consolidate continuity');
  expect(screen.getByRole('checkbox', { name: 'Enable automatic occurrences' })).not.toBeChecked();
  expect(screen.getByText(/Current Agent prerequisites: Enable Allow agent consolidation/)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' }));
  await waitFor(() => expect(writes()).toHaveLength(1));
  expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ presetId: 'consolidate-continuity', presetVersion: 1, enabled: false, requiresTools: true, schedule: { kind: 'weekly', interval: 1, weekdays: [1], maxOccurrences: 52 } });
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
});
it('keeps an edited draft until replacement is explicitly confirmed and starts the replacement disabled', async () => {
  await newDraft(); await recipe('Consolidate continuity');
  fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'Keep this draft' } });
  await recipe('Review recent work');
  let confirm = (await screen.findByRole('button', { name: 'Replace draft' })).closest('[role=dialog]') as HTMLElement;
  fireEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Replace draft' })).not.toBeInTheDocument());
  expect(screen.getByRole('textbox', { name: 'Automation name' })).toHaveValue('Keep this draft');
  await recipe('Review recent work'); confirm = (await screen.findByRole('button', { name: 'Replace draft' })).closest('[role=dialog]') as HTMLElement;
  fireEvent.click(within(confirm).getByRole('button', { name: 'Replace draft' }));
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Automation name' })).toHaveValue('Review recent work'));
  expect(screen.getByRole('checkbox', { name: 'Enable automatic occurrences' })).not.toBeChecked(); expect(writes()).toHaveLength(0);
});
it('can save a disabled known-source recipe despite model and event catalog refresh failures', async () => {
  catalogFailed = true; await newDraft(); await recipe('Review recent work');
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save Automation' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ presetId: 'review-recent-work', enabled: false, triggers: [{ source: { kind: 'builtin', key: 'run.completed' }, dispatch: { mode: 'coalesceLatest', windowSeconds: 900 } }] });
});
it('keeps an uncertain save open and blocks replay when the receipt claims different recipe provenance', async () => {
  mismatchedPreset = true; await newDraft(); await recipe('Consolidate continuity');
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await screen.findByText('Outcome unknown');
  expect(screen.getByRole('textbox', { name: 'Automation name' })).toHaveValue('Consolidate continuity');
  expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled();
  expect(screen.queryByText('Automation saved. Reading current registrations.')).not.toBeInTheDocument();
  expect(screen.queryByText(/The change was saved, but this view/)).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Reload current Automation' })); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled();
});
it('shows immutable saved origin while permitting customized instructions', async () => {
  current.items[0].automation.presetId = 'consolidate-continuity'; current.items[0].automation.presetVersion = 1;
  await open(); await edit(); expect(screen.getByText('Consolidate continuity (version 1)')).toBeVisible();
  expect(screen.queryByRole('combobox', { name: 'Automation starting recipe' })).not.toBeInTheDocument();
  fireEvent.change(screen.getByRole('textbox', { name: 'Automation instructions' }), { target: { value: 'Customized saved task' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ presetId: 'consolidate-continuity', presetVersion: 1, instructions: 'Customized saved task' });
});
it('retires a pending recipe replacement when the scoped editor unmounts', async () => {
  const rendered = render(<App><TenantAgentAutomations context={context} id="agent" /></App>); await screen.findByText('Saved schedule');
  fireEvent.click(screen.getByRole('button', { name: 'New Automation' })); await screen.findByRole('textbox', { name: 'Automation name' });
  fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'Private draft' } }); await recipe('Improve agent harness');
  await screen.findByRole('button', { name: 'Replace draft' }); rendered.unmount();
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Replace draft' })).not.toBeInTheDocument()); expect(writes()).toHaveLength(0);
});
