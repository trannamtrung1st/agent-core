import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import type { SystemCredential } from '../../services/adminApi';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantAgentConnections, type TenantBindingPage } from './TenantAgentConnections';

const context: TenantContext = { tenant: { tenantId: 'tenant', name: 'Binding organization', slug: 'binding' }, userId: 'user', principalId: 'viewer', permissions: [], roles: [], revision: 7 };
const credential: SystemCredential = { credentialId: 'credential', displayName: 'Exact shared account', kind: 'Password', status: 'Active', metadata: {}, allowedOrigins: [], revision: 3, bindingCount: 1, createdAtUtc: '', updatedAtUtc: '' };
const view: TenantBindingPage = { items: [{ bindingId: 'binding', credentialId: 'credential', reference: 'primary', revision: 2, credential }], total: 1, page: 0, pageSize: 10, allRead: false, agentName: 'Alex <safe>', lifecycle: 'Active', instanceRevision: 4, securityRevision: 7, canUpdate: true, removableBindingIds: ['binding'] };
const fetchMock = vi.fn<typeof fetch>(); let current: TenantBindingPage; let denied = false; let stale = false; let bind = true;
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
beforeEach(() => {
  resetIdentityCsrf(); current = view; denied = false; stale = false; bind = true; fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path);
    if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (options?.method && options.method !== 'GET') return stale ? response({ reasonCode: 'stale_revision' }, 409) : response({});
    if (url.includes('/credential-bindings')) return denied ? response({ reasonCode: 'permission_denied' }, 403) : response(current);
    if (url.includes('/credentials?')) return response({ items: [{ tenantId: 'tenant', credential }], total: 1, page: 0, pageSize: 10 });
    if (url.endsWith('/access')) return response({ permissions: bind ? ['credentials.read', 'credentials.bind'] : ['credentials.read'], revision: 7 });
    if (url.endsWith('/credentials/credential')) return response({ tenantId: 'tenant', credential });
    throw new Error(`Unexpected request: ${url}`);
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async () => { render(<App><TenantAgentConnections context={context} id="agent" /></App>); await screen.findByText('Alex <safe>'); };
const select = async () => {
  fireEvent.mouseDown(await screen.findByRole('combobox', { name: /Credential/ }));
  fireEvent.click(await screen.findByText('Exact shared account · General credential', { selector: '.ant-select-item-option-content' }));
};

it('requires an exact confirmation and sends all displayed removal witnesses to the canonical route', async () => {
  await open(); fireEvent.click(screen.getByRole('button', { name: 'Remove binding primary' }));
  const dialog = await screen.findByRole('dialog');
  await waitFor(() => { for (const text of ['Agent: Alex <safe>', 'Alias: primary', 'Credential: Exact shared account', 'Organization: Binding organization']) expect(within(dialog).getByText(text)).toBeVisible(); });
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'DELETE')).toHaveLength(0);
  fireEvent.click(within(dialog).getByRole('button', { name: 'Remove binding' }));
  await waitFor(() => expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'DELETE')).toHaveLength(1));
  expect(fetchMock.mock.calls.find(([, options]) => options?.method === 'DELETE')![0]).toBe('/api/v2/tenants/tenant/agents/agent/credential-bindings/binding?expectedRevision=7&expectedInstanceRevision=4&expectedBindingRevision=2');
});

it('keeps a failed draft, reloads witnesses without resubmitting, and normalizes only the explicit save payload', async () => {
  stale = true; await open(); fireEvent.click(screen.getByRole('button', { name: 'Add binding' })); await select();
  const dialog = screen.getByRole('dialog', { name: 'Add credential binding' });
  fireEvent.change(within(dialog).getByLabelText('Alias'), { target: { value: ' Shared-Operations ' } });
  await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Save binding' })).toBeEnabled());
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save binding' }));
  await within(dialog).findByText(/Access has changed since you opened/);
  expect(within(dialog).getByLabelText('Alias')).toHaveValue(' Shared-Operations ');
  const mutations = () => fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST');
  expect(mutations()).toHaveLength(1);
  expect(JSON.parse(String(mutations()[0][1]?.body))).toEqual({ credentialId: 'credential', reference: 'shared-operations', expectedRevision: 7, expectedInstanceRevision: 4, expectedCredentialRevision: 3 });
  current = { ...view, securityRevision: 9, instanceRevision: 5 }; fireEvent.click(within(dialog).getByRole('button', { name: 'Reload latest' }));
  await waitFor(() => expect(within(dialog).getByLabelText('Alias')).toHaveValue(' Shared-Operations '));
  expect(mutations()).toHaveLength(1); stale = false;
  await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Save binding' })).toBeEnabled()); fireEvent.click(within(dialog).getByRole('button', { name: 'Save binding' }));
  await waitFor(() => expect(mutations()).toHaveLength(2));
  expect(JSON.parse(String(mutations()[1][1]?.body))).toMatchObject({ expectedRevision: 9, expectedInstanceRevision: 5, reference: 'shared-operations' });
});

it('does not confuse readable credentials with bind authority', async () => {
  bind = false; await open(); fireEvent.click(screen.getByRole('button', { name: 'Add binding' })); await select();
  const dialog = screen.getByRole('dialog', { name: 'Add credential binding' });
  expect(await within(dialog).findByText('Bind access is required')).toBeVisible(); expect(within(dialog).getByRole('button', { name: 'Save binding' })).toBeDisabled();
});

it('hides editing for read-only/archived views and removes protected content after a denied reload', async () => {
  current = { ...view, canUpdate: false, lifecycle: 'Archived', removableBindingIds: [] }; await open();
  expect(screen.queryByRole('button', { name: 'Add binding' })).not.toBeInTheDocument(); expect(screen.queryByRole('button', { name: 'Remove binding primary' })).not.toBeInTheDocument();
  expect(screen.getByText('Archived instance')).toBeVisible(); denied = true; fireEvent.click(screen.getByRole('button', { name: 'Reload connections' }));
  await screen.findByText(/You do not have permission/); expect(screen.queryByText('Exact shared account')).not.toBeInTheDocument(); expect(screen.queryByText('Alex <safe>')).not.toBeInTheDocument();
});
