import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Descriptions, Flex, Layout, List, Result, Spin, Tag, Typography } from "antd";
import {
  type AdminDefinitionInventoryItem,
  type AdminEffectiveConfiguration,
  type AdminInstanceInventoryItem,
  getAdminEffectiveConfig,
  listAdminDefinitions,
  listAdminInstances
} from "../../services/adminApi";
import {
  adminDefinitionPath,
  adminHomePath,
  adminInstancePath,
  lastChatUrl,
  navigateToAppPath,
  rememberChatUrl,
  type AdminRoute
} from "../../app/appRoute";

const { Header, Content } = Layout;

type LoadState<T> =
  | { kind: "loading" }
  | { kind: "error"; message: string }
  | { kind: "ready"; data: T };

export function AdminApp({ route }: { route: AdminRoute }) {
  const [definitions, setDefinitions] = useState<LoadState<AdminDefinitionInventoryItem[]>>({ kind: "loading" });
  const [instances, setInstances] = useState<LoadState<AdminInstanceInventoryItem[]>>({ kind: "loading" });
  const [effectiveConfig, setEffectiveConfig] = useState<LoadState<AdminEffectiveConfiguration>>({ kind: "loading" });

  const reloadInventory = useCallback(async () => {
    setDefinitions({ kind: "loading" });
    setInstances({ kind: "loading" });
    try {
      const [definitionItems, instanceItems] = await Promise.all([listAdminDefinitions(), listAdminInstances()]);
      setDefinitions({ kind: "ready", data: definitionItems });
      setInstances({ kind: "ready", data: instanceItems });
    } catch (error) {
      const message = error instanceof Error ? error.message : "Failed to load admin inventory.";
      setDefinitions({ kind: "error", message });
      setInstances({ kind: "error", message });
    }
  }, []);

  useEffect(() => {
    void reloadInventory();
  }, [reloadInventory]);

  useEffect(() => {
    if (route.view !== "instance") {
      setEffectiveConfig({ kind: "loading" });
      return;
    }

    let cancelled = false;
    setEffectiveConfig({ kind: "loading" });
    void getAdminEffectiveConfig(route.instanceId)
      .then((data) => {
        if (!cancelled) {
          setEffectiveConfig({ kind: "ready", data });
        }
      })
      .catch((error) => {
        if (!cancelled) {
          const message = error instanceof Error ? error.message : "Failed to load effective configuration.";
          setEffectiveConfig({ kind: "error", message });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [route]);

  const returnToChat = () => {
    navigateToAppPath(lastChatUrl(), true);
  };

  const openChat = () => {
    rememberChatUrl(window.location.pathname);
    navigateToAppPath("/", false);
  };

  return (
    <Layout className="admin-layout">
      <Header className="admin-header">
        <Flex align="center" justify="space-between" gap={12} wrap="wrap">
          <Flex align="center" gap={12}>
            <Typography.Title level={3} className="admin-title">
              Admin
            </Typography.Title>
            <Button type="link" onClick={openChat}>
              Chat
            </Button>
          </Flex>
          <Button onClick={returnToChat}>Return to last chat</Button>
        </Flex>
      </Header>
      <Content className="admin-content">
        {route.view === "home" ? (
          <Flex vertical gap={24}>
            <InventorySection
              title="Definitions"
              emptyLabel="No definitions found."
              loading={definitions.kind === "loading"}
              error={definitions.kind === "error" ? definitions.message : null}
              items={
                definitions.kind === "ready"
                  ? definitions.data.map((item) => ({
                      key: `${item.definitionId}:${item.version}`,
                      title: `${item.displayName} (${item.definitionId} v${item.version})`,
                      description: `Source: ${item.source} · Status: ${item.status}`,
                      onClick: () => navigateToAppPath(adminDefinitionPath(item.definitionId))
                    }))
                  : []
              }
            />
            <InventorySection
              title="Instances"
              emptyLabel="No instances yet. Start a chat to create compatibility instances."
              loading={instances.kind === "loading"}
              error={instances.kind === "error" ? instances.message : null}
              items={
                instances.kind === "ready"
                  ? instances.data.map((item) => ({
                      key: item.instanceId,
                      title: `${item.personaName} · ${item.definitionId} v${item.activeVersion}`,
                      description: item.compatibility ? "Compatibility / legacy instance" : "Managed instance",
                      tag: item.compatibility ? "Compatibility" : "Managed",
                      onClick: () => navigateToAppPath(adminInstancePath(item.instanceId))
                    }))
                  : []
              }
            />
          </Flex>
        ) : null}

        {route.view === "definition" ? (
          <DefinitionDetail
            definitionId={route.definitionId}
            definitions={definitions}
            onBack={() => navigateToAppPath(adminHomePath())}
          />
        ) : null}

        {route.view === "instance" ? (
          <InstanceDetail
            instanceId={route.instanceId}
            instances={instances}
            effective={effectiveConfig}
            onBack={() => navigateToAppPath(adminHomePath())}
          />
        ) : null}
      </Content>
    </Layout>
  );
}

function InventorySection({
  title,
  emptyLabel,
  loading,
  error,
  items
}: {
  title: string;
  emptyLabel: string;
  loading: boolean;
  error: string | null;
  items: Array<{ key: string; title: string; description: string; tag?: string; onClick: () => void }>;
}) {
  return (
    <section aria-label={title}>
      <Typography.Title level={4}>{title}</Typography.Title>
      {error ? <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} /> : null}
      {loading ? (
        <Spin aria-label={`Loading ${title}`} />
      ) : items.length === 0 ? (
        <Typography.Text type="secondary">{emptyLabel}</Typography.Text>
      ) : (
        <List
          dataSource={items}
          renderItem={(item) => (
            <List.Item>
              <List.Item.Meta
                title={
                  <Button type="link" onClick={item.onClick} style={{ padding: 0, height: "auto" }}>
                    {item.title}
                  </Button>
                }
                description={
                  <Flex gap={8} wrap="wrap" align="center">
                    <span>{item.description}</span>
                    {item.tag ? <Tag>{item.tag}</Tag> : null}
                  </Flex>
                }
              />
            </List.Item>
          )}
        />
      )}
    </section>
  );
}

function DefinitionDetail({
  definitionId,
  definitions,
  onBack
}: {
  definitionId: string;
  definitions: LoadState<AdminDefinitionInventoryItem[]>;
  onBack: () => void;
}) {
  const rows = definitions.kind === "ready"
    ? definitions.data.filter((item) => item.definitionId === definitionId)
    : [];

  return (
    <Flex vertical gap={16}>
      <Button onClick={onBack}>Back to inventory</Button>
      <Typography.Title level={4}>{definitionId}</Typography.Title>
      {definitions.kind === "loading" ? <Spin /> : null}
      {definitions.kind === "error" ? <Alert type="error" showIcon message={definitions.message} /> : null}
      {definitions.kind === "ready" && rows.length === 0 ? (
        <Result status="404" title="Definition not found" />
      ) : null}
      {rows.length > 0 ? (
        <Descriptions bordered size="small" column={1}>
          {rows.map((row) => (
            <Descriptions.Item key={`${row.definitionId}:${row.version}`} label={`Version ${row.version}`}>
              {row.displayName} · {row.source} · {row.status}
            </Descriptions.Item>
          ))}
        </Descriptions>
      ) : null}
    </Flex>
  );
}

function InstanceDetail({
  instanceId,
  instances,
  effective,
  onBack
}: {
  instanceId: string;
  instances: LoadState<AdminInstanceInventoryItem[]>;
  effective: LoadState<AdminEffectiveConfiguration>;
  onBack: () => void;
}) {
  const row = instances.kind === "ready"
    ? instances.data.find((item) => item.instanceId.toLowerCase() === instanceId.toLowerCase())
    : undefined;

  return (
    <Flex vertical gap={16}>
      <Button onClick={onBack}>Back to inventory</Button>
      <Typography.Title level={4}>Instance {instanceId}</Typography.Title>
      {row ? (
        <Flex gap={8} wrap="wrap">
          {row.compatibility ? <Tag color="gold">Compatibility / legacy</Tag> : <Tag color="blue">Managed</Tag>}
          <Tag>{row.lifecycle}</Tag>
        </Flex>
      ) : null}
      {effective.kind === "loading" ? <Spin aria-label="Loading effective configuration" /> : null}
      {effective.kind === "error" ? (
        <Alert type="error" showIcon message={effective.message} />
      ) : null}
      {effective.kind === "ready" ? (
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Definition">
            {effective.data.definitionId} v{effective.data.definitionVersion} ({effective.data.definitionSource})
          </Descriptions.Item>
          <Descriptions.Item label="Persona">
            {effective.data.persona.name} · {effective.data.persona.role}
          </Descriptions.Item>
          <Descriptions.Item label="Effective model">
            {effective.data.effectiveModel.displayName} ({effective.data.effectiveModel.catalogKey}) ·{" "}
            {effective.data.effectiveModel.selectionSource}
            {effective.data.effectiveModel.reasoningEffort
              ? ` · ${effective.data.effectiveModel.reasoningEffort}`
              : ""}
          </Descriptions.Item>
          <Descriptions.Item label="Provider alias">
            {effective.data.providerPreferences.languageModel}
          </Descriptions.Item>
          <Descriptions.Item label="Offered tools">
            {effective.data.effectiveToolAllowlist.length > 0
              ? effective.data.effectiveToolAllowlist.join(", ")
              : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Harness references">
            {effective.data.harnessReferences.length > 0 ? effective.data.harnessReferences.join(", ") : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Workspace template">
            {effective.data.workspaceTemplateId ?? "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Knowledge">
            {effective.data.knowledgeSources.length > 0
              ? effective.data.knowledgeSources.map((item) => item.identity).join(", ")
              : "None"}
          </Descriptions.Item>
          <Descriptions.Item label="Memory policy">
            Session {effective.data.memoryPolicy.sessionMemory ? "on" : "off"} · Identity retrieval{" "}
            {effective.data.memoryPolicy.identityUserRetrieval ? "on" : "off"} · User retrieval{" "}
            {effective.data.memoryPolicy.userRetrieval ? "on" : "off"}
          </Descriptions.Item>
          <Descriptions.Item label="Trigger policy">
            {effective.data.triggerPolicy?.enabled ? "Enabled" : "Disabled"}
            {effective.data.triggerPolicy
              ? ` · sources: ${effective.data.triggerPolicy.allowedSourceKinds.join(", ") || "none"}`
              : ""}
          </Descriptions.Item>
          <Descriptions.Item label="Durable work eligibility">
            {effective.data.durableExecutionEligibility.canAcceptNewTriggeredWork ? "Eligible" : "Not eligible"}
            {" · schedule "}
            {effective.data.durableExecutionEligibility.allowsScheduleSource ? "allowed" : "blocked"}
            {" · application events "}
            {effective.data.durableExecutionEligibility.allowsApplicationEventSource ? "allowed" : "blocked"}
          </Descriptions.Item>
        </Descriptions>
      ) : null}
    </Flex>
  );
}
