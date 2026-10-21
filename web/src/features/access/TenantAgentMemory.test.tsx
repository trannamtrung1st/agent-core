import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantAgentMemory } from './TenantAgentMemory';
import type { TenantMemoryPage } from '../../services/tenantMemory';
const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Organization', slug: 'private' }, userId: 'user', principalId: 'person', permissions: [], roles: [], revision: 7 };
const response = (v: unknown, status = 200) => new Response(JSON.stringify(v), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>(); let saved: TenantMemoryPage; let commands: unknown[]; let failed: number;
beforeEach(() => { resetIdentityCsrf(); commands = []; failed = 0; saved = { items: [{ memoryId: 'fact', scope: 'identityUser', kind: 'Preference', status: 'Active', subject: 'Private fact', content: 'PRIVATE_BODY', revision: 3, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), originAgentId: 'agent', source: 'userExplicit', canForget: true }], total: 1, retainedCount: 1, page: 1, pageSize: 25, securityRevision: 7, canReset: true, agentName: 'Memory agent' };
  vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); fetchMock.mockImplementation(async (path, options) => {
    if (String(path).endsWith('/csrf')) return response({ token: 'test' });
    if (String(path).includes('/experience')) return response({ items: [], total: 0, retainedCount: 0, page: 1, pageSize: 25, securityRevision: 7, canReset: false, agentName: 'Memory agent' });
    if (options?.method === 'POST') { commands.push(JSON.parse(options.body as string)); return failed ? response({ reasonCode: 'stale_revision' }, failed) : response({ itemsForgotten: 1, securityRevision: 8 }); }
    return response(saved);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async () => { render(<App><TenantAgentMemory context={context} id="agent" /></App>); await screen.findByText('Private fact'); };
it('inspects escaped private facts and sends exact confirmation witnesses once', async () => {
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Expand row' })); await screen.findByText('PRIVATE_BODY');
  fireEvent.click(screen.getByRole('button', { name: 'Forget Private fact' })); await screen.findByRole('dialog'); expect(commands).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Forget item' })); await screen.findByText('1 learned fact forgotten. Reload Memory to inspect current facts.');
  expect(commands).toEqual([{ scope: 'identityUser', expectedRevision: 7, memoryId: 'fact', expectedMemoryRevision: 3 }]); expect(screen.queryByText('PRIVATE_BODY')).not.toBeInTheDocument();
});
it('resets the exact readable scope and count only after separate confirmation', async () => {
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Forget readable scope' })); await screen.findByRole('dialog');
  fireEvent.click(screen.getByRole('button', { name: 'Forget scope' })); await screen.findByText('1 learned fact forgotten. Reload Memory to inspect current facts.');
  expect(commands).toEqual([{ scope: 'identityUser', expectedRevision: 7, expectedRetainedCount: 1 }]);
});
it('withholds controls without current manage authority', async () => {
  saved.canReset = false; saved.items[0].canForget = false; await open(); expect(screen.getByRole('button', { name: 'Forget readable scope' })).toBeDisabled(); expect(screen.getByRole('button', { name: 'Forget Private fact' })).toBeDisabled(); expect(commands).toHaveLength(0);
});
it('does not replay a stale mutation and retires old private content until explicit reload', async () => {
  failed = 409; await open(); fireEvent.click(screen.getByRole('button', { name: 'Forget Private fact' })); fireEvent.click(await screen.findByRole('button', { name: 'Forget item' }));
  await screen.findByText('Memory needs a fresh read'); expect(screen.queryByText('Private fact')).not.toBeInTheDocument(); expect(commands).toHaveLength(1);
  saved.items = []; saved.total = 0; saved.retainedCount = 0; saved.canReset = false; fireEvent.click(screen.getByRole('button', { name: 'Reload Memory' })); await screen.findByText('No readable learned facts in this scope.'); expect(commands).toHaveLength(1);
});
it('fences a retired tenant read and cannot show its late facts', async () => {
  let finish!: (r: Response) => void; let signal: AbortSignal | undefined; fetchMock.mockImplementationOnce(async (_, options) => { signal = options?.signal as AbortSignal; return new Promise(resolve => { finish = resolve; }); });
  const shown = render(<App><TenantAgentMemory context={context} id="agent" /></App>); await waitFor(() => expect(finish).toBeDefined());
  saved = { ...saved, items: [], total: 0, retainedCount: 0 }; shown.rerender(<App><TenantAgentMemory context={{ ...context, tenant: { ...context.tenant, tenantId: 'other' } }} id="agent" /></App>); await screen.findByText('No readable learned facts in this scope.'); expect(signal?.aborted).toBe(true);
  finish(response({ ...saved, items: [{ subject: 'RETIRED_PRIVATE_FACT' }] })); await waitFor(() => expect(screen.queryByText('RETIRED_PRIVATE_FACT')).not.toBeInTheDocument());
});
it('retires the previously loaded Session when its visible ID draft changes', async () => {
  await open(); fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Memory scope' })); fireEvent.click(await screen.findByText('This Session', { exact: true }));
  const input = await screen.findByRole('textbox', { name: 'Session ID' }); fireEvent.change(input, { target: { value: '11111111-1111-1111-1111-111111111111' } }); fireEvent.click(screen.getByRole('button', { name: 'Load Session memory' }));
  await screen.findByText('Private fact'); fireEvent.change(input, { target: { value: '22222222-2222-2222-2222-222222222222' } }); expect(screen.queryByText('Private fact')).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Forget readable scope' })).not.toBeInTheDocument(); expect(commands).toHaveLength(0);
});
