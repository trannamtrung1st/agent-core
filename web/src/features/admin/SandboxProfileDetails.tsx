import { Descriptions, Typography } from 'antd';
import type { SandboxProfile } from '../../services/sandboxes';
import { useAdminDetailLayout } from './useAdminDetailLayout';

/** Shared Global profile facts; the enclosing owner renders its policy and actions. */
export function SandboxProfileDetails({ profile, instanceInitializationScript }: { profile: SandboxProfile; instanceInitializationScript?: string | null }) {
  const layout = useAdminDetailLayout();
  return <Descriptions {...layout} bordered size="small" column={1}>
    <Descriptions.Item label="Immutable image"><Typography.Text code style={{ overflowWrap: 'anywhere' }}>{profile.imageReference}</Typography.Text></Descriptions.Item>
    <Descriptions.Item label="Resources">{profile.cpu} CPU · {profile.memoryMiB} MiB · {profile.pids} PIDs</Descriptions.Item>
    <Descriptions.Item label="Command limit">{profile.maxCommandSeconds} seconds; setup scripts share this budget</Descriptions.Item>
    <Descriptions.Item label="Lifecycle">Idle {profile.idleTtlSeconds} seconds after completed work · maximum lifetime {profile.maxLifetimeSeconds} seconds</Descriptions.Item>
    <Descriptions.Item label="Canonical storage">{Math.round(profile.storageBytes / 1024 / 1024)} MiB · {profile.storageBytes.toLocaleString()} bytes</Descriptions.Item>
    <Descriptions.Item label="Global initialization"><Typography.Paragraph code style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{profile.initializationScript || 'No Profile script'}</Typography.Paragraph></Descriptions.Item>
    {instanceInitializationScript !== undefined && <Descriptions.Item label="Instance initialization"><Typography.Paragraph code style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{instanceInitializationScript || 'No Instance script'}</Typography.Paragraph></Descriptions.Item>}
  </Descriptions>;
}
