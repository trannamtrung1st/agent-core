import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { EmailAccountDialog } from './EmailAccountDialog';
import * as api from '../../services/adminApi';
vi.mock('../../services/adminApi', () => ({ createEmailAccount: vi.fn(), updateEmailAccount: vi.fn() }));
beforeEach(() => vi.clearAllMocks()); afterEach(cleanup);
it('submits typed Gmail endpoints with a masked secret and clears it after failure', async () => {
  vi.mocked(api.createEmailAccount).mockRejectedValue(new Error('Revision conflict'));
  render(<ConfigProvider><App><EmailAccountDialog open onClose={vi.fn()} onSaved={vi.fn()} /></App></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Add email account' }));
  fireEvent.change(dialog.getByLabelText('Display name'), { target: { value: 'Dedicated mailbox' } });
  fireEvent.change(dialog.getByLabelText('Sender email address'), { target: { value: 'demo@example.test' } });
  const password = dialog.getByLabelText('App Password', { exact: true });
  expect(password).toHaveAttribute('type', 'password');
  fireEvent.change(password, { target: { value: 'transient-password' } });
  fireEvent.click(dialog.getByRole('button', { name: 'Save email account' }));
  await waitFor(() => expect(api.createEmailAccount).toHaveBeenCalledWith({ displayName: 'Dedicated mailbox', protectedValue: 'transient-password', profile: {
    preset: 'Gmail', emailAddress: 'demo@example.test', imapUsername: 'demo@example.test', smtpUsername: 'demo@example.test',
    imapEndpoint: { host: 'imap.gmail.com', port: 993, tlsMode: 'ImplicitTls' }, smtpEndpoint: { host: 'smtp.gmail.com', port: 465, tlsMode: 'ImplicitTls' }
  } }));
  await waitFor(() => expect(password).toHaveValue(''));
  expect(dialog.getByLabelText('Display name')).toHaveValue('Dedicated mailbox');
  expect(dialog.getByRole('alert')).toHaveTextContent('Revision conflict');
});
it('preserves an existing Gmail STARTTLS endpoint when editing safe account metadata', async () => {
  const profile: api.EmailAccountProfile = { preset: 'Gmail', emailAddress: 'demo@example.test', imapUsername: 'demo@example.test', smtpUsername: 'demo@example.test',
    imapEndpoint: { host: 'imap.gmail.com', port: 993, tlsMode: 'ImplicitTls' }, smtpEndpoint: { host: 'smtp.gmail.com', port: 587, tlsMode: 'StartTls' } };
  const account = { credentialId: 'account', displayName: 'Mailbox', status: 'Active', purpose: 'EmailAccount', revision: 7, emailAccount: profile } as api.SystemCredential;
  vi.mocked(api.updateEmailAccount).mockResolvedValue(account);
  render(<ConfigProvider><App><EmailAccountDialog open account={account} onClose={vi.fn()} onSaved={vi.fn().mockResolvedValue(undefined)} /></App></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Edit email account' }));
  await waitFor(() => expect(dialog.getByText('smtp.gmail.com:587 · Required STARTTLS')).toBeVisible());
  expect(dialog.queryByLabelText('App Password', { exact: true })).toBeNull();
  fireEvent.change(dialog.getByLabelText('Display name'), { target: { value: 'Renamed mailbox' } });
  fireEvent.click(dialog.getByRole('button', { name: 'Save email account' }));
  await waitFor(() => expect(api.updateEmailAccount).toHaveBeenCalledWith(account, { displayName: 'Renamed mailbox', status: 'Active', profile }));
});
