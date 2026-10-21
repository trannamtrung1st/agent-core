import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantAgentState } from './TenantAgentState';
import type { RuntimeResourceState } from '../../services/tenantRuntimeState';

const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Observed organization', slug: 'observed' }, userId: 'user', principalId: 'reader', permissions: [], roles: [], revision: 7 };
const current = () => ({ agent: { instance: { instanceId: 'agent', persona: { name: 'Visible runtime agent' }, activeVersion: 1, lifecycle: 'Active' } }, securityRevision: 7,
  observation: { agentInstanceId: 'agent', scope: 'readableInstance', observedAtUtc: new Date().toISOString(), freshThroughUtc: new Date(Date.now() + 15000).toISOString(), availability: 'available',
    sessions: { open: 2, paused: 0, terminal: 3, lastActivityAtUtc: null }, hostedSessions: { runtimes: 1, attached: 1 }, runs: { queued: 0, executing: 1, waitingApproval: 0, waitingSignal: 0, retrying: 0, terminal: 2 },
    resources: [{ kind: 'browser', availability: 'available', sharing: 'isolated', liveContexts: 0, openPages: 0, code: null }, { kind: 'sandbox', availability: 'available', sharing: 'isolated', activeGenerations: 0, activeCommands: 0, cleanupPending: 0, code: null }] as RuntimeResourceState[], issues: [] as { kind: string; code: string }[] } });
const fetchMock = vi.fn<typeof fetch>(); let saved: ReturnType<typeof current>; let denied = false;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
beforeEach(() => { saved = current(); denied = false; resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockImplementation(async () => denied ? response({ reasonCode: 'permission_denied' }, 403) : response(saved)); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async () => { render(<App><TenantAgentState context={context} id="agent" /></App>); await screen.findByText('Visible runtime agent'); };
it('renders readable metadata and refreshes through a canonical read without mutations', async () => {
  await open(); const sessions = screen.getByRole('region', { name: 'Visible Session state' }); expect(sessions).toHaveTextContent(/Open Sessions\s*2/); expect(sessions).toHaveTextContent(/Paused Sessions\s*0/);
  saved.observation.sessions.open = 4; fireEvent.click(screen.getByRole('button', { name: 'Refresh state' })); await waitFor(() => expect(screen.getByRole('region', { name: 'Visible Session state' })).toHaveTextContent(/Open Sessions\s*4/));
  expect(fetchMock.mock.calls.every(([path, options]) => String(path) === '/api/v2/tenants/tenant/agents/agent/runtime-state' && (!options?.method || options.method === 'GET'))).toBe(true);
});
it('keeps restricted counts unknown instead of zero and explains source access', async () => {
  saved.observation.runs = null!; saved.observation.issues = [{ kind: 'runs', code: 'read_restricted' }]; saved.observation.availability = 'partial'; await open();
  const runs = screen.getByRole('region', { name: 'Visible Run state' }); expect(within(runs).getAllByText('Unknown')).toHaveLength(6); expect(runs).toHaveTextContent('requires additional read access');
  expect(screen.getByRole('region', { name: 'Visible Session state' })).toHaveTextContent(/Open Sessions\s*2/);
});
it('preserves readable sources when another source times out', async () => {
  saved.observation.resources[0] = { ...saved.observation.resources[0], availability: 'unavailable', liveContexts: undefined, openPages: undefined, code: 'observation_timeout' };
  saved.observation.availability = 'partial'; await open(); expect(screen.getByRole('region', { name: 'Readable Browser state' })).toHaveTextContent('did not respond within the observation window');
  expect(within(screen.getByRole('region', { name: 'Readable Browser state' })).getAllByText('Unknown')).toHaveLength(2);
  expect(screen.getByRole('region', { name: 'Visible Run state' })).toHaveTextContent(/Executing\s*1/);
});
it('hides expired facts and recovers only after a fresh read', async () => {
  saved.observation.freshThroughUtc = new Date(Date.now() - 1).toISOString(); await open(); await screen.findByText('This observation expired'); expect(screen.queryByRole('region', { name: 'Visible Session state' })).not.toBeInTheDocument();
  saved = current(); fireEvent.click(screen.getByRole('button', { name: 'Refresh state' })); await screen.findByRole('region', { name: 'Visible Session state' }); expect(screen.queryByText('This observation expired')).not.toBeInTheDocument();
});
it('retires private metadata when refreshed access is denied', async () => {
  await open(); denied = true; fireEvent.click(screen.getByRole('button', { name: 'Refresh state' })); await screen.findByRole('alert');
  expect(screen.queryByText('Visible runtime agent')).not.toBeInTheDocument(); expect(screen.queryByRole('region', { name: 'Visible Session state' })).not.toBeInTheDocument();
});
it('aborts a retired scope and cannot display its late observation', async () => {
  let finish!: (value: Response) => void; let signal: AbortSignal | undefined; fetchMock.mockImplementationOnce(async (_, options) => { signal = options?.signal as AbortSignal; return new Promise(resolve => { finish = resolve; }); });
  const rendered = render(<App><TenantAgentState context={context} id="agent" /></App>);
  await waitFor(() => expect(finish).toBeDefined()); saved.agent.instance.persona.name = 'New scope agent';
  rendered.rerender(<App><TenantAgentState context={{ ...context, tenant: { ...context.tenant, tenantId: 'second' } }} id="agent" /></App>); await screen.findByText('New scope agent'); expect(signal?.aborted).toBe(true);
  finish(response({ ...current(), agent: { instance: { ...current().agent.instance, persona: { name: 'Retired private agent' } } } }));
  await waitFor(() => expect(screen.queryByText('Retired private agent')).not.toBeInTheDocument());
});
