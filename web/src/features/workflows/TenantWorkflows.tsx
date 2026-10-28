import { useEffect, useRef, useState } from 'react';
import { Button, Empty, Flex, Table, Tabs, Tag, Typography } from 'antd';
import { PlusOutlined } from '@ant-design/icons';
import { navigateToAppPath } from '../../app/appRoute';
import { type Page, type TenantContext } from '../../services/identity';
import { executionApi, workflowApi, workflowPath, workflowStatus, type WorkflowControls, type WorkflowDefinition, type WorkflowExecution, type WorkflowStartReceipt } from '../../services/tenantWorkflows';
import { AdminCollectionToolbar, adminCollectionPagination } from '../admin/AdminCollectionToolbar';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { WorkflowEditor } from './WorkflowEditor';
import { WorkflowExecutionInspector } from './WorkflowExecutionInspector';
import { WorkflowLauncher } from './WorkflowLauncher';

function querySelection() { const params = new URLSearchParams(window.location.search); return { workflow: params.get('workflow') ?? undefined, execution: params.get('execution') ?? undefined }; }
export function TenantWorkflows({ context }: { context: TenantContext }) {
  const tenant = context.tenant.tenantId; const [selection, setSelection] = useState(querySelection);
  const [tab, setTab] = useState(selection.execution ? 'executions' : 'definitions'); const [search, setSearch] = useState(''); const [page, setPage] = useState(0);
  const [launch, setLaunch] = useState<{ definition: WorkflowDefinition; version: number }>(); const opener = useRef<HTMLButtonElement | null>(null);
  const catalog = useIdentityQuery<Page<WorkflowDefinition>>(workflowApi(tenant, `/?page=${page}&pageSize=20`));
  const executions = useIdentityQuery<Page<WorkflowExecution>>(tab === 'executions' ? executionApi(tenant, `/?page=${page}&pageSize=20`) : null);
  const controls = useIdentityQuery<WorkflowControls>(workflowApi(tenant, '/editor'));
  const editor = useIdentityQuery<WorkflowControls>(selection.workflow && selection.workflow !== 'new' ? workflowApi(tenant, `/${selection.workflow}/editor`) : null);
  useEffect(() => { const sync = () => setSelection(querySelection()); window.addEventListener('popstate', sync); return () => window.removeEventListener('popstate', sync); }, []);
  function closeEditor() { catalog.reload(); navigateToAppPath(workflowPath(tenant)); }
  function openRun(definition: WorkflowDefinition, version: number) { opener.current = document.activeElement instanceof HTMLButtonElement ? document.activeElement : null; setLaunch({ definition, version }); }
  function started(receipt: WorkflowStartReceipt) { setLaunch(undefined); executions.reload(); navigateToAppPath(workflowPath(tenant, selection.workflow && selection.workflow !== 'new' ? selection.workflow : undefined, receipt.executionId)); }
  const definition = selection.workflow && selection.workflow !== 'new' ? editor.data?.definition : null;
  return <Flex vertical gap="var(--ac-space-section)">
    {selection.workflow ? selection.workflow !== 'new' && editor.loading ? <AccessLoading /> : selection.workflow !== 'new' && editor.error ? <AccessError error={editor.error} retry={editor.reload} /> : (selection.workflow === 'new' || definition) && <WorkflowEditor key={`${tenant}:${context.principalId}:${selection.workflow}`} context={context} existing={definition ?? null} onClose={closeEditor} onRun={openRun} onCreated={created => navigateToAppPath(workflowPath(tenant, created.workflowId), true)} /> : <>
      <Typography.Paragraph type="secondary" className="workflow-reading">Publish a saved process, then inspect its required evidence. Agent Tasks, Goal verification and protected-operation approvals keep their own authority.</Typography.Paragraph>
      <Tabs activeKey={tab} onChange={key => { setTab(key); setPage(0); setSearch(''); }} items={[{ key: 'definitions', label: 'Definitions' }, { key: 'executions', label: 'Executions' }]} />
      <AdminCollectionToolbar showScrollHint={false} label={tab === 'definitions' ? 'Workflow names on this page' : 'execution statuses on this page'} value={search} onChange={setSearch} filters={<Flex wrap gap="var(--ac-space-compact)"><Button onClick={() => { catalog.reload(); executions.reload(); controls.reload(); }}>Refresh</Button>{tab === 'definitions' && controls.data?.canCreate && <Button aria-label="New Workflow" type="primary" icon={<PlusOutlined aria-hidden />} onClick={() => navigateToAppPath(workflowPath(tenant, 'new'))}>New Workflow</Button>}</Flex>} />
      {tab === 'definitions' ? catalog.loading ? <AccessLoading /> : catalog.error ? <AccessError error={catalog.error} retry={catalog.reload} /> : <Table<WorkflowDefinition> className="admin-collection-table" rowKey="workflowId" dataSource={catalog.data?.items.filter(item => `${item.name} ${item.description}`.toLowerCase().includes(search.toLowerCase()))}
        pagination={{ ...adminCollectionPagination, current: page + 1, total: catalog.data?.total, pageSize: 20, showSizeChanger: false, onChange: next => setPage(next - 1) }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching names on this page.' : 'No readable Workflows saved.'} /> }} columns={[
          { title: 'Workflow', render: (_, row) => <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>{row.name}</Typography.Text><Typography.Text type="secondary">{row.description || 'No description'}</Typography.Text><Flex wrap gap="var(--ac-space-tight)"><Tag>{row.retired ? 'Retired' : row.publishedVersion ? 'Published' : 'Draft only'}</Tag>{row.publishedVersion > 0 && <Tag>v{row.publishedVersion}</Tag>}</Flex></Flex> },
          { title: 'Actions', width: 136, render: (_, row) => <Button aria-label={`Inspect ${row.name}`} onClick={() => navigateToAppPath(workflowPath(tenant, row.workflowId))}>Inspect process</Button> }
        ]} /> : executions.loading ? <AccessLoading /> : executions.error ? <AccessError error={executions.error} retry={executions.reload} /> : <Table<WorkflowExecution> className="admin-collection-table" rowKey="executionId" dataSource={executions.data?.items.filter(item => workflowStatus(item.status).toLowerCase().includes(search.toLowerCase()))}
          pagination={{ ...adminCollectionPagination, current: page + 1, total: executions.data?.total, pageSize: 20, showSizeChanger: false, onChange: next => setPage(next - 1) }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No readable Workflow executions." /> }} columns={[
            { title: 'Execution', render: (_, row) => <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>{row.workflowName}</Typography.Text><Tag>{workflowStatus(row.status)}</Tag><Typography.Text type="secondary">Version {row.version} · {new Date(row.createdAt).toLocaleString()}</Typography.Text><Typography.Text>{row.provenCount}/{row.requiredCount} applicable required steps proven</Typography.Text></Flex> },
            { title: 'Inspect', width: 120, render: (_, row) => <Button onClick={event => { opener.current = event.currentTarget as HTMLButtonElement; navigateToAppPath(workflowPath(tenant, undefined, row.executionId)); }}>View execution</Button> }
          ]} />}
    </>}
    {launch && <WorkflowLauncher key={`${tenant}:${context.principalId}:${launch.definition.workflowId}:${launch.version}`} context={context} definition={launch.definition} version={launch.version} openerRef={opener} onClose={() => setLaunch(undefined)} onStarted={started} />}
    {selection.execution && <WorkflowExecutionInspector key={`${tenant}:${context.principalId}:${selection.execution}`} context={context} id={selection.execution} openerRef={opener} onClose={() => navigateToAppPath(workflowPath(tenant, selection.workflow))} />}
  </Flex>;
}
