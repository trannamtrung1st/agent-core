import { useAdminDetailLayout } from "./useAdminDetailLayout";
import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, App, Button, Descriptions, Flex, Select, Table, Tabs, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import type { ColumnsType } from "antd/es/table";
import { listModels, type ModelDescriptor } from "../../services/api";
import {
  type AdminAutomationRegistration,
  type AdminEffectiveConfiguration,
  type AdminLearnedMemoryItem,
  type AdminLearnedMemoryScope,
  cancelAdminAutomationRegistration,
  deleteAdminLearnedMemory,
  getAdminLearnedMemory,
  listAdminAutomationRegistrations,
  listAdminLearnedMemory,
  resetAdminLearnedMemoryScope,
  setAdminRegistrationModel,
  setAdminUnattendedModel
} from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminErrorNotice, showAdminFailure } from "./adminFailure";
import { adminCollectionPagination } from "./AdminCollectionToolbar";
import {
  formatAutomationNextRun,
  isMemoryScopePermitted,
  isMemorySelectionReady,
  memoryResetConfirmTitle
} from "./instanceMemoryAutomationLogic";

import { ExecutionModelFields } from "./ExecutionModelFields";
import { AdminSessionPicker } from "./AdminSessionPicker";

export function MemoryLineageDetails({ instanceId, row, scope, sessionId }: { instanceId: string; row: AdminLearnedMemoryItem; scope: AdminLearnedMemoryScope; sessionId?: string }) {
  const { token } = theme.useToken();
  const layout = useAdminDetailLayout();
  const [sources, setSources] = useState<AdminLearnedMemoryItem[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const parents = row.provenance.derivedFromMemoryIds ?? [];
  async function inspect() {
    setBusy(true); setError(null);
    try { setSources(await Promise.all(parents.slice(0, 8).map(id => getAdminLearnedMemory(instanceId, id, scope, sessionId)))); }
    catch (reason) { setError(describeAdminError(reason, "Unable to inspect sources. Try again.")); }
    finally { setBusy(false); }
  }
  return <Flex vertical gap={token.padding} role="region" aria-label="Learned memory details">
    <Descriptions bordered column={1} size="small" {...layout} items={[
      { key: "state", label: "State", children: <Tag>{row.status ?? "Active"}</Tag> },
      { key: "id", label: "Memory ID", children: row.memoryId },
      { key: "subject", label: "Subject", children: row.subject || "Removed" },
      { key: "content", label: "Content", children: row.content || "Removed" },
      ...(parents.length ? [
        { key: "lineage", label: `Consolidated from ${parents.length} memories`, children: <ul style={{ margin: 0, paddingInlineStart: token.padding }}>{parents.map(id => <li key={id}>{id}</li>)}</ul> },
        { key: "origin", label: "Maintenance origin", children: row.provenance.maintenanceOrigin ?? "Not recorded" }
      ] : [])
    ]} />
    {parents.length ? <Button style={{ alignSelf: "flex-start" }} loading={busy} onClick={() => void inspect()}>{error ? "Retry sources" : "View sources"}</Button> : null}
    {error ? <AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} tone="danger" /> : null}
    {sources?.map(source => <Descriptions key={source.memoryId} title={source.subject || "Forgotten learned memory"} bordered column={1} size="small" {...layout} items={[
      { key: "id", label: "Source ID", children: source.memoryId },
      { key: "state", label: "State", children: <Tag>{source.status ?? "Active"}</Tag> },
      { key: "content", label: "Content", children: source.content || "Removed from learned-memory retrieval" }
    ]} />)}
  </Flex>;
}

const MEMORY_SCOPES: AdminLearnedMemoryScope[] = ["Session", "IdentityUser", "User"];

function modelOptions(models: ModelDescriptor[]) {
  return [
    { value: "", label: "Use conversation or unattended default" },
    ...models.map((model) => ({ value: model.key, label: model.displayName }))
  ];
}

function UnattendedModelForm({
  config,
  models
}: {
  config: AdminEffectiveConfiguration;
  models: ModelDescriptor[];
}) {
  const { token } = theme.useToken();
  const [catalogKey, setCatalogKey] = useState(config.unattendedModelCatalogKey ?? "");
  const [effort, setEffort] = useState(config.unattendedReasoningEffort ?? "");
  const [revision, setRevision] = useState(config.instanceRevision);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setRevision(config.instanceRevision);
  }, [config.instanceRevision]);
  const selected = models.find((model) => model.key === catalogKey);
  const source = catalogKey
    ? `Effective source: Unattended default (${selected?.displayName ?? catalogKey})`
    : `Effective source: Conversation default (${config.effectiveModel.displayName})`;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const updated = await setAdminUnattendedModel(
        config.instanceId,
        revision,
        catalogKey || null,
        effort || null
      );
      setRevision(updated.revision);
      setCatalogKey(updated.unattendedModelCatalogKey ?? "");
      setEffort(updated.unattendedReasoningEffort ?? "");
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "The unattended model could not be saved.").message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Flex vertical gap={token.paddingSM} aria-label="Unattended model" className="admin-config-form">
      <Typography.Text>{source}</Typography.Text>
      <Typography.Text type="secondary">
        Conversation default is {config.effectiveModel.displayName}. An unattended default applies to scheduled and
        reactive work unless a registration sets its own model.
      </Typography.Text>
      {error ? <Alert type="error" showIcon title={error} /> : null}
      <ExecutionModelFields models={models} modelKey={catalogKey} reasoningEffort={effort} disabled={busy} showLabels
        modelLabel="Unattended model" effortLabel="Unattended reasoning effort" defaultLabel="Conversation default"
        onChange={(key, effort) => { setCatalogKey(key); setEffort(effort); }} />
      <Flex wrap gap={token.paddingXS}>
        <Button type="primary" loading={busy} disabled={busy} onClick={() => void save()}>
          Save unattended model
        </Button>
      </Flex>
    </Flex>
  );
}

function RegistrationModelControl({
  instanceId,
  row,
  models,
  onSaved
}: {
  instanceId: string;
  row: AdminAutomationRegistration;
  models: ModelDescriptor[];
  onSaved: (updated: AdminAutomationRegistration) => void;
}) {
  const [catalogKey, setCatalogKey] = useState(row.modelOverrideCatalogKey ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      onSaved(await setAdminRegistrationModel(instanceId, row.registrationId, row.revision, catalogKey || null, null));
    } catch (reason: unknown) {
      setError(describeAdminError(reason, "The registration model could not be saved.").message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Flex vertical gap={8}>
      <Flex align="center" gap={8}>
        <Select
          aria-label={`Model for ${row.intent}`}
          value={catalogKey}
          disabled={busy || row.status !== "active"}
          style={{ width: 180 }}
          options={modelOptions(models)}
          optionRender={(option) => option.label}
          onChange={setCatalogKey}
        />
        {row.status === "active" ? (
          <Button size="small" disabled={busy} aria-label={`Save model for ${row.intent}`} onClick={() => void save()}>
            Save model
          </Button>
        ) : null}
      </Flex>
      {error ? <Alert type="error" showIcon title={error} /> : null}
    </Flex>
  );
}

function memoryPolicySummary(config: AdminEffectiveConfiguration): string {
  const p = config.memoryPolicy;
  const flags = [
    p.sessionMemory && "session (current instance version)",
    p.identityUserRetrieval && "identity user",
    p.userRetrieval && "user-wide"
  ].filter(Boolean);
  return flags.length > 0 ? flags.join(", ") : "disabled";
}

function triggerPolicySummary(config: AdminEffectiveConfiguration): string {
  const p = config.triggerPolicy;
  if (!p?.enabled) {
    return "disabled";
  }
  return `enabled · ${p.allowedSourceKinds.join(", ")}`;
}

export function InstanceMemoryAutomationPanel({ config, section }: { config: AdminEffectiveConfiguration; section?: "memory" | "automation" }) {
  const { message, modal } = App.useApp();
  const { token } = theme.useToken();
  const detailLayout = useAdminDetailLayout();
  const [memoryScope, setMemoryScope] = useState<AdminLearnedMemoryScope>("IdentityUser");
  const [sessionId, setSessionId] = useState("");
  const [memoryItems, setMemoryItems] = useState<AdminLearnedMemoryItem[] | null>(null);
  const [memoryError, setMemoryError] = useState<AdminFailureNotice | null>(null);
  const [memoryLoading, setMemoryLoading] = useState(false);
  const [memoryMutating, setMemoryMutating] = useState(false);
  const [automationItems, setAutomationItems] = useState<AdminAutomationRegistration[] | null>(null);
  const [automationError, setAutomationError] = useState<AdminFailureNotice | null>(null);
  const [automationBusy, setAutomationBusy] = useState(false);
  const [models, setModels] = useState<ModelDescriptor[]>([]);
  const memoryLoadGenRef = useRef(0);

  useEffect(() => {
    let current = true;
    void listModels()
      .then((catalog) => {
        if (current) {
          setModels(catalog.models);
        }
      })
      .catch(() => {
        if (current) {
          setModels([]);
        }
      });
    return () => {
      current = false;
    };
  }, []);

  const scopePermitted = isMemoryScopePermitted(config.memoryPolicy, memoryScope);
  const selectionReady = isMemorySelectionReady(memoryScope, sessionId);
  const selectionLocked = memoryMutating;
  const memoryActionsEnabled =
    scopePermitted && selectionReady && !memoryMutating && !memoryLoading;

  const invalidateActiveMemoryLoads = () => {
    memoryLoadGenRef.current += 1;
    setMemoryLoading(false);
  };

  const bumpMemoryLoadGeneration = () => {
    invalidateActiveMemoryLoads();
    setMemoryItems(null);
    setMemoryError(null);
  };

  const fetchMemoryItems = useCallback(
    async (scope: AdminLearnedMemoryScope, session: string, gen: number) => {
      const items = await listAdminLearnedMemory(
        config.instanceId,
        scope,
        scope === "Session" ? session : undefined
      );
      if (gen !== memoryLoadGenRef.current) {
        return;
      }
      setMemoryItems(items);
      setMemoryError(null);
    },
    [config.instanceId]
  );

  const loadMemory = useCallback(async () => {
    if (!scopePermitted) {
      setMemoryError({ message: "Effective memory policy does not allow this scope." });
      return;
    }
    if (!selectionReady) {
      setMemoryError({ message: "Session scope requires a session id." });
      return;
    }
    const gen = ++memoryLoadGenRef.current;
    const session = sessionId.trim();
    setMemoryLoading(true);
    setMemoryError(null);
    try {
      await fetchMemoryItems(memoryScope, session, gen);
    } catch (error) {
      if (gen !== memoryLoadGenRef.current) {
        return;
      }
      setMemoryItems([]);
      setMemoryError(describeAdminError(error, "Unable to load learned memory."));
    } finally {
      if (gen === memoryLoadGenRef.current) {
        setMemoryLoading(false);
      }
    }
  }, [fetchMemoryItems, memoryScope, scopePermitted, selectionReady, sessionId]);

  const loadAutomation = useCallback(async () => {
    setAutomationBusy(true);
    setAutomationError(null);
    try {
      const items = await listAdminAutomationRegistrations(config.instanceId);
      setAutomationItems(items);
    } catch (error) {
      setAutomationItems([]);
      setAutomationError(describeAdminError(error, "Unable to load automation."));
    } finally {
      setAutomationBusy(false);
    }
  }, [config.instanceId]);

  const deleteMemoryItem = async (memoryId: string) => {
    if (!memoryActionsEnabled) {
      return;
    }
    const scopeAtStart = memoryScope;
    const sessionAtStart = sessionId.trim();
    invalidateActiveMemoryLoads();
    const refreshGen = memoryLoadGenRef.current;
    setMemoryMutating(true);
    try {
      await deleteAdminLearnedMemory(
        config.instanceId,
        memoryId,
        scopeAtStart,
        scopeAtStart === "Session" ? sessionAtStart : undefined
      );
      message.success("Learned-memory item deleted.");
      if (refreshGen === memoryLoadGenRef.current) {
        try {
          await fetchMemoryItems(scopeAtStart, sessionAtStart, refreshGen);
        } catch (error) {
          setMemoryItems([]);
          setMemoryError(describeAdminError(error, "Unable to reload learned memory."));
        }
      }
    } catch (error) {
      showAdminFailure(message, error, "Delete failed.");
    } finally {
      setMemoryMutating(false);
    }
  };

  const resetMemoryScope = async () => {
    if (!memoryActionsEnabled) {
      return;
    }
    const scopeAtStart = memoryScope;
    const sessionAtStart = sessionId.trim();
    invalidateActiveMemoryLoads();
    const refreshGen = memoryLoadGenRef.current;
    setMemoryMutating(true);
    try {
      const removed = await resetAdminLearnedMemoryScope(
        config.instanceId,
        scopeAtStart,
        scopeAtStart === "Session" ? sessionAtStart : undefined
      );
      message.success(`Reset removed ${removed} item(s).`);
      if (refreshGen === memoryLoadGenRef.current) {
        try {
          await fetchMemoryItems(scopeAtStart, sessionAtStart, refreshGen);
        } catch (error) {
          setMemoryItems([]);
          setMemoryError(describeAdminError(error, "Unable to reload learned memory."));
        }
      }
    } catch (error) {
      showAdminFailure(message, error, "Reset failed.");
    } finally {
      setMemoryMutating(false);
    }
  };

  const cancelRegistration = async (row: AdminAutomationRegistration) => {
    setAutomationBusy(true);
    try {
      await cancelAdminAutomationRegistration(config.instanceId, row.registrationId, row.revision);
      message.success("Registration cancelled.");
      await loadAutomation();
    } catch (error) {
      showAdminFailure(message, error, "Cancel failed.");
    } finally {
      setAutomationBusy(false);
    }
  };

  const memoryColumns: ColumnsType<AdminLearnedMemoryItem> = [
    { title: "Kind", dataIndex: "kind", key: "kind", width: 100 },
    { title: "Subject", dataIndex: "subject", key: "subject", width: 220, ellipsis: true },
    { title: "Content", dataIndex: "content", key: "content", width: 480, ellipsis: true },
    {
      title: "Provenance",
      key: "provenance",
      width: 200,
      render: (_, row) => row.provenance.derivedFromMemoryIds?.length ? `Consolidated from ${row.provenance.derivedFromMemoryIds.length} memories` : row.provenance.source
    },
    {
      title: "Actions",
      key: "actions",
      width: 100,
      render: (_, row) => (
        <Button
          size="small"
          danger
          disabled={!memoryActionsEnabled}
          onClick={() =>
            confirmAction(modal, {
              title: "Forget this learned-memory item?",
              content: "Removes this item from future learned-memory retrieval. Source conversations and other retained continuity records are not deleted.",
              okText: "Delete",
              danger: true,
              onOk: () => void deleteMemoryItem(row.memoryId)
            })
          }
        >
          Delete
        </Button>
      )
    }
  ];

  const automationColumns: ColumnsType<AdminAutomationRegistration> = [
    { title: "Intent", dataIndex: "intent", key: "intent", width: 220, ellipsis: true },
    { title: "Status", dataIndex: "status", key: "status", width: 120 },
    { title: "Schedule", dataIndex: "scheduleSummary", key: "scheduleSummary", width: 240, ellipsis: true },
    {
      title: "Next run",
      key: "nextRun",
      width: 260,
      ellipsis: true,
      render: (_, row) => formatAutomationNextRun(row.timeZoneId, row.nextOccurrenceAtUtc)
    },
    {
      title: "Suspended",
      key: "suspension",
      width: 180,
      ellipsis: true,
      render: (_, row) => row.suspensionReason ?? "—"
    },
    {
      title: "Provenance",
      key: "prov",
      width: 180,
      render: (_, row) => row.provenance.authorizationOrigin
    },
    { title: "Model source", key: "modelSource", width: 160, ellipsis: true,
      render: (_, row) => row.modelSource ?? "Conversation default" },
    {
      title: "Model",
      key: "model",
      width: 290,
      render: (_, row) => (
        <RegistrationModelControl
          instanceId={config.instanceId}
          row={row}
          models={models}
          onSaved={(updated) =>
            setAutomationItems((current) =>
              current?.map((item) => (item.registrationId === updated.registrationId ? updated : item)) ?? null
            )
          }
        />
      )
    },
    {
      title: "Actions",
      key: "actions",
      width: 100,
      render: (_, row) =>
        row.status === "cancelled" || row.status === "completed" ? null : (
          <Button
            size="small"
            danger
            disabled={automationBusy}
            onClick={() =>
              confirmAction(modal, {
                title: "Cancel this registration?",
                okText: "Cancel registration",
                danger: true,
                onOk: () => void cancelRegistration(row)
              })
            }
          >
            Revoke
          </Button>
        )
    }
  ];

  const resetTitle = memoryResetConfirmTitle(memoryScope, config.instanceId, sessionId);

  const memoryActions = (
    <Flex className="admin-memory-actions" gap={token.paddingXS} wrap="wrap">
      <Button
        onClick={() => void loadMemory()}
        loading={memoryLoading}
        disabled={!scopePermitted || !selectionReady || memoryMutating || memoryLoading}
      >
        Load items
      </Button>
      <Button
        danger
        disabled={!memoryActionsEnabled}
        onClick={() =>
          confirmAction(modal, {
            title: resetTitle,
            okText: "Reset scope",
            danger: true,
            onOk: () => void resetMemoryScope()
          })
        }
      >
        Reset scope
      </Button>
    </Flex>
  );

  const sections = [

        {
          key: "memory",
          label: "Memory",
          children: (
            <Flex vertical gap={token.paddingSM} aria-label="Learned memory administration">
              <Descriptions {...detailLayout} size="small" column={1} bordered>
                <Descriptions.Item label="Effective memory policy">{memoryPolicySummary(config)}</Descriptions.Item>
              </Descriptions>
              {memoryScope === "Session" ? (
                <Typography.Text type="secondary">
                  Session scope uses the selected session&apos;s pinned definition policy. The summary above reflects the
                  instance&apos;s current version only.
                </Typography.Text>
              ) : null}
              {!scopePermitted ? (
                <Typography.Text type="warning">
                  Effective memory policy does not allow administration for {memoryScope} scope.
                </Typography.Text>
              ) : null}
              <Flex gap={token.paddingXS} wrap="wrap" align={memoryScope === "Session" ? "start" : "flex-end"}>
                <Flex vertical gap={token.paddingXS} className="admin-memory-scope">
                  <Typography.Text>Memory scope</Typography.Text>
                  <Select
                    aria-label="Memory scope"
                    value={memoryScope}
                    disabled={selectionLocked}
                    onChange={(value) => {
                      setMemoryScope(value);
                      bumpMemoryLoadGeneration();
                    }}
                    options={MEMORY_SCOPES.map((value) => ({ value, label: value }))}
                  />
                </Flex>
                {memoryScope === "Session" ? (
                  <div className="admin-memory-source">
                    <Typography.Text>Source conversation</Typography.Text>
                    <AdminSessionPicker instanceId={config.instanceId} value={sessionId} disabled={selectionLocked}
                      onChange={value => { setSessionId(value); bumpMemoryLoadGeneration(); }} />
                  </div>
                ) : memoryActions}
              </Flex>
              {memoryScope === "Session" ? memoryActions : null}
              {memoryError ? (
                <AdminErrorNotice message={memoryError.message} diagnosticId={memoryError.diagnosticId} tone="danger" />
              ) : null}
              {memoryItems ? (
                <Table
                  className="admin-collection-table"
                  scroll={{ x: 1100 }}
                  size="small"
                  rowKey="memoryId"
                  dataSource={memoryItems}
                  columns={memoryColumns}
                  expandable={{ expandedRowRender: row => <MemoryLineageDetails key={`${memoryScope}:${sessionId}:${row.memoryId}`} instanceId={config.instanceId} row={row} scope={memoryScope} sessionId={memoryScope === "Session" ? sessionId.trim() : undefined} /> }}
                  pagination={adminCollectionPagination}
                  locale={{ emptyText: "No active learned-memory items in this scope." }}
                />
              ) : null}
            </Flex>
          )
        },
        {
          key: "automation",
          label: "Automation",
          children: (
            <Flex vertical gap={token.padding} aria-label="Automation administration">
              <section className="admin-definition-panel" aria-label="Execution defaults">
                <div className="admin-definition-panel-heading">
                  <Typography.Title level={4}>Execution defaults</Typography.Title>
                  <Typography.Text type="secondary">Model settings for scheduled and reactive work.</Typography.Text>
                </div>
                <Flex vertical gap={token.padding} className="admin-definition-panel-body">
                  <Descriptions {...detailLayout} size="small" column={1} bordered>
                    <Descriptions.Item label="Effective trigger policy">{triggerPolicySummary(config)}</Descriptions.Item>
                  </Descriptions>
                  <UnattendedModelForm config={config} models={models} />
                </Flex>
              </section>
              <section className="admin-definition-panel" aria-label="Advanced registrations">
                <Flex wrap align="center" justify="space-between" gap={token.paddingSM} className="admin-definition-panel-heading">
                  <Typography.Title level={4}>Advanced registrations</Typography.Title>
                  <Button onClick={() => void loadAutomation()} loading={automationBusy}>
                    Review advanced registrations
                  </Button>
                </Flex>
                <div className="admin-definition-panel-body">
                  {!automationItems && !automationError ? (
                    <Typography.Text type="secondary">Load registrations to inspect their status, model and provenance.</Typography.Text>
                  ) : null}
                  {automationError ? (
                    <AdminErrorNotice
                      message={automationError.message}
                      diagnosticId={automationError.diagnosticId}
                      tone="danger"
                    />
                  ) : null}
                  {automationItems ? (
                    <Table
                      className="admin-collection-table"
                      scroll={{ x: 1850 }}
                      size="small"
                      rowKey="registrationId"
                      dataSource={automationItems}
                      columns={automationColumns}
                      pagination={adminCollectionPagination}
                      locale={{ emptyText: "No active or suspended registrations." }}
                    />
                  ) : null}
                </div>
              </section>
            </Flex>
          )
        }
      ];
  if (section) return sections.find(item => item.key === section)?.children;
  return <Tabs className="admin-instance-admin-tabs" aria-label="Memory and automation administration" items={sections} />;
}
