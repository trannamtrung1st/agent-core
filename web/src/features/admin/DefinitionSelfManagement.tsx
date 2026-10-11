import { Checkbox, Flex, Select, Switch, Typography, theme } from 'antd';
import { managementAreas } from './SelfManagementSection';
import type { DefinitionCandidate } from './definitionCandidate';
import type { HarnessScope, ManagementAutoApply, SelfManagementPolicy } from '../../services/adminApi';

const defaults: SelfManagementPolicy = {
  mode: 'Disabled', scopes: [], sources: [], eligibleTools: [], frozen: false,
  managedAutoApply: [], builtInSkills: { configurationAssistance: true, harnessAuthoring: true }
};

export function DefinitionSelfManagement({ candidate, disabled, onChange }: {
  candidate: DefinitionCandidate;
  disabled: boolean;
  onChange: (candidate: DefinitionCandidate) => void;
}) {
  const { token } = theme.useToken();
  const policy = { ...defaults, ...(candidate.selfManagement as Partial<SelfManagementPolicy> | undefined) };
  function update(change: Partial<SelfManagementPolicy>) {
    onChange({ ...candidate, selfManagement: { ...policy, ...change, frozen: false } });
  }
  return <section className="admin-draft-form-section admin-settings-form" aria-label="Definition self-management defaults">
    <Flex vertical gap={token.padding}>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>
        Instances inherit these defaults unless customized. Frozen locks remain Instance-local. Runtime tool permissions still apply independently.
      </Typography.Paragraph>
      <Flex vertical gap={token.paddingXS}>
        <Typography.Text>Default self-management mode</Typography.Text>
        <Select aria-label="Default self-management mode" disabled={disabled} value={policy.mode}
          options={[{ value: 'Disabled', label: 'Off' }, { value: 'Assisted', label: 'Assisted' }, { value: 'Managed', label: 'Managed' }]}
          onChange={mode => update({ mode })} />
      </Flex>
      <Flex vertical gap={token.paddingXS}>
        <Typography.Text>Default allowed areas</Typography.Text>
        <Checkbox.Group aria-label="Default self-management areas" disabled={disabled} value={policy.scopes}
          options={managementAreas} onChange={scopes => update({ scopes: scopes as HarnessScope[] })} />
      </Flex>
      {policy.mode === 'Managed' && <Flex vertical gap={token.paddingXS}>
        <Typography.Text>Default automatic changes</Typography.Text>
        <Checkbox.Group aria-label="Default automatic changes" disabled={disabled} value={policy.managedAutoApply ?? []}
          options={[{ label: 'Conversation preferences', value: 'ConversationPreferences' },
            { label: 'New text resources', value: 'TextResources' }, { label: 'Skill administration', value: 'SkillChanges' }]}
          onChange={managedAutoApply => update({ managedAutoApply: managedAutoApply as ManagementAutoApply[] })} />
      </Flex>}
      {(['configurationAssistance', 'harnessAuthoring'] as const).map(key => <Flex key={key} wrap align="center" gap={token.paddingXS}>
        <Switch aria-label={key === 'configurationAssistance' ? 'Default configuration assistance procedure' : 'Default harness authoring procedure'}
          disabled={disabled} checked={policy.builtInSkills?.[key] ?? true}
          onChange={enabled => update({ builtInSkills: { ...defaults.builtInSkills!, ...policy.builtInSkills, [key]: enabled } })} />
        <Typography.Text>{key === 'configurationAssistance' ? 'Configuration assistance' : 'Harness authoring'} · Read-only · On demand</Typography.Text>
      </Flex>)}
    </Flex>
  </section>;
}
