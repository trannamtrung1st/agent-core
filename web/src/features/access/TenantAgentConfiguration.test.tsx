import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantAgentConfiguration } from './TenantAgentConfiguration';

const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Editor organization', slug: 'editor' }, userId: 'user', principalId: 'viewer', permissions: [], roles: [], revision: 7 };
const persona = { name: 'Alex', role: 'Guide', description: 'Scoped guidance', tone: 'Calm' };
const editor = () => ({ agent: { principalId: 'agent-principal', systemIdentityId: 'identity', instance: { instanceId: 'agent', persona, personaRevision: 2, revision: 4, activeVersion: 1, lifecycle: 'Active' } }, canUpdate: true, securityRevision: 7, unavailableSettings: ['sandboxPolicy'], settings: [{ section: 'conversationPolicy', instanceRevision: 4, definitionId: 'definition', definitionVersion: 1, effective: { language: 'en' }, definitionDefaults: { language: 'en' }, overrides: {}, sources: { language: 'definition' }, configurationHash: 'hash' }] });
const fetchMock = vi.fn<typeof fetch>(); let current: ReturnType<typeof editor>; let stale = false; let denied = false;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
beforeEach(() => {
  resetIdentityCsrf(); current = editor(); stale = false; denied = false; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (url.endsWith('/chat/models')) return response({ defaultOfferingId: null, models: [] });
    if (options?.method === 'PUT') return stale ? response({ reasonCode: 'stale_revision' }, 409) : response({ instance: { ...current.agent.instance, persona: JSON.parse(String(options.body)).persona, revision: 5, personaRevision: 3 } });
    if (url.endsWith('/editor')) return denied ? response({ reasonCode: 'permission_denied' }, 403) : response(current);
    throw new Error(`Unexpected request: ${url}`);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async (page: 'profile' | 'settings' = 'profile') => { render(<App><TenantAgentConfiguration context={context} id="agent" page={page} /></App>); await screen.findByText('Alex'); };
const writes = () => fetchMock.mock.calls.filter(([, options]) => options?.method === 'PUT');

it('synchronizes Form/JSON and saves only an explicit canonical persona with exact witnesses', async () => {
  await open(); fireEvent.change(screen.getByRole('textbox', { name: 'Persona name' }), { target: { value: 'Scoped guide' } });
  fireEvent.click(screen.getByRole('tab', { name: 'JSON' }));
  const json = screen.getByRole('textbox', { name: 'Persona JSON' }); expect((json as HTMLTextAreaElement).value).toContain('Scoped guide');
  fireEvent.change(json, { target: { value: 'null' } }); fireEvent.click(screen.getByRole('button', { name: 'Save persona' }));
  expect(await screen.findByText(/Persona JSON must be an object/)).toBeVisible(); expect(writes()).toHaveLength(0);
  fireEvent.change(json, { target: { value: JSON.stringify({ ...persona, name: 'Scoped guide' }) } }); fireEvent.click(screen.getByRole('button', { name: 'Save persona' }));
  await waitFor(() => expect(writes()).toHaveLength(1));
  const [url, options] = writes()[0]; expect(url).toBe('/api/v2/tenants/tenant/agents/agent/persona');
  expect(JSON.parse(String(options?.body))).toEqual({ persona: { ...persona, name: 'Scoped guide' }, expectedRevision: 7, expectedInstanceRevision: 4, expectedPersonaRevision: 2 });
});

it('retains a stale draft through read recovery and hides the editor when read access is lost', async () => {
  stale = true; await open(); fireEvent.change(screen.getByRole('textbox', { name: 'Persona name' }), { target: { value: 'Retained draft' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save persona' })); await screen.findByText(/Access has changed since you opened/);
  current = { ...current, securityRevision: 9, agent: { ...current.agent, instance: { ...current.agent.instance, revision: 6 } } };
  fireEvent.click(screen.getByRole('button', { name: 'Reload profile' })); expect(await screen.findByRole('textbox', { name: 'Persona name' })).toHaveValue('Retained draft'); expect(writes()).toHaveLength(1);
  stale = false; fireEvent.click(screen.getByRole('button', { name: 'Save persona' })); await waitFor(() => expect(writes()).toHaveLength(2));
  expect(JSON.parse(String(writes()[1][1]?.body))).toMatchObject({ expectedRevision: 9, expectedInstanceRevision: 6 });
  denied = true; fireEvent.click(screen.getByRole('button', { name: 'Reload profile' })); await waitFor(() => expect(screen.queryByRole('textbox', { name: 'Persona name' })).not.toBeInTheDocument());
});

it('renders exact read-only Settings without requiring an IAM inspector or legacy catalog', async () => {
  current.canUpdate = false; await open('settings'); await screen.findByText('Read-only configuration');
  fireEvent.click(screen.getByRole('button', { name: 'Conversation' })); expect(screen.getByRole('textbox', { name: 'Language' })).toBeDisabled();
  expect(screen.getByText('Sandbox has a separate settings page')).toBeVisible();
  expect(fetchMock.mock.calls.every(([path]) => String(path).startsWith('/api/v2/'))).toBe(true);
  expect(fetchMock.mock.calls.some(([path]) => /access|sandbox|agent-instances/.test(String(path)))).toBe(false);
});
