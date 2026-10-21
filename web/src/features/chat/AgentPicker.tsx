import { Alert, Button, Flex, Select, Spin, Typography } from "antd";
import { useEffect, useRef } from 'react';
import type { ChatAgentInstance } from "../../services/api";
import { managedChatIdentityKey, managedInstancePickerLabel } from "./chatIdentity";
import type { SessionErrorView } from "./sessionError";
import { SessionFailureAlert } from "./SessionFailureAlert";

export function AgentPicker({
  managedInstances, onSearch, onLoadMore, hasMore = false,
  identityKey,
  error,
  managedInstancesError,
  managedInstancesLoading,
  onIdentityChange,
  onRetryManagedInstances
}: {
  managedInstances: ChatAgentInstance[];
  onSearch?: (query: string) => void; onLoadMore?: () => void; hasMore?: boolean;
  identityKey: string;
  error: SessionErrorView | string | null;
  managedInstancesError: string | null;
  managedInstancesLoading: boolean;
  onIdentityChange: (identityKey: string) => void;
  onRetryManagedInstances: () => void;
}) {
  const searchTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(searchTimer.current), []);
  const hasOptions = managedInstances.length > 0;
  const options = managedInstances.map(item => ({ value: managedChatIdentityKey(item.instanceId), label: managedInstancePickerLabel(item) }));

  return (
    <Flex vertical gap={8} className="agent-picker">
      {error ? <SessionFailureAlert error={error} /> : null}
      {managedInstancesError ? (
        <Alert
          type="warning"
          showIcon
          message="Agents could not be loaded."
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
        disabled={!onSearch && (managedInstancesLoading || !hasOptions)}
        loading={managedInstancesLoading}
        filterOption={onSearch ? false : undefined}
        onSearch={onSearch ? query => { clearTimeout(searchTimer.current); searchTimer.current = setTimeout(() => onSearch(query), 250); } : undefined}
        options={options}
        showSearch
        optionFilterProp="label"
        onChange={(nextKey) => onIdentityChange(nextKey)}
        style={{ width: "100%" }}
      />
      {!managedInstancesLoading && !managedInstancesError && !hasOptions ? (
        <Typography.Text type="secondary">No agents are available. Try another search or ask an organization owner for Chat access.</Typography.Text>
      ) : null}
      {hasMore && <Button onClick={onLoadMore} loading={managedInstancesLoading}>More agents</Button>}
    </Flex>
  );
}
