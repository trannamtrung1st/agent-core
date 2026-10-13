import { Button, Descriptions, Flex, Typography, theme } from 'antd';
import type { AdminEffectiveConfiguration } from '../../services/adminApi';
import { adminInstancePath } from '../../app/appRoute';
import { followAppLink } from '../../app/appLink';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { useAdminDetailLayout } from './useAdminDetailLayout';
import { instancePages } from './adminNavigation';
export function InstanceOverview({ config }: { config: AdminEffectiveConfiguration }) {
  const layout = useAdminDetailLayout(); const { token } = theme.useToken();
  return <AgentConfigurationPanel title="Saved configuration" label="Instance overview" bodyGap="section">
    <Typography.Text type="secondary">Host restrictions constrain Definition defaults and Instance overrides. Unsaved editor changes are excluded from this summary.</Typography.Text>
    <Descriptions {...layout} bordered size="small" column={1}>
      <Descriptions.Item label="Persona">{config.persona.name} · {config.persona.role}</Descriptions.Item>
      <Descriptions.Item label="Definition">{config.definitionId} · adopted v{config.definitionVersion} · {config.definitionSource}</Descriptions.Item>
      <Descriptions.Item label="Lifecycle">{config.instanceLifecycle}</Descriptions.Item>
      <Descriptions.Item label="Interactive model">{config.effectiveModel.displayName} · {config.effectiveModel.selectionSource} · reasoning {config.effectiveModel.reasoningEffort ?? 'None'}</Descriptions.Item>
      <Descriptions.Item label="Effective tools">{config.effectiveToolAllowlist.length} · constrained by Definition and Host</Descriptions.Item>
      <Descriptions.Item label="Workspace">Durable /home · temporary Session /working</Descriptions.Item>
    </Descriptions>
    <Flex vertical gap={token.paddingSM}>
      {[...new Set(instancePages.filter(page => page.id !== 'overview').map(page => page.group))].map(group => <Flex vertical gap={token.paddingXS} key={group}>
        <Typography.Text type="secondary">{group}</Typography.Text>
        <Flex wrap gap={token.paddingXS} className="admin-overview-destinations" role="group" aria-label={`${group} destinations`}>
          {instancePages.filter(page => page.group === group).map(page => <Button type="link" size="small" key={page.id} href={adminInstancePath(config.instanceId, page.id)} onClick={followAppLink} style={{ paddingInline: token.paddingXS }}>{page.label}</Button>)}
        </Flex>
      </Flex>)}
    </Flex>
  </AgentConfigurationPanel>;
}
