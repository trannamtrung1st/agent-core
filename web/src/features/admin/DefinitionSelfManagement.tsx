import { Flex, Form, Typography, theme } from 'antd';
import type { DefinitionCandidate } from './definitionCandidate';
import type { SelfManagementPolicy } from '../../services/adminApi';
import { defaultManagement, SelfManagementFields } from './SelfManagementFields';

export function DefinitionSelfManagement({ candidate, disabled, onChange }: {
  candidate: DefinitionCandidate;
  disabled: boolean;
  onChange: (candidate: DefinitionCandidate) => void;
}) {
  const { token } = theme.useToken();
  const policy = { ...defaultManagement, ...(candidate.selfManagement as Partial<SelfManagementPolicy> | undefined) };
  return <section className="admin-draft-form-section admin-settings-form" aria-label="Definition self-management defaults">
    <Flex vertical gap={token.padding}>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>
        Instances inherit these defaults unless customized. Frozen locks remain Instance-local. Runtime tool permissions still apply independently.
      </Typography.Paragraph>
      <Form layout="vertical" className="admin-config-form">
        <SelfManagementFields definition disabled={disabled} defaults={defaultManagement}
          value={{ mode: policy.mode, scopes: policy.scopes, managedAutoApply: policy.managedAutoApply ?? [],
            configurationAssistance: policy.builtInSkills?.configurationAssistance ?? true,
            harnessAuthoring: policy.builtInSkills?.harnessAuthoring ?? true }}
          onChange={value => onChange({ ...candidate, selfManagement: { ...policy, mode: value.mode!, scopes: value.scopes!,
            managedAutoApply: value.managedAutoApply, frozen: false,
            builtInSkills: { configurationAssistance: value.configurationAssistance!, harnessAuthoring: value.harnessAuthoring! } } })} />
      </Form>
    </Flex>
  </section>;
}
