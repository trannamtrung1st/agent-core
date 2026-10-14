import { useEffect, useState } from 'react';
import { Alert, Descriptions, Flex, Spin, Typography, theme } from 'antd';
import { getInstanceSandbox } from '../../services/sandboxes';
import { useAdminDetailLayout } from './useAdminDetailLayout';

export function InstanceSandboxSummary({ instanceId }: { instanceId: string }) {
  const { token } = theme.useToken(); const layout = useAdminDetailLayout();
  const [value, setValue] = useState<Awaited<ReturnType<typeof getInstanceSandbox>> | null>(null); const [error, setError] = useState(false);
  useEffect(() => { setValue(null); setError(false); const controller = new AbortController(); getInstanceSandbox(instanceId, controller.signal).then(next => { if (!controller.signal.aborted) setValue(next); }).catch(() => { if (!controller.signal.aborted) setError(true); }); return () => controller.abort(); }, [instanceId]);
  return <section className="admin-effective-config-section admin-effective-config-wide" aria-label="Effective sandbox configuration">
    <Flex vertical gap={token.paddingXS}><Typography.Title level={5}>Sandbox environment</Typography.Title>
      {error ? <Alert showIcon type="warning" title="Sandbox configuration unavailable. Check Platform → Sandboxes, then refresh this page." /> : !value ? <Spin aria-label="Loading effective sandbox" /> :
        <Descriptions {...layout} bordered size="small" column={1}>
          <Descriptions.Item label="Readiness">{value.state} · {value.message}</Descriptions.Item>
          <Descriptions.Item label="Selected profile">{value.profile ? `${value.profile.name} · revision ${value.profile.revision}` : 'No configured profile'}</Descriptions.Item>
          <Descriptions.Item label="Allowed profiles">{value.policy?.allowedProfileIds.join(', ') || 'Historical bounded sandbox.run compatibility only'}</Descriptions.Item>
          <Descriptions.Item label="Activation">Next new Run pins configuration. A live Session generation keeps its concrete image and script revision until reset.</Descriptions.Item>
        </Descriptions>}
    </Flex>
  </section>;
}
