import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { resetIdentityCsrf } from '../../services/identity';
import { TenantSessionContext } from './TenantSessionContext';
const privateId = '11111111-1111-4111-8111-111111111111'; const sharedId = '22222222-2222-4222-8222-222222222222';
const fetchMock = vi.fn(); let selected = privateId; let revision = 1; let security = 8; let conflict = false; let badAck = false; let network = false;
const response = (value: unknown) => new Response(JSON.stringify(value));
beforeEach(() => {
  selected = privateId; revision = 1; security = 8; conflict = badAck = network = false; resetIdentityCsrf(); fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async (url: string, options?: RequestInit) => {
    if (url.endsWith('/csrf')) return response({ token: 'fixture-csrf' });
    if (url.endsWith('/switch')) {
      if (network) throw new TypeError('Network unavailable');
      if (conflict) return new Response(JSON.stringify({reasonCode: 'stale_revision'}), {status: 409});
      const body = JSON.parse(String(options?.body)); selected = body.spaceId; revision++;
      return response({selection: {tenantId: 'organization', spaceId: badAck ? privateId : selected, environmentId: null, revision, transitionId: body.transitionId}, state: 'pending_next_turn', sessionRevision: 21, securityRevision: security + 1});
    }
    if (url.includes('/options?')) return response({ spaces: [{ spaceId: privateId, name: 'Private Space', kind: 'Private' }, { spaceId: sharedId, name: 'Shared project', kind: 'Shared' }], page: 0, hasMore: false,
      spaceId: new URL(url, 'https://local.test').searchParams.get('spaceId') ?? selected, environmentsEnabled: false, environmentSelectionAllowed: false, canSwitch: true, canClearEnvironment: false, environments: [], securityRevision: security, sessionRevision: 20, selectionRevision: revision });
    return response({ selection: { spaceId: selected, environmentId: null, revision }, state: revision === 1 ? 'applied' : 'pending_next_turn', space: { spaceId: selected, name: selected === privateId ? 'Private Space' : 'Shared project', kind: selected === privateId ? 0 : 1, environmentsEnabled: false }, environment: null, sessionRevision: 20, securityRevision: 8 });
  });
});
afterEach(() => { cleanup(); resetIdentityCsrf(); vi.unstubAllGlobals(); });
it('keeps a per-Session draft open through response refresh, hides gate-off controls and submits exact next-turn witnesses', async () => {
  const view = (key: string) => <App><TenantSessionContext tenant="organization" session="conversation" refreshKey={key} /></App>;
  const mounted = render(view('run-start'));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Session Space' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Session Space' }));
  const drawer = await screen.findByRole('dialog', { name: 'Session context' });
  await within(drawer).findByRole('combobox', { name: 'Space' });
  expect(within(drawer).queryByRole('combobox', { name: 'Environment' })).not.toBeInTheDocument();
  fireEvent.mouseDown(within(drawer).getByRole('combobox', { name: 'Space' }));
  await act(async () => { fireEvent.click(await screen.findByText('Shared project · Shared', { selector: '.ant-select-item-option-content' })); });
  await act(async () => { mounted.rerender(view('run-finished')); });
  expect(screen.getByRole('dialog', { name: 'Session context' })).toBeVisible();
  expect(within(drawer).getByTitle('Shared project · Shared', { exact: true })).toBeVisible();
  await waitFor(() => expect(within(drawer).getByRole('button', { name: 'Apply to next turn' })).toBeEnabled());
  fireEvent.click(within(drawer).getByRole('button', { name: 'Apply to next turn' }));
  await screen.findByText('Pending next turn');
  const writes = fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/switch'));
  expect(writes).toHaveLength(1); expect(writes[0][0]).toBe('/api/v2/tenants/organization/sessions/conversation/context/switch');
  expect(JSON.parse(writes[0][1].body)).toMatchObject({ spaceId: sharedId, environmentId: null, expectedRevision: 8, expectedSessionRevision: 20, expectedSelectionRevision: 1 });
  expect(screen.getByRole('button', { name: 'Session Space' })).toHaveTextContent('Shared project');
});

it.each(['conflict', 'acknowledgement', 'network'])('retains a context draft after %s and never repeats an uncertain transition', async mode => {
  conflict = mode === 'conflict'; badAck = mode === 'acknowledgement'; network = mode === 'network';
  render(<App><TenantSessionContext tenant="organization" session="conversation" refreshKey="initial" /></App>);
  await waitFor(() => expect(screen.getByRole('button', { name: 'Session Space' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Session Space' }));
  const drawer = await screen.findByRole('dialog', { name: 'Session context' });
  fireEvent.mouseDown(await within(drawer).findByRole('combobox', { name: 'Space' }));
  await act(async () => { fireEvent.click(await screen.findByText('Shared project · Shared', { selector: '.ant-select-item-option-content' })); });
  const apply = within(drawer).getByRole('button', { name: 'Apply to next turn' });
  await waitFor(() => expect(apply).toBeEnabled());
  await act(async () => { fireEvent.click(apply); });
  await waitFor(() => expect(apply).toBeDisabled());
  expect(within(drawer).getByTitle('Shared project · Shared', { exact: true })).toBeVisible();
  const writes = () => fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/switch'));
  expect(writes()).toHaveLength(1);
  const reload = within(drawer).getByRole('button', { name: 'Reload context choices' });
  if (mode === 'conflict') {
    conflict = false; security = 9;
    await act(async () => { fireEvent.click(reload); });
    await waitFor(() => expect(apply).toBeEnabled());
    await act(async () => { fireEvent.click(apply); });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Session context' })).not.toBeInTheDocument());
    expect(writes()).toHaveLength(2); expect(JSON.parse(String(writes()[1][1]?.body)).expectedRevision).toBe(9);
  } else {
    expect(within(drawer).getByText('Context outcome is unconfirmed')).toBeVisible();
    if (mode === 'network') await act(async () => { fireEvent.click(reload); });
    else expect(reload).toBeDisabled();
    expect(apply).toBeDisabled(); expect(writes()).toHaveLength(1);
  }
});
