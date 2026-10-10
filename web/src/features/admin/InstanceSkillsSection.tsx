import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { CopyOutlined, DeleteOutlined, EditOutlined, EyeOutlined } from '@ant-design/icons';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Empty, Flex, Grid, Spin, Switch, Table, Tag, Tooltip, Typography, theme } from 'antd';
import { SkillDrawer, type SkillDrawerValue } from './SkillDrawer';
import { confirmAction } from '../../app/confirmAction';
import { listInstanceSkills, inspectInstanceSkill, createInstanceSkill, updateInstanceSkill, toggleInstanceSkill, resetInstanceSkill, customizeInstanceSkill, deleteInstanceSkill, type InstanceSkill, type SkillInput } from '../../services/instanceSkills';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { AdminErrorNotice } from './adminFailure';
import { AdminCollectionToolbar, useAdminCollectionSearch } from './AdminCollectionToolbar';


export function InstanceSkillsSection({ instanceId, archived, active = true, onUpdated }: { instanceId: string; archived: boolean; active?: boolean; onUpdated?: () => void }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const compact = !Grid.useBreakpoint().md;
  const [skills, setSkills] = useState<InstanceSkill[] | null>(null); const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [drawerVisible, setDrawerVisible] = useState(false);
  const [editing, setEditing] = useState<InstanceSkill | 'new' | null>(null); const [inspected, setInspected] = useState<InstanceSkill | null>(null);
  const [drawerValue, setDrawerValue] = useState<SkillDrawerValue | null>(null); const { search, setSearch, pagination } = useAdminCollectionSearch();
  const generation = useRef(0);
  const returnFocus = useRef<HTMLElement | null>(null);
  const restoreFocus = useRef(false);
  const closePanel = () => {
    setEditing(null); setInspected(null);
    restoreFocus.current = true;
  };
  useEffect(() => {
    if (!restoreFocus.current || busy || loading || editing || inspected || drawerVisible) return;
    restoreFocus.current = false;
    if (returnFocus.current?.isConnected) returnFocus.current.focus();
  }, [busy, loading, editing, inspected, drawerVisible]);
  const owner = useRef(instanceId); owner.current = instanceId;
  useEffect(() => {
    generation.current++; restoreFocus.current = false; setSkills(null); setEditing(null); setInspected(null); setBusy(false);
    return () => { generation.current++; };
  }, [instanceId]);
  const reload = useCallback(async () => {
    const request = ++generation.current;
    const current = () => generation.current === request && owner.current === instanceId;
    setLoading(true); setError(null);
    try { const result = await listInstanceSkills(instanceId); if (current()) setSkills(result); }
    catch (e) { if (current()) { setSkills(null); setError(describeAdminError(e, 'Unable to load Skills. Retry to reload.')); } }
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
      setDrawerValue(value ? { ...value, id: value.key.slice(value.key.indexOf(':') + 1), capabilities: value.requiredCapabilities.join(', ') } : { name: '', description: '', procedure: '', projection: 'OnDemand', enabled: true, capabilities: '' });
      if (value && (value.origin === 'Definition' || archived)) { setEditing(null); setInspected(value); return; }
      setEditing(value ?? 'new'); setInspected(null);
    } catch (e) { if (current()) setError(describeAdminError(e, 'Unable to inspect the Skill. Retry to load its current content.')); }
    finally { if (owner.current === instanceId) setBusy(false); }
  }
  async function save(value: SkillDrawerValue) {
    if (!editing || archived || !value.projection || value.enabled === undefined) return;
    const input: SkillInput = { ...(editing === 'new' && value.id?.trim() ? { id: value.id.trim() } : {}), name: value.name, description: value.description, procedure: value.procedure, projection: value.projection, enabled: value.enabled, requiredCapabilities: value.capabilities.split(',').map(s => s.trim()).filter(Boolean) };
    await mutate(() => editing === 'new' ? createInstanceSkill(instanceId, input) : updateInstanceSkill(instanceId, editing as InstanceSkill, input), true);
  }
  const visible = (skills ?? []).filter(s => `${s.name} ${s.description} ${s.key} ${s.projection === 'Always' ? 'Always' : 'On demand'} ${s.requiredCapabilities.join(' ')} ${s.sourceDefinitionId ?? ''} ${s.sourceDefinitionSkillId ?? ''}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  function group(origin: InstanceSkill['origin']) {
    const definition = origin === 'Definition';
    return <AgentConfigurationPanel title={<>{origin} Skills</>} label={`${origin} Skills`}
      description={definition ? 'Reusable procedures from the active Definition. Content is read-only; enabled choices belong to this instance.' : 'Independent procedures owned by this instance, retained across sessions and Definition changes.'}><Table<InstanceSkill> size="small" rowKey="key" loading={loading} pagination={pagination}
        className="admin-collection-table admin-skills-table" tableLayout="fixed"
        scroll={{ x: compact ? (definition ? 652 : 582) : definition ? 1430 : 1300 }} dataSource={visible.filter(s => s.origin === origin)}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching Skills' : definition ? 'This Definition has no reusable Skills.' : 'Create a local Skill or Customize a Definition Skill to keep your own procedure.'} /> }}
        columns={[
          { title: 'Skill', width: compact ? 200 : 180, onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, s) => <Flex vertical gap={token.paddingXS}>
            <Typography.Text strong className="admin-skill-summary" ellipsis={{ tooltip: true }} title={s.name}>{s.name}</Typography.Text>
            {compact ? <>
              <Typography.Text type="secondary" className="admin-skill-summary" ellipsis={{ tooltip: true }}>{s.description}</Typography.Text>
              <Flex wrap gap={token.paddingXS}><Tag>{s.projection === 'Always' ? 'Always' : 'On demand'}</Tag>{s.definitionVersion ? <Typography.Text type="secondary">Version {s.definitionVersion}</Typography.Text> : null}</Flex>
              {s.sourceDefinitionId ? <Typography.Text type="secondary" className="admin-skill-summary" ellipsis={{ tooltip: true }}>Copied from {s.sourceDefinitionId} v{s.sourceDefinitionVersion} · {s.sourceDefinitionSkillId}</Typography.Text> : null}
              <Typography.Text type="secondary" className="admin-skill-summary" ellipsis={{ tooltip: true }}>Requires: {s.requiredCapabilities.join(', ') || 'No capabilities'}</Typography.Text>
              {s.missingCapabilities.length ? <Typography.Text type="warning" className="admin-skill-summary" ellipsis={{ tooltip: true }}>Missing authority: {s.missingCapabilities.join(', ')}</Typography.Text> : null}
            </> : null}
          </Flex> },
          { title: 'Skill ID', width: 180, onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, s) => <Typography.Text type="secondary" className="admin-skill-summary" ellipsis={{ tooltip: true }} title={s.key.slice(s.key.indexOf(':') + 1)}>{s.key.slice(s.key.indexOf(':') + 1)}</Typography.Text> },
          { title: 'Description', width: 220, responsive: ['md'], onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, s) => <Typography.Text className="admin-skill-summary" ellipsis={{ tooltip: true }} title={s.description}>{s.description}</Typography.Text> },
          { title: 'Activation', width: 120, responsive: ['md'], render: (_, s) => <Tag>{s.projection === 'Always' ? 'Always' : 'On demand'}</Tag> },
          { title: definition ? 'Version' : 'Provenance', width: 160, responsive: ['md'], onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, s) => <Typography.Text type="secondary" className="admin-skill-summary" ellipsis={{ tooltip: true }} title={definition ? `Version ${s.definitionVersion}` : s.sourceDefinitionId ? `Copied from ${s.sourceDefinitionId} v${s.sourceDefinitionVersion} · ${s.sourceDefinitionSkillId}` : 'Created in this instance'}>{definition ? `Version ${s.definitionVersion}` : s.sourceDefinitionId ? `Copied from ${s.sourceDefinitionId} v${s.sourceDefinitionVersion} · ${s.sourceDefinitionSkillId}` : 'Created in this instance'}</Typography.Text> },
          { title: 'Requires', width: 170, responsive: ['md'], onCell: () => ({ style: { whiteSpace: 'normal' } }), render: (_, s) => <Flex vertical gap={token.paddingXS}><Typography.Text className="admin-skill-summary" ellipsis={{ tooltip: true }} title={s.requiredCapabilities.join(', ') || 'No capabilities'}>{s.requiredCapabilities.join(', ') || 'No capabilities'}</Typography.Text>{s.missingCapabilities.length ? <Typography.Text type="warning" className="admin-skill-summary" ellipsis={{ tooltip: true }}>Missing authority: {s.missingCapabilities.join(', ')}</Typography.Text> : null}</Flex> },
          { title: 'Enabled', width: definition ? 160 : 90, render: (_, s) => <Flex vertical align="start" gap={token.paddingXS}><Switch aria-label={`Enable ${origin} Skill ${s.name}`} checked={s.enabled} disabled={archived || busy || loading} onChange={enabled => void mutate(() => toggleInstanceSkill(instanceId, s, enabled))} />{definition && <Typography.Text type="secondary">{s.enabledOverride == null ? 'Inherit Definition' : 'Local choice'}</Typography.Text>}{definition && s.enabledOverride != null && <Button size="small" disabled={archived || busy || loading} onClick={() => void mutate(() => resetInstanceSkill(instanceId, s))}>Reset to default</Button>}</Flex> },
          { title: 'Actions', fixed: 'right', width: compact ? 112 : definition ? 240 : 180, render: (_, s) => <Flex className="admin-table-actions" gap={token.paddingXS}><Tooltip title={compact ? (definition || archived ? 'Inspect' : 'Edit') : undefined}><Button aria-label={definition || archived ? 'Inspect' : 'Edit'} icon={compact ? (definition || archived ? <EyeOutlined /> : <EditOutlined />) : undefined} disabled={busy} aria-expanded={definition || archived ? inspected?.key === s.key : undefined} onClick={() => void open(s)}>{compact ? null : definition || archived ? 'Inspect' : 'Edit'}</Button></Tooltip>{definition ? <Tooltip title={compact ? 'Customize' : undefined}><Button aria-label="Customize" icon={compact ? <CopyOutlined /> : undefined} disabled={archived || busy} onClick={() => confirmAction(modal, { title: 'Customize Definition Skill?', content: 'Creates an independent Instance Skill and disables this Definition Skill. Future Definition changes will not update the copy.', okText: 'Customize', onOk: () => mutate(() => customizeInstanceSkill(instanceId, s)) })}>{compact ? null : 'Customize'}</Button></Tooltip> : <Tooltip title={compact ? 'Delete' : undefined}><Button aria-label="Delete" icon={compact ? <DeleteOutlined /> : undefined} danger disabled={archived || busy} onClick={() => confirmAction(modal, { title: 'Delete Instance Skill?', content: `Delete ${s.name}? This removes the local procedure for future executions.`, okText: 'Delete Skill', danger: true, onOk: () => mutate(() => deleteInstanceSkill(instanceId, s)) })}>{compact ? null : 'Delete'}</Button></Tooltip>}</Flex> }
        ]} /></AgentConfigurationPanel>;
  }
  return <Flex vertical gap={token.padding}>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Skills do not grant capabilities. Enabled Skills with the same name both remain available. Changes apply to the next execution.</Typography.Paragraph>
    {archived ? <Alert type="info" showIcon title="Archived Skills are read-only" /> : null}
    <Flex wrap align="center" gap={token.paddingXS}><Button type="primary" disabled={archived || busy} onClick={() => void open()}>New Instance Skill</Button><Button disabled={loading || busy} onClick={() => void reload()}>Reload Skills</Button></Flex>
    {error && !editing ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button onClick={() => void reload()}>Retry Skills</Button>} /> : null}
    {loading && !skills ? <Spin aria-label="Loading Skills" /> : null}
    {skills ? <><AdminCollectionToolbar value={search} onChange={setSearch} label="Skills" />{group('Definition')}{group('Instance')}</> : null}
    <SkillDrawer open={Boolean(editing || inspected) && active} value={drawerValue}
      title={inspected ? `${inspected.origin} Skill details` : editing === 'new' ? 'New Instance Skill' : 'Edit Instance Skill'}
      idReadOnly={Boolean(inspected) || editing !== 'new'} validateId={id => id && (skills ?? []).some(s => s.origin === 'Instance' && s.key === `instance:${id}`) ? 'This Skill ID already exists in this Agent Instance.' : null}
      readOnly={Boolean(inspected)} busy={busy} afterOpenChange={setDrawerVisible} focusTriggerAfterClose={false}
      onClose={() => { if (!busy) closePanel(); }} onSave={save}
      contentLabel={inspected ? `${inspected.origin} Skill content` : 'Instance Skill editor'}
      context={inspected?.origin === 'Definition' ? 'Reusable procedure from the active Definition. Customize it to create an independent Instance Skill.' : 'An independent procedure for this instance. Changes apply to the next execution.'}
      notice={error ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} /> : null}
    />
  </Flex>;
}
