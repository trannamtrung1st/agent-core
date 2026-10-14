import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { useState } from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import { AddCredentialDialog } from './AddCredentialDialog';
import * as api from '../../services/adminApi';
import * as drive from '../../services/drive';
vi.mock('../../services/adminApi', () => ({
  createCredential: vi.fn(),
  createEmailAccount: vi.fn(),
  emailOAuthConfiguration: vi.fn(),
  startEmailOAuth: vi.fn(),
}));
vi.mock('../../services/drive', () => ({
  createDriveAccount: vi.fn(),
  getDriveOptions: vi.fn(),
}));
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(api.emailOAuthConfiguration).mockResolvedValue({
    configured: true,
    synthetic: true,
  });
  vi.mocked(drive.getDriveOptions).mockResolvedValue({
    enabled: true,
    configured: true,
    synthetic: true,
    scope: 'drive.file',
  });
});
function show() {
  function Owner() {
    const [open, setOpen] = useState(true);
    return <AddCredentialDialog open={open} onClose={() => setOpen(false)}
      onSaved={vi.fn().mockResolvedValue(undefined)} onClosed={vi.fn()} />;
  }
  render(
    <ConfigProvider>
      <App>
        <Owner />
      </App>
    </ConfigProvider>,
  );
}
async function choose(label: string) {
  fireEvent.mouseDown(
    screen.getByRole('combobox', { name: 'Credential purpose' }),
  );
  fireEvent.click(
    await screen.findByText(label, {
      selector: '.ant-select-item-option-content',
    }),
  );
}
it('switching purposes clears transient secrets and names and submits a typed Custom profile', async () => {
  show();
  await choose('Custom email account');
  const d = within(screen.getByRole('dialog', { name: 'Add credential' }));
  fireEvent.change(d.getByLabelText('Password', { exact: true }), {
    target: { value: 'must-disappear' },
  });
  fireEvent.change(d.getByLabelText('Display name', { exact: true }), {
    target: { value: 'unsaved-custom' },
  });
  await choose('General credential');
  expect(d.getByLabelText('Protected value', { exact: true })).toHaveValue('');
  expect(d.getByLabelText('Display name', { exact: true })).toHaveValue('');
  await choose('Custom email account');
  fireEvent.change(d.getByLabelText('Display name', { exact: true }), {
    target: { value: 'Custom fixture' },
  });
  for (const label of [
    'Sender email address',
    'IMAP username',
    'SMTP username',
  ])
    fireEvent.change(d.getByLabelText(label, { exact: true }), {
      target: { value: 'fixture@example.test' },
    });
  for (const label of ['IMAP host', 'SMTP host'])
    fireEvent.change(d.getByLabelText(label, { exact: true }), {
      target: { value: 'mail.example.test' },
    });
  fireEvent.change(d.getByLabelText('Password', { exact: true }), {
    target: { value: 'transient-secret' },
  });
  vi.mocked(api.createEmailAccount).mockRejectedValue(
    new Error('Save unavailable'),
  );
  fireEvent.click(d.getByRole('button', { name: 'Save credential' }));
  await waitFor(() =>
    expect(api.createEmailAccount).toHaveBeenCalledWith(
      expect.objectContaining({
        displayName: 'Custom fixture',
        protectedValue: 'transient-secret',
        profile: expect.objectContaining({
          preset: 'Custom',
          emailAddress: 'fixture@example.test',
          imapEndpoint: expect.objectContaining({ port: 993 }),
          smtpEndpoint: expect.objectContaining({ port: 465 }),
        }),
      }),
    ),
  );
  await waitFor(() =>
    expect(d.getByLabelText('Password', { exact: true })).toHaveValue(''),
  );
  expect(d.getByLabelText('Display name', { exact: true })).toHaveValue(
    'Custom fixture',
  );
  fireEvent.click(d.getByRole('button', { name: 'Cancel', exact: true }));
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add credential' })).not.toBeInTheDocument());
});
it('failed Gmail consent start reuses the saved account and locks purpose on retry', async () => {
  const account = {
    credentialId: 'saved',
    displayName: 'Gmail account',
    revision: 1,
  } as api.SystemCredential;
  vi.mocked(api.createEmailAccount).mockResolvedValue(account);
  vi.mocked(api.startEmailOAuth).mockRejectedValue(
    new Error('Start unavailable'),
  );
  show();
  await choose('Gmail account');
  const d = within(screen.getByRole('dialog', { name: 'Add credential' }));
  const connect = d.getByRole('button', { name: 'Connect with Google' });
  await waitFor(() => expect(connect).toBeEnabled());
  fireEvent.click(connect);
  await waitFor(() => expect(api.startEmailOAuth).toHaveBeenCalledTimes(1));
  await waitFor(() => expect(connect).toBeEnabled());
  expect(
    d.getByRole('combobox', { name: 'Credential purpose' }),
  ).toBeDisabled();
  fireEvent.click(connect);
  await waitFor(() => expect(api.startEmailOAuth).toHaveBeenCalledTimes(2));
  await waitFor(() => expect(connect).toBeEnabled());
  expect(api.createEmailAccount).toHaveBeenCalledTimes(1);
  fireEvent.click(d.getByRole('button', { name: 'Cancel', exact: true }));
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add credential' })).not.toBeInTheDocument());
});
