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
    <div className="admin-settings-field-grid">
      {Object.keys(stateIsolationChoices).map(field => {
        const selected = String(value(field));
        const allowed = choices(field);
        // A blocked preset still shows its requested value in product language.
        const options = allowed.includes(selected) ? allowed : [...allowed, selected];
        const label = field === 'agentStateVisibility' && definitionVisibility ? 'Default agent state visibility' : isolationLabels[field];
        return <Form.Item key={field} label={label} htmlFor={`${id}-${field}`} extra={extra?.(field)}>
          <Select id={`${id}-${field}`} aria-label={label} value={selected} disabled={disabled(field)}
            options={options.map(v => ({ value: v, label: isolationChoiceLabels[v] ?? v, disabled: !allowed.includes(v) }))} onChange={v => onChange(field, v)} />
        </Form.Item>;
      })}
      {definitionVisibility && <Form.Item label="Maximum agent state visibility" htmlFor={`${id}-maximum-visibility`}
        extra="Instances may choose any visibility up to this maximum. Inspection still requires an authorized capability.">
        <Select id={`${id}-maximum-visibility`} aria-label="Maximum agent state visibility" value={definitionVisibility.maximum} disabled={disabled('agentStateVisibility')}
          options={stateIsolationChoices.agentStateVisibility.map((visibility, index) => ({ value: visibility, label: isolationChoiceLabels[visibility],
            disabled: index < stateIsolationChoices.agentStateVisibility.indexOf(String(value('agentStateVisibility'))) }))}
          onChange={definitionVisibility.onChange} />
      </Form.Item>}
    </div>
    <div className="admin-settings-field-grid">
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
  const { token } = theme.useToken();
  return <Form.Item extra={extra}>
    <Flex align="center" justify="space-between" gap={token.paddingSM} className="instance-settings-field-label">
      <label htmlFor={id}>{label}</label>
      <Switch id={id} aria-label={label} checked={checked} disabled={disabled} onChange={onChange} />
    </Flex>
  </Form.Item>;
}
