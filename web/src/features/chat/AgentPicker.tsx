import { tenantChatEmptyReason, tenantChatCanManageSetup, tenantChatSetupPending, retryTenantGeneralAssistantSetup, chatTenantId } from '../../services/tenantChat';
import { tenantAccessPath, tenantModelsPath } from '../access/accessRoute';
import { followAppLink } from '../../app/appLink';
import { Alert, Button, Flex, Select, Spin, Typography } from "antd";
import { useEffect, useRef, useState, type ComponentRef } from 'react';
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
  const picker = useRef<ComponentRef<typeof Select>>(null);
  const retryInFlight = useRef(false); const restoreFocus = useRef(false);
  const [retryingSetup, setRetryingSetup] = useState(false);
  const [setupError, setSetupError] = useState<string | null>(null);
  useEffect(() => () => clearTimeout(searchTimer.current), []);
  const empty = tenantChatEmptyReason();
  const emptyText = empty === 'no_visible_agents' ? 'No visible agents. Ask an organization owner to make an agent visible in Chat.'
    : empty === 'setup_pending' ? 'Default assistant setup pending. An organization owner can retry setup or check Agent Management and model setup.'
    : empty === 'no_chat_permission' ? 'No Chat permission. Ask an organization owner for access to an agent.'
    : 'No matching agents. Try another search or reload the list.';
  const hasOptions = managedInstances.length > 0;
  const selected = managedInstances.find(item => managedChatIdentityKey(item.instanceId) === identityKey);
  const pendingSetup = tenantChatSetupPending() || empty === 'setup_pending';
  const needsSetup = pendingSetup || selected?.setupState === 'upgrade_requires_attention';
  const options = managedInstances.map(item => ({ value: managedChatIdentityKey(item.instanceId), label: managedInstancePickerLabel(item) }));
  useEffect(() => {
    if (restoreFocus.current && !managedInstancesLoading && hasOptions) { restoreFocus.current = false; picker.current?.focus(); }
  }, [managedInstancesLoading, hasOptions]);
  async function retrySetup() {
    if (retryInFlight.current) return;
    retryInFlight.current = true; setRetryingSetup(true); setSetupError(null);
    try {
      const result = await retryTenantGeneralAssistantSetup();
      if (result.status !== 'ready') setSetupError('Assistant setup is still pending. Check the organization configuration before retrying.');
      restoreFocus.current = result.status === 'ready'; onRetryManagedInstances();
    } catch { setSetupError('Setup retry could not be confirmed. Reload agents to inspect the current state before retrying.'); }
    finally { retryInFlight.current = false; setRetryingSetup(false); }
  }

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
      {selected?.setupState === 'upgrade_requires_attention' && <Alert type="warning" showIcon title="Upgrade requires attention" description="An organization owner must resolve the saved configuration before this system-managed agent can start new work." />}
      {selected?.setupState === 'no_model_configured' && <Alert type="info" showIcon title="No model configured" description="An organization owner must configure an available model before starting Chat." />}
      {pendingSetup && hasOptions && <Alert type="info" showIcon title="Default assistant setup pending" description="An organization owner can retry setup. Other available agents remain usable." />}
      {setupError && needsSetup && <Alert type="warning" showIcon title="Assistant setup needs attention" description={setupError} />}
      {tenantChatCanManageSetup() && chatTenantId && (!hasOptions || needsSetup || selected?.setupState) && <Flex wrap align="center" gap="var(--ac-space-default)">
        {needsSetup && <Button loading={retryingSetup} disabled={managedInstancesLoading} onClick={() => void retrySetup()}>Retry assistant setup</Button>}
        <Typography.Link href={tenantAccessPath(chatTenantId, 'agents')} onClick={followAppLink}>Agent Management</Typography.Link><Typography.Link href={tenantModelsPath(chatTenantId, 'models')} onClick={followAppLink}>Models</Typography.Link></Flex>}
      <Typography.Text>Agent</Typography.Text>
      {managedInstancesLoading ? (
        <Flex align="center" gap={8} aria-label="Loading managed instances">
          <Spin size="small" />
          <Typography.Text type="secondary">Loading agents…</Typography.Text>
        </Flex>
      ) : null}
      <Select
        ref={picker}
        aria-label="Agent"
        value={!managedInstancesLoading && hasOptions && identityKey ? identityKey : undefined}
        placeholder={managedInstancesLoading ? "Loading agents…" : "Choose an agent"}
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
        <Typography.Text type="secondary">{emptyText}</Typography.Text>
      ) : null}
      {hasMore && <Button onClick={onLoadMore} loading={managedInstancesLoading}>More agents</Button>}
    </Flex>
  );
}
