import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { EmailMonitoringSection } from './EmailMonitoringSection';
import * as api from '../../services/adminApi';
vi.mock('../../services/adminApi', () => ({ getEmailMonitoring: vi.fn(), saveEmailMonitoring: vi.fn(), checkEmailMonitoring: vi.fn() }));
const account = { credentialId: 'fixture', revision: 1 } as api.SystemCredential;
const initial: api.EmailMonitoring = { accountId: 'fixture', enabled: false, intervalSeconds: 300, configurationRevision: 0, status: 'Off', hostAvailable: true, accountAvailable: true, subscribedInstances: 0, checkPending: false, lastCheckedAtUtc: null, lastSuccessAtUtc: null, lastEventAtUtc: null, nextCheckAtUtc: null, lastGapAtUtc: null, errorCode: null, diagnosticId: null };
function view() { return render(<ConfigProvider><App><EmailMonitoringSection account={account} /></App></ConfigProvider>); }
beforeEach(() => { vi.clearAllMocks(); vi.mocked(api.getEmailMonitoring).mockResolvedValue(initial); });
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
  expect(await screen.findByRole('alert')).toHaveTextContent('Monitoring revision is stale.'); expect(toggle).toBeChecked(); expect(screen.getByText('Unsaved monitoring changes')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Reload monitoring' })); await waitFor(() => expect(toggle).not.toBeChecked());
});
it('preserves slow-save values and ignores completion after closing', async () => {
  let resolve!: (v: api.EmailMonitoring) => void; vi.mocked(api.saveEmailMonitoring).mockReturnValue(new Promise(r => { resolve = r; }));
  const mounted = view(); const toggle = await screen.findByRole('switch'); fireEvent.click(toggle); fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await waitFor(() => expect(toggle).toBeDisabled()); expect(toggle).toBeChecked(); mounted.unmount(); await act(async () => resolve({ ...initial, enabled: true }));
});
it('discloses gaps and coalesces queued checks without spinner lock', async () => {
  vi.mocked(api.getEmailMonitoring).mockResolvedValue({ ...initial, enabled: true, status: 'Gap', configurationRevision: 2, lastGapAtUtc: '2026-10-13T01:00:00Z' });
  vi.mocked(api.checkEmailMonitoring).mockResolvedValue({ ...initial, enabled: true, status: 'Checking', configurationRevision: 2, checkPending: true });
  view(); await screen.findByText('Some messages may have been missed'); fireEvent.click(screen.getByRole('button', { name: 'Check now' }));
  await screen.findByText('Check queued. It may trigger subscribed Automations.'); expect(api.checkEmailMonitoring).toHaveBeenCalledWith('fixture', 2);
  await waitFor(() => expect(screen.getByRole('button', { name: 'Check now' })).toBeDisabled()); expect(screen.getByRole('switch')).toBeEnabled();
  expect(screen.getByRole('link', { name: 'View Events catalog' })).toHaveAttribute('href', '/admin/connections/events');
});
it('recovers initial read failure and renders saved operator-disabled choices', async () => {
  vi.mocked(api.getEmailMonitoring).mockRejectedValueOnce(new Error('Account unavailable.')).mockResolvedValue({ ...initial, enabled: true, hostAvailable: false, status: 'HostDisabled' });
  view(); expect(await screen.findByRole('alert')).toHaveTextContent('Account unavailable.'); fireEvent.click(screen.getByRole('button', { name: 'Reload' }));
  await screen.findByText('Ask the operator to enable Email and EmailMonitoring. Your saved settings remain.'); expect(screen.getByRole('button', { name: 'Check now' })).toBeDisabled(); expect(screen.getByRole('switch')).toBeChecked();
});
it('fences a delayed account-status read so it cannot overwrite edits or their CAS revision', async () => {
  let resolve!: (v: api.EmailMonitoring) => void;
  const on = { ...initial, enabled: true, status: 'Monitoring' as const, configurationRevision: 1 };
  vi.mocked(api.getEmailMonitoring).mockResolvedValueOnce(on).mockReturnValueOnce(new Promise(r => { resolve = r; }));
  vi.mocked(api.saveEmailMonitoring).mockResolvedValue({ ...on, enabled: false, status: 'Off', configurationRevision: 2 });
  const wrapped = (revision: number) => <ConfigProvider><App><EmailMonitoringSection account={{ ...account, revision }} /></App></ConfigProvider>;
  const mounted = render(wrapped(1)); const toggle = await screen.findByRole('switch'); expect(toggle).toBeChecked();
  mounted.rerender(wrapped(2)); await waitFor(() => expect(api.getEmailMonitoring).toHaveBeenCalledTimes(2));
  fireEvent.click(toggle); await act(async () => resolve({ ...on, configurationRevision: 2 }));
  expect(toggle).not.toBeChecked(); expect(screen.getByRole('button', { name: 'Save monitoring' })).toBeEnabled();
  fireEvent.click(screen.getByRole('button', { name: 'Save monitoring' }));
  await waitFor(() => expect(api.saveEmailMonitoring).toHaveBeenCalledWith('fixture', { expectedRevision: 1, enabled: false, intervalSeconds: 300 }));
});

it('keeps edits and their original revision when an explicit Reload finishes after editing begins', async () => {
  let resolve!: (v: api.EmailMonitoring) => void;
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
  let resolve!: (v: api.EmailMonitoring) => void;
  vi.mocked(api.saveEmailMonitoring).mockReturnValue(new Promise(r => { resolve = r; }));
  const wrapped = (id: string) => <ConfigProvider><App><EmailMonitoringSection account={{ ...account, credentialId: id }} /></App></ConfigProvider>;
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

it('names the Custom incoming test in paused recovery and leaves explicit Check now available', async () => {
  vi.mocked(api.getEmailMonitoring).mockResolvedValue({ ...initial, enabled: true, status: 'Paused', errorCode: 'email_watch_access_required' });
  render(<ConfigProvider><App><EmailMonitoringSection account={{ ...account, emailAccount: { preset: 'Custom' } as api.EmailAccountProfile }} /></App></ConfigProvider>);
  await screen.findByText('Review account status and Test incoming above. Update Custom credentials, then Check now.');
  expect(screen.getByRole('button', { name: 'Check now' })).toBeEnabled();
});

it('uses canonical contextual Automations and reports retained drafts to the Instance workspace', async () => {
  const report = vi.fn();
  const mounted = render(<ConfigProvider><App><EmailMonitoringSection account={account} instanceId="AABBCCDD-0000-0000-0000-000000000001" onDirtyChange={report} /></App></ConfigProvider>);
  const toggle = await screen.findByRole('switch');
  expect(screen.getByRole('link', { name: 'Configure an Instance Automation' })).toHaveAttribute('href', '/admin/instances/aabbccdd-0000-0000-0000-000000000001/automations');
  fireEvent.click(toggle); await waitFor(() => expect(report).toHaveBeenLastCalledWith(true));
  fireEvent.click(screen.getByRole('button', { name: 'Cancel changes' })); await waitFor(() => expect(report).toHaveBeenLastCalledWith(false));
  fireEvent.click(toggle); mounted.unmount(); expect(report).toHaveBeenLastCalledWith(false);
});
