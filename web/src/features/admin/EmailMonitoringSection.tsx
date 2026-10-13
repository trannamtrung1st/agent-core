import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Collapse, Descriptions, Flex, Select, Skeleton, Switch, Tag, Typography, theme } from 'antd';
import { CheckCircleOutlined, ExclamationCircleOutlined, MinusCircleOutlined, SyncOutlined } from '@ant-design/icons';
import { checkEmailMonitoring, getEmailMonitoring, saveEmailMonitoring, type EmailMonitoring, type SystemCredential } from '../../services/adminApi';
import { followAppLink } from '../../app/appLink';
import { AdminErrorNotice } from './adminFailure';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { useAdminDetailLayout } from './useAdminDetailLayout';

function LocalTime({ value }: { value: string | null }) {
  return value ? <time dateTime={value}>{new Date(value).toLocaleString()}</time> : <>Not yet</>;
}
const statusText: Record<EmailMonitoring['status'], string> = {
  Off: 'Monitoring Off', HostDisabled: 'Monitoring unavailable by operator', Paused: 'Monitoring paused',
  Initializing: 'Establishing baseline from now', Checking: 'Checking inbox', Retrying: 'Retrying', Gap: 'Monitoring · coverage gap', Monitoring: 'Monitoring On',
};
export function EmailMonitoringSection({ account, instanceId }: { account: SystemCredential; instanceId?: string }) {
  const { token } = theme.useToken(); const layout = useAdminDetailLayout();
  const [status, setStatus] = useState<EmailMonitoring>(); const [saved, setSaved] = useState<EmailMonitoring>();
  const [enabled, setEnabled] = useState(false); const [interval, setInterval] = useState(300);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState<'save' | 'check'>();
  const [error, setError] = useState<AdminFailureNotice>(); const [feedback, setFeedback] = useState('');
  const identity = useRef(0); const readSequence = useRef(0); const draftSequence = useRef(0); const dirtyRef = useRef(false); const busyRef = useRef(false);
  const dirty = !!saved && (enabled !== saved.enabled || interval !== saved.intervalSeconds);
  dirtyRef.current = dirty; busyRef.current = !!busy;
  const apply = useCallback((value: EmailMonitoring, replaceDraft: boolean) => {
    setStatus(value);
    if (replaceDraft) { dirtyRef.current = false; setSaved(value); setEnabled(value.enabled); setInterval(value.intervalSeconds); }
  }, []);
  const load = useCallback(async (replaceDraft: boolean, showLoading = false) => {
    const version = identity.current; const read = ++readSequence.current; const draft = draftSequence.current;
    if (showLoading) setLoading(true);
    try { const value = await getEmailMonitoring(account.credentialId); if (version !== identity.current || read !== readSequence.current) return;
      apply(value, replaceDraft || (!dirtyRef.current && draft === draftSequence.current)); if (replaceDraft) { setError(undefined); setFeedback(''); } }
    catch (e) { if (version === identity.current) setError(describeAdminError(e, 'Monitoring could not be loaded. Reload this account to retry.')); }
    finally { if (version === identity.current && showLoading) setLoading(false); }
  }, [account.credentialId, apply]);
  useEffect(() => {
    identity.current++; setStatus(undefined); setSaved(undefined); setError(undefined); setFeedback(''); dirtyRef.current = false;
    void load(true, true);
    return () => { identity.current++; };
  }, [load]);
  useEffect(() => { if (saved) void load(false); }, [account.revision]); // Keep unsaved monitoring choices during OAuth/account changes.
  useEffect(() => {
    if (!status?.enabled) return;
    const timer = window.setInterval(() => { if (!busyRef.current) void load(false); }, 5000);
    return () => window.clearInterval(timer);
  }, [status?.enabled, load]);
  async function act(action: 'save' | 'check') {
    if (!saved || busyRef.current) return;
    const version = ++identity.current; busyRef.current = true; setBusy(action); setError(undefined); setFeedback('');
    try {
      const value = action === 'save' ? await saveEmailMonitoring(account.credentialId, { expectedRevision: saved.configurationRevision, enabled, intervalSeconds: interval })
        : await checkEmailMonitoring(account.credentialId, saved.configurationRevision);
      if (version !== identity.current) return;
      apply(value, true); setFeedback(action === 'save' ? 'Monitoring settings saved.' : 'Check queued. It may trigger subscribed Automations.');
    } catch (e) { if (version === identity.current) setError(describeAdminError(e, 'Monitoring changed or is unavailable. Your draft is retained; reload to review current settings.')); }
    finally { if (version === identity.current) { busyRef.current = false; setBusy(undefined); } }
  }
  const warning = status && ['Paused', 'Retrying', 'Gap', 'HostDisabled'].includes(status.status);
  return <Flex vertical gap={token.paddingSM} role="region" aria-label="Email monitoring" style={{ paddingBlockStart: token.padding }}>
    <Typography.Text strong>Monitoring</Typography.Text>
    <Typography.Text type="secondary" style={{ maxInlineSize: '75ch' }}>Observe new inbox messages from now and notify subscribed Automations. Existing mail is skipped. Monitoring does not grant agents account access or approve sending.</Typography.Text>
    {loading ? <Skeleton active paragraph={{ rows: 2 }} title={false} /> : status && saved && <>
      <Flex gap={token.paddingXS} wrap align="center">
        <Tag color={warning ? 'warning' : status.status === 'Monitoring' ? 'success' : 'default'} icon={warning ? <ExclamationCircleOutlined /> : status.status === 'Monitoring' ? <CheckCircleOutlined /> : status.checkPending ? <SyncOutlined /> : <MinusCircleOutlined />}>{statusText[status.status]}</Tag>
        {status.checkPending && !['Initializing', 'Checking'].includes(status.status) && <Typography.Text>Check queued</Typography.Text>}
        {status.subscribedInstances === 0 && <Typography.Text type="secondary">No subscribed Automations</Typography.Text>}
      </Flex>
      {warning && <Alert showIcon type="warning" title={status.status === 'Gap' ? 'Some messages may have been missed' : statusText[status.status]}
        description={status.status === 'HostDisabled' ? 'Ask the operator to enable Email and EmailMonitoring. Your saved settings remain.' : status.status === 'Retrying' ? 'A temporary mailbox failure will retry automatically. Check now requests another attempt.' : status.status === 'Gap' ? 'The mailbox cursor changed or expired. Monitoring resumed from a new baseline; old mail will not be replayed.' : 'Review account status and Test access above. Reconnect Gmail or update Custom credentials, then Check now.'} />}
      <Flex wrap gap={token.paddingSM} align="center">
        <Flex gap={token.paddingXS} align="center"><Switch id={`email-monitor-${account.credentialId}`} aria-label="New inbox messages" checked={enabled} disabled={!!busy} onChange={value => { draftSequence.current++; dirtyRef.current = true; setEnabled(value); }} /><label htmlFor={`email-monitor-${account.credentialId}`}>New inbox messages</label></Flex>
        <Flex gap={token.paddingXS} align="center"><label htmlFor={`email-interval-${account.credentialId}`}>Check every</label><Select virtual={false} id={`email-interval-${account.credentialId}`} aria-label="Check every" value={interval} disabled={!!busy} onChange={value => { draftSequence.current++; dirtyRef.current = true; setInterval(value); }}
          options={[{ value: 60, label: '1 minute' }, { value: 300, label: '5 minutes' }, { value: 900, label: '15 minutes' }]} /></Flex>
      </Flex>
      {dirty && <Typography.Text type="secondary">Unsaved monitoring changes</Typography.Text>}
      <Flex gap={token.paddingXS} wrap>
        <Button aria-label="Save monitoring" type="primary" disabled={!dirty || !!busy} loading={busy === 'save'} onClick={() => void act('save')}>Save monitoring</Button>
        {dirty && <Button disabled={!!busy} onClick={() => { draftSequence.current++; apply(saved, true); setError(undefined); }}>Cancel changes</Button>}
        <Button aria-label="Check now" disabled={!!busy || dirty || !status.enabled || !status.hostAvailable || !status.accountAvailable || status.checkPending} loading={busy === 'check'} onClick={() => void act('check')}>Check now</Button>
        <Button disabled={!!busy} onClick={() => void load(true)}>Reload monitoring</Button>
      </Flex>
      <Typography.Text type="secondary" style={{ maxInlineSize: '75ch' }}>Check now queues a mailbox scan and may trigger Automations. Test access only checks the connection.</Typography.Text>
      {feedback && <Typography.Text role="status">{feedback}</Typography.Text>}
      <Descriptions size="small" column={1} {...layout} items={[
        { key: 'success', label: 'Last successful check', children: <LocalTime value={status.lastSuccessAtUtc} /> },
        { key: 'next', label: 'Next check', children: status.enabled && status.hostAvailable && status.accountAvailable ? <LocalTime value={status.nextCheckAtUtc} /> : 'Paused / Off' },
      ]} />
      <Flex wrap gap={token.paddingXS}><a href={instanceId ? `/admin/instances/${instanceId}/automation/automations` : '/admin/instances'} onClick={followAppLink}>Configure an Instance Automation</a><a href="/admin/connections/events" onClick={followAppLink}>View Events catalog</a></Flex>
      <Collapse ghost size="small" items={[{ key: 'details', label: 'Monitoring diagnostics', children: <Descriptions size="small" column={1} {...layout} items={[
        { key: 'checked', label: 'Last attempted check', children: <LocalTime value={status.lastCheckedAtUtc} /> },
        { key: 'event', label: 'Last Event staged', children: <LocalTime value={status.lastEventAtUtc} /> },
        { key: 'gap', label: 'Last coverage gap', children: <LocalTime value={status.lastGapAtUtc} /> },
        ...(status.errorCode ? [{ key: 'error', label: 'Recovery', children: <AdminErrorNotice message={status.errorCode === 'email_watch_access_required' ? 'Review account access, then Check now.' : 'Temporary mailbox failure. Retry is scheduled.'} diagnosticId={status.diagnosticId ?? undefined} showDetailsLabel /> }] : []),
      ]} /> }]} />
    </>}
    {error && <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button disabled={!!busy} onClick={() => void load(true, !saved)}>Reload</Button>} />}
  </Flex>;
}
