import { StrictMode } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { GmailConnectionPanel } from './GmailConnectionPanel';
import * as api from '../../services/adminApi';
vi.mock('../../services/adminApi', () => ({ getEmailConnection: vi.fn(), checkEmailConnection: vi.fn(), disconnectEmailOAuth: vi.fn(), startEmailOAuth: vi.fn() }));
const account = { credentialId: 'account', revision: 1, status: 'Active', emailAccount: { preset: 'Gmail', googleSubject: 'synthetic', emailAddress: 'mail@example.test', connectionStatus: 'Connected' } } as api.SystemCredential;
const initial: api.EmailOAuthConnection = { status: 'Connected', configured: true, synthetic: true, error: null, lastCheckedAtUtc: null };
const checked = { ...initial, lastCheckedAtUtc: '2026-10-13T09:00:00Z' };
const wrap = (value = account, onChanged = vi.fn()) => <ConfigProvider><App><GmailConnectionPanel account={value} onChanged={onChanged} /></App></ConfigProvider>;
beforeEach(() => { vi.clearAllMocks(); vi.mocked(api.getEmailConnection).mockResolvedValue(initial); });
afterEach(cleanup);

it('keeps a completed access check when a retired StrictMode startup read returns later', async () => {
  let release!: (value: api.EmailOAuthConnection) => void;
  vi.mocked(api.getEmailConnection).mockReturnValueOnce(new Promise(resolve => { release = resolve; })).mockResolvedValueOnce(initial);
  vi.mocked(api.checkEmailConnection).mockResolvedValue(checked);
  render(<StrictMode>{wrap()}</StrictMode>);
  const check = await screen.findByRole('button', { name: 'Test access' });
  await waitFor(() => expect(api.getEmailConnection).toHaveBeenCalledTimes(2));
  fireEvent.click(check); await screen.findByText('Last checked');
  await act(async () => release(initial));
  expect(screen.getByText('Last checked')).toBeVisible();
  expect(screen.getByText('Access verified · no email sent')).toBeVisible();
});

it('releases a pending action on account change and ignores its old result and refresh', async () => {
  let release!: (value: api.EmailOAuthConnection) => void;
  vi.mocked(api.checkEmailConnection).mockReturnValue(new Promise(resolve => { release = resolve; }));
  const refresh = vi.fn(); const mounted = render(wrap(account, refresh));
  fireEvent.click(await screen.findByRole('button', { name: 'Test access' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Test access' })).toBeDisabled());
  vi.mocked(api.getEmailConnection).mockResolvedValue({ ...initial, status: 'Disconnected' });
  mounted.rerender(wrap({ ...account, credentialId: 'other', emailAccount: { ...account.emailAccount!, connectionStatus: 'Disconnected' } }, refresh));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Reconnect with Google' })).toBeEnabled());
  await act(async () => release(checked));
  expect(screen.queryByText('Last checked')).not.toBeInTheDocument(); expect(refresh).not.toHaveBeenCalled();
});

it('does not refresh a closed workspace after a committed check finishes', async () => {
  let release!: (value: api.EmailOAuthConnection) => void;
  vi.mocked(api.checkEmailConnection).mockReturnValue(new Promise(resolve => { release = resolve; }));
  const refresh = vi.fn(); const mounted = render(wrap(account, refresh));
  fireEvent.click(await screen.findByRole('button', { name: 'Test access' }));
  mounted.unmount(); await act(async () => release(checked)); expect(refresh).not.toHaveBeenCalled();
});
