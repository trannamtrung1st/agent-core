import { Alert, Flex, Tag, Typography } from 'antd';
type InstanceHeadingView = { agent: { instance: { persona: { name: string }; lifecycle: string; activeVersion: number } }; canUpdate: boolean };

export function TenantAgentHeading({ view, configuration = true, archivedDescription = 'Configuration remains readable. Restore this instance before editing.', readOnlyDescription = 'Changing this instance requires update access on this exact agent.' }: { view: InstanceHeadingView; readOnlyDescription?: string; configuration?: boolean; archivedDescription?: string }) {
  return <><Flex wrap align="center" gap="var(--ac-space-compact)" className="admin-draft-section-heading"><Typography.Title level={4}>{view.agent.instance.persona.name}</Typography.Title><Tag>{view.agent.instance.lifecycle}</Tag><Typography.Text type="secondary">Definition v{view.agent.instance.activeVersion}</Typography.Text></Flex>
    {view.agent.instance.lifecycle !== 'Active' ? <Alert type="info" showIcon title="Archived instance" description={archivedDescription} /> : configuration && !view.canUpdate && <Alert type="info" showIcon title="Read-only configuration" description={readOnlyDescription} />}</>;
}
