import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
it('cancels without granting any account and retains a failed selection for retry', async () => {
  vi.mocked(api.decideEmailAccount).mockRejectedValue(new Error('Request expired. Send a new user turn.'));
  render(<ConfigProvider><EmailAccountPicker sessionId="session" connected /></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Choose an email account' }));
  fireEvent.click(dialog.getByRole('button', { name: 'Cancel' }));
  await waitFor(() => expect(api.decideEmailAccount).toHaveBeenCalledWith('session', request, null, 'email'));
  expect(await screen.findByText('Request expired. Send a new user turn.')).toBeInTheDocument();
  expect(dialog.getByRole('button', { name: 'Refresh' })).toBeEnabled();
});
