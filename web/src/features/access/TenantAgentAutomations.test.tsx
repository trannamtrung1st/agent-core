import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantAutomationEditor } from '../../services/tenantAutomations';
import { TenantAgentAutomations } from './TenantAgentAutomations';

const context = { tenant: { tenantId: 'tenant', name: 'Schedule organization', slug: 'schedules' }, principalId: 'human', permissions: [], roles: [], revision: 4 } as unknown as TenantContext;
const fixture = (): TenantAutomationEditor => ({ agent: { principalId: 'executor', instance: { instanceId: 'agent', revision: 3, activeVersion: 32, lifecycle: 'Active', persona: { name: 'Alex' } } }, securityRevision: 8, canCreate: true, canSchedule: true, total: 1, page: 0, pageSize: 10,
  policy: { allowDaily: true, allowOneShot: true, allowWeekly: true, allowFixedInterval: true, allowIndefiniteRecurrence: true }, items: [{ canUpdate: true, canCancel: true, canRun: true,
    automation: { automationId: 'schedule', name: 'Saved schedule', instructions: 'Exact saved instructions', status: 'active', revision: 6, triggerRevision: 2, occurrenceCount: 2, nextOccurrenceAtUtc: null, modelOverrideCatalogKey: null, modelOverrideReasoningEffort: null, requiresTools: true, requiresVision: false,
      executionTarget: 'backgroundSession', completionDelivery: 'none', triggers: [{ kind: 'schedule', schedule: { kind: 'daily', interval: 1, localTime: '09:00', timeZone: 'UTC', startDate: '2026-10-01', endDate: '2027-01-01', maxOccurrences: 10 } }] } }] } as TenantAutomationEditor);
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>(); let current: TenantAutomationEditor; let stale = false, denied = false, unknown = false, invalidAck = false;
const writes = () => fetchMock.mock.calls.filter(([, options]) => options?.method && options.method !== 'GET' && !String(options.body).includes('csrf'));
beforeEach(() => {
  resetIdentityCsrf(); current = fixture(); stale = denied = unknown = invalidAck = false;
  fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (url.endsWith('/chat/models')) return response({ defaultKey: 'alpha', models: [{ key: 'alpha', displayName: 'Alpha', supportedReasoningEfforts: [] }] });
    if (options?.method && options.method !== 'GET') {
      if (unknown) throw new TypeError('Connection lost');
      if (stale) return response({ reasonCode: 'stale_revision' }, 409);
      if (invalidAck) return new Response('{', { status: 200 });
      if (options.method === 'DELETE') { current.items[0].automation.status = 'cancelled'; current.items[0].automation.nextOccurrenceAtUtc = '2027-01-01T09:00:00Z'; return response(current.items[0].automation); }
      if (url.endsWith('/run')) return response({ runId: 'admitted-run', sessionId: 'task-session' });
      const body = JSON.parse(String(options.body ?? '{}'));
      if (body.name) current.items[0].automation = { ...current.items[0].automation, ...body, revision: 7 };
      return response(current.items[0].automation);
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
it('retires a known saved draft when the acknowledgement cannot be decoded', async () => {
  invalidAck = true; await open(); await edit(); fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'Committed acknowledgement' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save Automation' })); await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument()); expect(writes()).toHaveLength(1);
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
  await screen.findByText('cancelled'); expect(screen.getByText('Next: Not scheduled · Scheduled occurrences: 2')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Open the admitted Run in Activity' })).toHaveAttribute('href', '/t/tenant/admin/agents/agent/activity/runs?run=admitted-run');
});
it('starts disabled, requires valid instructions and refuses enabled timing outside current policy', async () => {
  current.canSchedule = false; await open(); fireEvent.click(screen.getByRole('button', { name: 'New Automation' })); await screen.findByRole('dialog');
  fireEvent.change(screen.getByRole('textbox', { name: 'Automation name' }), { target: { value: 'New task' } }); fireEvent.change(screen.getByRole('textbox', { name: 'Automation instructions' }), { target: { value: 'New saved objective' } });
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save Automation' })).toBeEnabled());
  fireEvent.click(screen.getByRole('checkbox', { name: 'Enable scheduled occurrences' })); expect(screen.getByRole('button', { name: 'Save Automation' })).toBeDisabled();
  expect(await screen.findByText('The current Definition does not permit this schedule')).toBeVisible();
});

it('acknowledges pending admission consent without claiming a Run exists or replaying the action', async () => {
  const original = fetchMock.getMockImplementation()!;
  fetchMock.mockImplementation(async (path, options) => String(path).endsWith('/run') ? response({ runId: null, sessionId: null, approvalRequestId: 'exact-approval' }, 202) : original(path, options));
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Run now' })); const dialog = await screen.findByRole('dialog', { name: 'Run saved Automation now?' });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Run now' }));
  expect(await screen.findByRole('link', { name: 'Review the exact admission request' })).toHaveAttribute('href', '/t/tenant/admin/approvals/exact-approval');
  expect(screen.queryByRole('link', { name: 'Open the admitted Run in Activity' })).not.toBeInTheDocument();
  expect(screen.getByText('No Session or Run has been created for this occurrence. Approval and admission remain separate steps.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Reload Automations' })); await waitFor(() => expect(writes()).toHaveLength(1));
});
