import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { EmailMonitoringSection } from './EmailMonitoringSection';
import type * as accountTypes from '../../services/adminApi';
const api = { getEmailMonitoring: vi.fn(), saveEmailMonitoring: vi.fn(), checkEmailMonitoring: vi.fn() };
const account = { credentialId: 'fixture', revision: 1 } as accountTypes.SystemCredential;
const initial: accountTypes.EmailMonitoring = { accountId: 'fixture', enabled: false, intervalSeconds: 300, configurationRevision: 0, status: 'Off', hostAvailable: true, accountAvailable: true, subscribedInstances: 0, checkPending: false, lastCheckedAtUtc: null, lastSuccessAtUtc: null, lastEventAtUtc: null, nextCheckAtUtc: null, lastGapAtUtc: null, errorCode: null, diagnosticId: null };
function view() { return render(<ConfigProvider><App><EmailMonitoringSection tenant="tenant" account={account} /></App></ConfigProvider>); }
beforeEach(() => {
  vi.clearAllMocks(); api.getEmailMonitoring.mockResolvedValue(initial);
  vi.mocked(identityRequest).mockReset().mockImplementation(async (_path, options) => {
    const body = options?.body as { expectedWatchRevision: number; enabled?: boolean; intervalSeconds?: number } | undefined;
    const saved = options?.method === 'PUT'
      ? await api.saveEmailMonitoring('fixture', { expectedRevision: body!.expectedWatchRevision, enabled: body!.enabled, intervalSeconds: body!.intervalSeconds })
      : options?.method === 'POST' ? await api.checkEmailMonitoring('fixture', body!.expectedWatchRevision) : await api.getEmailMonitoring();
    return { ...saved, securityRevision: 7, credentialRevision: 3, canManage: true, canEnable: true, canCheck: true };
  });
});
afterEach(cleanup);
it('saves only dirty settings with independent revision and permits editing while enabled', async () => {
  vi.mocked(api.saveEmailMonitoring).mockResolvedValue({ ...initial, enabled: true, status: 'Monitoring', configurationRevision: 1 });
  view(); const toggle = await screen.findByRole('switch', { name: 'New inbox messages' });
  expect(screen.getByRole('button', { name: 'Save monitoring' })).toBeDisabled(); fireEvent.click(toggle);
  expect(screen.getByRole('button', { name: 'Save monitoring' })).toBeEnabled(); fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await waitFor(() => expect(api.saveEmailMonitoring).toHaveBeenCalledWith('fixture', { expectedRevision: 0, enabled: true, intervalSeconds: 300 }));
  await screen.findByText('Monitoring settings saved.'); expect(toggle).toBeChecked(); fireEvent.click(toggle);
  expect(screen.getByRole('button', { name: 'Save monitoring' })).toBeEnabled(); expect(screen.queryByText('Monitoring settings saved.')).not.toBeInTheDocument(); fireEvent.click(screen.getByRole('button', { name: 'Cancel changes' })); expect(toggle).toBeChecked();
});
it('retains draft after revision conflict until explicit Reload', async () => {
  vi.mocked(api.saveEmailMonitoring).mockRejectedValue(new Error('Monitoring revision is stale.'));
  view(); const toggle = await screen.findByRole('switch'); fireEvent.click(toggle); fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  expect(await screen.findByText('Monitoring revision is stale.')).toBeVisible(); expect(toggle).toBeChecked(); expect(screen.getByText('Unsaved monitoring changes')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Reload monitoring' })); await waitFor(() => expect(toggle).not.toBeChecked());
});
it('preserves slow-save values and ignores completion after closing', async () => {
  let resolve!: (v: accountTypes.EmailMonitoring) => void; vi.mocked(api.saveEmailMonitoring).mockReturnValue(new Promise(r => { resolve = r; }));
  const mounted = view(); const toggle = await screen.findByRole('switch'); fireEvent.click(toggle); fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await waitFor(() => expect(toggle).toBeDisabled()); expect(toggle).toBeChecked(); mounted.unmount(); await act(async () => resolve({ ...initial, enabled: true }));
});
it('discloses gaps and coalesces queued checks without spinner lock', async () => {
  vi.mocked(api.getEmailMonitoring).mockResolvedValue({ ...initial, enabled: true, status: 'Gap', configurationRevision: 2, lastGapAtUtc: '2026-10-13T01:00:00Z' });
  vi.mocked(api.checkEmailMonitoring).mockResolvedValue({ ...initial, enabled: true, status: 'Checking', configurationRevision: 2, checkPending: true });
  view(); await screen.findByText('Some messages may have been missed'); fireEvent.click(screen.getByRole('button', { name: 'Check now' }));
  await screen.findByText('Check queued. It may trigger subscribed Automations.'); expect(api.checkEmailMonitoring).toHaveBeenCalledWith('fixture', 2);
  await waitFor(() => expect(screen.getByRole('button', { name: 'Check now' })).toBeDisabled()); expect(screen.getByRole('switch')).toBeEnabled();
  expect(screen.queryByRole('link', { name: 'View Events catalog' })).not.toBeInTheDocument();
});
it('recovers initial read failure and renders saved operator-disabled choices', async () => {
  vi.mocked(api.getEmailMonitoring).mockRejectedValueOnce(new Error('Account unavailable.')).mockResolvedValue({ ...initial, enabled: true, hostAvailable: false, status: 'HostDisabled' });
  view(); expect(await screen.findByRole('alert')).toHaveTextContent('Account unavailable.'); fireEvent.click(screen.getByRole('button', { name: 'Reload' }));
  await screen.findByText('Ask the operator to enable Email and EmailMonitoring. Your saved settings remain.'); expect(screen.getByRole('button', { name: 'Check now' })).toBeDisabled(); expect(screen.getByRole('switch')).toBeChecked();
});
it('fences a delayed account-status read so it cannot overwrite edits or their CAS revision', async () => {
  let resolve!: (v: accountTypes.EmailMonitoring) => void;
  const on = { ...initial, enabled: true, status: 'Monitoring' as const, configurationRevision: 1 };
  vi.mocked(api.getEmailMonitoring).mockResolvedValueOnce(on).mockReturnValueOnce(new Promise(r => { resolve = r; }));
  vi.mocked(api.saveEmailMonitoring).mockResolvedValue({ ...on, enabled: false, status: 'Off', configurationRevision: 2 });
  const wrapped = (revision: number) => <ConfigProvider><App><EmailMonitoringSection tenant="tenant" account={{ ...account, revision }} /></App></ConfigProvider>;
  const mounted = render(wrapped(1)); const toggle = await screen.findByRole('switch'); expect(toggle).toBeChecked();
  mounted.rerender(wrapped(2)); await waitFor(() => expect(api.getEmailMonitoring).toHaveBeenCalledTimes(2));
  fireEvent.click(toggle); await act(async () => resolve({ ...on, configurationRevision: 2 }));
  expect(toggle).not.toBeChecked(); expect(screen.getByRole('button', { name: 'Save monitoring' })).toBeEnabled();
  fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await waitFor(() => expect(api.saveEmailMonitoring).toHaveBeenCalledWith('fixture', { expectedRevision: 1, enabled: false, intervalSeconds: 300 }));
});

it('keeps edits and their original revision when an explicit Reload finishes after editing begins', async () => {
  let resolve!: (v: accountTypes.EmailMonitoring) => void;
  vi.mocked(api.getEmailMonitoring).mockResolvedValueOnce(initial).mockReturnValueOnce(new Promise(r => { resolve = r; }));
  vi.mocked(api.saveEmailMonitoring).mockResolvedValue({ ...initial, enabled: true, configurationRevision: 1 });
  view(); const toggle = await screen.findByRole('switch');
  fireEvent.click(screen.getByRole('button', { name: 'Reload monitoring' }));
  await waitFor(() => expect(api.getEmailMonitoring).toHaveBeenCalledTimes(2));
  fireEvent.click(toggle); await act(async () => resolve({ ...initial, configurationRevision: 2 }));
  expect(toggle).toBeChecked(); expect(screen.getByText('Unsaved monitoring changes')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await waitFor(() => expect(api.saveEmailMonitoring).toHaveBeenCalledWith('fixture', { expectedRevision: 0, enabled: true, intervalSeconds: 300 }));
});
it('releases an old pending save when the same detail component changes accounts', async () => {
  let resolve!: (v: accountTypes.EmailMonitoring) => void;
  vi.mocked(api.saveEmailMonitoring).mockReturnValue(new Promise(r => { resolve = r; }));
  const wrapped = (id: string) => <ConfigProvider><App><EmailMonitoringSection tenant="tenant" account={{ ...account, credentialId: id }} /></App></ConfigProvider>;
  const mounted = render(wrapped('fixture')); const toggle = await screen.findByRole('switch');
  fireEvent.click(toggle); fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await waitFor(() => expect(toggle).toBeDisabled());
  vi.mocked(api.getEmailMonitoring).mockResolvedValue({ ...initial, accountId: 'other' }); mounted.rerender(wrapped('other'));
  await waitFor(() => expect(screen.getByRole('switch')).toBeEnabled());
  await act(async () => resolve({ ...initial, enabled: true, configurationRevision: 1 }));
  expect(screen.getByRole('switch')).not.toBeChecked();
  expect(screen.queryByText('Monitoring settings saved.')).not.toBeInTheDocument();
});

it('ignores a retired read failure after a newer Reload succeeds', async () => {
  let reject!: (e: Error) => void;
  vi.mocked(api.getEmailMonitoring).mockResolvedValueOnce(initial).mockReturnValueOnce(new Promise((_, r) => { reject = r; })).mockResolvedValueOnce({ ...initial, configurationRevision: 2 });
  view(); await screen.findByRole('switch');
  fireEvent.click(screen.getByRole('button', { name: 'Reload monitoring' }));
  await waitFor(() => expect(api.getEmailMonitoring).toHaveBeenCalledTimes(2));
  fireEvent.click(screen.getByRole('button', { name: 'Reload monitoring' }));
  await waitFor(() => expect(api.getEmailMonitoring).toHaveBeenCalledTimes(3));
  await act(async () => reject(new Error('Retired read failed.')));
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
});

vi.mock('../../services/identity', async original => ({ ...await original<typeof import('../../services/identity')>(), identityRequest: vi.fn() }));
import { identityRequest, IdentityError } from '../../services/identity';
const owned = { ...initial, subscribedInstances: undefined, securityRevision: 7, credentialRevision: 3, canManage: true, canEnable: true, canCheck: false };
function tenantView() { return render(<ConfigProvider><App><EmailMonitoringSection tenant="tenant" account={account} /></App></ConfigProvider>); }
it('uses only the displayed tenant, account and watch witnesses and never a legacy mutation', async () => {
  vi.mocked(identityRequest).mockResolvedValueOnce(owned).mockResolvedValueOnce({ ...owned, enabled: true, configurationRevision: 1, securityRevision: 8 });
  tenantView(); fireEvent.click(await screen.findByRole('switch')); fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await screen.findByText('Monitoring settings saved.');
  expect(identityRequest).toHaveBeenLastCalledWith('/api/v2/tenants/tenant/credentials/fixture/monitoring', { method: 'PUT', body: {
    expectedRevision: 7, expectedCredentialRevision: 3, expectedWatchRevision: 0, enabled: true, intervalSeconds: 300 } });
  expect(api.saveEmailMonitoring).not.toHaveBeenCalled(); expect(screen.queryByText('No subscribed Automations')).not.toBeInTheDocument();
});
it('retains a conflicting tenant draft and requires an explicit reload before another command', async () => {
  vi.mocked(identityRequest).mockResolvedValueOnce(owned).mockRejectedValueOnce(new IdentityError(409, 'stale_revision')).mockResolvedValue(owned);
  tenantView(); const toggle = await screen.findByRole('switch'); fireEvent.click(toggle); fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await screen.findByText('Reload monitoring before trying again'); expect(toggle).toBeChecked(); expect(screen.getByRole('button', { name: 'Save monitoring' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Reload monitoring' })); await waitFor(() => expect(toggle).not.toBeChecked()); expect(toggle).toBeEnabled();
});
it('retires tenant monitoring data after permission loss and leaves a reload action', async () => {
  vi.mocked(identityRequest).mockResolvedValueOnce(owned).mockRejectedValue(new IdentityError(403, 'permission_denied'));
  tenantView(); await screen.findByRole('switch'); fireEvent.click(screen.getByRole('button', { name: 'Reload monitoring' }));
  await waitFor(() => expect(screen.queryByRole('switch')).not.toBeInTheDocument()); expect(screen.getByRole('button', { name: 'Reload' })).toBeEnabled();
});

it('allows a tenant account manager without account use access to stop monitoring but prevents enabling it', async () => {
  vi.mocked(identityRequest).mockResolvedValue({ ...owned, enabled: true, canEnable: false });
  tenantView(); const toggle = await screen.findByRole('switch'); expect(toggle).toBeEnabled(); fireEvent.click(toggle);
  expect(screen.getByRole('button', { name: 'Save monitoring' })).toBeEnabled(); expect(toggle).toBeDisabled(); expect(screen.getByRole('button', { name: 'Check now' })).toBeDisabled();
});
