import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Alert,
  App,
  Button,
  Descriptions,
  Flex,
  Spin,
  Typography,
  theme,
} from 'antd';
import { LinkOutlined } from '@ant-design/icons';
import {
  checkEmailConnection,
  disconnectEmailOAuth,
  getEmailConnection,
  startEmailOAuth,
  type EmailOAuthConnection,
  type SystemCredential,
} from '../../services/adminApi';
import { adminHomePath, adminInstancePath } from '../../app/appRoute';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { AdminErrorNotice } from './adminFailure';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { useAdminDetailLayout } from './useAdminDetailLayout';

export const googleSetupUrl =
  'https://github.com/trannamtrung1st/agent-core/blob/main/docs/17-observability-and-operations.md#gmail-google-oauth-operator-setup';
import { CredentialConnectionTag as EmailConnectionTag } from './CredentialConnectionTag';
export { EmailConnectionTag };
export function GmailConnectionPanel({
  account,
  onChanged,
  showCredentialStatus = true,
  beforeConnect,
  instanceId,
}: {
  instanceId?: string;
  account: SystemCredential;
  onChanged?: () => Promise<void>;
  showCredentialStatus?: boolean;
  beforeConnect?: () => Promise<void>;
}) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const layout = useAdminDetailLayout();
  const [connection, setConnection] = useState<EmailOAuthConnection>();
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<AdminFailureNotice>();
  const identity = useRef(0);
  const readSequence = useRef(0);
  const busyRef = useRef(false);
  const load = useCallback(async () => {
    const version = identity.current;
    const read = ++readSequence.current;
    const current = () =>
      version === identity.current && read === readSequence.current;
    setLoading(true);
    setError(undefined);
    try {
      const value = await getEmailConnection(account);
      if (current()) setConnection(value);
    } catch (e) {
      if (current())
        setError(
          describeAdminError(
            e,
            'Unable to load connection. Retry to get current status.',
          ),
        );
    } finally {
      if (current()) setLoading(false);
    }
  }, [account.credentialId, account.revision]);
  useEffect(() => {
    identity.current++;
    busyRef.current = false;
    setBusy(false);
    setConnection(undefined);
    void load();
    return () => {
      identity.current++;
      readSequence.current++;
    };
  }, [load]);
  async function act(action: 'connect' | 'check' | 'disconnect') {
    if (busyRef.current) return;
    const version = identity.current;
    readSequence.current++;
    busyRef.current = true;
    setBusy(true);
    setError(undefined);
    try {
      if (action === 'connect') {
        await beforeConnect?.();
        if (version !== identity.current) return;
        const start = await startEmailOAuth(account);
        if (version === identity.current)
          window.location.assign(start.authorizationUrl);
      } else {
        const value = await (action === 'check'
          ? checkEmailConnection(account)
          : disconnectEmailOAuth(account));
        if (version !== identity.current) return;
        setConnection(value);
        await onChanged?.();
      }
    } catch (e) {
      if (version === identity.current)
        setError(
          describeAdminError(
            e,
            'Connection changed or is unavailable. Refresh this account and retry.',
          ),
        );
    } finally {
      if (version === identity.current) {
        busyRef.current = false;
        setBusy(false);
      }
    }
  }
  const p = account.emailAccount!;
  const connected = connection?.status === 'Connected';
  return (
    <Flex vertical gap={token.paddingSM}>
      {loading ? (
        <Spin aria-label="Loading Google connection" />
      ) : (
        <>
          <Descriptions
            bordered
            size="small"
            column={1}
            {...layout}
            items={[
              ...(showCredentialStatus
                ? [
                    {
                      key: 'credential',
                      label: 'Credential status',
                      children: account.status,
                    },
                  ]
                : []),
              {
                key: 'connection',
                label: 'Google connection',
                children: <EmailConnectionTag account={account} />,
              },
              {
                key: 'mailbox',
                label: p.googleSubject ? 'Verified mailbox' : 'Account address',
                children: p.emailAddress || 'Verified after authorization',
              },
              {
                key: 'permissions',
                label: 'Permissions',
                children:
                  p.connectionStatus === 'Connected'
                    ? 'Read mailbox; send only after separate exact approval'
                    : 'Read and send permissions requested during consent',
              },
              ...(connection?.lastCheckedAtUtc
                ? [
                    {
                      key: 'checked',
                      label: 'Last checked',
                      children: (
                        <time dateTime={connection.lastCheckedAtUtc}>
                          {new Date(
                            connection.lastCheckedAtUtc,
                          ).toLocaleString()}
                        </time>
                      ),
                    },
                  ]
                : []),
            ]}
          />
          {connection?.synthetic && (
            <Alert
              type="info"
              showIcon
              title="Synthetic — no Google connection"
              description="This offline authorization exercises the owner journey. No real mailbox is accessed or email delivered."
            />
          )}
          {!connection?.configured && (
            <Alert
              type="warning"
              showIcon
              title="Google connection not configured"
              description={
                <span>
                  Ask the operator to complete{' '}
                  <a href={googleSetupUrl} target="_blank" rel="noreferrer">
                    Google OAuth setup
                  </a>
                  .
                </span>
              }
            />
          )}
          {connection?.error && (
            <Alert
              type="warning"
              showIcon
              title={
                <AdminErrorNotice
                  message={connection.error}
                  diagnosticId={connection.diagnosticId ?? undefined}
                  showDetailsLabel
                />
              }
            />
          )}
          <Typography.Text type="secondary">
            Connection actions take effect immediately. Test access checks
            authorization only and never sends email. Disconnect blocks this
            account for all bound agents. Instance bindings remain. Unbind
            removes only one agent’s access.
          </Typography.Text>
          {connection?.lastCheckedAtUtc && !connection.error && connected && (
            <Typography.Text type="success" role="status">
              Access verified · no email sent
            </Typography.Text>
          )}
          {connected && (
            <Typography.Text type="secondary">
              To use this account, grant access in an{' '}
              <a
                href={
                  instanceId
                    ? `${adminInstancePath(instanceId, 'credentials')}#credentials`
                    : adminHomePath('instances')
                }
                onClick={followAppLink}
              >
                Instance’s Credential bindings page
              </a>
              .
            </Typography.Text>
          )}
          <Flex gap={token.paddingXS} wrap>
            <Button
              type={connected ? 'default' : 'primary'}
              icon={<LinkOutlined aria-hidden />}
              disabled={
                busy || !connection?.configured || account.status !== 'Active'
              }
              loading={busy}
              onClick={() => void act('connect')}
            >
              {p.googleSubject
                ? 'Reconnect with Google'
                : 'Connect with Google'}
            </Button>
            {p.connectionStatus === 'Connected' && (
              <Button
                disabled={busy || account.status !== 'Active'}
                onClick={() => void act('check')}
              >
                {connection?.error ? 'Retry check' : 'Test access'}
              </Button>
            )}
            {(p.connectionStatus === 'Connected' ||
              p.connectionStatus === 'ReconnectRequired') && (
              <Button
                danger
                disabled={busy}
                onClick={() =>
                  confirmAction(modal, {
                    title: 'Disconnect this Google account for all agents?',
                    content:
                      'Bindings remain but cannot use this account until reconnected. Existing drafts require fresh authorization and approval.',
                    okText: 'Disconnect account',
                    danger: true,
                    onOk: () => act('disconnect'),
                  })
                }
              >
                Disconnect
              </Button>
            )}
          </Flex>
        </>
      )}
      {error && (
        <Alert
          type="error"
          showIcon
          title={
            <AdminErrorNotice
              message={error.message}
              diagnosticId={error.diagnosticId}
              showDetailsLabel
            />
          }
          action={
            <Button disabled={busy} onClick={() => void load()}>
              Retry
            </Button>
          }
        />
      )}
    </Flex>
  );
}
