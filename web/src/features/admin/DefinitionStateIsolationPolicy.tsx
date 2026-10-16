import type { ReactNode } from 'react';
import { Button, Flex, Form, Typography, theme } from 'antd';
import type { DefinitionCandidate } from './definitionCandidate';
import { StateIsolationPolicyFields, stateIsolationChoices, stateIsolationDefaults as defaults } from './StateIsolationPolicyFields';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { IsolationPresetControl } from './IsolationPresetControl';
import { IsolationChangePreview } from './IsolationChangePreview';
import { applyDefinitionIsolationPreset, definitionIsolationChanges, definitionIsolationPreset, definitionStatePolicy } from './definitionIsolationPresets';

export function DefinitionStateIsolationPolicy({ candidate, savedCandidate = candidate, disabled = false, readOnly = false, active = true, id, details, onChange }: {
  candidate: DefinitionCandidate; savedCandidate?: DefinitionCandidate; disabled?: boolean; readOnly?: boolean; active?: boolean;
  id?: string; details?: ReactNode; onChange?: (next: DefinitionCandidate) => void;
}) {
  const { token } = theme.useToken();
  const policy = definitionStatePolicy(candidate);
  const changes = definitionIsolationChanges(candidate, savedCandidate);
  function change(field: string, value: string | boolean) { onChange?.({ ...candidate, stateIsolationPolicy: { ...defaults, ...policy, [field]: value } }); }
  return <Flex vertical gap={token.padding}>
    <AgentConfigurationPanel id={id} title="Continuity and resource policy" label={readOnly ? 'Published State & Isolation' : 'Draft State & Isolation'} bodyGap="section"
      description={readOnly ? 'Published policy is read-only. Create a draft to change it.' : 'Presets prepare editable drafts. Save and publish a new version; existing Instances adopt it explicitly.'}>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Instances may narrow home access, sharing and learning, and choose state visibility up to the Definition maximum. State visibility also requires an explicitly authorized state-inspection capability. A persistent browser profile requires host support. Current-request continuity requires a fresh ordinary Session and text input.</Typography.Paragraph>
      {!candidate.stateIsolationPolicy && <Typography.Text type="secondary">No explicit policy. Existing continuity and host browser behavior are preserved; agent state awareness is off.</Typography.Text>}
      <Form layout="vertical" className="admin-config-form instance-settings-form">
        <Flex vertical gap={token.padding}>
          <IsolationPresetControl value={definitionIsolationPreset(candidate, savedCandidate)} disabled={disabled || readOnly} dirty={changes.length > 0}
            active={active} scope="Definition" onSelect={preset => { if (preset !== 'DefinitionDefaults') onChange?.(applyDefinitionIsolationPreset(candidate, savedCandidate, preset)); }} />
          <StateIsolationPolicyFields value={field => (policy[field] ?? defaults[field]) as string | boolean}
            choices={field => field === 'agentStateVisibility' ? stateIsolationChoices[field].filter((_, index) => index <= stateIsolationChoices[field].indexOf(String(policy.maxAgentStateVisibility))) : stateIsolationChoices[field]}
            definitionVisibility={{ maximum: String(policy.maxAgentStateVisibility), onChange: value => change('maxAgentStateVisibility', value) }}
            disabled={() => disabled || readOnly} onChange={change} />
        </Flex>
      </Form>
      {!readOnly && <Button style={{ alignSelf: 'flex-start' }} disabled={disabled || !candidate.stateIsolationPolicy} onClick={() => onChange?.({ ...candidate, stateIsolationPolicy: null })}>Use compatibility defaults</Button>}
      {details}
      <Typography.Text type="secondary">These Definition permissions do not activate Experience or autonomous identity maintenance. Both start Off for each new Instance; enable them separately in that Instance’s State & Isolation page. Existing Instances keep their saved opt-ins when adopting a new Definition version. Shared accounts retain their own authority and external effects.</Typography.Text>
    </AgentConfigurationPanel>
    {!readOnly && <IsolationChangePreview changes={changes} notice="Changes stay in this Definition draft until saved and published. Existing Instances require explicit version adoption. Existing history, home files, browser profiles and Artifacts are retained. Working files remain until Session deletion unless safe terminal cleanup is selected." />}
  </Flex>;
}
