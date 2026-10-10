import { useEffect, useState } from "react";
import { Alert, Button, Empty, Flex, Select, Spin, Table, Typography } from "antd";
import { adminInstancePath, navigateToAppPath } from "../../app/appRoute";
import { instanceContinuityRequest, listAdminInstances, type AdminInstanceInventoryItem, type Automation, type AutomationReview } from "../../services/adminApi";

/** Owner-scoped activity is loaded separately from global, immutable definitions. */
export function BuiltinEventSubscribers({ eventKey, instanceId }: { eventKey: string; instanceId?: string }) {
  const [instances, setInstances] = useState<AdminInstanceInventoryItem[]>([]);
  const [selected, setSelected] = useState<string>();
  const ownerInstanceId = instanceId ?? selected;
  const [rows, setRows] = useState<Automation[]>([]);
  const [inventoryError, setInventoryError] = useState(false);
  const [inventoryLoading, setInventoryLoading] = useState(false);
  const [error, setError] = useState(false); const [loading, setLoading] = useState(false); const [retry, setRetry] = useState(0);
  useEffect(() => {
    setInventoryError(false);
    if (instanceId) { setInventoryLoading(false); return; } let active = true;
    setInventoryLoading(true);
    void listAdminInstances().then(items => { if (active) setInstances(items); }).catch(() => { if (active) setInventoryError(true); }).finally(() => { if (active) setInventoryLoading(false); });
    return () => { active = false; };
  }, [instanceId, retry]);
  useEffect(() => {
    setError(false); setRows([]);
    if (!ownerInstanceId) { setLoading(false); return; } let active = true; setLoading(true);
    void instanceContinuityRequest<AutomationReview>(ownerInstanceId, "automations").then(review => {
      if (active) setRows(review.items.filter(a => a.triggers.some(t => t.source?.kind === "builtin" && t.source.key === eventKey)));
    }).catch(() => { if (active) setError(true); }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [ownerInstanceId, eventKey, retry]);
  return <Flex vertical gap="small">
    <Typography.Title level={5} style={{ margin: 0 }}>Subscribed Automations</Typography.Title>
    {!instanceId ? <Select aria-label="Subscriber Agent Instance" placeholder="Choose an owned Instance" value={selected} loading={inventoryLoading} showSearch optionFilterProp="label" options={instances.map(i => ({ value: i.instanceId, label: `${i.personaName} · ${i.instanceId}` }))} onChange={setSelected} /> : null}
    {inventoryError ? <Alert type="error" showIcon title="Owned Instances could not be loaded. Definition details remain available." action={<Button onClick={() => setRetry(v => v + 1)}>Retry Instances</Button>} /> : null}
    {error ? <Alert type="error" showIcon title="Subscribers could not be loaded. Definition details remain available." action={<Button onClick={() => setRetry(v => v + 1)}>Retry subscribers</Button>} /> : null}
    {loading ? <Spin aria-label="Loading subscribers" /> : ownerInstanceId && !error ? rows.length ? <Table size="small" rowKey="automationId" pagination={false} dataSource={rows} columns={[
      { title: "Automation", render: (_, a: Automation) => <Button type="link" onClick={() => navigateToAppPath(`${adminInstancePath(ownerInstanceId, "automation", "automations")}?automation=${a.automationId}`)}>{a.name}</Button> },
      { title: "State", render: (_, a: Automation) => a.enabled && a.triggers.some(t => t.enabled && t.source?.kind === "builtin" && t.source.key === eventKey) ? "Enabled · subject to current policy" : "Disabled" }
    ]} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No subscriptions on this Instance." /> : <Typography.Text type="secondary">Choose an Instance to inspect its subscriptions.</Typography.Text>}
  </Flex>;
}
