import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import { TenantEvents } from './TenantEvents';
import type { TenantWebhook, TenantWebhookPage } from '../../services/tenantWebhooks';

const context = { tenant: { tenantId: 'tenant', name: 'Webhook organization', slug: 'webhooks' }, principalId: 'human', permissions: ['core-events.read'], roles: [], revision: 8 } as unknown as TenantContext;
const source = (): TenantWebhook => ({ resourceId: 'source', displayName: 'Exact signed source', eventKey: 'orders.received', status: 'active', revision: 2, securityRevision: 8, canManage: true,
  createdAtUtc: '2026-10-21T00:00:00Z', updatedAtUtc: '2026-10-21T00:00:00Z', ingressPath: '/api/v2/hooks/tenant/source' });
let current: TenantWebhook; let collection: TenantWebhookPage; let stale: boolean; let unknown: boolean; let denied: boolean; let invalidAck: boolean;
const token = 'a'.repeat(43); const rotated = 'b'.repeat(43); const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const fetchMock = vi.fn<typeof fetch>(); const writes = () => fetchMock.mock.calls.filter(([, options]) => options?.method && options.method !== 'GET');
beforeEach(() => {
  resetIdentityCsrf(); current = source(); collection = { items: [current], total: 1, page: 0, pageSize: 10, securityRevision: 8, canCreate: true }; stale = unknown = denied = invalidAck = false;
  fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockImplementation(async (path, options) => {
    const url = String(path); if (url.endsWith('/csrf')) return response({ token: 'csrf' });
    if (url.endsWith('/events/builtin')) return response({ items: [{ key: 'run.completed', description: 'Shared read-only completion definition.' }] });
    if (options?.method && options.method !== 'GET') {
      if (unknown) throw new TypeError('Connection lost'); if (stale) return response({ reasonCode: 'stale_revision' }, 409); if (invalidAck) return response({ source: current });
      const body = JSON.parse(String(options.body));
      if (url.endsWith('/rotate')) { current = { ...current, revision: current.revision + 1, securityRevision: 9, status: 'active' }; return response({ source: current, token: rotated }); }
      if (url.endsWith('/revoke')) { current = { ...current, revision: current.revision + 1, securityRevision: 9, status: 'revoked' }; return response(current); }
      if (options.method === 'PUT') { current = { ...current, displayName: body.displayName, revision: current.revision + 1 }; return response(current); }
      current = { ...current, displayName: body.displayName, eventKey: body.eventKey }; return response({ source: current, token }, 201);
    }
    if (url.includes('/receipts?')) return denied ? response({ reasonCode: 'permission_denied' }, 403) : response({ source: current, items: [{ eventId: 'receipt', sourceEventId: 'external-1', sourceRevision: 2, occurredAtUtc: current.createdAtUtc, admittedAtUtc: current.createdAtUtc, data: { title: '<script>Untrusted literal</script>', tenantId: 'untrusted-data-only' } }], total: 1, page: 0, pageSize: 10 });
    if (url.includes('/webhooks?')) return response({ ...collection, items: denied ? [] : [current], total: denied ? 0 : 1 });
    if (url.endsWith('/webhooks/source')) return denied ? response({ reasonCode: 'permission_denied' }, 403) : response(current);
    throw new Error(`Unexpected endpoint ${url}`);
  });
});
afterEach(() => { cleanup(); Reflect.deleteProperty(navigator, 'clipboard'); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const open = async (selectedContext = context) => { render(<App><TenantEvents context={selectedContext} /></App>); await screen.findByText('Exact signed source'); };
const inspect = async () => { fireEvent.click(screen.getByRole('button', { name: 'Inspect webhook' })); const dialog = await screen.findByRole('dialog', { name: 'Webhook details' }); await waitFor(() => expect(within(dialog).getByRole('textbox', { name: 'Webhook name' })).toHaveValue(current.displayName)); return dialog; };
it('creates a scoped source with exact witnesses and shows the issued token once without saving it in browser storage', async () => {
  const copy = vi.fn().mockResolvedValue(undefined); Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: copy } });
  const storage = vi.spyOn(Storage.prototype, 'setItem'); await open(); fireEvent.click(screen.getByRole('button', { name: 'New webhook' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Webhook name' }), { target: { value: 'New exact source' } }); fireEvent.change(screen.getByRole('textbox', { name: 'Webhook event key' }), { target: { value: 'exact.received' } });
  fireEvent.click(screen.getByRole('button', { name: 'Create webhook' })); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ displayName: 'New exact source', eventKey: 'exact.received', expectedRevision: 8 });
  expect(await screen.findByLabelText('New webhook token')).toHaveValue(token); expect(screen.getByLabelText('New webhook token')).toHaveAttribute('type', 'password');
  fireEvent.click(screen.getByRole('button', { name: 'Copy token' })); await waitFor(() => expect(copy).toHaveBeenCalledWith(token));
  expect(storage.mock.calls.some(([, value]) => value.includes(token))).toBe(false); fireEvent.click(screen.getByRole('button', { name: 'Dismiss token' })); expect(screen.queryByLabelText('New webhook token')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Reload Events' })); await waitFor(() => expect(writes()).toHaveLength(1));
});
it('keeps the key immutable and preserves a stale rename draft until deliberate read recovery', async () => {
  await open(); const dialog = await inspect(); expect(within(dialog).getByRole('textbox', { name: 'Webhook event key' })).toBeDisabled();
  stale = true; fireEvent.change(within(dialog).getByRole('textbox', { name: 'Webhook name' }), { target: { value: 'Retained exact rename' } }); fireEvent.click(within(dialog).getByRole('button', { name: 'Save name' }));
  await screen.findByText(/Access has changed since/); expect(within(dialog).getByRole('button', { name: 'Save name' })).toBeDisabled();
  stale = false; current.securityRevision = 19; current.revision = 10; fireEvent.click(within(dialog).getByRole('button', { name: 'Reload webhook' }));
  await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Save name' })).toBeEnabled()); expect(within(dialog).getByRole('textbox', { name: 'Webhook name' })).toHaveValue('Retained exact rename');
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save name' })); await waitFor(() => expect(writes()).toHaveLength(2));
  expect(JSON.parse(String(writes()[1][1]?.body))).toEqual({ displayName: 'Retained exact rename', expectedRevision: 19, expectedSourceRevision: 10 });
});
it('confirms exact source rotation and explains sender reconfiguration before issuing a new token', async () => {
  await open(); const dialog = await inspect(); await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Rotate token' })).toBeEnabled()); fireEvent.click(within(dialog).getByRole('button', { name: 'Rotate token' })); const confirm = (await screen.findAllByText('Rotate this webhook token?'))[0].closest('[role=dialog]') as HTMLElement;
  expect(confirm).toHaveTextContent('Exact signed source · Webhook organization'); expect(confirm).toHaveTextContent('Reconfigure the external sender'); expect(writes()).toHaveLength(0);
  fireEvent.click(within(confirm).getByRole('button', { name: 'Rotate token' })); await waitFor(() => expect(writes()).toHaveLength(1));
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ expectedRevision: 8, expectedSourceRevision: 2 }); expect(await screen.findByLabelText('New webhook token')).toHaveValue(rotated);
});
it('keeps uncertain rotation blocked through reload and never claims a token can be recovered', async () => {
  unknown = true; await open(); const dialog = await inspect(); await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Rotate token' })).toBeEnabled()); fireEvent.click(within(dialog).getByRole('button', { name: 'Rotate token' })); const confirm = (await screen.findAllByText('Rotate this webhook token?'))[0].closest('[role=dialog]') as HTMLElement;
  fireEvent.click(within(confirm).getByRole('button', { name: 'Rotate token' })); await screen.findByText('Check current source before another action');
  fireEvent.click(within(dialog).getByRole('button', { name: 'Reload webhook' })); await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Rotate token' })).toBeDisabled());
  expect(writes()).toHaveLength(1); expect(screen.queryByLabelText('New webhook token')).not.toBeInTheDocument();
});
it('blocks a successful rotation with an undecodable token acknowledgement from automatic replay', async () => {
  invalidAck = true; await open(); const dialog = await inspect(); await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Rotate token' })).toBeEnabled()); fireEvent.click(within(dialog).getByRole('button', { name: 'Rotate token' })); const confirm = (await screen.findAllByText('Rotate this webhook token?'))[0].closest('[role=dialog]') as HTMLElement;
  fireEvent.click(within(confirm).getByRole('button', { name: 'Rotate token' })); await screen.findByText('Check current source before another action');
  expect(within(dialog).getByRole('button', { name: 'Rotate token' })).toBeDisabled(); expect(screen.queryByLabelText('New webhook token')).not.toBeInTheDocument(); expect(writes()).toHaveLength(1);
});
it('renders receipt data as bounded literal evidence and clears protected detail when read access is lost', async () => {
  await open(); const dialog = await inspect(); fireEvent.click(await within(dialog).findByRole('button', { name: 'Inspect receipt data' }));
  expect((within(dialog).getByRole('textbox', { name: 'Untrusted webhook receipt data' }) as HTMLTextAreaElement).value).toContain('<script>Untrusted literal</script>');
  expect(dialog.querySelector('script')).toBeNull(); expect(within(dialog).getByRole('textbox', { name: 'Untrusted webhook receipt data' })).toHaveAttribute('readonly');
  denied = true; fireEvent.click(within(dialog).getByRole('button', { name: 'Reload webhook' })); await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  await waitFor(() => expect(screen.queryByText('Exact signed source')).not.toBeInTheDocument()); await waitFor(() => expect(screen.getByRole('button', { name: 'New webhook' })).toHaveFocus());
});
it('keeps read-only source access independent from management and built-in catalog access', async () => {
  current.canManage = false; collection.canCreate = false; await open({ ...context, permissions: [] }); const dialog = await inspect();
  expect(within(dialog).getByRole('textbox', { name: 'Webhook name' })).toBeDisabled(); expect(within(dialog).queryByRole('button', { name: 'Rotate token' })).not.toBeInTheDocument();
  expect(within(dialog).queryByRole('button', { name: 'Revoke webhook' })).not.toBeInTheDocument(); expect(fetchMock.mock.calls.some(([path]) => String(path).endsWith('/events/builtin'))).toBe(false); expect(writes()).toHaveLength(0);
});
it('reuses current read-only built-in metadata without exposing event instances or subscriber authority', async () => {
  await open(); fireEvent.click(await screen.findByText('run.completed')); expect(await screen.findByText('Shared read-only completion definition.')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Manage built-in event' })).not.toBeInTheDocument(); expect(writes()).toHaveLength(0);
});

it('explains that issuing a token for a revoked source reactivates incoming receipts', async () => {
  current.status = 'revoked'; await open(); const dialog = await inspect(); fireEvent.click(within(dialog).getByRole('button', { name: 'Issue new token and activate' }));
  const confirm = (await screen.findAllByText('Activate this webhook with a new token?'))[0].closest('[role=dialog]') as HTMLElement;
  expect(confirm).toHaveTextContent('Reactivate incoming receipts for this exact source'); expect(writes()).toHaveLength(0);
  fireEvent.click(within(confirm).getByRole('button', { name: 'Activate source' })); expect(await screen.findByLabelText('New webhook token')).toHaveValue(rotated);
  await screen.findByText('active', { exact: true });
});

it('retires a one-time token on explicit collection recovery and handles an unavailable Clipboard API', async () => {
  await open(); const dialog = await inspect(); fireEvent.click(within(dialog).getByRole('button', { name: 'Rotate token' }));
  const confirm = (await screen.findAllByText('Rotate this webhook token?'))[0].closest('[role=dialog]') as HTMLElement;
  fireEvent.click(within(confirm).getByRole('button', { name: 'Rotate token' })); await screen.findByLabelText('New webhook token');
  fireEvent.click(screen.getByRole('button', { name: 'Copy token' })); await screen.findByText('Copy is unavailable. Reveal and select the token to save it.');
  expect(screen.getByLabelText('New webhook token')).toHaveValue(rotated); denied = true; fireEvent.click(screen.getByRole('button', { name: 'Reload Events' }));
  await waitFor(() => expect(screen.queryByLabelText('New webhook token')).not.toBeInTheDocument()); expect(writes()).toHaveLength(1);
});
