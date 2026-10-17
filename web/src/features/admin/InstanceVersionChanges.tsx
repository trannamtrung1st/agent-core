import { Flex, Typography } from 'antd';
import type { VersionPreview } from '../../services/instanceConfiguration';

const sections: Record<string, string> = { identity: 'Persona', instructions: 'Instructions', selfManagement: 'Self-management', executionBudgets: 'Execution budgets', modelDefaults: 'Model defaults', conversationPolicy: 'Conversation', behaviorPolicy: 'Behavior', initiativePolicy: 'Initiative', providerPreferences: 'Providers', stateIsolationPolicy: 'State & isolation', sandboxPolicy: 'Sandbox', memoryPolicy: 'Memory policy', triggerPolicy: 'Trigger restrictions' };
export const versionFieldLabel = (value: string) => value.split('.').map(part => sections[part] ?? (part.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ').replace(/^./, first => first.toUpperCase()))).join(' · ');

/** Version-change facts shared by tenant review and the existing administrative confirmation. */
export function InstanceVersionChanges({ preview }: { preview: VersionPreview }) {
  const groups = [
    ['Changed defaults', preview.changedDefinitionFields.map(versionFieldLabel)],
    ['Preserved Instance overrides', preview.preservedInstanceOverrides.map(versionFieldLabel)],
    ['New inherited items', preview.newInheritedItems], ['Changed inherited items', preview.changedInheritedItems ?? []], ['Removed inherited items', preview.removedInheritedItems]
  ] as const;
  return <Flex vertical gap="var(--ac-space-section)">
    <Typography.Text>{preview.activationNotice}</Typography.Text>
    {groups.map(([label, values]) => <Flex key={label} vertical gap="var(--ac-space-compact)"><Typography.Text strong>{label}</Typography.Text><Typography.Text className="admin-instance-id">{values.join(', ') || 'None'}</Typography.Text></Flex>)}
    <Typography.Text type="secondary">Compatible Skill enablement choices, local resources and class budget overrides are retained.</Typography.Text>
  </Flex>;
}
