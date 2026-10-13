import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Flex, Typography, theme } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import {
  getDriveConnection,
  checkDriveConnection,
  disconnectDriveConnection,
  startDriveConnection,
  type DriveConnection,
} from '../../services/drive';
import type { SystemCredential } from '../../services/adminApi';
export function DriveConnectionPanel({
  account,
  onChanged,
}: {
  account: SystemCredential;
  onChanged?: () => Promise<void>;
}) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const [connection, setConnection] = useState<DriveConnection | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  const reads = useRef(0);
  const busyRef = useRef(false);
  useEffect(() => {
    const version = ++generation.current;
    const read = ++reads.current;
    busyRef.current = false;
    setBusy(false);
    setConnection(null);
    setError(null);
    void getDriveConnection(account)
      .then((value) => {
        if (version === generation.current && read === reads.current)
          setConnection(value);
      })
      .catch(() => {
        if (version === generation.current && read === reads.current)
          setError('Connection state unavailable. Reload credentials.');
      });
    return () => {
      generation.current++;
      reads.current++;
    };
  }, [account.credentialId, account.revision]);
  async function run(action: () => Promise<DriveConnection>) {
    if (busyRef.current) return;
    const version = generation.current;
    reads.current++;
    busyRef.current = true;
    setBusy(true);
    setError(null);
    try {
      const value = await action();
      if (version !== generation.current) return;
      setConnection(value);
      await onChanged?.();
    } catch (reason) {
      if (version === generation.current)
        setError(
          reason instanceof Error
            ? reason.message
            : 'Connection operation failed. Retry.',
        );
    } finally {
      if (version === generation.current) {
        busyRef.current = false;
        setBusy(false);
      }
    }
  }
  async function connect() {
    if (busyRef.current) return;
    const version = generation.current;
    reads.current++;
    busyRef.current = true;
    setBusy(true);
    setError(null);
    try {
      const result = await startDriveConnection(account);
      if (version !== generation.current) return;
      const url = new URL(result.authorizationUrl, location.origin);
      if (
        result.synthetic
          ? url.origin !== location.origin ||
            !url.pathname.startsWith(
              '/api/v2/integrations/google-drive/oauth/synthetic',
            )
          : url.protocol !== 'https:' || url.hostname !== 'accounts.google.com'
      )
        throw new Error('The authorization destination is unavailable.');
      location.assign(url.href);
    } catch (reason) {
      if (version === generation.current)
        setError(
          reason instanceof Error
            ? reason.message
            : 'Connection could not be started. Retry.',
        );
    } finally {
      if (version === generation.current) {
        busyRef.current = false;
        setBusy(false);
      }
    }
  }
  return (
    <Flex vertical gap={token.paddingSM}>
      <Flex wrap gap={token.paddingXS} align="center">
        <Typography.Text strong>Google Drive</Typography.Text>
        <Typography.Text type="secondary">
          {connection?.status ?? 'Reading connection…'}
          {connection?.synthetic ? ' · Synthetic' : ''}
        </Typography.Text>
      </Flex>
      <Typography.Text type="secondary">
        Files available to Agent Core · app-authorized access. Email
        authorization and tool grants are separate.
      </Typography.Text>
      {account.driveAccount?.emailAddress && (
        <Typography.Text>{account.driveAccount.emailAddress}</Typography.Text>
      )}
      {connection?.status === 'HostDisabled' && (
        <Alert
          type="info"
          showIcon
          title="Ask the operator to enable GoogleDrive:Enabled."
        />
      )}
      {connection &&
        !connection.configured &&
        connection.status !== 'HostDisabled' && (
          <Alert
            type="info"
            showIcon
            title="Google Drive OAuth is not configured. Ask the operator to configure the connection."
          />
        )}
      {(error || connection?.error) && (
        <Alert type="warning" showIcon title={error ?? connection?.error} />
      )}
      <Flex wrap gap={token.paddingXS}>
        <Button
          aria-label={
            account.driveAccount?.connectedAtUtc
              ? 'Reconnect with Google'
              : 'Connect with Google'
          }
          disabled={
            busy || account.status !== 'Active' || !connection?.configured
          }
          loading={busy}
          onClick={() => void connect()}
        >
          {account.driveAccount?.connectedAtUtc
            ? 'Reconnect with Google'
            : 'Connect with Google'}
        </Button>
        <Button
          disabled={busy || connection?.status !== 'Connected'}
          onClick={() => void run(() => checkDriveConnection(account))}
        >
          Check connection
        </Button>
        <Button
          danger
          disabled={
            busy ||
            !['Connected', 'ReconnectRequired'].includes(
              account.driveAccount?.connectionStatus ?? '',
            )
          }
          onClick={() =>
            confirmAction(modal, {
              title: 'Disconnect Google Drive?',
              content:
                'Removes local authorization and attempts Google revocation. Remote Drive files are kept.',
              okText: 'Disconnect',
              danger: true,
              onOk: () => run(() => disconnectDriveConnection(account)),
            })
          }
        >
          Disconnect
        </Button>
      </Flex>
      {connection?.lastCheckedAtUtc && (
        <Typography.Text type="secondary">
          Last checked{' '}
          <time dateTime={connection.lastCheckedAtUtc}>
            {new Date(connection.lastCheckedAtUtc).toLocaleString()}
          </time>
        </Typography.Text>
      )}
    </Flex>
  );
}
