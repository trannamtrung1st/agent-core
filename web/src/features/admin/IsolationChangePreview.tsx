import type { ReactNode } from 'react';
import { Alert, Descriptions, Spin, Typography } from 'antd';
import type { IsolationPlan } from '../../services/stateIsolation';
import type { SettingValue } from '../../services/instanceConfiguration';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { useAdminDetailLayout } from './useAdminDetailLayout';
import { isolationChoiceLabels, isolationLabels, isolationOwnerLabels } from './stateIsolationPresentation';

const display = (value: SettingValue) => typeof value === 'boolean' ? value ? 'On' : 'Off' : value == null ? 'None' : isolationChoiceLabels[String(value)] ?? String(value);
export function IsolationChangePreview({ changes, notice, blockingReasons = [], busy, children }: {
  changes?: IsolationPlan['changes']; notice?: string; blockingReasons?: string[]; busy?: boolean; children?: ReactNode;
}) {
  const detailLayout = useAdminDetailLayout();
  return <AgentConfigurationPanel title="Change preview" label="Isolation change preview" bodyGap="default">
    {busy && <Spin aria-label="Preparing isolation preview" />}
    {blockingReasons.map(reason => <Alert key={reason} type="warning" showIcon title={reason} />)}
    {changes && <><Typography.Paragraph type="secondary" style={{ margin: 0 }}>{notice}</Typography.Paragraph>
      {changes.length ? <Descriptions {...detailLayout} column={1} size="small" items={changes.map(change => ({ key: `${change.owner}:${change.field}`,
        label: `${isolationOwnerLabels[change.owner] ?? 'Configuration'} · ${isolationLabels[change.owner === 'experience' && change.field === 'enabled' ? 'experienceEnabled' : change.owner === 'identityMaintenance' && change.field === 'allowAgentConsolidation' ? 'allowMaintenance' : change.field] ?? change.field}`,
        children: `${display(change.before)} → ${display(change.after)} · ${change.source === 'instance' ? 'Instance override' : change.source === 'draft' ? 'Definition draft' : 'Inherited Definition'}` }))} />
        : <Typography.Text>{blockingReasons.length ? 'Resolve the policy restrictions to calculate the complete change preview.' : 'No configuration changes.'}</Typography.Text>}</>}
    {children}
  </AgentConfigurationPanel>;
}
