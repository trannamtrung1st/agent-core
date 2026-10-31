import { AgentSettingRow } from './AgentConfigurationLayout';
import { useId, type ReactNode } from 'react';
import { Flex, Form, Select, Switch, theme } from 'antd';
import type { SettingValue } from '../../services/instanceConfiguration';
import { isolationChoiceLabels, isolationLabels } from './stateIsolationPresentation';
import { stateIsolationDefaults } from '../../services/isolationPresets';
export { stateIsolationDefaults } from '../../services/isolationPresets';

export const stateIsolationChoices: Record<string, string[]> = {
  conversationContextScope: ['Session', 'CurrentRequest'], homeAccess: ['ReadWrite', 'ReadOnly', 'Disabled'],
  workingCleanup: ['OnSessionDeletion', 'AfterTerminal'], browserIsolation: ['HostDefault', 'PersistentAgent', 'EphemeralSession'],
  agentStateVisibility: ['Off', 'CurrentSession', 'InstanceSummary']
};

/** Shared policy order and grid boundary for Definition drafts, publications and Instances. */
export function StateIsolationPolicyFields({ value, choices, disabled, extra, onChange, children, definitionVisibility }: {
  value: (field: string) => SettingValue;
  choices: (field: string) => readonly string[];
  disabled: (field: string) => boolean;
  extra?: (field: string) => ReactNode;
  onChange: (field: string, value: string | boolean) => void;
  children?: ReactNode;
  definitionVisibility?: { maximum: string; onChange: (value: string) => void };
}) {
  const id = useId();
  const { token } = theme.useToken();
  return <Flex vertical gap={token.padding}>
    <div className="agent-settings-rows">
      {Object.keys(stateIsolationChoices).map(field => {
        const selected = String(value(field));
        const allowed = choices(field);
        // A blocked preset still shows its requested value in product language.
        const options = allowed.includes(selected) ? allowed : [...allowed, selected];
        const label = field === 'agentStateVisibility' && definitionVisibility ? 'Default agent state visibility' : isolationLabels[field];
        return <AgentSettingRow key={field} label={<label htmlFor={`${id}-${field}`}>{label}</label>} description={extra?.(field)}><Form.Item>
          <Select id={`${id}-${field}`} aria-label={label} value={selected} disabled={disabled(field)}
            options={options.map(v => ({ value: v, label: isolationChoiceLabels[v] ?? v, disabled: !allowed.includes(v) }))} onChange={v => onChange(field, v)} />
        </Form.Item></AgentSettingRow>;
      })}
      {definitionVisibility && <AgentSettingRow label={<label htmlFor={`${id}-maximum-visibility`}>Maximum agent state visibility</label>} description="Instances may choose any visibility up to this maximum. Inspection still requires an authorized capability."><Form.Item>
        <Select id={`${id}-maximum-visibility`} aria-label="Maximum agent state visibility" value={definitionVisibility.maximum} disabled={disabled('agentStateVisibility')}
          options={stateIsolationChoices.agentStateVisibility.map((visibility, index) => ({ value: visibility, label: isolationChoiceLabels[visibility],
            disabled: index < stateIsolationChoices.agentStateVisibility.indexOf(String(value('agentStateVisibility'))) }))}
          onChange={definitionVisibility.onChange} />
      </Form.Item></AgentSettingRow>}
    </div>
    <div className="agent-settings-rows">
      {Object.entries(stateIsolationDefaults).filter(([, fallback]) => typeof fallback === 'boolean').map(([field]) =>
        <StateIsolationToggleField key={field} label={isolationLabels[field]} checked={value(field) === true}
          disabled={disabled(field)} extra={extra?.(field)} onChange={v => onChange(field, v)} />)}
      {children}
    </div>
  </Flex>;
}

export function StateIsolationToggleField({ label, checked, disabled, extra, onChange }: {
  label: string; checked: boolean; disabled: boolean; extra?: ReactNode; onChange: (value: boolean) => void;
}) {
  const id = useId();
  return <AgentSettingRow label={<label htmlFor={id}>{label}</label>} description={extra}>
    <Form.Item><Switch id={id} aria-label={label} checked={checked} disabled={disabled} onChange={onChange} /></Form.Item>
  </AgentSettingRow>;
}
