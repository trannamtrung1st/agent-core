import { Alert, Button, Flex, Select, Spin, Typography } from "antd";
import type { ChatAgentInstance } from "../../services/api";
import { managedChatIdentityKey, managedInstancePickerLabel } from "./chatIdentity";
import { SessionFailureAlert } from "./SessionFailureAlert";
import type { SessionErrorView } from "./sessionError";
import { SpeechLocalePicker } from "./SpeechLocalePicker";

export function AgentPicker({
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
  const hasOptions = managedInstances.length > 0;
  const options = managedInstances.map(item => ({ value: managedChatIdentityKey(item.instanceId), label: managedInstancePickerLabel(item) }));

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
        showSearch
        optionFilterProp="label"
        onChange={(nextKey) => onIdentityChange(nextKey)}
        style={{ width: "100%" }}
      />
      {!managedInstancesLoading && !managedInstancesError && !hasOptions ? (
        <Typography.Text type="secondary">Create an instance in Admin to chat.</Typography.Text>
      ) : null}
      <SpeechLocalePicker value={speechLocale} onChange={onSpeechLocaleChange} />
    </Flex>
  );
}
