import { useEffect, useRef, useState } from "react";
import { Alert, App, Button, Empty, Flex, Select, Spin, Typography, theme } from "antd";
import {
  createEventSubscription,
  listEventSources,
  listEventSubscriptions,
  type AdminEventSource,
  type AdminEventSubscription
} from "../../services/adminApi";
import { describeAdminError } from "./adminErrors";

export function EventSubscriptionsSection({ instanceId, selection }: { instanceId: string; selection?: { registrationId: string; request: number } }) {
  const { token } = theme.useToken();
  const { message } = App.useApp();
  const [sources, setSources] = useState<AdminEventSource[]>([]);
  const [subscriptions, setSubscriptions] = useState<AdminEventSubscription[]>([]);
  const [sourceId, setSourceId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retryKey, setRetryKey] = useState(0);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError(null);
    void Promise.all([listEventSources(), listEventSubscriptions(instanceId)])
      .then(([nextSources, nextSubscriptions]) => {
        if (current) {
          setSources(nextSources);
          setSubscriptions(nextSubscriptions);
        }
      })
      .catch((reason: unknown) => {
        if (current) {
          setError(describeAdminError(reason, "Unable to load event subscriptions.").message);
        }
      })
      .finally(() => {
        if (current) {
          setLoading(false);
        }
      });
    return () => {
      current = false;
    };
  }, [instanceId, selection?.request, retryKey]);

  const focusedSelection = useRef<typeof selection>(undefined);
  useEffect(() => {
    if (!selection || loading || focusedSelection.current === selection) return;
    const frame = requestAnimationFrame(() => {
      const row = document.querySelector<HTMLElement>(`[data-event-registration-id="${selection.registrationId}"]`);
      row?.scrollIntoView({ block: "nearest" }); row?.focus({ preventScroll: true });
      if (row) focusedSelection.current = selection;
    });
    return () => cancelAnimationFrame(frame);
  }, [selection, loading, subscriptions]);

  async function subscribe() {
    if (!sourceId) {
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const created = await createEventSubscription(instanceId, sourceId);
      setSubscriptions((current) => [
        created,
        ...current.filter((item) => item.registrationId !== created.registrationId)
      ]);
      message.success("Subscribed to order.placed.");
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "This agent could not subscribe.").message);
    } finally {
      setBusy(false);
    }
  }

  const activeSources = sources.filter((item) => item.status === "Active");
  const sourceName = (id: string) => sources.find((item) => item.sourceId === id)?.displayName ?? id;

  return (
    <section className="admin-definition-panel" aria-label="Event subscriptions">
      <div className="admin-definition-panel-heading">
        <Typography.Title level={4}>Event subscriptions</Typography.Title>
        <Typography.Text type="secondary">
          Subscribe this agent to order.placed. A subscription does not connect the agent to the store.
        </Typography.Text>
      </div>
      <div className="admin-definition-panel-body">
        <Flex vertical gap={token.paddingSM}>
          {loading ? <Spin aria-label="Loading event subscriptions" /> : null}
          {selection && !loading && !error && !subscriptions.some(item => item.registrationId === selection.registrationId) ? <Alert type="info" showIcon title="This event subscription is no longer available" /> : null}
          {error ? <Alert type="error" showIcon title={error} action={<Button disabled={busy || loading} onClick={() => setRetryKey(value => value + 1)}>Retry subscriptions</Button>} /> : null}
          {!loading ? (
            <>
              {sources.length === 0 ? (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Create an event source on Admin home first." />
              ) : activeSources.length === 0 ? (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No active event source." />
              ) : (
                <Flex gap={token.paddingXS} wrap="wrap" align="center">
                  <Select
                    aria-label="Event source"
                    placeholder="Event source"
                    value={sourceId ?? undefined}
                    disabled={busy}
                    style={{ minWidth: 220 }}
                    options={activeSources.map((source) => ({
                      value: source.sourceId,
                      label: `${source.displayName} · ${source.status}`
                    }))}
                    onChange={(value) => setSourceId(value)}
                  />
                  <Button type="primary" disabled={busy || !sourceId} onClick={() => void subscribe()}>
                    Subscribe to order.placed
                  </Button>
                </Flex>
              )}
              {subscriptions.length === 0 ? (
                <Typography.Text type="secondary">No order.placed subscription yet.</Typography.Text>
              ) : (
                subscriptions.map((item) => (
                  <Flex key={item.registrationId} vertical data-event-registration-id={item.registrationId} tabIndex={-1} className={selection?.registrationId === item.registrationId ? "admin-selected-source" : undefined}>
                    <Typography.Text strong>{item.eventType}</Typography.Text>
                    <Typography.Text type="secondary">
                      {sourceName(item.sourceId)} · {item.status}
                    </Typography.Text>
                  </Flex>
                ))
              )}
            </>
          ) : null}
        </Flex>
      </div>
    </section>
  );
}
