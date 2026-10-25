import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { type TenantSpace } from '../../services/tenantSpaces';
import { TenantSpaceResources } from './TenantSpaceResources';
const tenant = '11111111-1111-4111-8111-111111111111'; const id = '22222222-2222-4222-8222-222222222222';
const context = { tenant: { tenantId: tenant, name: 'Project organization' }, permissions: [], revision: 8 } as unknown as TenantContext;
const space: TenantSpace = { tenantId: tenant, spaceId: id, kind: 1, ownerInstanceId: null, name: 'Project Space', description: '', instructions: '', environmentsEnabled: false, revision: 3 };
const fetchMock = vi.fn(); const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
let conflict = false;
beforeEach(() => { conflict = false; resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (url: string, options?: RequestInit) => {
    if (url.endsWith('/csrf')) return response({ token: 'fixture-csrf' });
    if (url.endsWith('/access/me')) return response({ revision: 8 });
    if (options?.method === 'PUT') { const body = JSON.parse(String(options.body)); if (conflict) return response({ title: 'Current snapshot changed', reasonCode: 'stale_revision' }, 409); return response({ resourceId: url.split('/').at(-1), spaceId: id, revision: 1, logicalPath: body.logicalPath }); }
    if (url.includes('/resources?')) return response({ items: [], total: 0, page: 0, pageSize: 50 });
    return response({ ...space, revision: 4 });
  });
});
afterEach(() => { resetIdentityCsrf(); vi.unstubAllGlobals(); });
it('registers one stable Space source ID with exact parent/source revisions and native form controls', async () => {
  const changed = vi.fn(); render(<App><TenantSpaceResources context={context} space={space} access={{ permissions: ['resources.manage'], securityRevision: 8, spaceRevision: 3 }} onChanged={changed} /></App>);
  fireEvent.click(screen.getByRole('button', { name: 'Register snapshot' })); const drawer = await screen.findByRole('dialog', { name: 'Register file snapshot' });
  fireEvent.change(within(drawer).getByRole('textbox', { name: 'Home source file' }), { target: { value: '/home/policy.txt' } });
  fireEvent.change(within(drawer).getByRole('textbox', { name: 'Snapshot logical path' }), { target: { value: 'knowledge/policy.txt' } });
  await act(async () => { fireEvent.click(within(drawer).getByRole('button', { name: 'Register snapshot' })); });
  await waitFor(() => expect(changed).toHaveBeenCalledOnce());
  const writes = fetchMock.mock.calls.filter(([, options]) => options?.method === 'PUT'); expect(writes).toHaveLength(1);
  expect(writes[0][0]).toMatch(new RegExp(`/spaces/${id}/resources/[a-f0-9-]{36}$`));
  expect(JSON.parse(String(writes[0][1].body))).toEqual({ sourcePath: '/home/policy.txt', logicalPath: 'knowledge/policy.txt', kind: 'Knowledge', expectedRevision: 8, expectedSpaceRevision: 3, expectedResourceRevision: 0 });
});
it('preserves a conflicted snapshot draft and blocks repeat save until deliberate parent refresh', async () => {
  conflict = true; render(<App><TenantSpaceResources context={context} space={space} access={{ permissions: ['resources.manage'], securityRevision: 8, spaceRevision: 3 }} onChanged={vi.fn()} /></App>);
  fireEvent.click(screen.getByRole('button', { name: 'Register snapshot' })); const drawer = await screen.findByRole('dialog', { name: 'Register file snapshot' });
  fireEvent.change(within(drawer).getByRole('textbox', { name: 'Home source file' }), { target: { value: '/home/keep.txt' } });
  fireEvent.change(within(drawer).getByRole('textbox', { name: 'Snapshot logical path' }), { target: { value: 'keep.txt' } });
  await act(async () => { fireEvent.click(within(drawer).getByRole('button', { name: 'Register snapshot' })); });
  await within(drawer).findByRole('button', { name: 'Reload current snapshot · keep draft' });
  expect(within(drawer).getByRole('button', { name: 'Register snapshot' })).toBeDisabled();
  expect(within(drawer).getByRole('textbox', { name: 'Snapshot logical path' })).toHaveValue('keep.txt');
  await act(async () => { fireEvent.click(within(drawer).getByRole('button', { name: 'Reload current snapshot · keep draft' })); });
  await waitFor(() => expect(within(drawer).getByRole('button', { name: 'Register snapshot' })).toBeEnabled());
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'PUT')).toHaveLength(1);
});
