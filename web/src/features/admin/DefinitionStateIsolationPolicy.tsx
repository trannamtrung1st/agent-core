import { Button, Flex, Form, Select, Switch, Typography, theme } from 'antd';
import type { DefinitionCandidate } from './definitionCandidate';
import { isolationChoiceLabels, isolationLabels } from './stateIsolationPresentation';

const defaults: Record<string, string | boolean> = { conversationContextScope: 'Session', homeAccess: 'ReadWrite', workingCleanup: 'OnSessionDeletion', browserIsolation: 'HostDefault',
  agentStateVisibility: 'Off', experienceRecord: true, experienceRecall: true, profileEvidence: true, crossSessionContinuity: true };
const choices: Record<string, string[]> = { conversationContextScope: ['Session', 'CurrentRequest'], homeAccess: ['ReadWrite', 'ReadOnly', 'Disabled'], workingCleanup: ['OnSessionDeletion', 'AfterTerminal'],
  browserIsolation: ['HostDefault', 'PersistentAgent', 'EphemeralSession'], agentStateVisibility: ['Off', 'CurrentSession', 'InstanceSummary'] };

export function DefinitionStateIsolationPolicy({ candidate, disabled = false, readOnly = false, onChange }: {
  candidate: DefinitionCandidate; disabled?: boolean; readOnly?: boolean; onChange?: (next: DefinitionCandidate) => void;
}) {
  const { token } = theme.useToken();
  const policy = candidate.stateIsolationPolicy && typeof candidate.stateIsolationPolicy === 'object' && !Array.isArray(candidate.stateIsolationPolicy)
    ? candidate.stateIsolationPolicy as Record<string, unknown> : null;
  function change(field: string, value: string | boolean) { onChange?.({ ...candidate, stateIsolationPolicy: { ...defaults, ...policy, [field]: value } }); }
  return <Flex vertical gap={token.padding}>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Instances may narrow home access, sharing, learning and visibility. State visibility also requires an explicitly authorized state-inspection capability. A persistent browser profile requires host support. Current-request continuity requires a fresh ordinary Session and text input.</Typography.Paragraph>
    {!policy && <Typography.Text type="secondary">No explicit policy. Existing continuity and host browser behavior are preserved; agent state awareness is off.</Typography.Text>}
    <Form layout="vertical" className="admin-config-form admin-settings-field-grid">
      {Object.entries(defaults).map(([field, fallback]) => {
        const value = policy?.[field] ?? fallback;
        return <Form.Item key={field} label={isolationLabels[field]}>
          {typeof fallback === 'boolean' ? <Switch aria-label={isolationLabels[field]} checked={value === true} disabled={disabled || readOnly} onChange={v => change(field, v)} /> :
            <Select aria-label={isolationLabels[field]} value={typeof value === 'string' ? value : String(value)} disabled={disabled || readOnly}
              options={choices[field].map(v => ({ value: v, label: isolationChoiceLabels[v] ?? v }))} onChange={v => change(field, v)} />}
        </Form.Item>;
      })}
    </Form>
    {!readOnly && <Button style={{ alignSelf: 'flex-start' }} disabled={disabled || !policy} onClick={() => onChange?.({ ...candidate, stateIsolationPolicy: null })}>Use compatibility defaults</Button>}
  </Flex>;
}
