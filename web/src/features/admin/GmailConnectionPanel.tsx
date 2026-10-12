import { useCallback, useEffect, useState } from 'react';
import { Alert, App, Button, Descriptions, Flex, Spin, Tag, Typography, theme } from 'antd';
import { CheckCircleOutlined, ExclamationCircleOutlined, LinkOutlined, MinusCircleOutlined } from '@ant-design/icons';
import { checkEmailConnection, disconnectEmailOAuth, getEmailConnection, startEmailOAuth, type EmailOAuthConnection, type SystemCredential } from '../../services/adminApi';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { AdminErrorNotice } from './adminFailure';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { useAdminDetailLayout } from './useAdminDetailLayout';

export const googleSetupUrl = 'https://github.com/trannamtrung1st/agent-core/blob/main/docs/17-observability-and-operations.md#gmail-google-oauth-operator-setup';
export function EmailConnectionTag({ account }: { account: SystemCredential }) {
  const p = account.emailAccount;
  if (!p) return null;
  const connected = p.connectionStatus === 'Connected';
  const status = account.status === 'Disabled' ? 'Disabled' : p.preset === 'Custom' ? 'Configured' : connected ? p.synthetic ? 'Synthetic' : 'Connected' : p.connectionStatus === 'ReconnectRequired' ? 'Reconnect required' : p.connectionStatus === 'Disconnected' ? 'Disconnected' : 'Not connected';
  return <Tag color={account.status === 'Disabled' ? 'default' : connected ? 'success' : p.connectionStatus === 'ReconnectRequired' ? 'warning' : 'default'}
    icon={connected ? <CheckCircleOutlined /> : p.connectionStatus === 'ReconnectRequired' ? <ExclamationCircleOutlined /> : <MinusCircleOutlined />}>{status}</Tag>;
}
export function GmailConnectionPanel({ account, onChanged, showCredentialStatus = true, beforeConnect }: { account: SystemCredential; onChanged?: () => Promise<void>; showCredentialStatus?: boolean; beforeConnect?: () => Promise<void> }) {
  const { token } = theme.useToken(); const { modal } = App.useApp(); const layout = useAdminDetailLayout();
  const [connection, setConnection] = useState<EmailOAuthConnection>(); const [busy, setBusy] = useState(false); const [loading, setLoading] = useState(true);
  const [error, setError] = useState<AdminFailureNotice>();
  const load = useCallback(async () => { setLoading(true); setError(undefined); try { setConnection(await getEmailConnection(account)); }
    catch (e) { setError(describeAdminError(e, 'Unable to load connection. Retry to get current status.')); } finally { setLoading(false); } }, [account.credentialId, account.revision]);
  useEffect(() => { void load(); }, [load]);
  async function act(action: 'connect' | 'check' | 'disconnect') {
    setBusy(true); setError(undefined);
    try {
      if (action === 'connect') { await beforeConnect?.(); const start = await startEmailOAuth(account); window.location.assign(start.authorizationUrl); }
      else { setConnection(await (action === 'check' ? checkEmailConnection(account) : disconnectEmailOAuth(account))); await onChanged?.(); }
    } catch (e) { setError(describeAdminError(e, 'Connection changed or is unavailable. Refresh this account and retry.')); }
    finally { setBusy(false); }
  }
  const p = account.emailAccount!; const connected = connection?.status === 'Connected';
  return <Flex vertical gap={token.paddingSM}>
    {loading ? <Spin aria-label="Loading Google connection" /> : <>
      <Descriptions bordered size="small" column={1} {...layout} items={[
        ...(showCredentialStatus ? [{ key: 'credential', label: 'Credential status', children: account.status }] : []),
        { key: 'connection', label: 'Google connection', children: <EmailConnectionTag account={account} /> },
        { key: 'mailbox', label: p.googleSubject ? 'Verified mailbox' : 'Account address', children: p.emailAddress || 'Verified after authorization' },
        { key: 'permissions', label: 'Permissions', children: p.connectionStatus === 'Connected' ? 'Read mailbox; send only after separate exact approval' : 'Read and send permissions requested during consent' },
        ...(connection?.lastCheckedAtUtc ? [{ key: 'checked', label: 'Last checked', children: <time dateTime={connection.lastCheckedAtUtc}>{new Date(connection.lastCheckedAtUtc).toLocaleString()}</time> }] : [])
      ]} />
      {connection?.synthetic && <Alert type="info" showIcon title="Synthetic — no Google connection" description="This offline authorization exercises the owner journey. No real mailbox is accessed or email delivered." />}
      {!connection?.configured && <Alert type="warning" showIcon title="Google connection not configured" description={<span>Ask the operator to complete <a href={googleSetupUrl} target="_blank" rel="noreferrer">Google OAuth setup</a>.</span>} />}
      {connection?.error && <Alert type="warning" showIcon title={connection.error} />}
      <Typography.Text type="secondary">Connection actions take effect immediately. Test access checks authorization only and never sends email. Disconnect blocks this account for all bound agents. Instance bindings remain. Unbind removes only one agent’s access.</Typography.Text>
      {connection?.lastCheckedAtUtc && !connection.error && connected && <Typography.Text type="success" role="status">Access verified · no email sent</Typography.Text>}
      {connected && <Typography.Text type="secondary">To use this account, grant access in an <a href="/admin/instances" onClick={followAppLink}>agent’s Credentials section</a>.</Typography.Text>}
      <Flex gap={token.paddingXS} wrap>
        <Button type={connected ? 'default' : 'primary'} icon={<LinkOutlined aria-hidden />} disabled={busy || !connection?.configured || account.status !== 'Active'} loading={busy} onClick={() => void act('connect')}>{p.googleSubject ? 'Reconnect with Google' : 'Connect with Google'}</Button>
        {p.connectionStatus === 'Connected' && <Button disabled={busy || account.status !== 'Active'} onClick={() => void act('check')}>{connection?.error ? 'Retry check' : 'Test access'}</Button>}
        {(p.connectionStatus === 'Connected' || p.connectionStatus === 'ReconnectRequired') && <Button danger disabled={busy} onClick={() => confirmAction(modal, { title: 'Disconnect this Google account for all agents?', content: 'Bindings remain but cannot use this account until reconnected. Existing drafts require fresh authorization and approval.', okText: 'Disconnect account', danger: true, onOk: () => act('disconnect') })}>Disconnect</Button>}
      </Flex>
    </>}
    {error && <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button disabled={busy} onClick={() => void load()}>Retry</Button>} />}
  </Flex>;
}
