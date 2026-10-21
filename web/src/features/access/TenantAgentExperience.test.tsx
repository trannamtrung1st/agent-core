import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantExperiencePage } from '../../services/tenantExperience';
import { TenantAgentExperience } from './TenantAgentExperience';
const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Organization', slug: 'private' }, userId: 'user', principalId: 'person', permissions: [], roles: [], revision: 7 };
const response = (v: unknown, status = 200) => new Response(JSON.stringify(v), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>(); let saved: TenantExperiencePage; let commands: { path: string; body: unknown }[]; let failure: number | 'lost';
beforeEach(() => {
  resetIdentityCsrf(); commands = []; failure = 0;
  saved = { items: [{ experienceId: 'experience', scope: 'AgentUser', visibility: 'Eligible', content: { goal: 'Observed frontend grouping', attempts: ['<script>plain evidence</script>'], decisions: [], outcomes: ['Observed result'], corrections: [], unresolved: [], difficulties: [], lessons: ['Inspect actual provenance'] }, revision: 3, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), sourceKind: 'Session', sourceId: 'source-session', throughCursor: 8, sourceExperienceIds: [], canManage: true }], total: 1, retainedCount: 1, page: 1, pageSize: 25, securityRevision: 7, agentName: 'Experience agent', canReset: true };
  vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); fetchMock.mockImplementation(async (path, options) => {
    if (String(path).endsWith('/csrf')) return response({ token: 'test' });
    if (options?.method === 'POST') { commands.push({ path: String(path), body: JSON.parse(options.body as string) }); if (failure === 'lost') throw new TypeError('Reply unavailable'); return failure ? response({ reasonCode: 'stale_revision' }, failure) : response({ changed: 1, securityRevision: 8 }); }
    return response(saved);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async () => { render(<App><TenantAgentExperience context={context} id="agent" /></App>); await screen.findByText('Observed frontend grouping'); };
it('inspects literal structured content and provenance then confirms exact suppression once', async () => {
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Expand row' })); await screen.findByText('<script>plain evidence</script>'); expect(document.querySelector('script')).toBeNull(); await screen.findByText('source-session');
  fireEvent.click(screen.getByRole('button', { name: 'Suppress Observed frontend grouping' })); await screen.findByRole('dialog'); expect(commands).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Suppress observation' })); await screen.findByText('1 observation changed. Reload Experience to inspect the saved state.');
  expect(commands).toEqual([{ path: '/api/v2/tenants/tenant/agents/agent/experience/visibility', body: { expectedRevision: 7, experienceId: 'experience', expectedExperienceRevision: 3, visibility: 'Suppressed' } }]);
  expect(screen.queryByText('<script>plain evidence</script>')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Reset readable Experience' })).toBeDisabled();
});
it('restores only suppressed observations and rechecks through a fresh read after success', async () => {
  saved.items[0].visibility = 'Suppressed'; await open(); fireEvent.click(screen.getByRole('button', { name: 'Restore Observed frontend grouping' })); await screen.findByRole('dialog'); fireEvent.click(screen.getByRole('button', { name: 'Restore to recall' }));
  await screen.findByText('1 observation changed. Reload Experience to inspect the saved state.'); expect(commands[0].body).toEqual({ expectedRevision: 7, experienceId: 'experience', expectedExperienceRevision: 3, visibility: 'Eligible' });
  saved.items[0].visibility = 'Eligible'; saved.items[0].revision = 4; saved.securityRevision = 8; fireEvent.click(screen.getByRole('button', { name: 'Reload Experience' })); await screen.findByText('Observed frontend grouping'); expect(screen.getByRole('button', { name: 'Suppress Observed frontend grouping' })).toBeEnabled();
});
it('confirms content deletion with separate retention disclosure and shows the retained tombstone', async () => {
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Delete Observed frontend grouping' })); const dialog = await screen.findByRole('dialog'); expect(dialog).toHaveTextContent('retains provenance'); expect(dialog).toHaveTextContent('Source conversations'); fireEvent.click(screen.getByRole('button', { name: 'Delete observation' }));
  await screen.findByText('1 observation changed. Reload Experience to inspect the saved state.'); expect(commands[0].body).toEqual({ expectedRevision: 7, experienceId: 'experience', expectedExperienceRevision: 3, visibility: 'Deleted' });
  saved.items[0].content = null; saved.items[0].visibility = 'Deleted'; saved.items[0].canManage = false; saved.items[0].revision = 4; saved.retainedCount = 0; saved.canReset = false;
  fireEvent.click(screen.getByRole('button', { name: 'Reload Experience' })); await screen.findByText('Content removed'); expect(screen.queryByRole('button', { name: /Suppress|Restore/ })).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Delete observation' })).toBeDisabled();
});
it('resets the exact readable retained count independently of history filtering', async () => {
  await open(); fireEvent.click(screen.getByRole('switch', { name: 'Include Experience history' })); await waitFor(() => expect(fetchMock.mock.calls.some(([path]) => String(path).includes('includeHistorical=false'))).toBe(true)); await screen.findByText('Observed frontend grouping');
  fireEvent.click(screen.getByRole('button', { name: 'Reset readable Experience' })); const dialog = await screen.findByRole('dialog'); expect(dialog).toHaveTextContent('Other Users’ private records are excluded'); fireEvent.click(within(dialog).getByRole('button', { name: 'Reset readable Experience' }));
  await screen.findByText('1 observation changed. Reload Experience to inspect the saved state.'); expect(commands).toEqual([{ path: '/api/v2/tenants/tenant/agents/agent/experience/reset', body: { expectedRevision: 7, expectedRetainedCount: 1 } }]);
});
it.each([409, 'lost'] as const)('requires deliberate readback after %s without replaying a change', async error => {
  failure = error; await open(); fireEvent.click(screen.getByRole('button', { name: 'Suppress Observed frontend grouping' })); await screen.findByRole('dialog'); fireEvent.click(screen.getByRole('button', { name: 'Suppress observation' }));
  await screen.findByText('Experience needs a fresh read'); expect(commands).toHaveLength(1); expect(screen.queryByText('Observed frontend grouping')).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Reset readable Experience' })).toBeDisabled();
  failure = 0; saved.securityRevision = 9; fireEvent.click(screen.getByRole('button', { name: 'Reload Experience' })); await screen.findByText('Observed frontend grouping'); expect(commands).toHaveLength(1);
});
it('keeps historical provenance inspectable without allowing superseded restoration or missing manage rights', async () => {
  saved.items[0].visibility = 'Superseded'; saved.items[0].canManage = false; saved.canReset = false; await open(); expect(screen.queryByRole('button', { name: /Suppress|Restore/ })).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Delete Observed frontend grouping' })).toBeDisabled(); expect(screen.getByRole('button', { name: 'Reset readable Experience' })).toBeDisabled(); fireEvent.click(screen.getByRole('button', { name: 'Expand row' })); await screen.findByText('source-session'); expect(commands).toHaveLength(0);
});
it('retires a pending command and modal when the actual tenant scope changes', async () => {
  let finish: ((value: Response) => void) | undefined; let signal: AbortSignal | null | undefined;
  fetchMock.mockImplementation(async (path, options) => { if (String(path).endsWith('/csrf')) return response({ token: 'test' }); if (options?.method === 'POST') { signal = options.signal; return new Promise<Response>(resolve => { finish = resolve; }); } return response(saved); });
  const shown = render(<App><TenantAgentExperience context={context} id="agent" /></App>); await screen.findByText('Observed frontend grouping'); fireEvent.click(screen.getByRole('button', { name: 'Suppress Observed frontend grouping' })); await screen.findByRole('dialog'); fireEvent.click(screen.getByRole('button', { name: 'Suppress observation' })); await waitFor(() => expect(finish).toBeDefined());
  saved = { ...saved, items: [], retainedCount: 0, total: 0 }; shown.rerender(<App><TenantAgentExperience context={{ ...context, tenant: { ...context.tenant, tenantId: 'other' } }} id="agent" /></App>); await screen.findByText('No readable Experience observations.'); expect(signal?.aborted).toBe(true); finish!(response({ changed: 1 })); await waitFor(() => expect(screen.queryByText('1 observation changed. Reload Experience to inspect the saved state.')).not.toBeInTheDocument());
});
