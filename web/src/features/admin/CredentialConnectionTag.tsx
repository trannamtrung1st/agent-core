import { Tag } from 'antd';
import {
  CheckCircleOutlined,
  ExclamationCircleOutlined,
  MinusCircleOutlined,
} from '@ant-design/icons';
import type { SystemCredential } from '../../services/adminApi';
export function CredentialConnectionTag({
  account,
}: {
  account: SystemCredential;
}) {
  const p = account.driveAccount ?? account.emailAccount;
  if (!p) return null;
  const connected = p.connectionStatus === 'Connected';
  const status =
    account.status === 'Disabled'
      ? 'Disabled'
      : account.emailAccount?.preset === 'Custom'
        ? 'Configured'
        : connected
          ? p.synthetic
            ? 'Synthetic'
            : 'Connected'
          : p.connectionStatus === 'ReconnectRequired'
            ? 'Reconnect required'
            : p.connectionStatus === 'Disconnected'
              ? 'Disconnected'
              : 'Not connected';
  return (
    <Tag
      color={
        account.status === 'Disabled'
          ? 'default'
          : connected
            ? 'success'
            : p.connectionStatus === 'ReconnectRequired'
              ? 'warning'
              : 'default'
      }
      icon={
        connected ? (
          <CheckCircleOutlined />
        ) : p.connectionStatus === 'ReconnectRequired' ? (
          <ExclamationCircleOutlined />
        ) : (
          <MinusCircleOutlined />
        )
      }
    >
      {status}
    </Tag>
  );
}
