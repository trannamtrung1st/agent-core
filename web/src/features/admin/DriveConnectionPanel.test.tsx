import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { App } from 'antd';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { DriveConnectionPanel } from './DriveConnectionPanel';
import * as drive from '../../services/drive';
import type { SystemCredential } from '../../services/adminApi';
vi.mock('../../services/drive', () => ({
  getDriveConnection: vi.fn(),
  checkDriveConnection: vi.fn(),
  startDriveConnection: vi.fn(),
  disconnectDriveConnection: vi.fn(),
}));
const connected = {
  status: 'Connected',
  configured: true,
  synthetic: true,
  error: null,
  lastCheckedAtUtc: null,
  providerRevoked: null,
};
const account: SystemCredential = {
  credentialId: 'a',
  displayName: 'Work',
  kind: 'Token',
  purpose: 'GoogleDriveAccount',
  status: 'Active',
  revision: 2,
  metadata: {},
  allowedOrigins: [],
  bindingCount: 1,
  createdAtUtc: '2026-10-13',
  updatedAtUtc: '2026-10-13',
  driveAccount: {
    emailAddress: 'work@example.test',
    connectionStatus: 'Connected',
    authEpoch: 1,
    connectedAtUtc: '2026-10-13',
    synthetic: true,
  },
};
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(drive.getDriveConnection).mockResolvedValue(connected);
});
describe('Drive connection authority changes', () => {
  it('ignores a retired read after a successful check', async () => {
    let finish!: (value: drive.DriveConnection) => void;
    vi.mocked(drive.getDriveConnection).mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
    const view = render(
      <App>
        <DriveConnectionPanel account={account} />
      </App>,
    );
    view.rerender(
      <App>
        <DriveConnectionPanel account={{ ...account, revision: 3 }} />
      </App>,
    );
    await screen.findByText('Connected · Synthetic');
    vi.mocked(drive.checkDriveConnection).mockResolvedValue({
      ...connected,
      error: 'Safe latest observation',
    });
    fireEvent.click(screen.getByRole('button', { name: 'Check connection' }));
    await screen.findByText('Safe latest observation');
    await act(async () => finish({ ...connected, status: 'NotConnected' }));
    expect(screen.getByText('Connected · Synthetic')).toBeInTheDocument();
  });
  it('resets busy and rejects late checks on account revision or identity change', async () => {
    let finish!: (value: drive.DriveConnection) => void;
    vi.mocked(drive.checkDriveConnection).mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
    const view = render(
      <App>
        <DriveConnectionPanel account={account} />
      </App>,
    );
    await screen.findByText('Connected · Synthetic');
    fireEvent.click(screen.getByRole('button', { name: 'Check connection' }));
    view.rerender(
      <App>
        <DriveConnectionPanel
          account={{
            ...account,
            credentialId: 'b',
            revision: 4,
            displayName: 'Other',
          }}
        />
      </App>,
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Reconnect with Google' }),
      ).toBeEnabled(),
    );
    await act(async () =>
      finish({
        ...connected,
        status: 'Disconnected',
        error: 'Old account result',
      }),
    );
    expect(screen.queryByText('Old account result')).toBeNull();
  });
});
