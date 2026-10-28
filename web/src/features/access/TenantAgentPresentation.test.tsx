import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { resetIdentityCsrf, type TenantContext } from '../../services/identity';
import type { TenantAgentEditor } from '../../services/tenantAgentConfiguration';
import { TenantAgentPresentation } from './TenantAgentPresentation';

const context = { tenant: { tenantId: 'tenant', name: 'Assistant organization' } } as TenantContext;
const view = { canUpdate: true, securityRevision: 7, agent: { managedKind: 'GeneralAssistant', canResetDefaults: true, instance: {
  instanceId: 'agent', revision: 4, lifecycle: 'Active', persona: { name: 'General Assistant' }, showInChat: false
} } } as TenantAgentEditor;
const fetchMock = vi.fn<typeof fetch>(); const saved = vi.fn();
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
beforeEach(() => { resetIdentityCsrf(); fetchMock.mockReset(); saved.mockReset(); vi.stubGlobal('fetch', fetchMock); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
function open(current = view) { render(<App><TenantAgentPresentation context={context} id="agent" view={current} disabled={false} onSaved={saved} onBusyChange={() => {}} /></App>); }
const writes = () => fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST' || init?.method === 'PUT');
it('changes visibility with saved witnesses and validates its acknowledgement', async () => {
  fetchMock.mockImplementation(async (url, init) => String(url).endsWith('/csrf') ? json({ token: 'csrf' }) : json({ instance: { instanceId: 'agent', revision: 5, showInChat: JSON.parse(String(init?.body)).showInChat } }));
  open(); expect(screen.getByRole('switch', { name: 'Visible in Chat' })).not.toBeChecked();
  fireEvent.click(screen.getByRole('switch', { name: 'Visible in Chat' }));
  await waitFor(() => expect(saved).toHaveBeenCalledOnce());
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ showInChat: true, expectedRevision: 7, expectedInstanceRevision: 4 });
});
it('confirms retained data and current identity; cancel writes nothing, confirmed reset uses one operation', async () => {
  fetchMock.mockImplementation(async (url, init) => String(url).endsWith('/csrf') ? json({ token: 'csrf' }) : String(url).endsWith('/editor') ? json(view) : json({ operationId: JSON.parse(String(init?.body)).operationId, instanceId: 'agent', instanceRevision: 5, status: 'applied' }));
  open(); fireEvent.click(screen.getByRole('button', { name: 'Reset to System Defaults' }));
  let dialog = await screen.findByRole('dialog'); expect(dialog).toHaveTextContent('Assistant organization'); expect(dialog).toHaveTextContent('local Skills/resources'); expect(dialog).toHaveTextContent('Chat visibility are retained');
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' })); await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument()); expect(writes()).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Reset to System Defaults' })); dialog = await screen.findByRole('dialog');
  fireEvent.click(within(dialog).getByRole('button', { name: 'Reset to System Defaults' })); await waitFor(() => expect(saved).toHaveBeenCalledOnce());
  expect(writes()).toHaveLength(1); expect(JSON.parse(String(writes()[0][1]?.body))).toMatchObject({ expectedRevision: 7, expectedInstanceRevision: 4, operationId: expect.any(String) });
});
it('retires uncertain visibility writes and keeps read-only configuration disabled', async () => {
  fetchMock.mockImplementation(async url => String(url).endsWith('/csrf') ? json({ token: 'csrf' }) : Promise.reject(new TypeError('connection lost')));
  open(); fireEvent.click(screen.getByRole('switch', { name: 'Visible in Chat' })); await screen.findByText('Outcome needs confirmation');
  expect(screen.getByRole('switch', { name: 'Visible in Chat' })).toBeDisabled(); expect(saved).not.toHaveBeenCalled(); expect(writes()).toHaveLength(1);
  cleanup(); open({ ...view, canUpdate: false }); expect(screen.getByRole('switch', { name: 'Visible in Chat' })).toBeDisabled(); expect(screen.getByRole('button', { name: 'Reset to System Defaults' })).toBeDisabled();
});
