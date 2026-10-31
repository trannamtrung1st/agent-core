import { Alert, Flex, Tag, Typography, theme } from 'antd';
type InstanceHeadingView = { agent: { managedKind?: string | null; instance: { persona: { name: string }; lifecycle: string; activeVersion: number } }; canUpdate: boolean };

export function TenantAgentHeading({ view, configuration = true, archivedDescription = 'Configuration remains readable. Restore this instance before editing.', readOnlyDescription = 'Changing this instance requires update access on this exact agent.' }: { view: InstanceHeadingView; readOnlyDescription?: string; configuration?: boolean; archivedDescription?: string }) {
  const { token } = theme.useToken();
  return <><Flex wrap align="center" gap="var(--ac-space-compact)" className="admin-draft-section-heading"><Typography.Title level={2} style={{ fontSize: token.fontSizeLG, margin: 0 }}>{view.agent.instance.persona.name}</Typography.Title><Tag>{view.agent.instance.lifecycle}</Tag>{view.agent.managedKind && <Tag>System-managed</Tag>}<Typography.Text type="secondary">Definition v{view.agent.instance.activeVersion}</Typography.Text></Flex>
    {view.agent.instance.lifecycle !== 'Active' ? <Alert type="info" showIcon title="Archived instance" description={archivedDescription} /> : configuration && !view.canUpdate && <Alert type="info" showIcon title="Read-only configuration" description={readOnlyDescription} />}</>;
}
