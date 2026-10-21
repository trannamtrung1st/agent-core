import { CheckCircleOutlined,ExclamationCircleOutlined,MinusCircleOutlined,SyncOutlined } from '@ant-design/icons';
import { Alert,Button,Collapse,Descriptions,Flex,Select,Skeleton,Switch,Tag,Typography,theme } from 'antd';
import { useCallback,useEffect,useRef,useState } from 'react';
import { type EmailMonitoring,type SystemCredential } from '../../services/adminApi';
import { IdentityError,identityRequest } from '../../services/identity';
import { tenantEmailMonitoring,type TenantEmailMonitoring } from '../../services/tenantCredentials';
import { describeAdminError,type AdminFailureNotice } from './adminErrors';
import { AdminErrorNotice } from './adminFailure';
import { useAdminDetailLayout } from './useAdminDetailLayout';

function LocalTime({ value }: { value: string | null }) {
  return value ? <time dateTime={value}>{new Date(value).toLocaleString()}</time> : <>Not yet</>;
}
type Monitoring = TenantEmailMonitoring;
const statusText: Record<EmailMonitoring['status'], string> = {
  Off: 'Monitoring Off', HostDisabled: 'Monitoring unavailable by operator', Paused: 'Monitoring paused',
  Initializing: 'Establishing baseline from now', Checking: 'Checking inbox', Retrying: 'Retrying', Gap: 'Monitoring · coverage gap', Monitoring: 'Monitoring On',
};
export function EmailMonitoringSection({ account, tenant, parentBusy = false, onDirtyChange, onBusyChange }: {
  account: SystemCredential; tenant: string; parentBusy?: boolean; onDirtyChange?: (dirty: boolean) => void; onBusyChange?: (busy: boolean) => void;
}) {
  const { token } = theme.useToken(); const layout = useAdminDetailLayout();
  const [status, setStatus] = useState<Monitoring>(); const [saved, setSaved] = useState<Monitoring>();
  const [enabled, setEnabled] = useState(false); const [interval, setInterval] = useState(300);
  const [requiresReload, setRequiresReload] = useState(false);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState<'save' | 'check'>();
  const [error, setError] = useState<AdminFailureNotice>(); const [feedback, setFeedback] = useState('');
  const identity = useRef(0); const readSequence = useRef(0); const draftSequence = useRef(0); const dirtyRef = useRef(false); const busyRef = useRef(false);
  const dirty = !!saved && (enabled !== saved.enabled || interval !== saved.intervalSeconds);
  dirtyRef.current = dirty; busyRef.current = !!busy;
  useEffect(() => { onDirtyChange?.(dirty); return () => onDirtyChange?.(false); }, [dirty, onDirtyChange]);
  useEffect(() => { onBusyChange?.(!!busy); return () => onBusyChange?.(false); }, [busy, onBusyChange]);
  const apply = useCallback((value: Monitoring, replaceDraft: boolean) => {
    setStatus(value);
    if (replaceDraft) { dirtyRef.current = false; setSaved(value); setEnabled(value.enabled); setInterval(value.intervalSeconds); }
  }, []);
  const load = useCallback(async (replaceDraft: boolean, showLoading = false) => {
    const version = identity.current; const read = ++readSequence.current; const draft = draftSequence.current;
    if (showLoading) setLoading(true);
    try { const value = await identityRequest<TenantEmailMonitoring>(tenantEmailMonitoring(tenant, account.credentialId)); if (version !== identity.current || read !== readSequence.current) return;
      const replace = !busyRef.current && draft === draftSequence.current && (replaceDraft || !dirtyRef.current);
      apply(value, replace); if (replaceDraft && replace) { setRequiresReload(false); setError(undefined); setFeedback(''); } }
    catch (e) { if (version === identity.current && read === readSequence.current) { setRequiresReload(true); if (e instanceof IdentityError && [401, 403, 404].includes(e.status)) { setStatus(undefined); setSaved(undefined); dirtyRef.current = false; } setError(describeAdminError(e, 'Monitoring could not be loaded. Reload this account to retry.')); } }
    finally { if (version === identity.current && showLoading) setLoading(false); }
  }, [account.credentialId, tenant, apply]);
  useEffect(() => {
    identity.current++; draftSequence.current++; busyRef.current = false; setBusy(undefined); setEnabled(false); setInterval(300);
    setStatus(undefined); setSaved(undefined); setRequiresReload(false); setError(undefined); setFeedback(''); dirtyRef.current = false;
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
    if (!saved || busyRef.current || parentBusy || requiresReload || (!saved.canManage || action === 'check' && !saved.canCheck || action === 'save' && enabled && !saved.canEnable)) return;
    const version = ++identity.current; busyRef.current = true; setBusy(action); setError(undefined); setFeedback('');
    try {
      const value = await identityRequest<TenantEmailMonitoring>(tenantEmailMonitoring(tenant, account.credentialId) + (action === 'check' ? '/check' : ''), {
        method: action === 'save' ? 'PUT' : 'POST', body: { expectedRevision: saved.securityRevision, expectedCredentialRevision: saved.credentialRevision,
          expectedWatchRevision: saved.configurationRevision, ...(action === 'save' ? { enabled, intervalSeconds: interval } : {}) }
      });
      if (version !== identity.current) return;
      apply(value, true); setFeedback(action === 'save' ? 'Monitoring settings saved.' : 'Check queued. It may trigger subscribed Automations.');
    } catch (e) { if (version === identity.current) { setRequiresReload(true); if (e instanceof IdentityError && [401, 403, 404].includes(e.status)) { setStatus(undefined); setSaved(undefined); dirtyRef.current = false; } setError(describeAdminError(e, 'Monitoring changed or is unavailable. Your draft is retained; reload to review current settings.')); } }
    finally { if (version === identity.current) { busyRef.current = false; setBusy(undefined); } }
  }
  function editDraft(change: () => void) {
    draftSequence.current++; dirtyRef.current = true; setFeedback(''); change();
  }
  const locked = !!busy || parentBusy || requiresReload || !status?.canManage;
  const warning = status && ['Paused', 'Retrying', 'Gap', 'HostDisabled'].includes(status.status);
  const checking = status && (status.checkPending || ['Initializing', 'Checking'].includes(status.status));
  return <Flex vertical gap={token.padding} role="region" aria-label="Email monitoring"
    style={{ paddingBlockStart: token.padding, borderBlockStart: `${token.lineWidth}px solid ${token.colorBorderSecondary}` }}>
    <Flex vertical gap={token.paddingXS}>
      <Flex gap={token.paddingXS} wrap align="center">
        <Typography.Text strong style={{ fontSize: token.fontSizeLG }}>Monitoring</Typography.Text>
        {status && <span role="status"><Tag color={warning ? 'warning' : status.status === 'Monitoring' ? 'success' : checking ? 'processing' : 'default'}
          icon={warning ? <ExclamationCircleOutlined /> : status.status === 'Monitoring' ? <CheckCircleOutlined /> : checking ? <SyncOutlined /> : <MinusCircleOutlined />}>{statusText[status.status]}</Tag></span>}
        {status?.checkPending && !['Initializing', 'Checking'].includes(status.status) && <Typography.Text>Check queued</Typography.Text>}
      </Flex>
      <Typography.Text type="secondary" style={{ maxInlineSize: '75ch' }}>Observe new inbox messages from now and notify subscribed Automations. Existing mail is skipped. Monitoring does not grant agents account access or approve sending.</Typography.Text>
    </Flex>
    {loading ? <Skeleton active paragraph={{ rows: 2 }} title={false} /> : status && saved && <>
      {warning && <Alert showIcon type="warning" title={status.status === 'Gap' ? 'Some messages may have been missed' : statusText[status.status]}
        description={status.status === 'HostDisabled' ? 'Ask the operator to enable Email and EmailMonitoring. Your saved settings remain.' : status.status === 'Retrying' ? 'A temporary mailbox failure will retry automatically. Check now requests another attempt.' : status.status === 'Gap' ? 'The mailbox cursor changed or expired. Monitoring resumed from a new baseline; old mail will not be replayed.' : 'Review the saved account status and connection. Restore account access, then Check now.'} />}
<Flex vertical gap={token.paddingSM}>
        <Flex className="admin-email-monitor-controls" wrap gap={token.padding} align="center">
          <Flex gap={token.paddingXS} align="center"><Switch id={`email-monitor-${account.credentialId}`} aria-label="New inbox messages" checked={enabled} disabled={locked || !enabled && !status.canEnable} onChange={value => editDraft(() => setEnabled(value))} /><label htmlFor={`email-monitor-${account.credentialId}`}>New inbox messages</label></Flex>
          <Flex gap={token.paddingXS} align="center"><label htmlFor={`email-interval-${account.credentialId}`}>Check every</label><Select virtual={false} id={`email-interval-${account.credentialId}`} aria-label="Check every" value={interval} disabled={locked} onChange={value => editDraft(() => setInterval(value))}
            options={[{ value: 60, label: '1 minute' }, { value: 300, label: '5 minutes' }, { value: 900, label: '15 minutes' }]} /></Flex>
        </Flex>
        {dirty && <Typography.Text type="secondary">Unsaved monitoring changes</Typography.Text>}
        <Flex gap={token.paddingXS} wrap>
          <Button aria-label="Save monitoring" type="primary" disabled={!dirty || locked || enabled && !status.canEnable} loading={busy === 'save'} onClick={() => void act('save')}>Save monitoring</Button>
          {dirty && <Button disabled={locked} onClick={() => { draftSequence.current++; apply(saved, true); setError(undefined); setFeedback(''); }}>Cancel changes</Button>}
          <Button aria-label="Check now" disabled={locked || !status.canCheck || dirty || !status.enabled || !status.hostAvailable || !status.accountAvailable || status.checkPending} loading={busy === 'check'} onClick={() => void act('check')}>Check now</Button>
          <Button type="text" disabled={!!busy || parentBusy} onClick={() => void load(true)}>Reload monitoring</Button>
        </Flex>
        {requiresReload && <Alert type="warning" showIcon title="Reload monitoring before trying again" description="Your choices are retained. Reload to review the current account, access and monitoring settings." />}
        {status.canManage && !status.canEnable && <Typography.Text type="secondary">You can turn monitoring off. Enabling or checking requires account use access.</Typography.Text>}
        {!status.canManage && <Typography.Text type="secondary">Monitoring settings are read-only with your current account permissions.</Typography.Text>}
        {feedback && <Typography.Text role="status" type={feedback === 'Monitoring settings saved.' ? 'success' : undefined}>{feedback}</Typography.Text>}
        <Typography.Text type="secondary" style={{ maxInlineSize: '75ch' }}>Check now queues a mailbox scan and may trigger Automations.</Typography.Text>
      </Flex>
      <Descriptions size="small" column={1} {...layout} items={[
        { key: 'success', label: 'Last successful check', children: <LocalTime value={status.lastSuccessAtUtc} /> },
        { key: 'next', label: 'Next check', children: !status.enabled ? 'Monitoring is off' : !status.hostAvailable ? 'Unavailable by operator' : !status.accountAvailable || status.status === 'Paused' ? 'Paused' : <LocalTime value={status.nextCheckAtUtc} /> },
      ]} />
      <Collapse ghost size="small" items={[{ key: 'details', label: 'Monitoring diagnostics', children: <Descriptions size="small" column={1} {...layout} items={[
        { key: 'checked', label: 'Last attempted check', children: <LocalTime value={status.lastCheckedAtUtc} /> },
        { key: 'event', label: 'Last Event staged', children: <LocalTime value={status.lastEventAtUtc} /> },
        { key: 'gap', label: 'Last coverage gap', children: <LocalTime value={status.lastGapAtUtc} /> },
        ...(status.errorCode ? [{ key: 'error', label: 'Recovery', children: <AdminErrorNotice message={status.errorCode === 'email_watch_access_required' ? 'Review account access, then Check now.' : 'Temporary mailbox failure. Retry is scheduled.'} diagnosticId={status.diagnosticId ?? undefined} showDetailsLabel /> }] : []),
      ]} /> }]} />
    </>}
    {error && <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button disabled={!!busy || parentBusy} onClick={() => void load(true, !saved)}>Reload</Button>} />}
  </Flex>;
}
