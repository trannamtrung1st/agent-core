import type { CSSProperties } from 'react';
import { Button, Checkbox, Flex, Form, Select, Switch, Typography, theme } from 'antd';
import type { HarnessMode, HarnessScope, ManagementAutoApply, SelfManagementPolicy, SelfManagementView, ManagementOverrides } from '../../services/adminApi';

export const managementAreas = [
  { label: 'Settings', value: 'Settings' }, { label: 'Skills', value: 'Skills' }, { label: 'Resources', value: 'Resources' },
  { label: 'Connections', value: 'Connections' }, { label: 'Harness knowledge', value: 'KnowledgeResources' },
  { label: 'Harness instructions', value: 'Instructions' }, { label: 'Tool proposals', value: 'ToolSelection' },
  { label: 'Management policy proposals', value: 'ManagementPolicy' }, { label: 'Automation preferences', value: 'AutomationPreferences' }
];
const automaticChanges = [
  { label: 'Conversation preferences', value: 'ConversationPreferences' },
  { label: 'New text resources', value: 'TextResources' }, { label: 'Skill create/update/enable', value: 'SkillChanges' }
];
export type SelfManagementDraft = {
  mode: HarnessMode | null; scopes: HarnessScope[] | null; managedAutoApply: ManagementAutoApply[] | null;
  configurationAssistance: boolean | null; harnessAuthoring: boolean | null;
};
export const inheritedManagement: SelfManagementDraft = {
  mode: null, scopes: null, managedAutoApply: null, configurationAssistance: null, harnessAuthoring: null
};
export const defaultManagement: SelfManagementPolicy = {
  mode: 'Disabled', scopes: [], sources: [], eligibleTools: [], frozen: false,
  managedAutoApply: [], builtInSkills: { configurationAssistance: true, harnessAuthoring: true }
};

export function managementDraftOf(view: SelfManagementView): SelfManagementDraft {
  const o = view.overrides;
  return o ? { mode: o.mode?.value ?? null, scopes: o.scopes?.value ?? null, managedAutoApply: o.autoApply?.value ?? null,
    configurationAssistance: o.configurationAssistance?.value ?? null, harnessAuthoring: o.harnessAuthoring?.value ?? null }
    : view.source === 'definition' ? inheritedManagement : { mode: view.effective.mode, scopes: view.effective.scopes,
      managedAutoApply: view.effective.managedAutoApply ?? [], configurationAssistance: view.effective.builtInSkills?.configurationAssistance ?? true,
      harnessAuthoring: view.effective.builtInSkills?.harnessAuthoring ?? true };
}
export function managementOverridesOf(draft: SelfManagementDraft): ManagementOverrides {
  const field = <T,>(value: T | null) => value === null ? null : { value };
  return { mode: field(draft.mode), scopes: field(draft.scopes), autoApply: field(draft.managedAutoApply),
    configurationAssistance: field(draft.configurationAssistance), harnessAuthoring: field(draft.harnessAuthoring) };
}

/** Shared policy fields; owners supply persistence, inheritance defaults and lock actions. */
export function SelfManagementFields({ value, defaults, disabled, definition = false, onChange }: {
  value: SelfManagementDraft; defaults: SelfManagementPolicy; disabled: boolean; definition?: boolean;
  onChange: (value: SelfManagementDraft) => void;
}) {
  const { token } = theme.useToken();
  const optionStyle = {
    gap: token.paddingXS,
    '--self-management-checkbox-offset': `${(token.fontSize * token.lineHeight - token.controlInteractiveSize) / 2}px`
  } as CSSProperties;
  const update = (change: Partial<SelfManagementDraft>) => onChange({ ...value, ...change });
  const mode = value.mode ?? defaults.mode;
  const modeLabel = definition ? 'Default self-management mode' : 'Self-management mode';
  const areasLabel = definition ? 'Default allowed areas' : 'Allowed management areas';
  const automaticLabel = definition ? 'Default automatic changes' : 'Managed automatic changes';
  return <Flex vertical gap={token.padding} className="self-management-fields">
    <Form.Item label={modeLabel} extra="Off removes management tools and procedures. Assisted changes wait for approval; Managed uses only explicitly delegated safe changes.">
      <Select aria-label={modeLabel} value={value.mode ?? 'inherit'} disabled={disabled}
        onChange={next => update({ mode: next === 'inherit' ? null : next as HarnessMode })}
        options={[...(!definition ? [{ value: 'inherit', label: `Inherit (${defaults.mode === 'Disabled' ? 'Off' : defaults.mode})` }] : []),
          { value: 'Disabled', label: 'Off' }, { value: 'Assisted', label: 'Assisted' }, { value: 'Managed', label: 'Managed' }]} />
    </Form.Item>
    <Form.Item label={areasLabel} extra="These areas add a management gate. They cannot exceed Definition capability or host authority. With no areas selected, the agent has no management access.">
      <Flex vertical gap={token.paddingXS}>
        {!definition && <Button disabled={disabled} style={{ alignSelf: 'flex-start' }} onClick={() => update({ scopes: null })}>Inherit areas</Button>}
        <Checkbox.Group aria-label={definition ? 'Default self-management areas' : areasLabel} className="self-management-options"
          style={optionStyle} options={managementAreas} disabled={disabled} value={value.scopes ?? defaults.scopes}
          onChange={scopes => update({ scopes: scopes as HarnessScope[] })} />
      </Flex>
    </Form.Item>
    {mode === 'Managed' && <Form.Item label={automaticLabel} extra="Capabilities, privacy, triggers, credentials and management widening always require exact approval. Skill deletion and customization also require exact approval.">
      <Flex vertical gap={token.paddingXS}>
        {!definition && <Button disabled={disabled} style={{ alignSelf: 'flex-start' }} onClick={() => update({ managedAutoApply: null })}>Inherit automatic changes</Button>}
        <Checkbox.Group aria-label={automaticLabel} className="self-management-options" style={optionStyle}
          disabled={disabled} value={value.managedAutoApply ?? defaults.managedAutoApply ?? []} options={automaticChanges}
          onChange={values => update({ managedAutoApply: values as ManagementAutoApply[] })} />
      </Flex>
    </Form.Item>}
    <Typography.Text strong>Built-in management procedures</Typography.Text>
    <Typography.Text type="secondary">Read-only · On demand · version 1. Procedure enablement never grants authority; Off/Frozen suppresses both.</Typography.Text>
    {(['configurationAssistance', 'harnessAuthoring'] as const).map(key => {
      const label = key === 'configurationAssistance' ? 'Configuration assistance' : 'Harness authoring';
      return <Form.Item key={key} label={label}>
        {definition ? <Switch aria-label={`Default ${label.toLowerCase()} procedure`} disabled={disabled}
          checked={value[key] ?? defaults.builtInSkills?.[key] ?? true} onChange={enabled => update({ [key]: enabled })} />
          : <Select aria-label={`${label} procedure`} disabled={disabled}
            value={value[key] === null ? 'inherit' : value[key] ? 'enabled' : 'disabled'}
            options={[{ value: 'inherit', label: `Inherit (${(defaults.builtInSkills?.[key] ?? true) ? 'Enabled' : 'Disabled'})` },
              { value: 'enabled', label: 'Enabled' }, { value: 'disabled', label: 'Disabled' }]}
            onChange={next => update({ [key]: next === 'inherit' ? null : next === 'enabled' })} />}
      </Form.Item>;
    })}
  </Flex>;
}
