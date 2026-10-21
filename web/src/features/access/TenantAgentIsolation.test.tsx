import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { stateIsolationDefaults, memoryFields } from '../../services/isolationPresets';
import type { IsolationDraft } from '../../services/stateIsolation';
import { TenantAgentIsolation } from './TenantAgentIsolation';

const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Isolation organization', slug: 'isolation' }, userId: 'user', principalId: 'viewer', permissions: [], roles: [], revision: 7 };
const section = (section: string, effective: Record<string, string | boolean>) => ({ section, effective, definitionDefaults: effective, overrides: {}, sources: Object.fromEntries(Object.keys(effective).map(k => [k, 'definition'])), constraints: Object.fromEntries(Object.entries(effective).map(([k, value]) => [k, { choices: typeof value === 'string' ? [value, 'CurrentRequest', 'Disabled', 'AfterTerminal', 'EphemeralSession', 'CurrentSession', 'InstanceSummary'] : undefined }])) });
const fresh = () => ({ agent: { instance: { instanceId: 'agent', revision: 4, persona: { name: 'Scoped isolation agent' }, activeVersion: 1, lifecycle: 'Active' } }, securityRevision: 7, canUpdate: true, configuration: { revisions: { instance: 4, experience: 0, maintenance: 0 }, experienceEnabled: false, allowMaintenance: false, browserEnabled: false, hostBrowserMode: 'EphemeralSession', settings: [section('stateIsolationPolicy', stateIsolationDefaults), section('memoryPolicy', Object.fromEntries(memoryFields.map(k => [k, true]))), section('voice', { enabled: true })] } });
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>(); let current: ReturnType<typeof fresh>; let failure: 'stale' | 'denied' | 'lost' | 'malformed' | undefined;
beforeEach(() => {
  current = fresh(); failure = undefined; resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (url.endsWith('/preview')) {
      const draft = JSON.parse(String(options?.body)) as IsolationDraft;
      return response({ securityRevision: current.securityRevision, plan: { expected: current.configuration.revisions, draft, fingerprint: 'exact-preview', changes: [{ owner: 'experience', field: 'enabled', before: false, after: true, source: 'instance' }], blockingReasons: [], notice: 'Existing history and Artifacts remain.' } });
    }
    if (url.endsWith('/apply')) {
      if (failure === 'stale') return response({ reasonCode: 'stale_revision' }, 409);
      if (failure === 'denied') return response({ reasonCode: 'permission_denied' }, 403);
      if (failure === 'lost') throw new TypeError('Network lost after commit');
      if (failure === 'malformed') return response({});
      const body = JSON.parse(String(options?.body)); current = { ...current, securityRevision: 8, configuration: { ...current.configuration, experienceEnabled: body.draft.experienceEnabled, revisions: { ...current.configuration.revisions, instance: 5 } } };
      return response({ instance: { instanceId: 'agent', revision: 5 } });
    }
    if (url.endsWith('/state-isolation')) return response(current);
    throw new Error(`Unexpected request ${url}`);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async () => { render(<App><TenantAgentIsolation context={context} id="agent" /></App>); await screen.findByText('Scoped isolation agent'); };
const applies = () => fetchMock.mock.calls.filter(([path]) => String(path).endsWith('/apply'));
async function preview() { fireEvent.click(screen.getByRole('switch', { name: 'Experience enabled' })); fireEvent.click(screen.getByRole('button', { name: 'Preview changes' })); await screen.findByLabelText('Configuration preview'); }
const save = () => fireEvent.click(screen.getByRole('button', { name: 'Save coordinated configuration' }));
it('previews an explicit draft and applies the exact three owner and security witnesses once', async () => {
  await open(); await preview(); expect(applies()).toHaveLength(0); save(); await screen.findByText('Configuration saved');
  expect(applies()).toHaveLength(1); expect(JSON.parse(String(applies()[0][1]?.body))).toMatchObject({ expectedRevision: 7, expected: { instance: 4, experience: 0, maintenance: 0 }, fingerprint: 'exact-preview', draft: { experienceEnabled: true, allowMaintenance: false } });
  expect(screen.getByRole('switch', { name: 'Experience enabled' })).toBeChecked(); expect(screen.getByRole('button', { name: 'Save coordinated configuration' })).toBeDisabled();
});
it('retains the draft after a stale save and requires reload and a fresh deliberate preview', async () => {
  failure = 'stale'; await open(); await preview(); save(); await screen.findByText(/Access has changed since/);
  expect(screen.getByRole('button', { name: 'Preview changes' })).toBeDisabled();
  current.securityRevision = 9; current.configuration.revisions.maintenance = 2;
  fireEvent.click(screen.getByRole('button', { name: 'Reload configuration' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Preview changes' })).toBeEnabled());
  expect(screen.getByRole('switch', { name: 'Experience enabled' })).toBeChecked(); expect(applies()).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Preview changes' })); await screen.findByLabelText('Configuration preview'); failure = undefined; save(); await screen.findByText('Configuration saved');
  expect(JSON.parse(String(applies()[1][1]?.body))).toMatchObject({ expectedRevision: 9, expected: { maintenance: 2 } });
});
it('retains an uncertain submitted draft and never retries the effect during read recovery', async () => {
  failure = 'lost'; await open(); await preview(); save(); await screen.findByText('Read current configuration before continuing');
  await waitFor(() => expect(screen.getByRole('button', { name: 'Reload configuration' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Reload configuration' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Preview changes' })).toBeEnabled());
  expect(screen.getByRole('switch', { name: 'Experience enabled' })).toBeChecked(); expect(applies()).toHaveLength(1);
});
it('retires private fields immediately when a save loses access', async () => {
  failure = 'denied'; await open(); await preview(); save(); await waitFor(() => expect(screen.queryByText('Scoped isolation agent')).not.toBeInTheDocument());
  expect(screen.queryByRole('switch', { name: 'Experience enabled' })).not.toBeInTheDocument(); expect(applies()).toHaveLength(1);
});
it('keeps exact readers read-only without any owner transport or protected observation calls', async () => {
  current.canUpdate = false; await open(); expect(screen.getByRole('switch', { name: 'Experience enabled' })).toBeDisabled();
  expect(fetchMock.mock.calls.every(([path]) => /\/csrf$|\/tenants\/tenant\/agents\/agent\/state-isolation$/.test(String(path)))).toBe(true);
});
it('treats a successful unreadable mutation reply as saved and requires page recovery', async () => {
  failure = 'malformed'; await open(); await preview(); save(); await screen.findByText(/The change was saved, but this view could not finish updating/);
  expect(screen.getByRole('button', { name: 'Save coordinated configuration' })).toBeDisabled(); expect(applies()).toHaveLength(1);
});
