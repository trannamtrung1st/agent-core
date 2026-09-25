import { Alert, Button, Flex, Select, Spin, Typography } from "antd";
import type { AgentDescriptor, ChatAgentInstance } from "../../services/api";
import { legacyChatIdentityKey, managedChatIdentityKey, managedInstancePickerLabel } from "./chatIdentity";
import { SessionFailureAlert } from "./SessionFailureAlert";
import type { SessionErrorView } from "./sessionError";
import { SpeechLocalePicker } from "./SpeechLocalePicker";

export function AgentPicker({
  agents,
  managedInstances,
  identityKey,
  error,
  managedInstancesError,
  managedInstancesLoading,
  speechLocale,
  onIdentityChange,
  onSpeechLocaleChange,
  onRetryManagedInstances
}: {
  agents: AgentDescriptor[];
  managedInstances: ChatAgentInstance[];
  identityKey: string;
  error: SessionErrorView | string | null;
  managedInstancesError: string | null;
  managedInstancesLoading: boolean;
  speechLocale: string;
  onIdentityChange: (identityKey: string) => void;
  onSpeechLocaleChange: (locale: string | null) => void;
  onRetryManagedInstances: () => void;
}) {
  const hasOptions = managedInstances.length > 0 || agents.length > 0;
  const options = [
    ...(managedInstances.length > 0
      ? [
          {
            label: "Managed instances",
            options: managedInstances.map((item) => ({
              value: managedChatIdentityKey(item.instanceId),
              label: managedInstancePickerLabel(item)
            }))
          }
        ]
      : []),
    ...(agents.length > 0
      ? [
          {
            label: "Legacy compatibility",
            options: agents.map((agent) => ({
              value: legacyChatIdentityKey(agent.id),
              label: `${agent.name} — ${agent.role} (legacy)`
            }))
          }
        ]
      : [])
  ];

  return (
    <Flex vertical gap={8} className="agent-picker">
      {error ? <SessionFailureAlert error={error} /> : null}
      {managedInstancesError ? (
        <Alert
          type="warning"
          showIcon
          message="Managed instances could not be loaded."
          description={managedInstancesError}
          action={
            <Button size="small" aria-label="Retry managed instances" onClick={onRetryManagedInstances} loading={managedInstancesLoading}>
              Retry
            </Button>
          }
        />
      ) : null}
      <Typography.Text>Identity</Typography.Text>
      {managedInstancesLoading ? (
        <Flex align="center" gap={8} aria-label="Loading managed instances">
          <Spin size="small" />
          <Typography.Text type="secondary">Loading managed instances…</Typography.Text>
        </Flex>
      ) : null}
      <Select
        aria-label="Identity"
        value={!managedInstancesLoading && hasOptions && identityKey ? identityKey : undefined}
        placeholder={managedInstancesLoading ? "Loading managed instances…" : "Select identity"}
        disabled={managedInstancesLoading || !hasOptions}
        options={options}
        onChange={(nextKey) => onIdentityChange(nextKey)}
        style={{ width: "100%" }}
      />
      <SpeechLocalePicker value={speechLocale} onChange={onSpeechLocaleChange} />
    </Flex>
  );
}
