import { useRef, useState, type RefObject } from 'react';
import { Button, Empty, Flex, Tag, Typography } from 'antd';
import { type Page, type TenantContext } from '../../services/identity';
import { workflowApi, type WorkflowDefinition, type WorkflowStartReceipt } from '../../services/tenantWorkflows';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { SessionSandboxFrame } from '../chat/SessionSandboxFrame';
import { DrawerListFooter } from '../chat/DrawerListFooter';
import { WorkflowLauncher } from './WorkflowLauncher';
import { WorkflowExecutionInspector } from './WorkflowExecutionInspector';

type Available = Pick<WorkflowDefinition, 'workflowId' | 'name' | 'description' | 'publishedVersion'> & { graphHash: string };
export function SessionWorkflowsDrawer(props: { context: TenantContext; session: string; open: boolean; narrow: boolean; openerRef: RefObject<HTMLButtonElement | null>; onClose: () => void }) {
  return props.open ? <SessionWorkflowScope key={`${props.context.tenant.tenantId}:${props.context.principalId}:${props.session}`} {...props} /> : null;
}
function SessionWorkflowScope({ context, session, narrow, openerRef, onClose }: { context: TenantContext; session: string; narrow: boolean; openerRef: RefObject<HTMLButtonElement | null>; onClose: () => void }) {
  const tenant = context.tenant.tenantId; const [page, setPage] = useState(0); const [selected, setSelected] = useState<Available>(); const [execution, setExecution] = useState<string>(); const innerOpener = useRef<HTMLButtonElement | null>(null);
  const state = useIdentityQuery<Page<Available>>(workflowApi(tenant, `/available?page=${page}&pageSize=10`));
  function started(receipt: WorkflowStartReceipt) { setSelected(undefined); setExecution(receipt.executionId); }
  return <><SessionSandboxFrame open narrow={narrow} title="Workflows" blocked={!!selected || !!execution} openerRef={openerRef} onClose={onClose}>
    <Typography.Paragraph type="secondary">Choose a published process for this conversation. Required steps and verification govern its completion; it does not replace Session Goals or the composer.</Typography.Paragraph>
    <Button onClick={state.reload}>Refresh available processes</Button>
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : !state.data?.items.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No published Workflows are currently available for you to start." /> : <ul className="background-work-list" aria-label="Available Workflows">
      {state.data.items.map(item => <li className="background-work-item" key={item.workflowId}><Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>{item.name}</Typography.Text><Typography.Paragraph style={{ margin: 0 }}>{item.description}</Typography.Paragraph><Tag>Published v{item.publishedVersion}</Tag><Button onClick={event => { innerOpener.current = event.currentTarget as HTMLButtonElement; setSelected(item); }}>Inspect inputs and start</Button></Flex></li>)}
    </ul>}
    <DrawerListFooter error={null} loadingMore={state.loading} hasMore={!!state.data && (page + 1) * 10 < state.data.total} count={state.data?.items.length ?? 0} onLoadMore={() => setPage(p => p + 1)} onRetry={state.reload} />
    {page > 0 && <Button onClick={() => setPage(p => p - 1)}>Previous processes</Button>}
  </SessionSandboxFrame>
    {selected && <WorkflowLauncher key={selected.workflowId} context={context} definition={selected} version={selected.publishedVersion} sourceSessionId={session} openerRef={innerOpener} onClose={() => setSelected(undefined)} onStarted={started} />}
    {execution && <WorkflowExecutionInspector key={execution} context={context} id={execution} openerRef={innerOpener} onClose={() => setExecution(undefined)} />}
  </>;
}
