import { useCallback, useRef, useState } from "react";
import { App, Button, Descriptions, Flex, Input, Popconfirm, Select, Table, Tabs, Typography } from "antd";
import type { ColumnsType } from "antd/es/table";
import {
  type AdminAutomationRegistration,
  type AdminEffectiveConfiguration,
  type AdminLearnedMemoryItem,
  type AdminLearnedMemoryScope,
  cancelAdminAutomationRegistration,
  deleteAdminLearnedMemory,
  listAdminAutomationRegistrations,
  listAdminLearnedMemory,
  resetAdminLearnedMemoryScope
} from "../../services/adminApi";
import {
  formatAutomationNextRun,
  isMemoryScopePermitted,
  isMemorySelectionReady,
  memoryResetConfirmTitle
} from "./instanceMemoryAutomationLogic";

const MEMORY_SCOPES: AdminLearnedMemoryScope[] = ["Session", "IdentityUser", "User"];

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

export function InstanceMemoryAutomationPanel({ config }: { config: AdminEffectiveConfiguration }) {
  const { message } = App.useApp();
  const [memoryScope, setMemoryScope] = useState<AdminLearnedMemoryScope>("IdentityUser");
  const [sessionId, setSessionId] = useState("");
  const [memoryItems, setMemoryItems] = useState<AdminLearnedMemoryItem[] | null>(null);
  const [memoryError, setMemoryError] = useState<string | null>(null);
  const [memoryLoading, setMemoryLoading] = useState(false);
  const [memoryMutating, setMemoryMutating] = useState(false);
  const [automationItems, setAutomationItems] = useState<AdminAutomationRegistration[] | null>(null);
  const [automationError, setAutomationError] = useState<string | null>(null);
  const [automationBusy, setAutomationBusy] = useState(false);
  const memoryLoadGenRef = useRef(0);

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
      setMemoryError("Effective memory policy does not allow this scope.");
      return;
    }
    if (!selectionReady) {
      setMemoryError("Session scope requires a session id.");
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
      setMemoryError(error instanceof Error ? error.message : "Unable to load learned memory.");
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
      setAutomationError(error instanceof Error ? error.message : "Unable to load automation.");
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
          setMemoryError(error instanceof Error ? error.message : "Unable to reload learned memory.");
        }
      }
    } catch (error) {
      message.error(error instanceof Error ? error.message : "Delete failed.");
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
          setMemoryError(error instanceof Error ? error.message : "Unable to reload learned memory.");
        }
      }
    } catch (error) {
      message.error(error instanceof Error ? error.message : "Reset failed.");
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
      message.error(error instanceof Error ? error.message : "Cancel failed.");
    } finally {
      setAutomationBusy(false);
    }
  };

  const memoryColumns: ColumnsType<AdminLearnedMemoryItem> = [
    { title: "Kind", dataIndex: "kind", key: "kind", width: 100 },
    { title: "Subject", dataIndex: "subject", key: "subject" },
    { title: "Content", dataIndex: "content", key: "content", ellipsis: true },
    {
      title: "Provenance",
      key: "provenance",
      render: (_, row) => row.provenance.source
    },
    {
      title: "",
      key: "actions",
      width: 100,
      render: (_, row) => (
        <Popconfirm
          title="Delete this learned-memory item?"
          onConfirm={() => void deleteMemoryItem(row.memoryId)}
          okText="Delete"
        >
          <Button size="small" danger disabled={!memoryActionsEnabled}>
            Delete
          </Button>
        </Popconfirm>
      )
    }
  ];

  const automationColumns: ColumnsType<AdminAutomationRegistration> = [
    { title: "Intent", dataIndex: "intent", key: "intent" },
    { title: "Status", dataIndex: "status", key: "status", width: 120 },
    { title: "Schedule", dataIndex: "scheduleSummary", key: "scheduleSummary" },
    {
      title: "Next run",
      key: "nextRun",
      render: (_, row) => formatAutomationNextRun(row.timeZoneId, row.nextOccurrenceAtUtc)
    },
    {
      title: "Suspended",
      key: "suspension",
      render: (_, row) => row.suspensionReason ?? "—"
    },
    {
      title: "Provenance",
      key: "prov",
      render: (_, row) => row.provenance.authorizationOrigin
    },
    {
      title: "",
      key: "actions",
      width: 100,
      render: (_, row) =>
        row.status === "cancelled" || row.status === "completed" ? null : (
          <Popconfirm
            title="Cancel this registration?"
            onConfirm={() => void cancelRegistration(row)}
            okText="Cancel registration"
          >
            <Button size="small" danger disabled={automationBusy}>
              Revoke
            </Button>
          </Popconfirm>
        )
    }
  ];

  const resetTitle = memoryResetConfirmTitle(memoryScope, config.instanceId, sessionId);

  return (
    <Tabs
      className="admin-instance-admin-tabs"
      aria-label="Memory and automation administration"
      items={[
        {
          key: "memory",
          label: "Memory",
          children: (
            <Flex vertical gap={12} aria-label="Learned memory administration">
              <Descriptions size="small" column={1} bordered>
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
              <Flex gap={8} wrap="wrap" align="end">
                <Select
                  aria-label="Memory scope"
                  value={memoryScope}
                  disabled={selectionLocked}
                  onChange={(value) => {
                    setMemoryScope(value);
                    bumpMemoryLoadGeneration();
                  }}
                  options={MEMORY_SCOPES.map((value) => ({ value, label: value }))}
                  style={{ minWidth: 160 }}
                />
                {memoryScope === "Session" ? (
                  <Input
                    aria-label="Session id"
                    placeholder="Session id (required for Session scope)"
                    value={sessionId}
                    disabled={selectionLocked}
                    onChange={(event) => {
                      setSessionId(event.target.value);
                      bumpMemoryLoadGeneration();
                    }}
                    style={{ minWidth: 280 }}
                  />
                ) : null}
                <Button
                  onClick={() => void loadMemory()}
                  loading={memoryLoading}
                  disabled={!scopePermitted || !selectionReady || memoryMutating || memoryLoading}
                >
                  Load items
                </Button>
                <Popconfirm title={resetTitle} onConfirm={() => void resetMemoryScope()} okText="Reset scope">
                  <Button danger disabled={!memoryActionsEnabled}>
                    Reset scope
                  </Button>
                </Popconfirm>
              </Flex>
              {memoryError ? <Typography.Text type="danger">{memoryError}</Typography.Text> : null}
              {memoryItems ? (
                <Table
                  size="small"
                  rowKey="memoryId"
                  dataSource={memoryItems}
                  columns={memoryColumns}
                  pagination={false}
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
            <Flex vertical gap={12} aria-label="Automation administration">
              <Descriptions size="small" column={1} bordered>
                <Descriptions.Item label="Effective trigger policy">{triggerPolicySummary(config)}</Descriptions.Item>
              </Descriptions>
              <Button onClick={() => void loadAutomation()} loading={automationBusy}>
                Load registrations
              </Button>
              {automationError ? <Typography.Text type="danger">{automationError}</Typography.Text> : null}
              {automationItems ? (
                <Table
                  size="small"
                  rowKey="registrationId"
                  dataSource={automationItems}
                  columns={automationColumns}
                  pagination={false}
                  locale={{ emptyText: "No active or suspended registrations." }}
                />
              ) : null}
            </Flex>
          )
        }
      ]}
    />
  );
}
