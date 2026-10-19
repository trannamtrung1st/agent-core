import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantBrowserProfile, TenantBrowserResetResult } from '../../services/tenantBrowserProfile';
import { TenantBrowserProfilePanel } from './TenantBrowserProfilePanel';
import { TenantAgentConnections } from './TenantAgentConnections';

const original: TenantBrowserProfile = { instanceId: 'agent', instanceRevision: 4, securityRevision: 7, witness: 'a'.repeat(64), profilePresent: true, liveViews: 2,
  canReset: true, busy: false, lastReset: null, agentName: 'Alex <safe> long account', lifecycle: 'Active' };
const fetchMock = vi.fn<typeof fetch>(); let view: TenantBrowserProfile; let mutation: (body: { operationId: string }) => Promise<Response>; let readStatus = 200;
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const result = (operationId: string, status: TenantBrowserResetResult['status'] = 'reset'): TenantBrowserResetResult => ({ operationId, status,
  reasonCode: status === 'reset' ? 'browser_profile_reset_completed' : status === 'unknown' ? 'browser_profile_reset_unknown' : 'browser_profile_reset_started',
  requestedAt: '2026-10-20T01:00:00Z', recordedAt: status === 'pending' ? null : '2026-10-20T01:00:01Z' });
const posts = () => fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST');
const panel = () => screen.getByRole('region', { name: 'Browser state' });
const show = () => render(<App><TenantBrowserProfilePanel key="tenant:agent" tenant="tenant" id="agent" organization="Scoped organization" /></App>);
async function confirm() { fireEvent.click(await within(panel()).findByRole('button', { name: 'Reset Browser profile' })); const dialog = await screen.findByRole('dialog', { name: 'Reset this Browser profile?' }); fireEvent.click(within(dialog).getByRole('button', { name: 'Reset Browser profile' })); }
beforeEach(() => {
  resetIdentityCsrf(); view = structuredClone(original); readStatus = 200; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  mutation = async body => { const saved = result(body.operationId); view = { ...view, lastReset: saved, instanceRevision: 6, securityRevision: 9, profilePresent: false, liveViews: 0, witness: 'b'.repeat(64) }; return response(saved); };
  fetchMock.mockImplementation(async (path, options) => {
    if (String(path).endsWith('/csrf')) return response({ token: 'csrf' });
    if (String(path).includes('/credential-bindings')) return response({ reasonCode: 'permission_denied' }, 403);
    if (options?.method === 'POST') return mutation(JSON.parse(String(options.body)));
    return readStatus === 200 ? response(view) : response({ reasonCode: 'permission_denied' }, readStatus);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('names the exact Agent and organization, sends only captured witnesses once and requires deliberate readback', async () => {
  show(); fireEvent.click(await screen.findByRole('button', { name: 'Reset Browser profile' }));
  const dialog = await screen.findByRole('dialog', { name: 'Reset this Browser profile?' });
  await waitFor(() => { expect(within(dialog).getByText('Agent: Alex <safe> long account')).toBeVisible(); expect(within(dialog).getByText('Organization: Scoped organization')).toBeVisible(); });
  expect(within(dialog).getByText(/Close all persistent Browser views/)).toBeVisible(); expect(posts()).toHaveLength(0);
  fireEvent.click(within(dialog).getByRole('button', { name: 'Reset Browser profile' }));
  await screen.findByText('Profile reset confirmed'); await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(posts()).toHaveLength(1); const submitted = JSON.parse(String(posts()[0][1]?.body));
  expect(submitted).toEqual({ operationId: expect.any(String), expectedRevision: 7, expectedInstanceRevision: 4, witness: 'a'.repeat(64) });
  expect(posts()[0][0]).toBe('/api/v2/tenants/tenant/agents/agent/browser/profile/reset');
  expect(within(panel()).queryByRole('button', { name: 'Reset Browser profile' })).not.toBeInTheDocument();
  await waitFor(() => expect(screen.getByRole('button', { name: 'Reload Browser state' })).toHaveFocus());
  fireEvent.click(screen.getByRole('button', { name: 'Reload Browser state' })); await screen.findByText('Last reset confirmed');
  expect(screen.getByText('No saved Browser profile')).toBeVisible(); expect(screen.getByText('Open persistent views: 0')).toBeVisible(); expect(posts()).toHaveLength(1);
});

it.each(['unknown', 'pending'] as const)('keeps %s distinct and never repeats the request while reading its recorded outcome', async status => {
  mutation = async body => { const saved = result(body.operationId, status); view = { ...view, lastReset: saved, canReset: status !== 'pending', instanceRevision: 6, securityRevision: 9 }; return response(saved); };
  show(); await confirm(); await screen.findByText(status === 'pending' ? 'Reset pending' : 'Reset outcome unknown');
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  fireEvent.click(screen.getByRole('button', { name: 'Reload Browser state' }));
  if (status === 'pending') { await waitFor(() => expect(screen.getByRole('button', { name: 'Reset Browser profile' })).toBeDisabled()); expect(screen.getByText('Reset pending')).toBeVisible(); }
  else { await screen.findByText('Last reset outcome unknown'); expect(screen.getByRole('button', { name: 'Reset Browser profile' })).toBeEnabled(); }
  expect(posts()).toHaveLength(1);
});

it.each(['network', 'malformed', 'wrong-operation'] as const)('requires readback after a %s reply and never resubmits the old operation', async fault => {
  const actual = mutation; mutation = async body => { await actual(body); if (fault === 'network') throw new TypeError('private transport text'); return fault === 'malformed' ? new Response('broken') : response(result('other-operation')); };
  show(); await confirm(); await screen.findByText('Reset outcome not confirmed');
  expect(screen.queryByText('private transport text')).not.toBeInTheDocument(); await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(within(panel()).queryByText(original.agentName)).not.toBeInTheDocument(); expect(posts()).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Reload Browser state' })); await screen.findByText('Last reset confirmed'); expect(posts()).toHaveLength(1);
  const oldOperation = JSON.parse(String(posts()[0][1]?.body)).operationId; mutation = actual; await confirm(); await screen.findByText('Profile reset confirmed');
  expect(posts()).toHaveLength(2); const next = JSON.parse(String(posts()[1][1]?.body)); expect(next.operationId).not.toBe(oldOperation); expect(next.expectedRevision).toBe(9); expect(next.expectedInstanceRevision).toBe(6);
});

it.each([403, 409])('clears the old observation after refusal %s and uses newly loaded witnesses only after another confirmation', async status => {
  mutation = async () => response({ reasonCode: status === 409 ? 'stale_revision' : 'permission_denied' }, status); show(); await confirm(); await screen.findByText('Reset was refused');
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument()); expect(within(panel()).queryByText(original.agentName)).not.toBeInTheDocument();
  if (status === 403) readStatus = 403; else view = { ...view, securityRevision: 11, instanceRevision: 5, witness: 'c'.repeat(64) };
  fireEvent.click(screen.getByRole('button', { name: 'Reload Browser state' }));
  if (status === 403) { await within(panel()).findByRole('alert'); expect(within(panel()).queryByRole('button', { name: 'Reset Browser profile' })).not.toBeInTheDocument(); }
  else { await waitFor(() => expect(screen.getByRole('button', { name: 'Reset Browser profile' })).toBeEnabled()); await confirm(); await waitFor(() => expect(posts()).toHaveLength(2)); expect(JSON.parse(String(posts()[1][1]?.body))).toMatchObject({ expectedRevision: 11, expectedInstanceRevision: 5, witness: 'c'.repeat(64) }); }
});

it.each(['readonly', 'archived', 'busy'] as const)('explains %s facts without granting reset authority', async mode => {
  view = { ...view, canReset: false, lifecycle: mode === 'archived' ? 'Archived' : 'Active', busy: mode === 'busy' }; show();
  expect(await screen.findByRole('button', { name: 'Reset Browser profile' })).toBeDisabled();
  expect(screen.getByText(mode === 'archived' ? /Restore this instance/ : mode === 'busy' ? /Browser is busy/ : /Reset requires Browser management access/)).toBeVisible(); expect(posts()).toHaveLength(0);
});

it('retains independent Browser facts when credential bindings are denied', async () => {
  const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Scoped organization', slug: 'scope' }, userId: 'user', principalId: 'human', permissions: [], roles: [], revision: 7 };
  render(<App><TenantAgentConnections context={context} id="agent" /></App>);
  await screen.findByText(/You do not have permission/); expect(await within(panel()).findByText(original.agentName)).toBeVisible(); expect(within(panel()).getByRole('button', { name: 'Reset Browser profile' })).toBeEnabled();
});

it('returns focus after cancellation and destroys the named confirmation when its owner unmounts', async () => {
  const mounted = show(); const reset = await screen.findByRole('button', { name: 'Reset Browser profile' }); reset.focus(); fireEvent.click(reset);
  let dialog = await screen.findByRole('dialog'); fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' })); await waitFor(() => expect(reset).toHaveFocus());
  fireEvent.click(reset); dialog = await screen.findByRole('dialog'); mounted.unmount(); await waitFor(() => expect(dialog).not.toBeInTheDocument()); expect(posts()).toHaveLength(0);
});

it('does not publish a late completed command into another mounted Agent', async () => {
  let release!: (response: Response) => void; mutation = () => new Promise(resolve => { release = resolve; }); const mounted = show(); await confirm(); await waitFor(() => expect(posts()).toHaveLength(1));
  view = { ...original, instanceId: 'other', agentName: 'Other Agent' };
  mounted.rerender(<App><TenantBrowserProfilePanel key="tenant:other" tenant="tenant" id="other" organization="Scoped organization" /></App>); await screen.findByText('Other Agent');
  release(response(result(JSON.parse(String(posts()[0][1]?.body)).operationId))); await waitFor(() => expect(screen.queryByText('Profile reset confirmed')).not.toBeInTheDocument()); expect(screen.getByText('Other Agent')).toBeVisible();
});
