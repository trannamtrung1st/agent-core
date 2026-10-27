import { useEvaluationLaunch } from './useEvaluationLaunch';
import { useEvaluationCatalog } from './useEvaluationCatalog';
import { useState } from 'react';
import { Alert, App, Button, Checkbox, Descriptions, Drawer, Empty, Flex, Form, Input, InputNumber, Select, Table, Tabs, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { AdminCollectionToolbar, adminCollectionPagination } from '../admin/AdminCollectionToolbar';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { IdentityCompletionError, identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import { evaluationCatalogWrite, evaluatorLabels, freshTenantRevision, hookLabels, outcomeLabel, predicates, statusLabel, type EvaluationAttempt, type EvaluationPage, type EvaluationSuite, type ScriptVersion } from '../../services/tenantEvaluations';
import { EvaluationSuiteEditor } from './EvaluationSuiteEditor';
import { EvaluationAttemptInspector } from './EvaluationAttemptInspector';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
import { useAccessDraftGuard } from './useAccessDraftGuard';

export function TenantEvaluations({ context }: { context: TenantContext }) {
  const params = new URLSearchParams(window.location.search); const [tab, setTab] = useState(params.has('subjectId') ? 'results' : 'suites');
  return <Flex vertical gap="var(--ac-space-section)">
    <Typography.Paragraph type="secondary">Versioned checks and attributable evidence for Definitions and Runs. Passing an evaluation never grants operation permission.</Typography.Paragraph>
    <Tabs activeKey={tab} onChange={setTab} items={[
      { key: 'suites', label: 'Suites', destroyOnHidden: true, children: <EvaluationCatalog key={`suites:${context.tenant.tenantId}`} context={context} /> },
      { key: 'scripts', label: 'Scripts', destroyOnHidden: true, children: <EvaluationCatalog key={`scripts:${context.tenant.tenantId}`} context={context} scripts /> },
      { key: 'results', label: 'Results / Manual review', destroyOnHidden: false, children: <EvaluationResults key={`${context.tenant.tenantId}:${context.principalId}`} context={context} /> }
    ]} />
  </Flex>;
}
function EvaluationCatalog({ context, scripts = false }: { context: TenantContext; scripts?: boolean }) {
  const tenant = context.tenant.tenantId; const family = scripts ? 'scripts' : 'evaluation-suites';
  const [page, setPage] = useState(0); const [size, setSize] = useState(20); const [search, setSearch] = useState('');
  const [editing, setEditing] = useState<EvaluationSuite | ScriptVersion | null | undefined>(); const [selected, setSelected] = useState<EvaluationSuite | ScriptVersion>();
  const state = useIdentityQuery<EvaluationPage<EvaluationSuite | ScriptVersion>>(tenantApi(tenant, `/${family}?page=${page}&pageSize=${size}`));
  const canManage = context.permissions.includes(scripts ? 'scripts.manage' : 'evaluations.manage');
  const command = useTenantCommand(); const { modal, message } = App.useApp();
  const id = (row: EvaluationSuite | ScriptVersion) => 'scriptId' in row ? row.scriptId : row.suiteId;
  const retire = (row: EvaluationSuite | ScriptVersion) => confirmAction(modal, { title: `${row.archived ? 'Restore' : 'Archive'} ${row.name}?`, content: 'This saves a new catalog version. Immutable references and past evidence are retained. Archival prevents new evaluations using this catalog item.', okText: row.archived ? 'Restore item' : 'Archive item', onOk: () => command.run(async signal => identityRequest<EvaluationSuite | ScriptVersion>(tenantApi(tenant, `/${family}/${id(row)}/versions`), { method: 'POST', signal, body: { ...row, archived: !row.archived, expectedVersion: row.version, expectedRevision: await freshTenantRevision(tenant, signal) } }), result => { if (id(result) !== id(row) || result.version !== row.version + 1) throw new IdentityCompletionError(); void message.success('Catalog version stored.'); state.reload(); }) });
  return <Flex vertical gap="var(--ac-space-section)">
    {scripts && <Alert type="info" showIcon title="Explicit execution authority" description="Assertions inspect permitted snapshots. Sandbox Scripts run against isolated terminal Run evidence after exact Review in Approvals, with current caller and agent execution authority. Saving never executes a process." />}
    <AdminCollectionToolbar label={`${scripts ? 'Scripts' : 'Suites'} on this page`} value={search} onChange={setSearch} showScrollHint={false} filters={<Flex wrap gap="var(--ac-space-compact)"><Button onClick={state.reload}>Refresh</Button>{canManage && <Button type="primary" onClick={() => setEditing(null)}>New {scripts ? 'Script' : 'Suite'}</Button>}</Flex>} />
    {command.error !== undefined && <AccessError error={command.error} retry={state.reload} />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Table<EvaluationSuite | ScriptVersion> className="admin-collection-table" rowKey={id} dataSource={state.data?.items.filter(r => r.name.toLocaleLowerCase().includes(search.toLocaleLowerCase()))}
      pagination={{ ...adminCollectionPagination, current: page + 1, total: state.data?.total, pageSize: size, onChange: (next, nextSize) => { setPage(nextSize === size ? next - 1 : 0); setSize(nextSize); } }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No names match on this page.' : `No readable ${scripts ? 'Scripts' : 'Suites'} saved.`} /> }} columns={[
        { title: scripts ? 'Script' : 'Suite', render: (_, row) => <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>{row.name}</Typography.Text><Typography.Text type="secondary">Version {row.version} · {'checks' in row ? `${row.checks.length} checks` : row.kind === 0 ? 'Deterministic assertion' : 'Sandbox body'}</Typography.Text><Tag>{row.archived ? 'Archived' : 'Available'}</Tag></Flex> },
        { title: 'Actions', render: (_, row) => <Flex wrap gap="var(--ac-space-compact)"><Button aria-label={`Inspect ${row.name}`} onClick={() => setSelected(row)}>Inspect</Button>{canManage && <><Button disabled={command.busy} aria-label={`New version of ${row.name}`} onClick={() => setEditing(row)}>New version</Button><Button disabled={command.busy} onClick={() => retire(row)}>{row.archived ? 'Restore' : 'Archive'}</Button></>}</Flex> }
      ]} />}
    {editing !== undefined && (scripts ? <ScriptEditor tenant={tenant} existing={editing as ScriptVersion | null} onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); state.reload(); }} /> : <EvaluationSuiteEditor tenant={tenant} existing={editing as EvaluationSuite | null} onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); state.reload(); }} />)}
    {selected && <CatalogInspector tenant={tenant} source={selected} onClose={() => setSelected(undefined)} />}
  </Flex>;
}
function CatalogInspector({ tenant, source, onClose }: { tenant: string; source: EvaluationSuite | ScriptVersion; onClose: () => void }) {
  const [version, setVersion] = useState(source.version); const [requested, setRequested] = useState(source.version); const scripts = 'scriptId' in source;
  const id = scripts ? source.scriptId : source.suiteId;
  const state = useIdentityQuery<EvaluationSuite | ScriptVersion>(tenantApi(tenant, `/${scripts ? 'scripts' : 'evaluation-suites'}/${id}?version=${requested}`));
  return <Drawer open title={source.name} size={720} className="access-editor-drawer" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={onClose}><Flex vertical gap="var(--ac-space-section)">
    <Flex wrap gap="var(--ac-space-compact)" align="center"><Typography.Text>Immutable version</Typography.Text><InputNumber aria-label="Immutable version" min={1} max={source.version} precision={0} value={version} onChange={v => setVersion(v ?? source.version)} /><Button onClick={() => setRequested(version)}>Inspect version</Button></Flex>
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : state.data && <>
      <Descriptions column={1} size="small" items={[{ key: 'version', label: 'Version', children: state.data.version }, { key: 'id', label: 'ID', children: <Typography.Text copyable>{id}</Typography.Text> }, { key: 'hash', label: 'Content hash', children: <Typography.Text copyable style={{ overflowWrap: 'anywhere' }}>{state.data.contentHash}</Typography.Text> }]} />
      {'checks' in state.data ? state.data.checks.map(check => <section key={check.checkId}><Typography.Text strong>{check.title}</Typography.Text><Typography.Paragraph type="secondary">{evaluatorLabels[check.kind]} · {check.required ? 'Required' : 'Advisory'}</Typography.Paragraph><Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{check.rubric ?? check.prompt ?? predicates[check.predicate]}</Typography.Paragraph></section>) : <>
        <Typography.Text>{state.data.hooks.map(h => hookLabels[h]).join(' · ')}</Typography.Text>
        <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{state.data.body ?? `${predicates[state.data.predicate]}: ${state.data.expected ?? ''}`}</Typography.Paragraph>
      </>}
    </>}
  </Flex></Drawer>;
}
function ScriptEditor({ tenant, existing, onClose, onSaved }: { tenant: string; existing: ScriptVersion | null; onClose: () => void; onSaved: () => void }) {
  const [form] = Form.useForm(); const [dirty, setDirty] = useState(false); const command = useTenantCommand(); const { message } = App.useApp(); const guard = useAccessDraftGuard(dirty, command.busy, onClose); const kind = Form.useWatch('kind', form) ?? existing?.kind ?? 0;
  return <>{guard.holder}<Drawer open title={existing ? 'New Script version' : 'New Script'} size={640} className="access-editor-drawer" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy}
    footer={<Flex justify="end" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={guard.close}>Cancel</Button><Button type="primary" loading={command.busy} disabled={command.requiresReload} form="script-version-form" htmlType="submit">Save version</Button></Flex>}>
    <Typography.Paragraph type="secondary">Save immutable content for use by Suite checks. Every execution repeats current subject, Script and operation authority.</Typography.Paragraph>
    {command.error !== undefined && <AccessError error={command.error} />}
    <Form form={form} id="script-version-form" layout="vertical" disabled={command.busy || command.requiresReload} initialValues={existing ?? { name: '', kind: 0, predicate: 4, expected: 'email.send', hooks: [3] }} onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)}
      onFinish={value => void command.run(async signal => evaluationCatalogWrite<ScriptVersion>(tenantApi(tenant, existing ? `/scripts/${existing.scriptId}/versions` : '/scripts'), { ...value, body: kind === 1 ? value.body : null, sandboxProfileId: kind === 1 ? value.sandboxProfileId : null, sandboxTimeoutSeconds: kind === 1 ? value.sandboxTimeoutSeconds : 10, sandboxInput: kind === 1 ? value.sandboxInput : 0, expectedVersion: existing?.version ?? 0, expectedRevision: await freshTenantRevision(tenant, signal) }, signal), result => { if (result.tenantId !== tenant || result.version !== (existing?.version ?? 0) + 1 || !result.contentHash) throw new IdentityCompletionError(); guard.release(); void message.success('Immutable Script version saved.'); onSaved(); })}>
      <Form.Item name="name" label="Script name" rules={[{ required: true, whitespace: true }, { max: 128 }]}><Input maxLength={128} autoFocus /></Form.Item>
      <Form.Item name="kind" label="Execution kind"><Select onChange={value => { if (value === 1) form.setFieldValue('hooks', (form.getFieldValue('hooks') as number[]).filter(h => h !== 0)); }} options={[{ value: 0, label: 'Deterministic assertion' }, { value: 1, label: 'Sandbox script' }]} /></Form.Item>
      {kind === 1 ? <><SandboxScriptFields tenant={tenant} /><Form.Item name="body" label="Sandbox script body" rules={[{ required: true, whitespace: true }, { max: 16000 }]}><Input.TextArea rows={8} maxLength={16000} spellCheck={false} /></Form.Item></> : <>
        <Form.Item name="predicate" label="Assertion"><Select options={predicates.map((label, value) => ({ label, value }))} /></Form.Item><Form.Item name="expected" label="Capability name or resource path" rules={[{ max: 256 }]}><Input maxLength={256} /></Form.Item>
      </>}
      <Form.Item name="hooks" label="Allowed lifecycle hooks" rules={[{ required: true, type: 'array', min: 1 }]}><Checkbox.Group options={hookLabels.slice(0, 4).map((label, value) => ({ label, value, disabled: kind === 1 && value === 0 }))} /></Form.Item>
    </Form>
  </Drawer></>;
}
function EvaluationResults({ context }: { context: TenantContext }) {
  const params = new URLSearchParams(window.location.search); const [kind, setKind] = useState(Number(params.get('subjectKind') ?? 1)); const [subject, setSubject] = useState(params.get('subjectId') ?? ''); const [source, setSource] = useState(params.get('subjectId') ?? '');
  const [attempt, setAttempt] = useState<string | undefined>(params.get('attemptId') ?? undefined); const [page, setPage] = useState(0); const tenant = context.tenant.tenantId;
  const { modal } = App.useApp(); const [suiteId, setSuiteId] = useState<string>();
  const state = useIdentityQuery<EvaluationPage<EvaluationAttempt>>(source ? tenantApi(tenant, `/evaluation-attempts?subjectKind=${kind}&subjectId=${encodeURIComponent(source)}&page=${page}&pageSize=20`) : null);
  const command = useEvaluationLaunch(tenant, result => { setAttempt(result.attemptId); state.reload(); }); const recovering = !!command.pending;
  const recoveryGuard = useAccessDraftGuard(recovering, command.busy, () => {});
  const subjectState = useIdentityQuery<{ subjectId: string; revision: number; label: string; canEvaluate: boolean }>(source ? tenantApi(tenant, `/evaluation-subjects/${kind}/${source}`) : null);
  const suites = useEvaluationCatalog<EvaluationSuite>(tenant, 'evaluation-suites');
  const selectedSuite = suites.data?.items.find(s => s.suiteId === suiteId);
  const invoke = () => {
    if (recovering) { void command.recover(); return; }
    if (!subjectState.data || !selectedSuite) return;
    confirmAction(modal, { title: 'Evaluate this saved subject?', content: 'Use the selected immutable Suite version and current source access. Configured model judges may incur provider charges. Uncertain submissions are recovered by reading their exact status, without replay.', okText: 'Run evaluation', onOk: () => command.submit({ subjectKind: kind, subjectId: source, subjectRevision: subjectState.data!.revision, suiteId: selectedSuite.suiteId, suiteVersion: selectedSuite.version, dedupKey: crypto.randomUUID(), hook: 3 }) });
  };
  return <Flex vertical gap="var(--ac-space-section)">{recoveryGuard.holder}
    <Typography.Paragraph type="secondary">Open evidence from a Definition or Run, or inspect an exact readable subject below. Private sources require independent current access.</Typography.Paragraph>
    <Flex wrap gap="var(--ac-space-compact)"><Select aria-label="Subject kind" disabled={command.busy || recovering} value={kind} onChange={value => { setKind(value); setSource(''); setPage(0); }} options={[{ value: 0, label: 'Definition draft' }, { value: 1, label: 'Agent Run' }]} /><Input aria-label="Exact subject ID" disabled={command.busy || recovering} placeholder="Exact subject ID" value={subject} onChange={event => setSubject(event.target.value)} style={{ flex: '1 1 240px' }} /><Button disabled={command.busy || recovering || !/^[a-f0-9-]{36}$/i.test(subject)} onClick={() => { setPage(0); setSource(subject); state.reload(); }}>Inspect results</Button></Flex>
    {source && <Flex vertical gap="var(--ac-space-compact)">
      {subjectState.error ? <AccessError error={subjectState.error} retry={subjectState.reload} /> : subjectState.data && <Typography.Text strong>{subjectState.data.label} · revision {subjectState.data.revision}</Typography.Text>}
      {suites.error && <AccessError error={suites.error} retry={suites.reload} />}
      {context.permissions.includes('evaluations.run') && <Flex wrap gap="var(--ac-space-compact)"><Select aria-label="Evaluation Suite" placeholder="Choose an immutable Suite" disabled={command.busy || recovering} style={{ flex: '1 1 240px' }} value={suiteId} onChange={id => { setSuiteId(id); }} options={suites.data?.items.filter(s => !s.archived).map(s => ({ value: s.suiteId, label: `${s.name} · v${s.version}` }))} /><Button disabled={command.busy || !recovering && (!selectedSuite || !subjectState.data?.canEvaluate)} loading={command.busy} onClick={invoke}>{recovering ? 'Recover evaluation status' : 'Run evaluation'}</Button></Flex>}
      {recovering && <Alert type="info" showIcon title="Submission outcome is uncertain" description="Recover the exact stored status with the button above. Recovery only reads evidence and never runs the evaluator again. Keep this page open until recovery completes." />}
      {command.error !== undefined && <AccessError error={command.error} />}
    </Flex>}
    {!source ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Choose a subject to inspect its authorized evidence." /> : state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Table<EvaluationAttempt> className="admin-collection-table" rowKey="attemptId" dataSource={state.data?.items} pagination={{ ...adminCollectionPagination, current: page + 1, pageSize: 20, showSizeChanger: false, total: state.data?.total, onChange: value => setPage(value - 1) }} columns={[
      { title: 'Assessment', render: (_, row) => <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>{outcomeLabel(row.outcome)}</Typography.Text><Typography.Text type="secondary">{statusLabel(row.status)} · Suite v{row.suiteVersion} · Subject revision {row.subjectRevision}</Typography.Text><Typography.Text type="secondary">{new Date(row.updatedAt).toLocaleString()}</Typography.Text></Flex> },
      { title: 'Action', render: (_, row) => <Button onClick={() => setAttempt(row.attemptId)}>Inspect evidence</Button> }
    ]} />}
    {attempt && <EvaluationAttemptInspector tenant={tenant} id={attempt} canReview={context.permissions.includes('evaluations.review')} onClose={() => setAttempt(undefined)} onSaved={state.reload} />}
  </Flex>;
}

function SandboxScriptFields({ tenant }: { tenant: string }) {
  const profiles = useEvaluationCatalog<{ profile: { id: string; name: string; enabled: boolean } }>(tenant, 'sandbox-profiles');
  return <>
    <Alert type="info" showIcon title="Independent Sandbox execution" description="Manual, After Run and On failure can inspect an exact terminal Run. Each execution needs current caller and agent permissions plus exact operation Review. Inputs mount read-only at /home/input.json; /working is disposable. Egress is denied. Profile setup scripts do not run. Before Run and Publication are unsupported." />
    {profiles.error && <AccessError error={profiles.error} retry={profiles.reload} />}
    <Form.Item name="sandboxProfileId" label="Sandbox profile" rules={[{ required: true, message: 'Choose a readable saved profile. Its exact revision is pinned at admission.' }]}><Select loading={profiles.loading} options={profiles.data?.items.filter(p => p.profile.enabled).map(p => ({ value: p.profile.id, label: p.profile.name }))} /></Form.Item>
    <Form.Item name="sandboxInput" label="Required evidence" initialValue={0} rules={[{ required: true }]}><Select options={[{ value: 0, label: 'Run metadata only' }, { value: 1, label: 'Saved Run output' }, { value: 2, label: 'Admitted task input, when pinned' }]} /></Form.Item>
    <Typography.Paragraph type="secondary">Missing or unreadable required evidence is Inconclusive. Current workspaces are never substituted for historical inputs.</Typography.Paragraph>
    <Form.Item name="sandboxTimeoutSeconds" label="Command limit (seconds)" initialValue={10} rules={[{ required: true, type: 'number', min: 1, max: 30 }]}><InputNumber min={1} max={30} precision={0} /></Form.Item>
  </>;
}
