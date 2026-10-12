import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { EmailAccountDialog } from './EmailAccountDialog';
import * as api from '../../services/adminApi';
vi.mock('../../services/adminApi', () => ({ createEmailAccount: vi.fn(), updateEmailAccount: vi.fn(), emailOAuthConfiguration: vi.fn(), startEmailOAuth: vi.fn(), getEmailConnection: vi.fn(), checkEmailConnection: vi.fn(), disconnectEmailOAuth: vi.fn() }));
const profile: api.EmailAccountProfile = { preset: 'Gmail', emailAddress: '', imapUsername: '', smtpUsername: '', imapEndpoint: null, smtpEndpoint: null };
const account = { credentialId: 'account', displayName: 'Mailbox', status: 'Active', kind: 'Token', purpose: 'EmailAccount', revision: 7, emailAccount: profile } as api.SystemCredential;
beforeEach(() => { vi.clearAllMocks(); vi.mocked(api.emailOAuthConfiguration).mockResolvedValue({ configured: true, synthetic: true }); vi.mocked(api.getEmailConnection).mockResolvedValue({ status: 'NotConnected', configured: true, synthetic: true, error: null }); }); afterEach(() => { cleanup(); vi.restoreAllMocks(); });
function show(c?: api.SystemCredential) { render(<ConfigProvider><App><EmailAccountDialog open account={c} onClose={vi.fn()} onSaved={vi.fn().mockResolvedValue(undefined)} /></App></ConfigProvider>); }
it('Gmail creates without passwords or endpoints and begins owner consent, retaining account after start failure', async () => {
  vi.mocked(api.createEmailAccount).mockResolvedValue(account); vi.mocked(api.startEmailOAuth).mockRejectedValue(new Error('Connection temporarily unavailable'));
  show(); const dialog = within(await screen.findByRole('dialog', { name: 'Add email account' }));
  expect(dialog.queryByLabelText('Sender email address')).toBeNull(); expect(dialog.queryByLabelText('App Password')).toBeNull(); expect(dialog.queryByLabelText('Password', { exact: true })).toBeNull();
  fireEvent.change(dialog.getByLabelText('Display name'), { target: { value: 'Dedicated mailbox' } });
  const connect = dialog.getByRole('button', { name: 'Connect with Google' }); await waitFor(() => expect(connect).toBeEnabled()); fireEvent.click(connect);
  await waitFor(() => expect(api.createEmailAccount).toHaveBeenCalledWith({ displayName: 'Dedicated mailbox', profile }));
  expect(api.startEmailOAuth).toHaveBeenCalledWith(account);
  await waitFor(() => expect(dialog.getByText('Connection temporarily unavailable')).toBeVisible());
  expect(api.createEmailAccount).toHaveBeenCalledTimes(1); expect(dialog.getByLabelText('Display name')).toHaveValue('Dedicated mailbox');
});
it('missing operator config has guidance and disabled Connect without password fallback', async () => {
  vi.mocked(api.emailOAuthConfiguration).mockResolvedValue({ configured: false, synthetic: false }); show();
  const dialog = within(await screen.findByRole('dialog', { name: 'Add email account' }));
  await waitFor(() => expect(dialog.getByText('Google connection not configured')).toBeVisible());
  expect(dialog.getByRole('button', { name: 'Connect with Google' })).toBeDisabled(); expect(dialog.getByRole('link', { name: 'Google OAuth setup' })).toHaveAttribute('href', expect.stringContaining('gmail-google-oauth-operator-setup'));
  expect(dialog.queryByLabelText('Password', { exact: true })).toBeNull();
});
it('Google verified identity remains read-only while safe metadata edits preserve profile', async () => {
  const connected = { ...account, emailAccount: { ...profile, emailAddress: 'verified@example.test', googleSubject: 'subject', connectionStatus: 'Connected' as const } };
  vi.mocked(api.getEmailConnection).mockResolvedValue({ status: 'Connected', configured: true, synthetic: true, error: null }); vi.mocked(api.updateEmailAccount).mockResolvedValue(connected);
  show(connected); const dialog = within(await screen.findByRole('dialog', { name: 'Edit email account' }));
  await waitFor(() => expect(dialog.getByText('verified@example.test')).toBeVisible()); expect(dialog.queryByLabelText('Sender email address')).toBeNull();
  fireEvent.change(dialog.getByLabelText('Display name'), { target: { value: 'Renamed mailbox' } }); fireEvent.click(dialog.getByRole('button', { name: 'Save email account' }));
  await waitFor(() => expect(api.updateEmailAccount).toHaveBeenCalledWith(connected, { displayName: 'Renamed mailbox', status: 'Active', profile: connected.emailAccount }));
});
it('Custom keeps typed TLS endpoints, protected password, and failure clears only transient secret', async () => {
  vi.mocked(api.createEmailAccount).mockRejectedValue(new Error('Revision conflict')); show();
  const dialog = within(await screen.findByRole('dialog', { name: 'Add email account' }));
  fireEvent.mouseDown(dialog.getByLabelText('Provider')); fireEvent.click(await screen.findByText('Custom IMAP/SMTP · Advanced'));
  fireEvent.change(await dialog.findByLabelText('Sender email address'), { target: { value: 'demo@example.test' } });
  for (const label of ['IMAP username', 'SMTP username']) fireEvent.change(dialog.getByLabelText(label), { target: { value: 'demo@example.test' } });
  for (const label of ['IMAP host', 'SMTP host']) fireEvent.change(dialog.getByLabelText(label), { target: { value: 'mail.example.test' } });
  const password = dialog.getByLabelText('Password', { exact: true }); expect(password).toHaveAttribute('type', 'password'); fireEvent.change(password, { target: { value: 'transient-password' } });
  fireEvent.click(dialog.getByRole('button', { name: 'Save email account' }));
  await waitFor(() => expect(api.createEmailAccount).toHaveBeenCalledWith(expect.objectContaining({ protectedValue: 'transient-password', profile: expect.objectContaining({ preset: 'Custom', imapEndpoint: { host: 'mail.example.test', port: 993, tlsMode: 'ImplicitTls' } }) })));
  await waitFor(() => expect(password).toHaveValue('')); expect(dialog.getByLabelText('Sender email address')).toHaveValue('demo@example.test');
});

it('restores safe name/status after callback only after modal fields mount', async () => {
  sessionStorage.setItem('agent-core.gmail-detail-draft:account', JSON.stringify({ displayName: 'Pending safe edits', status: 'Disabled' }));
  show(account); const dialog = within(await screen.findByRole('dialog', { name: 'Edit email account' }));
  await dialog.findByText('Unsaved account details restored');
  expect(dialog.getByLabelText('Display name')).toHaveValue('Pending safe edits');
  expect(sessionStorage.getItem('agent-core.gmail-detail-draft:account')).toBeNull();
});

it('blocked storage cleanup cannot prevent dismissal or misreport a committed metadata save', async () => {
  vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new DOMException('Storage blocked'); });
  vi.mocked(api.updateEmailAccount).mockResolvedValue(account); const close = vi.fn();
  render(<ConfigProvider><App><EmailAccountDialog open account={account} onClose={close} onSaved={vi.fn().mockResolvedValue(undefined)} /></App></ConfigProvider>);
  const dialog = within(await screen.findByRole('dialog', { name: 'Edit email account' }));
  fireEvent.click(dialog.getByRole('button', { name: 'Save email account' }));
  await waitFor(() => expect(close).toHaveBeenCalled());
  expect(dialog.queryByText('Storage blocked')).toBeNull();
});
