import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Flex, Form, Input, Modal, Select, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { followAppLink } from '../../app/appLink';
import { identityRequest, type TenantContext } from '../../services/identity';
import { tenantAgentApi } from '../../services/tenantAgentConfiguration';
import { tenantAutomationConversations } from '../../services/tenantAutomations';
import { AdminSessionPicker } from '../admin/AdminSessionPicker';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantAgentPath } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

type SourceKind = 'Session' | 'AgentRun';
type Selected = { kind: SourceKind; id: string; epoch: number };
type Preview = { sourceKind: SourceKind; sourceId: string; title: string; throughCursor: number; sourceRevision: number; securityRevision: number; instanceRevision: number; sourceFingerprint: string };
type Receipt = { created: boolean; sessionId: string; runId: string; securityRevision: number };
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function TenantExperienceReviewButton({ context, id }: { context: TenantContext; id: string }) {
  const [open, setOpen] = useState(false);
  return <><Button onClick={() => setOpen(true)}>Review completed work</Button>
    {open && <ReviewDialog key={`${context.tenant.tenantId}:${context.principalId}:${id}`} context={context} id={id} close={() => setOpen(false)} />}</>;
}
function ReviewDialog({ context, id, close }: { context: TenantContext; id: string; close: () => void }) {
  const [kind, setKind] = useState<SourceKind>('Session'); const [sourceId, setSourceId] = useState(''); const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState<Selected>(); const count = useRef(0);
  const conversations = useMemo(() => tenantAutomationConversations(context.tenant.tenantId), [context.tenant.tenantId]);
  const inspect = () => setSelected({ kind, id: sourceId, epoch: ++count.current });
  return <Modal open title="Review completed work" onCancel={close} closable={!busy} keyboard={!busy} mask={{ closable: !busy }}
    footer={<Button disabled={busy} onClick={close}>Close</Button>}>
    <Flex vertical gap="var(--ac-space-section)">
      <Typography.Paragraph>Queue one background review of a stable conversation or completed Run. The agent inspects the selected source under its own current access. Inspection and recording may require separate approval.</Typography.Paragraph>
      <Form layout="vertical">
        <Form.Item label="Source type"><Select aria-label="Source type" disabled={busy} value={kind} options={[{ value: 'Session', label: 'Conversation' }, { value: 'AgentRun', label: 'Completed Run' }]}
          onChange={value => { setKind(value); setSourceId(''); setSelected(undefined); }} /></Form.Item>
        <Form.Item label={kind === 'Session' ? 'Source conversation' : 'Completed Run ID'}>
          {kind === 'Session' ? <AdminSessionPicker instanceId={id} source={conversations} value={sourceId} disabled={busy} label="Review source conversation"
            onChange={value => { setSourceId(value); setSelected(undefined); }} /> : <Input aria-label="Completed Run ID" value={sourceId} disabled={busy}
              placeholder="Paste an exact completed Run ID" onChange={event => { setSourceId(event.target.value); setSelected(undefined); }} />}
        </Form.Item>
        <Button disabled={busy || !uuid.test(sourceId)} onClick={inspect}>Inspect selected source</Button>
      </Form>
      {selected && <ReviewSource key={`${selected.kind}:${selected.id}:${selected.epoch}`} context={context} id={id} selected={selected} onBusy={setBusy} inspect={inspect} />}
    </Flex>
  </Modal>;
}
function ReviewSource({ context, id, selected, onBusy, inspect }: { context: TenantContext; id: string; selected: Selected; onBusy: (busy: boolean) => void; inspect: () => void }) {
  const path = tenantAgentApi(context.tenant.tenantId, id, '/experience');
  const source = useIdentityQuery<Preview>(path + '/review-source?' + new URLSearchParams({ sourceKind: selected.kind, sourceId: selected.id }));
  const command = useTenantCommand(); const { modal } = App.useApp(); const layout = useAdminDetailLayout();
  const [receipt, setReceipt] = useState<Receipt>(); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  useEffect(() => () => { confirmation.current?.destroy(); confirmation.current = null; }, []);
  useEffect(() => { onBusy(command.busy); return () => onBusy(false); }, [command.busy]);
  const request = () => {
    if (!source.data || command.busy || command.error !== undefined || receipt || confirmation.current) return; const saved = source.data;
    confirmation.current = confirmAction(modal, { title: 'Queue this review?', okText: 'Queue review', cancelText: 'Keep inspecting',
      content: `${context.tenant.name}: ${saved.title || 'Selected completed work'} (${saved.sourceKind}, through ${saved.throughCursor}). This queues one review of this exact source observation. It grants no source-read consent or recording permission and changes no existing Memory or Experience.`,
      afterClose: () => { confirmation.current = null; },
      onOk: () => command.run(signal => identityRequest<Receipt>(path + '/checkpoints', { method: 'POST', signal, body: {
        sourceKind: saved.sourceKind, sourceId: saved.sourceId, expectedRevision: saved.securityRevision,
        expectedInstanceRevision: saved.instanceRevision, sourceFingerprint: saved.sourceFingerprint
      } }), setReceipt) });
  };
  return <Flex vertical gap="var(--ac-space-default)">
    {receipt ? <><Alert showIcon type="success" role="status" title={receipt.created ? 'Review queued' : 'Existing review found'}
      description={receipt.created ? 'The admitted review may still need approval or finish without recording an observation.' : 'No new work was queued. Inspect the saved review result before deciding on further work.'} />
      <Typography.Link href={tenantAgentPath(context.tenant.tenantId, id, 'activity-sessions') + '?session=' + encodeURIComponent(receipt.sessionId)} onClick={followAppLink}>Inspect review work</Typography.Link></>
      : command.error !== undefined ? <><AccessError error={command.error} /><Alert showIcon type="warning" title="A fresh source read is required" description="The command is not repeated. Inspect the selected source again before deciding on another review." /><Button onClick={inspect}>Inspect source again</Button></>
      : source.loading ? <AccessLoading /> : source.error ? <AccessError error={source.error} retry={inspect} /> : source.data && <>
        <Descriptions {...layout} column={1} size="small" items={[
          { key: 'title', label: 'Selected source', children: source.data.title }, { key: 'kind', label: 'Type', children: source.data.sourceKind === 'Session' ? 'Conversation' : 'Completed Run' },
          { key: 'id', label: 'Source ID', children: <Typography.Text copyable>{source.data.sourceId}</Typography.Text> },
          { key: 'cursor', label: 'Observed through', children: source.data.throughCursor }, { key: 'revision', label: 'Source revision', children: source.data.sourceRevision }
        ]} /><Button type="primary" disabled={command.busy || command.requiresReload} onClick={request}>Queue review</Button>
      </>}
  </Flex>;
}
