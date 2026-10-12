import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { EmailAccountPicker } from './EmailAccountPicker';
import * as api from '../../services/emailAccounts';
vi.mock('../../services/emailAccounts', () => ({ getEmailAccountRequest: vi.fn(), decideEmailAccount: vi.fn() }));
const request: api.EmailAccountRequest = { requestId: 'request', expiresAtUtc: '2099-01-01T00:00:00Z', instanceId: 'instance', agentName: 'Riley', expectedInstanceRevision: 4,
  choices: [{ accountId: 'account', displayName: 'Demo', emailAddress: 'demo@example.test', preset: 'Gmail', accountRef: null }] };
beforeEach(() => { vi.clearAllMocks(); vi.mocked(api.getEmailAccountRequest).mockResolvedValue(request); }); afterEach(cleanup);
it('requires an explicit account choice and records owner consent with the current revision', async () => {
  vi.mocked(api.decideEmailAccount).mockResolvedValue({ status: 'allowed', message: 'Account access saved' });
  render(<ConfigProvider><EmailAccountPicker sessionId="session" connected /></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Choose an email account' }));
  expect(dialog.getByRole('button', { name: 'Allow account' })).toBeDisabled();
  fireEvent.click(dialog.getByRole('radio'));
  fireEvent.change(dialog.getByRole('textbox', { name: 'Account reference' }), { target: { value: 'demo-mail' } });
  fireEvent.click(dialog.getByRole('button', { name: 'Allow account' }));
  await waitFor(() => expect(api.decideEmailAccount).toHaveBeenCalledWith('session', request, 'account', 'demo-mail'));
  expect(await screen.findByText('Account access saved')).toBeInTheDocument();
});
it('refreshes the same request revision before confirming a still-eligible account', async () => {
  const updated = { ...request, expectedInstanceRevision: 5 };
  vi.mocked(api.getEmailAccountRequest).mockResolvedValueOnce(request).mockResolvedValue(updated);
  vi.mocked(api.decideEmailAccount).mockResolvedValue({ status: 'allowed', message: 'Saved' });
  render(<ConfigProvider><EmailAccountPicker sessionId="session" connected /></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Choose an email account' }));
  fireEvent.click(dialog.getByRole('radio'));
  await waitFor(() => expect(api.getEmailAccountRequest).toHaveBeenCalledTimes(2), { timeout: 3500 });
  fireEvent.click(dialog.getByRole('button', { name: 'Allow account' }));
  await waitFor(() => expect(api.decideEmailAccount).toHaveBeenCalledWith('session', updated, 'account', 'email'));
});
it('removes a revoked account from the current request and requires a new explicit choice', async () => {
  vi.mocked(api.getEmailAccountRequest).mockResolvedValueOnce(request).mockResolvedValue({ ...request, choices: [] });
  render(<ConfigProvider><EmailAccountPicker sessionId="session" connected /></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Choose an email account' }));
  fireEvent.click(dialog.getByRole('radio'));
  expect(dialog.getByRole('button', { name: 'Allow account' })).toBeEnabled();
  expect(await screen.findByText('No connected email accounts', {}, { timeout: 3500 })).toBeInTheDocument();
  expect(dialog.getByRole('button', { name: 'Allow account' })).toBeDisabled();
  expect(dialog.queryByRole('radio')).toBeNull();
  expect(api.decideEmailAccount).not.toHaveBeenCalled();
});
it('cancels without granting any account and retains a failed selection for retry', async () => {
  vi.mocked(api.decideEmailAccount).mockRejectedValue(new Error('Request expired. Send a new user turn.'));
  render(<ConfigProvider><EmailAccountPicker sessionId="session" connected /></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Choose an email account' }));
  fireEvent.click(dialog.getByRole('button', { name: 'Cancel' }));
  await waitFor(() => expect(api.decideEmailAccount).toHaveBeenCalledWith('session', request, null, 'email'));
  expect(await screen.findByText('Request expired. Send a new user turn.')).toBeInTheDocument();
  expect(dialog.getByRole('button', { name: 'Refresh' })).toBeEnabled();
});
it('discards a late decision receipt after disconnecting the same session', async () => {
  let complete!: (value: { status: string; message: string }) => void;
  vi.mocked(api.decideEmailAccount).mockReturnValue(new Promise(resolve => { complete = resolve; }));
  const view = render(<ConfigProvider><EmailAccountPicker sessionId="session" connected /></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Choose an email account' }));
  fireEvent.click(dialog.getByRole('button', { name: 'Cancel' }));
  await waitFor(() => expect(api.decideEmailAccount).toHaveBeenCalledTimes(1));
  view.rerender(<ConfigProvider><EmailAccountPicker sessionId="session" connected={false} /></ConfigProvider>);
  await act(async () => complete({ status: 'cancelled', message: 'Obsolete decision receipt' }));
  expect(screen.queryByText('Obsolete decision receipt')).toBeNull();
});
