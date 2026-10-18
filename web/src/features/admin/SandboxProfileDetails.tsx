import { Descriptions, Typography } from 'antd';
import { sandboxSourceLabel, sandboxStatusLabel, type SandboxProfile } from '../../services/sandboxes';
import { useAdminDetailLayout } from './useAdminDetailLayout';

/** Shared saved profile facts; the enclosing owner renders its policy and actions. */
export function SandboxProfileDetails({ profile, instanceInitializationScript }: { profile: SandboxProfile; instanceInitializationScript?: string | null }) {
  const layout = useAdminDetailLayout();
  return <Descriptions {...layout} bordered size="small" column={1}>
    <Descriptions.Item label="Source">{sandboxSourceLabel(profile)} · {sandboxStatusLabel(profile)}</Descriptions.Item>
    <Descriptions.Item label="Immutable image"><Typography.Text code copyable style={{ overflowWrap: 'anywhere' }}>{profile.imageReference}</Typography.Text></Descriptions.Item>
    <Descriptions.Item label="Resources">{profile.cpu} CPU · {profile.memoryMiB} MiB · {profile.pids} PIDs</Descriptions.Item>
    <Descriptions.Item label="Command limit">{profile.maxCommandSeconds} seconds; setup scripts share this budget</Descriptions.Item>
    <Descriptions.Item label="Lifecycle">Idle {profile.idleTtlSeconds} seconds after completed work · maximum lifetime {profile.maxLifetimeSeconds} seconds</Descriptions.Item>
    <Descriptions.Item label="Session /working storage">{Math.round(profile.storageBytes / 1024 / 1024)} MiB · {profile.storageBytes.toLocaleString()} bytes · sampled budget. Written files remain after enforcement.</Descriptions.Item>
    <Descriptions.Item label="Workspace mounts">Private writable /working; shared /home is read/write, read-only or absent under State &amp; Isolation. Verification uses disposable roots; each Session checks its own mounts. Home and container-only storage have separate limits.</Descriptions.Item>
    <Descriptions.Item label="Profile initialization"><Typography.Paragraph code style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{profile.initializationScript || 'No Profile script'}</Typography.Paragraph></Descriptions.Item>
    {instanceInitializationScript !== undefined && <Descriptions.Item label="Instance initialization"><Typography.Paragraph code style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{instanceInitializationScript || 'No Instance script'}</Typography.Paragraph></Descriptions.Item>}
  </Descriptions>;
}
