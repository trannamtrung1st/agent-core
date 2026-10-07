import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Empty, Flex, Form, Input, Select, Spin, Switch, Table, Tag, Typography, theme } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { listInstanceSkills, inspectInstanceSkill, createInstanceSkill, updateInstanceSkill, toggleInstanceSkill, customizeInstanceSkill, deleteInstanceSkill, type InstanceSkill, type SkillInput } from '../../services/instanceSkills';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { AdminErrorNotice } from './adminFailure';
import { AdminCollectionToolbar, useAdminCollectionSearch } from './AdminCollectionToolbar';

type SkillForm = Omit<SkillInput, 'requiredCapabilities'> & { capabilities: string };
export function InstanceSkillsSection({ instanceId, archived, active = true, onUpdated }: { instanceId: string; archived: boolean; active?: boolean; onUpdated?: () => void }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [skills, setSkills] = useState<InstanceSkill[] | null>(null); const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [editing, setEditing] = useState<InstanceSkill | 'new' | null>(null); const [inspected, setInspected] = useState<InstanceSkill | null>(null);
  const [form] = Form.useForm<SkillForm>(); const { search, setSearch, pagination } = useAdminCollectionSearch();
  const generation = useRef(0);
  const returnFocus = useRef<HTMLElement | null>(null);
  const restoreFocus = useRef(false);
  const editorPanel = useRef<HTMLElement>(null); const inspectionPanel = useRef<HTMLElement>(null);
  const closePanel = () => {
    setEditing(null); setInspected(null);
    restoreFocus.current = true;
  };
  useEffect(() => {
    if (!restoreFocus.current || busy || loading || editing || inspected) return;
    restoreFocus.current = false;
    if (returnFocus.current?.isConnected) returnFocus.current.focus();
  }, [busy, loading, editing, inspected]);
  useEffect(() => {
    const panel = inspected ? inspectionPanel.current : editing ? editorPanel.current : null;
    if (!panel) return;
    const heading = panel.querySelector('h4');
    heading?.focus(); panel.scrollIntoView?.({ block: 'nearest' });
  }, [editing, inspected]);
  const owner = useRef(instanceId); owner.current = instanceId;
  useEffect(() => {
    generation.current++; restoreFocus.current = false; setSkills(null); setEditing(null); setInspected(null); setBusy(false);
    return () => { generation.current++; };
  }, [instanceId, form]);
  const reload = useCallback(async () => {
    const request = ++generation.current;
    const current = () => generation.current === request && owner.current === instanceId;
    setLoading(true); setError(null);
    try { const result = await listInstanceSkills(instanceId); if (current()) setSkills(result); }
    catch (e) { if (current()) setError(describeAdminError(e, 'Unable to load Skills. Retry to reload.')); }
    finally { if (current()) setLoading(false); }
  }, [instanceId]);
  useEffect(() => { if (active) void reload(); }, [reload, active]);
  async function mutate(action: () => Promise<unknown>, close = false) {
    const request = generation.current;
    const current = () => generation.current === request && owner.current === instanceId;
    setBusy(true); setError(null);
    try { await action(); if (!current()) return; if (close) closePanel(); await reload(); if (owner.current === instanceId) onUpdated?.(); }
    catch (e) { if (current()) setError(describeAdminError(e, 'The Skill changed or could not be saved. Reload and reopen it before retrying; your draft is retained.')); }
    finally { if (owner.current === instanceId) setBusy(false); }
  }
  async function open(skill?: InstanceSkill) {
    returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const request = generation.current;
    const current = () => generation.current === request && owner.current === instanceId;
    setBusy(true); setError(null);
    try {
      const value = skill ? await inspectInstanceSkill(instanceId, skill.key) : null;
      if (!current()) return;
      if (value && (value.origin === 'Definition' || archived)) { setEditing(null); setInspected(value); return; }
      setEditing(value ?? 'new'); setInspected(null);
      form.setFieldsValue(value ? { ...value, capabilities: value.requiredCapabilities.join(', ') } : { name: '', description: '', procedure: '', projection: 'OnDemand', enabled: true, capabilities: '' });
    } catch (e) { if (current()) setError(describeAdminError(e, 'Unable to inspect the Skill. Retry to load its current content.')); }
    finally { if (owner.current === instanceId) setBusy(false); }
  }
  async function save(value: SkillForm) {
    const input: SkillInput = { name: value.name, description: value.description, procedure: value.procedure, projection: value.projection, enabled: value.enabled, requiredCapabilities: value.capabilities.split(',').map(s => s.trim()).filter(Boolean) };
    await mutate(() => editing === 'new' ? createInstanceSkill(instanceId, input) : updateInstanceSkill(instanceId, editing as InstanceSkill, input), true);
  }
  const visible = (skills ?? []).filter(s => `${s.name} ${s.description}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  function group(origin: InstanceSkill['origin']) {
    const definition = origin === 'Definition';
    return <section className="admin-definition-panel" aria-label={`${origin} Skills`}>
      <div className="admin-definition-panel-heading"><Typography.Title level={4}>{origin} Skills</Typography.Title><Typography.Text type="secondary">{definition ? 'Reusable procedures from the active Definition. Content is read-only; enabled choices belong to this instance.' : 'Independent procedures owned by this instance, retained across sessions and Definition changes.'}</Typography.Text></div>
      <div className="admin-definition-panel-body"><Table<InstanceSkill> size="small" rowKey="key" loading={loading} pagination={pagination} scroll={{ x: 720 }} dataSource={visible.filter(s => s.origin === origin)}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching Skills' : definition ? 'This Definition has no reusable Skills.' : 'Create a local Skill or Customize a Definition Skill to keep your own procedure.'} /> }}
        columns={[
          { title: 'Skill', width: 280, render: (_, s) => <Flex vertical gap={token.paddingXS}><Typography.Text strong style={{ overflowWrap: 'anywhere' }}>{s.name}</Typography.Text><Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>{s.description}</Typography.Text><Flex wrap gap={token.paddingXS}><Tag>{origin}</Tag><Tag>{s.projection === 'Always' ? 'Always' : 'On demand'}</Tag>{s.definitionVersion ? <Typography.Text type="secondary">Version {s.definitionVersion}</Typography.Text> : null}</Flex>{s.sourceDefinitionId ? <Typography.Text type="secondary">Copied from {s.sourceDefinitionId} v{s.sourceDefinitionVersion} · {s.sourceDefinitionSkillId}</Typography.Text> : null}{s.missingCapabilities.length ? <Alert type="warning" showIcon title={`Missing authority: ${s.missingCapabilities.join(', ')}`} /> : null}</Flex> },
          { title: 'Requires', width: 170, render: (_, s) => <Typography.Text style={{ overflowWrap: 'anywhere' }}>{s.requiredCapabilities.join(', ') || 'No capabilities'}</Typography.Text> },
          { title: 'Enabled', width: 90, render: (_, s) => <Switch aria-label={`Enable ${origin} Skill ${s.name}`} checked={s.enabled} disabled={archived || busy || loading} onChange={enabled => void mutate(() => toggleInstanceSkill(instanceId, s, enabled))} /> },
          { title: 'Actions', width: 180, render: (_, s) => <Flex wrap gap={token.paddingXS}><Button disabled={busy} onClick={() => void open(s)}>{definition || archived ? 'Inspect' : 'Edit'}</Button>{definition ? <Button disabled={archived || busy} onClick={() => confirmAction(modal, { title: 'Customize Definition Skill?', content: 'Creates an independent Instance Skill and disables this Definition Skill. Future Definition changes will not update the copy.', okText: 'Customize', onOk: () => mutate(() => customizeInstanceSkill(instanceId, s)) })}>Customize</Button> : <Button danger disabled={archived || busy} onClick={() => confirmAction(modal, { title: 'Delete Instance Skill?', content: `Delete ${s.name}? This removes the local procedure for future executions.`, okText: 'Delete Skill', danger: true, onOk: () => mutate(() => deleteInstanceSkill(instanceId, s)) })}>Delete</Button>}</Flex> }
        ]} /></div>
    </section>;
  }
  return <Flex vertical gap={token.padding}>
    <Typography.Paragraph type="secondary">Skills do not grant capabilities. Enabled Skills with the same name both remain available. Changes apply to the next execution.</Typography.Paragraph>
    {archived ? <Alert type="info" showIcon title="Archived Skills are read-only" /> : null}
    <Flex wrap align="center" gap={token.paddingXS}><Button type="primary" disabled={archived || busy} onClick={() => void open()}>New Instance Skill</Button><Button disabled={loading || busy} onClick={() => void reload()}>Reload Skills</Button></Flex>
    {error ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button onClick={() => void reload()}>Retry Skills</Button>} /> : null}
    {loading && !skills ? <Spin aria-label="Loading Skills" /> : null}
    {skills ? <><AdminCollectionToolbar value={search} onChange={setSearch} label="Skills" />{group('Definition')}{group('Instance')}</> : null}
    {inspected ? <section ref={inspectionPanel} className="admin-definition-panel" aria-label={`${inspected.origin} Skill content`}><div className="admin-definition-panel-heading"><Typography.Title level={4} tabIndex={-1}>{inspected.name}</Typography.Title><Button onClick={closePanel}>Close inspection</Button></div><div className="admin-definition-panel-body"><Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{inspected.procedure}</Typography.Paragraph></div></section> : null}
    <section ref={editorPanel} hidden={!editing} style={{ display: editing ? undefined : 'none' }} className="admin-definition-panel" aria-label="Instance Skill editor"><div className="admin-definition-panel-heading"><Typography.Title level={4} tabIndex={-1}>{editing === 'new' ? 'New Instance Skill' : 'Edit Instance Skill'}</Typography.Title></div><div className="admin-definition-panel-body"><Form form={form} layout="vertical" onFinish={value => void save(value)} style={{ maxWidth: '48rem' }} disabled={busy || archived}>
      <Form.Item name="name" label="Skill name" rules={[{ required: true, whitespace: true, max: 80 }]}><Input maxLength={80} /></Form.Item>
      <Form.Item name="description" label="Description" rules={[{ required: true, whitespace: true, max: 240 }]}><Input.TextArea maxLength={240} rows={2} /></Form.Item>
      <Form.Item name="procedure" label="Procedure" rules={[{ required: true, whitespace: true, max: 4000 }]}><Input.TextArea maxLength={4000} rows={6} /></Form.Item>
      <Form.Item name="projection" label="Projection" rules={[{ required: true }]}><Select options={[{ value: 'Always', label: 'Always · active at execution start' }, { value: 'OnDemand', label: 'On demand · loaded when needed' }]} /></Form.Item>
      <Form.Item name="capabilities" label="Required capabilities" extra="Comma-separated authorized capability names. Requirements never grant authority."><Input /></Form.Item>
      <Form.Item name="enabled" label="Enabled" valuePropName="checked"><Switch /></Form.Item>
      <Flex wrap gap={token.paddingXS}><Button type="primary" htmlType="submit" loading={busy}>Save Skill</Button></Flex>
    </Form><Button disabled={busy} onClick={closePanel}>Cancel editing</Button></div></section>
  </Flex>;
}
