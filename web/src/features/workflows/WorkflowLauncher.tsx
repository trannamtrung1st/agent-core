import { useEffect, useRef, useState, type RefObject } from 'react';
import { Alert, Button, Checkbox, DatePicker, Flex, Form, Grid, Input, InputNumber, Select, Typography } from 'antd';
import { IdentityCompletionError, IdentityError, identityRequest, tenantApi, type Page, type TenantContext } from '../../services/identity';
import { freshTenantRevision } from '../../services/tenantEvaluations';
import { executionApi, readWorkflowGraph, startWorkflow, workflowApi, type WorkflowControls, type WorkflowDefinition, type WorkflowStartReceipt, type WorkflowVersion } from '../../services/tenantWorkflows';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { useAccessDraftGuard } from '../access/useAccessDraftGuard';
import { SessionSandboxFrame } from '../chat/SessionSandboxFrame';
import type { WorkflowAgent } from './WorkflowNodeProperties';

type StartValues = { inputs: Record<string, unknown>; coordinator?: string };
export function WorkflowLauncher({ context, definition, version, sourceSessionId, openerRef, onClose, onStarted }: {
  context: TenantContext; definition: Pick<WorkflowDefinition, 'workflowId' | 'name'>; version: number; sourceSessionId?: string; openerRef?: RefObject<HTMLButtonElement | null>;
  onClose: () => void; onStarted: (receipt: WorkflowStartReceipt) => void;
}) {
  const tenant = context.tenant.tenantId; const screens = Grid.useBreakpoint(); const [form] = Form.useForm<StartValues>();
  const publication = useIdentityQuery<WorkflowVersion>(workflowApi(tenant, `/${definition.workflowId}/versions/${version}`));
  const controls = useIdentityQuery<WorkflowControls>(workflowApi(tenant, `/${definition.workflowId}/editor`));
  const [agentPage, setAgentPage] = useState(0);
  const agents = useIdentityQuery<Page<WorkflowAgent>>(context.permissions.includes('agents.read') ? tenantApi(tenant, `/agents?page=${agentPage}&pageSize=100`) : null);
  const [busy, setBusy] = useState(false); const [dirty, setDirty] = useState(false); const [pending, setPending] = useState<string>(); const [error, setError] = useState<unknown>();
  const active = useRef(false); const controller = useRef(new AbortController());
  useEffect(() => { const current = new AbortController(); controller.current = current; return () => current.abort(); }, []);
  const guard = useAccessDraftGuard(dirty || !!pending, busy, onClose);
  const graph = publication.data ? readWorkflowGraph(publication.data.graph.canonicalJson) : undefined;
  const allowed = controls.data?.canStart && graph?.allowedOrigins.includes(0);
  const hasTasks = graph?.nodes.some(n => n.kind === 1);
  function accept(receipt: WorkflowStartReceipt) {
    if (receipt.workflowId !== definition.workflowId || receipt.version !== version || receipt.graphHash !== publication.data?.graph.hash || !receipt.executionId) throw new IdentityCompletionError();
    guard.release();
    try { onStarted(receipt); } catch { throw new IdentityCompletionError(); }
    setPending(undefined); setDirty(false);
  }
  async function submit(values: StartValues) {
    if (!graph || !allowed || active.current || pending) return;
    const signal = controller.current.signal; const key = crypto.randomUUID(); active.current = true; setBusy(true); setError(undefined);
    try {
      const inputs = { ...values.inputs };
      for (const field of graph.inputs) if (field.kind === 3 && inputs[field.key]) inputs[field.key] = (inputs[field.key] as { format: (format: string) => string }).format('YYYY-MM-DD');
      const securityRevision = await freshTenantRevision(tenant, signal);
      signal.throwIfAborted(); setPending(key);
      const receipt = await startWorkflow(tenant, definition.workflowId, version, inputs, key, signal, sourceSessionId,
        hasTasks ? values.coordinator ?? (graph.coordinatorInstanceIds.length === 1 ? graph.coordinatorInstanceIds[0] : undefined) : undefined, securityRevision);
      if (!signal.aborted) accept(receipt);
    } catch (failure) {
      if (!signal.aborted) { setError(failure); if (failure instanceof IdentityError && failure.status < 500) setPending(undefined); }
    } finally { active.current = false; if (!signal.aborted) setBusy(false); }
  }
  async function recover() {
    if (!pending || active.current) return;
    active.current = true; setBusy(true); setError(undefined); const signal = controller.current.signal;
    try { const receipt = await identityRequest<WorkflowStartReceipt | null>(executionApi(tenant, `/start-requests/${encodeURIComponent(pending)}`), { signal }); if (!signal.aborted && receipt) accept(receipt); }
    catch (failure) { if (!signal.aborted) setError(failure); }
    finally { active.current = false; if (!signal.aborted) setBusy(false); }
  }
  return <>{guard.holder}<SessionSandboxFrame open narrow={!screens.md} title={`Start ${definition.name}`} openerRef={openerRef} onClose={guard.close} blocked={busy}>
    <Typography.Paragraph type="secondary">Published version {version}. The process chooses the next step and requires its saved evidence. An accepted request is separate from completion.</Typography.Paragraph>
    {sourceSessionId && <Alert showIcon type="info" title="Linked to this conversation" description="Its current access and retention apply to this process and its worker evidence." />}
    {error !== undefined && <AccessError error={error} />}
    {pending && <Alert showIcon type="warning" title="Check the original request" description="Do not start another copy while its outcome is unconfirmed." action={<Button aria-label="Check request status" loading={busy} disabled={busy} onClick={() => void recover()}>Check request status</Button>} />}
    {publication.loading || controls.loading ? <AccessLoading /> : publication.error || controls.error ? <AccessError error={publication.error ?? controls.error} retry={() => { publication.reload(); controls.reload(); }} /> : !allowed ? <Alert type="warning" showIcon title="This version cannot be started here" description="Check retirement, allowed human origin and your current Workflow start permission." /> : graph && <Form form={form} layout="vertical" disabled={busy || !!pending}
      initialValues={{ inputs: Object.fromEntries(graph.inputs.filter(i => i.kind === 1).map(i => [i.key, false])), coordinator: graph.coordinatorInstanceIds.length === 1 ? graph.coordinatorInstanceIds[0] : undefined }}
      onValuesChange={() => setDirty(true)} onFinish={values => void submit(values)}>
      {hasTasks && <Form.Item label="Coordinator" name="coordinator" rules={[{ required: true, message: 'Choose an eligible coordinator.' }]}><Select placeholder="Choose a published participant" options={graph.coordinatorInstanceIds.map(id => ({ value: id, label: agents.data?.items.find(a => a.instance.instanceId === id)?.instance.persona.name ?? 'Published coordinator' }))} /></Form.Item>}
      {hasTasks && agents.data && <Flex wrap gap="var(--ac-space-compact)">{agentPage > 0 && <Button onClick={() => setAgentPage(p => p - 1)}>Previous agent names</Button>}{(agentPage + 1) * 100 < agents.data.total && <Button onClick={() => setAgentPage(p => p + 1)}>More agent names</Button>}</Flex>}
      {graph.inputs.map(field => <Form.Item key={field.key} name={['inputs', field.key]} label={field.kind === 1 ? undefined : field.label} valuePropName={field.kind === 1 ? 'checked' : 'value'}
        rules={field.kind === 1 ? [] : [{ required: field.required, message: `Enter ${field.label}.`, ...(field.kind === 0 ? { whitespace: true, max: field.maxLength } : {}) }]}>
        {field.kind === 1 ? <Checkbox>{field.label}</Checkbox> : field.kind === 2 ? <InputNumber min={-1e12} max={1e12} style={{ width: '100%' }} /> : field.kind === 3 ? <DatePicker style={{ width: '100%' }} /> : field.kind === 4 ? <Select options={field.choices?.map(choice => ({ value: choice, label: choice }))} /> : <Input maxLength={field.maxLength} />}
      </Form.Item>)}
      {!graph.inputs.length && <Typography.Paragraph>This version has no inputs.</Typography.Paragraph>}
      <Flex wrap justify="end" gap="var(--ac-space-compact)"><Button onClick={guard.close}>Cancel</Button><Button aria-label={`Start version ${version}`} type="primary" htmlType="submit" loading={busy} disabled={!!pending}>Start version {version}</Button></Flex>
    </Form>}
  </SessionSandboxFrame></>;
}
