import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantAgentControlsView } from '../../services/tenantAgentConfiguration';
import { defaultManagement } from '../admin/SelfManagementFields';
import { TenantAgentControls, type TenantControlPage } from './TenantAgentControls';

const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Controls organization', slug: 'controls' }, userId: 'user', principalId: 'viewer', permissions: [], roles: [], revision: 7 };
const profile = { maxSteps: 48, durationSeconds: 360, perToolSeconds: 30, preset: 1, sandboxCommandSeconds: 120 };
const controls = () => ({ agent: { principalId: 'principal', systemIdentityId: 'identity', instance: { instanceId: 'agent', persona: { name: 'Alex', role: 'Guide', description: 'Guide', tone: 'Calm' }, personaRevision: 1, revision: 4, activeVersion: 1, lifecycle: 'Active', unattendedModelCatalogKey: 'alpha', unattendedReasoningEffort: 'high' } }, securityRevision: 7, canUpdate: true,
  budgets: { instanceRevision: 4, securityRevision: 7, overrides: { standard: profile }, definitionDefaults: null, hostLimits: { maxSteps: 144, durationSeconds: 900, perToolSeconds: 30, sandboxCommandSeconds: 180 } },
  management: { securityRevision: 7, policy: { instanceRevision: 4, policyRevision: 2, source: 'instance', effective: { ...defaultManagement, mode: 'Assisted', scopes: ['Settings'] }, definitionDefaults: defaultManagement, overrides: { mode: { value: 'Assisted' }, scopes: { value: ['Settings'] } } } }
} as TenantAgentControlsView);
const fetchMock = vi.fn<typeof fetch>(); let current: TenantAgentControlsView; let stale = false, denied = false, readFailure = false, malformedAck = false, catalogFailure = false;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const writes = () => fetchMock.mock.calls.filter(([, options]) => ['PUT', 'POST'].includes(options?.method ?? ''));
beforeEach(() => {
  resetIdentityCsrf(); current = controls(); stale = denied = readFailure = malformedAck = catalogFailure = false;
  fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (url.endsWith('/chat/models')) return catalogFailure ? response({}, 503) : response({ defaultKey: 'alpha', models: [{ key: 'alpha', displayName: 'Alpha', supportedReasoningEfforts: ['low', 'high'] }, { key: 'beta', displayName: 'Beta', supportedReasoningEfforts: [] }] });
    if (['PUT', 'POST'].includes(options?.method ?? '')) {
      if (stale) return response({ reasonCode: 'stale_revision' }, 409);
      const body = JSON.parse(String(options?.body)); current = structuredClone(current); current.securityRevision++; current.agent.instance.revision++;
      if (url.endsWith('/execution-budgets')) current.budgets.overrides = body.budgets;
      if (url.endsWith('/self-management/freeze')) current.management.policy.effective.frozen = true;
      if (url.endsWith('/self-management')) { current.management.policy.overrides = body.overrides; current.management.policy.effective.frozen = body.frozen ?? current.management.policy.effective.frozen; }
      return malformedAck ? new Response('{', { status: 200, headers: { 'Content-Type': 'application/json' } }) : response({ saved: true });
    }
    if (url.endsWith('/configuration-controls')) return denied ? response({ reasonCode: 'permission_denied' }, 403) : readFailure ? response({}, 503) : response(current);
    throw new Error(`Unexpected ${url}`);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async (page: TenantControlPage) => { render(<App><TenantAgentControls context={context} id="agent" page={page} /></App>); await screen.findByText('Alex'); };
async function select(label: string, text: string) { fireEvent.mouseDown(await screen.findByRole('combobox', { name: label })); fireEvent.click(await screen.findByText(text, { selector: '.ant-select-item-option-content' })); }

it('stages inheritance reset and writes exact parent/security witnesses only on Save', async () => {
  await open('execution-budgets'); fireEvent.click(screen.getByRole('button', { name: 'Reset to Definition defaults' })); expect(writes()).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Save Execution budgets' })); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(writes()[0][0]).toBe('/api/v2/tenants/tenant/agents/agent/execution-budgets');
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ expectedRevision: 7, expectedInstanceRevision: 4, budgets: { standard: null, interactiveBrowser: null, unattendedBoundBrowser: null } });
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save Execution budgets' })).toBeDisabled());
  expect(fetchMock.mock.calls.every(([url]) => String(url).startsWith('/api/v2/'))).toBe(true);
});
it('preserves a rejected draft through explicit reload and hides protected fields on read revocation', async () => {
  stale = true; await open('execution-budgets'); await select('Standard profile', 'Deep Workflow');
  fireEvent.click(screen.getByRole('button', { name: 'Save Execution budgets' })); await screen.findByText(/Access has changed since you opened/);
  current.securityRevision = 10; current.agent.instance.revision = 6;
  fireEvent.click(screen.getByRole('button', { name: 'Reload controls' })); await screen.findByText(/72 steps/);
  stale = false; fireEvent.click(screen.getByRole('button', { name: 'Save Execution budgets' })); await waitFor(() => expect(writes()).toHaveLength(2));
  expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ expectedRevision: 10, expectedInstanceRevision: 6 });
  denied = true; fireEvent.click(screen.getByRole('button', { name: 'Reload controls' })); await waitFor(() => expect(screen.queryByRole('combobox', { name: 'Standard profile' })).not.toBeInTheDocument());
});
it('freezes the saved policy and unfreezes with saved overrides while retaining unrelated policy edits', async () => {
  await open('self-management'); await select('Self-management mode', 'Off');
  fireEvent.click(screen.getByRole('button', { name: 'Freeze self-management' })); await screen.findByText(/Organization: Controls organization/);
  fireEvent.click(screen.getAllByRole('button', { name: 'Freeze self-management' }).at(-1)!);
  await screen.findByText('Self-management is frozen'); expect(screen.getByRole('combobox', { name: 'Self-management mode' }).closest('.ant-select')).toHaveTextContent('Off');
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ expectedRevision: 7, expectedInstanceRevision: 4 });
  fireEvent.click(screen.getByRole('button', { name: 'Unfreeze self-management' }));
  fireEvent.click((await screen.findAllByRole('button', { name: 'Unfreeze self-management' })).at(-1)!);
  await waitFor(() => expect(writes()).toHaveLength(2));
  expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ frozen: false, overrides: { mode: { value: 'Assisted' }, scopes: { value: ['Settings'] } } });
  await waitFor(() => expect(screen.queryByText('Self-management is frozen')).not.toBeInTheDocument());
  expect(screen.getByRole('combobox', { name: 'Self-management mode' }).closest('.ant-select')).toHaveTextContent('Off');
  fireEvent.click(screen.getByRole('button', { name: 'Discard Self-management changes' })); expect(screen.getByRole('combobox', { name: 'Self-management mode' }).closest('.ant-select')).toHaveTextContent('Assisted');
});
it('retires a confirmed malformed acknowledgement without replay and recovers by read alone', async () => {
  await open('execution-budgets'); fireEvent.click(screen.getByRole('button', { name: 'Reset to Definition defaults' })); malformedAck = readFailure = true;
  fireEvent.click(screen.getByRole('button', { name: 'Save Execution budgets' })); await screen.findByText('Changes were saved');
  expect(writes()).toHaveLength(1); readFailure = false; fireEvent.click(screen.getByRole('button', { name: 'Reload controls' }));
  await screen.findByRole('combobox', { name: 'Standard inheritance' }); expect(writes()).toHaveLength(1); expect(screen.getByRole('button', { name: 'Save Execution budgets' })).toBeDisabled();
});
it('fails closed on catalog errors, retries, and saves a current model without a stale effort', async () => {
  catalogFailure = true; await open('unattended-model'); await screen.findByRole('alert'); expect(screen.getByRole('button', { name: 'Save Unattended model' })).toBeDisabled();
  catalogFailure = false; fireEvent.click(screen.getByRole('button', { name: 'Reload' })); await select('Unattended model', 'Beta');
  fireEvent.click(screen.getByRole('button', { name: 'Save Unattended model' })); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ expectedRevision: 7, expectedInstanceRevision: 4, catalogKey: 'beta', reasoningEffort: null });
});
it('renders an exact reader without write access or an IAM inspection request', async () => {
  current.canUpdate = false; await open('self-management'); await screen.findByText('Read-only configuration');
  expect(screen.getByRole('combobox', { name: 'Self-management mode' })).toBeDisabled(); expect(screen.getByRole('button', { name: 'Freeze self-management' })).toBeDisabled();
  expect(writes()).toHaveLength(0); expect(fetchMock.mock.calls.some(([url]) => /access|agent-instances|owner-capability/.test(String(url)))).toBe(false);
});
