import { Alert, Flex, Typography } from 'antd';
import type { SandboxVerification } from '../../services/sandboxes';
import { SandboxEvidence } from './SandboxEvidence';

/** Bounded saved verification facts, independent of profile or Instance command authority. */
export function SandboxVerificationDetails({ value, label = 'Profile verification' }: { value: SandboxVerification; label?: string }) {
  return <Flex vertical gap="var(--ac-space-compact)"><Alert showIcon type={value.passed ? 'success' : 'warning'} title={value.message} />
    <Typography.Text type="secondary">Profile revision {value.profileRevision}{value.instanceRevision != null ? ` · Instance revision ${value.instanceRevision}` : ''} · {new Date(value.verifiedAt).toLocaleString()} · {value.exitCode === null ? 'Exit unknown' : `Exit ${value.exitCode}`} · {(value.durationMs / 1000).toFixed(1)}s</Typography.Text>
    <SandboxEvidence stdout={value.stdout} stderr={value.stderr} truncated={value.truncated} label={label} />
  </Flex>;
}
