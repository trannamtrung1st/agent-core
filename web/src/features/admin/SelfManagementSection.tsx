import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Checkbox, Flex, Form, Select, Spin, Tag, Typography, theme } from 'antd';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { AdminErrorNotice } from './adminFailure';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { confirmAction } from '../../app/confirmAction';
import { getSelfManagement, saveSelfManagement, type SelfManagementView, type HarnessMode, type HarnessScope, type ManagementAutoApply } from '../../services/adminApi';

export const managementAreas = [
  { label: 'Settings', value: 'Settings' }, { label: 'Skills', value: 'Skills' }, { label: 'Resources', value: 'Resources' },
  { label: 'Connections', value: 'Connections' }, { label: 'Harness knowledge', value: 'KnowledgeResources' },
  { label: 'Harness instructions', value: 'Instructions' }, { label: 'Tool proposals', value: 'ToolSelection' },
  { label: 'Management policy proposals', value: 'ManagementPolicy' }, { label: 'Automation preferences', value: 'AutomationPreferences' }
];
type Draft = { mode: HarnessMode | null; scopes: HarnessScope[] | null; managedAutoApply: ManagementAutoApply[] | null;
  configurationAssistance: boolean | null; harnessAuthoring: boolean | null };
const inherited: Draft = { mode: null, scopes: null, managedAutoApply: null, configurationAssistance: null, harnessAuthoring: null };
function draftOf(view: SelfManagementView): Draft {
  const o = view.overrides;
  return o ? { mode: o.mode?.value ?? null, scopes: o.scopes?.value ?? null, managedAutoApply: o.autoApply?.value ?? null,
    configurationAssistance: o.configurationAssistance?.value ?? null, harnessAuthoring: o.harnessAuthoring?.value ?? null }
    : view.source === 'definition' ? inherited : { mode: view.effective.mode, scopes: view.effective.scopes,
      managedAutoApply: view.effective.managedAutoApply ?? [], configurationAssistance: view.effective.builtInSkills?.configurationAssistance ?? true,
      harnessAuthoring: view.effective.builtInSkills?.harnessAuthoring ?? true };
}
export function SelfManagementSection({ instanceId, archived, onUpdated }: { instanceId: string; archived: boolean; onUpdated: () => void }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [view, setView] = useState<SelfManagementView | null>(null); const [draft, setDraft] = useState<Draft>(inherited);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice | null>(null);
  const generation = useRef(0);
  useEffect(() => {
    const request = ++generation.current; setView(null); setDraft(inherited); setLoading(true); setBusy(false); setError(null);
    void getSelfManagement(instanceId).then(next => { if (request === generation.current) { setView(next); setDraft(draftOf(next)); } })
      .catch(e => { if (request === generation.current) setError(describeAdminError(e, 'Unable to load self-management. Retry.')); })
      .finally(() => { if (request === generation.current) setLoading(false); });
    return () => { generation.current++; };
  }, [instanceId]);
  async function reload() {
    const request = generation.current; setLoading(true); setError(null);
    try { const next = await getSelfManagement(instanceId); if (request === generation.current) setView(next); }
    catch (e) { if (request === generation.current) setError(describeAdminError(e, 'Unable to refresh policy. Retry.')); }
    finally { if (request === generation.current) setLoading(false); }
  }
  async function save(frozen?: boolean) {
    if (!view || archived) return;
    const request = generation.current; setBusy(true); setError(null);
    try {
      const next = await saveSelfManagement(instanceId, { expectedRevision: view.instanceRevision, ...draft, ...(frozen !== undefined ? { frozen } : {}) });
      if (request !== generation.current) return;
      setView(next); setDraft(draftOf(next)); onUpdated();
    } catch (e) { if (request === generation.current) setError(describeAdminError(e, 'Policy could not be saved. Refresh revisions; your draft is retained.')); }
    finally { if (request === generation.current) setBusy(false); }
  }
  const effectiveMode = draft.mode ?? view?.definitionDefaults.mode ?? 'Disabled';
  const changed = view && JSON.stringify(draft) !== JSON.stringify(draftOf(view));
  const disabled = archived || loading || busy;
  const builtinChoices = [{ value: 'inherit', label: 'Inherit' }, { value: 'enabled', label: 'Enabled' }, { value: 'disabled', label: 'Disabled' }];
  return <AgentConfigurationPanel title="Agent self-management" label="Agent self-management"
    description="Admin can always configure this Instance. Enabled Chat management uses the same owners, within selected areas and exact approvals." bodyGap="section">
    {loading && <Spin aria-label="Loading agent self-management" />}
    {error && <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />}
      action={<Button disabled={busy} onClick={() => void reload()}>Refresh policy</Button>} />}
    {view && <>
      <Flex wrap gap={token.paddingXS}><Tag>{view.effective.frozen ? 'Frozen' : view.effective.mode === 'Disabled' ? 'Off' : view.effective.mode}</Tag>
        <Typography.Text type="secondary">{view.source === 'per-field' ? 'Per-field inheritance' : view.source === 'definition' ? 'Definition defaults' : 'Instance policy'} · policy revision {view.policyRevision} · Instance revision {view.instanceRevision}</Typography.Text></Flex>
      {view.effective.frozen && <Alert type="info" showIcon title="Self-management is frozen" description="Only Admin can explicitly unfreeze. Normal tasks and retained Skills, resources and settings remain available." />}
      <Form layout="vertical" className="admin-config-form">
        <Flex vertical gap={token.padding}>
          <Form.Item label="Self-management mode" extra="Off removes management tools and procedures. Assisted changes wait for approval; Managed uses only explicitly delegated safe changes.">
            <Select aria-label="Self-management mode" value={draft.mode ?? 'inherit'} disabled={disabled} onChange={mode => setDraft(d => ({ ...d, mode: mode === 'inherit' ? null : mode as HarnessMode }))}
              options={[{ value: 'inherit', label: `Inherit (${view.definitionDefaults.mode === 'Disabled' ? 'Off' : view.definitionDefaults.mode})` }, { value: 'Disabled', label: 'Off' }, { value: 'Assisted', label: 'Assisted' }, { value: 'Managed', label: 'Managed' }]} />
          </Form.Item>
          <Form.Item label="Allowed management areas" extra="These areas add a management gate. They cannot exceed Definition capability or host authority.">
            <Flex vertical gap={token.paddingXS}>
              <Button disabled={disabled} style={{ alignSelf: 'flex-start' }} onClick={() => setDraft(d => ({ ...d, scopes: d.scopes === null ? view.effective.scopes : null }))}>{draft.scopes === null ? 'Customize areas' : 'Inherit areas'}</Button>
              <Checkbox.Group aria-label="Allowed management areas" options={managementAreas} disabled={disabled || draft.scopes === null}
                value={draft.scopes ?? view.definitionDefaults.scopes} onChange={scopes => setDraft(d => ({ ...d, scopes: scopes as HarnessScope[] }))} />
            </Flex>
          </Form.Item>
          {effectiveMode === 'Managed' && <Form.Item label="Managed automatic changes" extra="Capabilities, privacy, triggers, credentials and management widening always require exact approval.">
            <Flex vertical gap={token.paddingXS}>
              <Button disabled={disabled} style={{ alignSelf: 'flex-start' }} onClick={() => setDraft(d => ({ ...d, managedAutoApply: d.managedAutoApply === null ? view.effective.managedAutoApply ?? [] : null }))}>{draft.managedAutoApply === null ? 'Customize automatic changes' : 'Inherit automatic changes'}</Button>
              <Checkbox.Group aria-label="Managed automatic changes" disabled={disabled || draft.managedAutoApply === null} value={draft.managedAutoApply ?? view.definitionDefaults.managedAutoApply ?? []}
                options={[{ label: 'Conversation preferences', value: 'ConversationPreferences' }, { label: 'New text resources', value: 'TextResources' }, { label: 'Skill administration', value: 'SkillChanges' }]}
                onChange={values => setDraft(d => ({ ...d, managedAutoApply: values as ManagementAutoApply[] }))} />
            </Flex>
          </Form.Item>}
          <Typography.Text strong>Built-in management procedures</Typography.Text>
          <Typography.Text type="secondary">Read-only · On demand · version 1. Procedure enablement never grants authority; Off/Frozen suppresses both.</Typography.Text>
          {(['configurationAssistance', 'harnessAuthoring'] as const).map(key => <Form.Item key={key} label={key === 'configurationAssistance' ? 'Configuration assistance' : 'Harness authoring'}>
            <Select aria-label={key === 'configurationAssistance' ? 'Configuration assistance procedure' : 'Harness authoring procedure'} disabled={disabled}
              value={draft[key] === null ? 'inherit' : draft[key] ? 'enabled' : 'disabled'} options={builtinChoices}
              onChange={value => setDraft(d => ({ ...d, [key]: value === 'inherit' ? null : value === 'enabled' }))} />
          </Form.Item>)}
          <Typography.Text type="secondary">Changes apply to the next new Run in existing Sessions. Revocation and freeze take effect immediately.</Typography.Text>
          <Flex wrap gap={token.paddingXS}>
            <Button type="primary" disabled={disabled || !changed} onClick={() => void save()}>Save self-management</Button>
            <Button disabled={disabled} onClick={() => setDraft(inherited)}>Reset to Definition defaults</Button>
            {view.effective.frozen ? <Button disabled={disabled} onClick={() => confirmAction(modal, { title: 'Unfreeze self-management?', content: 'This explicitly removes the Instance lock. The selected mode and areas govern the next Run.', okText: 'Unfreeze', onOk: () => save(false) })}>Unfreeze self-management</Button>
              : view.effective.mode !== 'Disabled' && <Button disabled={disabled} onClick={() => confirmAction(modal, { title: 'Freeze self-management?', content: 'Revoke Chat configuration immediately. Normal tasks and retained content remain available. Only Admin can unfreeze.', okText: 'Freeze self-management', onOk: () => save(true) })}>Freeze self-management</Button>}
          </Flex>
        </Flex>
      </Form>
    </>}
  </AgentConfigurationPanel>;
}
