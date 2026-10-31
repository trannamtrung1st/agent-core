import { useState } from 'react';
import { App, Alert, Button, Collapse, Empty, Flex, Form, Grid, Input, InputNumber, Tabs, Tag, Typography } from 'antd';
import { ArrowLeftOutlined, PlusOutlined } from '@ant-design/icons';
import { confirmAction } from '../../app/confirmAction';
import { identityRequest, tenantApi, type Page, type TenantContext } from '../../services/identity';
import { freshTenantRevision } from '../../services/tenantEvaluations';
import { initialWorkflow, nodeKinds, readWorkflowGraph, workflowApi, workflowPath, connectWorkflow, type WorkflowControls, type WorkflowDefinition, type WorkflowDiagnostic, type WorkflowGraph, type WorkflowNode, type WorkflowVersion } from '../../services/tenantWorkflows';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { useAccessDraftGuard } from '../access/useAccessDraftGuard';
import { useTenantCommand } from '../access/useTenantCommand';
import { WorkflowDraftComparison } from './WorkflowDraftComparison';
import { WorkflowDiagram } from './WorkflowDiagram';
import { WorkflowOutline } from './WorkflowOutline';
import { WorkflowGraphSettings } from './WorkflowGraphSettings';
import { WorkflowNodeProperties, emptyGuid, type WorkflowAgent } from './WorkflowNodeProperties';
import { useWorkflowCatalogCommand } from './useWorkflowCatalogCommand';

export function WorkflowEditor({ context, existing, onClose, onRun, onCreated }: { context: TenantContext; existing: WorkflowDefinition | null; onClose: () => void; onRun: (definition: WorkflowDefinition, version: number) => void; onCreated?: (definition: WorkflowDefinition) => void }) {
  const tenant = context.tenant.tenantId; const screens = Grid.useBreakpoint(); const { modal, message } = App.useApp();
  const [workflow] = useState(existing?.workflowId ?? crypto.randomUUID()); const [saved, setSaved] = useState(existing);
  const [name, setName] = useState(existing?.name ?? ''); const [description, setDescription] = useState(existing?.description ?? '');
  const [draft, setDraft] = useState<WorkflowGraph>(() => existing ? readWorkflowGraph(existing.draftJson) : initialWorkflow(context.principalId));
  const [dirty, setDirty] = useState(false); const [selected, setSelected] = useState('start'); const [tab, setTab] = useState<string>(); const [palette, setPalette] = useState('');
  const [version, setVersion] = useState(0); const [versionInput, setVersionInput] = useState(existing?.publishedVersion ?? 1);
  const [diagnostics, setDiagnostics] = useState<WorkflowDiagnostic[] | null>(null); const [validatedRevision, setValidatedRevision] = useState<number>();
  const [paletteExpanded, setPaletteExpanded] = useState(false);
  const [agentPage, setAgentPage] = useState(0); const [comparison, setComparison] = useState<WorkflowDefinition>();
  const controls = useIdentityQuery<WorkflowControls>(workflowApi(tenant, saved ? `/${workflow}/editor` : '/editor'));
  const publication = useIdentityQuery<WorkflowVersion>(version > 0 ? workflowApi(tenant, `/${workflow}/versions/${version}`) : null);
  const agents = useIdentityQuery<Page<WorkflowAgent>>(context.permissions.includes('agents.read') ? tenantApi(tenant, `/agents?page=${agentPage}&pageSize=100`) : null);
  const graph = version > 0 && publication.data ? readWorkflowGraph(publication.data.graph.canonicalJson) : draft;
  const editAllowed = version === 0 && (saved ? controls.data?.canUpdate : controls.data?.canCreate) === true;
  const command = useWorkflowCatalogCommand(tenant, receipt => {
    setSaved(receipt.definition); setDraft(readWorkflowGraph(receipt.definition.draftJson)); setDirty(false); setDiagnostics(null); setValidatedRevision(undefined); controls.reload();
    if (receipt.version) { setVersionInput(receipt.version.version); void message.success(`Immutable version ${receipt.version.version} published.`); }
    else void message.success('Draft saved.');
    if (!saved && onCreated) { guard.release(); onCreated(receipt.definition); }
  });
  const operation = useTenantCommand(); const guard = useAccessDraftGuard(dirty || command.pending, command.busy || operation.busy, onClose, saved ? workflowPath(tenant, workflow) : undefined);
  const disabled = !editAllowed || command.busy || command.pending || command.stale || operation.busy;
  function change(value: WorkflowGraph) { if (disabled) return; setDraft(value); setDirty(true); setDiagnostics(null); setValidatedRevision(undefined); }
  async function save(retired = saved?.retired ?? false) {
    if (!name.trim() || name.length > 80) { void message.error('Enter a Workflow name of 1–80 characters.'); return; }
    const key = crypto.randomUUID();
    await operation.run(async signal => {
      const expectedSecurityRevision = await freshTenantRevision(tenant, signal);
      await command.submit({ workflow, key, suffix: `/${workflow}/draft`, method: 'PUT', expectedRevision: saved?.revision ?? 0,
        body: { name: name.trim(), description, graph: draft, retired, expectedRevision: saved?.revision ?? 0, expectedSecurityRevision, requestKey: key } });
      return true;
    }, () => {});
  }
  function validate() {
    if (!saved || dirty) return;
    void operation.run(async signal => identityRequest<{ diagnostics: WorkflowDiagnostic[] }>(workflowApi(tenant, `/${workflow}/validate`), { method: 'POST', signal }), result => {
      setDiagnostics(result.diagnostics); setValidatedRevision(saved.revision); if (!result.diagnostics.length) void message.success('This saved revision is valid.');
    });
  }
  function publish() {
    if (!saved) return;
    const publishKey = crypto.randomUUID();
    confirmAction(modal, { title: `Publish version ${saved.publishedVersion + 1}?`, content: 'This pins the saved process, participants, inputs and evidence bindings. Running executions keep their existing version. Publication grants no agent permissions.', okText: 'Publish immutable version',
      onOk: () => operation.run(async signal => command.submit({ workflow, key: publishKey, suffix: `/${workflow}/publish`, method: 'POST', expectedRevision: saved.revision,
        body: { expectedRevision: saved.revision, expectedSecurityRevision: await freshTenantRevision(tenant, signal), requestKey: publishKey } }), () => {}) });
  }
  function add(kind: number) {
    const id = `step-${crypto.randomUUID().slice(0, 8)}`;
    const node: WorkflowNode = { id, name: nodeKinds[kind], kind, requirement: kind < 4 ? 1 : 0,
      ...(kind === 1 ? { task: { targetInstanceId: emptyGuid, objective: '', spaceId: emptyGuid } } : kind === 2 ? { verification: { kind: 0 } }
        : kind === 3 ? { decision: { reviewerPrincipalId: context.principalId, prompt: '', timeoutHours: 72 } } : kind === 4 ? { branch: { sourceNodeId: '', fact: 0 } } : {}) };
    change({ ...draft, nodes: [...draft.nodes, node] }); setSelected(id);
  }
  function jump(id: string) { setSelected(id); setTab('outline'); requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-workflow-node="${CSS.escape(id)}"]`)?.focus()); }
  const active = graph.nodes.find(n => n.id === selected) ?? graph.nodes[0];
  return <Flex vertical gap="var(--ac-space-section)">{guard.holder}
    <Flex className="workflow-action-header" align="center" justify="space-between" gap="var(--ac-space-default)" wrap>
      <Flex align="center" gap="var(--ac-space-compact)" wrap><Button icon={<ArrowLeftOutlined />} disabled={command.busy || operation.busy} onClick={guard.close}>Workflows</Button><Tag>{version ? `Published v${version}` : saved ? `Draft · revision ${saved.revision}` : 'New draft'}</Tag>{dirty && <Tag color="warning">Unsaved changes</Tag>}{saved?.retired && <Tag>Retired</Tag>}</Flex>
      <Flex wrap gap="var(--ac-space-compact)">
        {version === 0 && editAllowed && <Button aria-label="Save draft" type="primary" disabled={disabled} loading={command.busy} onClick={() => void save()}>Save draft</Button>}
        {saved && version === 0 && <Button disabled={dirty || command.busy || operation.busy || command.pending} onClick={validate}>Validate saved draft</Button>}
        {version === 0 && controls.data?.canPublish && <Button disabled={dirty || command.busy || operation.busy || command.pending || diagnostics === null || !!diagnostics.length || validatedRevision !== saved?.revision} onClick={publish}>Publish</Button>}
        {saved && controls.data?.canStart && <Button disabled={command.pending || command.busy || operation.busy} onClick={() => onRun(saved, version || saved.publishedVersion)}>Run {version ? `v${version}` : `published v${saved.publishedVersion}`}</Button>}
      </Flex>
    </Flex>
    {command.error !== undefined && <AccessError error={command.error} />}{operation.error !== undefined && <AccessError error={operation.error} />}
    {command.pending && <Alert type="warning" showIcon title="Saved outcome needs confirmation" description="Your draft and original request are retained. Check its receipt first. If no receipt is saved, explicitly resume only this exact command and request key." action={<Flex wrap gap="var(--ac-space-compact)"><Button aria-label="Check saved request" loading={command.busy} disabled={command.busy} onClick={() => void command.recover()}>Check saved request</Button>{command.canResume && <Button disabled={command.busy} onClick={() => void command.resumeOriginal()}>Resume original command</Button>}</Flex>} />}
    {command.stale && <Alert type="warning" showIcon title="The saved revision changed" description="Your local draft is retained. Inspect the latest saved process before using its revision as your next base." action={<Button onClick={() => void operation.run(signal => identityRequest<WorkflowDefinition>(workflowApi(tenant, `/${workflow}`), { signal }), setComparison)}>Compare saved draft</Button>} />}
    {comparison && <Collapse items={[{ key: 'compare', label: `Latest saved draft: ${comparison.name} · revision ${comparison.revision}`, children: <Flex vertical gap="var(--ac-space-default)"><WorkflowDraftComparison name={name} description={description} graph={draft} saved={comparison} /><Typography.Paragraph>Using this revision retains your local edits. Saving then deliberately replaces the latest draft; published versions remain immutable.</Typography.Paragraph><Button onClick={() => { setSaved(comparison); setComparison(undefined); command.reviewedLatest(); controls.reload(); }}>Use this revision as base and keep local edits</Button></Flex> }]} />}
    {controls.loading ? <AccessLoading /> : controls.error ? <AccessError error={controls.error} retry={controls.reload} /> : <>
      {!editAllowed && version === 0 && <Alert showIcon type="info" title="Read-only draft" description="Editing and publishing require their own current permissions." />}
      <Form layout="vertical" component="div" disabled={disabled} className="workflow-reading"><Form.Item label="Workflow name" htmlFor="workflow-name"><Input id="workflow-name" value={name} maxLength={80} onChange={e => { setName(e.target.value); setDirty(true); }} /></Form.Item><Form.Item label="Description" htmlFor="workflow-description"><Input.TextArea id="workflow-description" value={description} rows={2} maxLength={1000} onChange={e => { setDescription(e.target.value); setDirty(true); }} /></Form.Item></Form>
      {saved && saved.publishedVersion > 0 && <Flex wrap gap="var(--ac-space-compact)" align="center"><Button type={version === 0 ? 'primary' : 'default'} onClick={() => setVersion(0)}>Draft</Button><InputNumber aria-label="Published version to inspect" min={1} max={saved.publishedVersion} value={versionInput} onChange={v => setVersionInput(v ?? 1)} /><Button disabled={versionInput < 1 || versionInput > saved.publishedVersion} onClick={() => setVersion(versionInput)}>Inspect published version</Button><Typography.Text type="secondary">Latest: v{saved.publishedVersion}</Typography.Text></Flex>}
      {diagnostics && <Alert showIcon type={diagnostics.length ? 'warning' : 'success'} title={diagnostics.length ? `${diagnostics.length} validation ${diagnostics.length === 1 ? 'issue' : 'issues'}` : 'Saved revision validated'} description={diagnostics.length ? <ul>{diagnostics.map((d, index) => <li key={index}>{d.nodeId ? <Button type="link" onClick={() => jump(d.nodeId!)}>{d.message}</Button> : <span>{d.message}</span>}</li>)}</ul> : 'Publish creates an immutable version after repeating current authority and binding checks.'} />}
      {version > 0 && publication.loading ? <AccessLoading /> : version > 0 && publication.error ? <AccessError error={publication.error} retry={publication.reload} /> : <div className="workflow-designer">
        <aside className="workflow-palette" aria-label="Step palette"><Collapse activeKey={paletteExpanded ? ['palette'] : []} onChange={keys => setPaletteExpanded(keys.length > 0)} items={[{ key: 'palette', label: 'Add a step', children: <Flex wrap gap="var(--ac-space-compact)"><Input style={{ flex: '1 1 240px' }} aria-label="Search step types" placeholder="Search step types" value={palette} onChange={e => setPalette(e.target.value)} />{nodeKinds.map((label, kind) => kind > 0 && label.toLowerCase().includes(palette.toLowerCase()) && <Button key={kind} icon={<PlusOutlined />} disabled={disabled || graph.nodes.length >= 40} onClick={() => add(kind)}>{label}</Button>)}<Typography.Text type="secondary">{graph.nodes.length}/40 steps. Connect every outcome in Properties.</Typography.Text></Flex> }]} /></aside>
        <section aria-label="Process structure"><Tabs activeKey={tab ?? (screens.lg ? 'diagram' : 'outline')} onChange={setTab} items={[{ key: 'diagram', label: 'Diagram', children: <><WorkflowDiagram graph={graph} selected={active?.id} onSelect={setSelected} onConnect={disabled ? undefined : (from, outcome, to) => change(connectWorkflow(draft, from, outcome, to))} /><Typography.Paragraph type="secondary">Drag to rearrange this view. Saved process behavior comes from its connections. Every editing control is also available in the Outline and Properties.</Typography.Paragraph></> }, { key: 'outline', label: 'Outline', children: <WorkflowOutline graph={graph} selected={active?.id} onSelect={setSelected} /> }]} /></section>
        <aside className="workflow-properties"><Tabs items={[{ key: 'step', label: 'Properties', children: active ? <WorkflowNodeProperties key={active.id} context={context} graph={graph} node={active} agents={agents.data?.items ?? []} disabled={disabled} onChange={change} onRemove={() => { change({ ...draft, nodes: draft.nodes.filter(n => n.id !== active.id), edges: draft.edges.filter(e => e.from !== active.id && e.to !== active.id) }); setSelected('start'); }} /> : <Empty description="Choose a step." /> }, { key: 'process', label: 'Process', children: <WorkflowGraphSettings tenant={tenant} graph={graph} agents={agents.data?.items ?? []} disabled={disabled} onChange={change} /> }]} />
          {agents.error && <AccessError error={agents.error} retry={agents.reload} />}{agents.data && <Flex wrap gap="var(--ac-space-compact)">{agentPage > 0 && <Button onClick={() => setAgentPage(p => p - 1)}>Previous agents</Button>}{(agentPage + 1) * 100 < agents.data.total && <Button onClick={() => setAgentPage(p => p + 1)}>More agents</Button>}</Flex>}
        </aside>
      </div>}
      {saved && controls.data?.canUpdate && <Button danger={!saved.retired} disabled={dirty || disabled} onClick={() => confirmAction(modal, { title: saved.retired ? 'Restore future starts?' : 'Retire this Workflow?', content: 'Retirement prevents new starts. Published history and running pinned executions remain available under current access.', okText: saved.retired ? 'Restore' : 'Retire', onOk: () => save(!saved.retired) })}>{saved.retired ? 'Restore Workflow' : 'Retire Workflow'}</Button>}
    </>}
  </Flex>;
}
