import { useState, type ReactNode } from "react";
import { Alert, App, Button, Dropdown, Empty, Flex, Input, Tooltip, Typography } from "antd";
import { MoreOutlined, PlusOutlined, ReloadOutlined } from "@ant-design/icons";
import type { MenuProps } from "antd";
import type { AgentDescriptor, CatalogItem } from "../../services/api";
import {
  archiveCatalogItem,
  deleteAllCatalogItems,
  deleteCatalogItem,
  refreshCatalog,
  renameCatalogItem,
  setIncludeArchived,
  unarchiveCatalogItem
} from "../../services/catalog";
import { sameSessionId } from "../../app/sessionRoute";
import { useSessionStore, type CatalogMutation } from "../../state/sessionStore";
import { formatChatTime } from "./chatTime";
import { lifecycleOutcomeLabel } from "./sessionLifecycle";

function agentLabel(agents: AgentDescriptor[], item: CatalogItem): string {
  const agent = agents.find((row) => row.id === item.agentId && row.version === item.agentVersion)
    ?? agents.find((row) => row.id === item.agentId);
  if (!agent) {
    return `${item.agentId} v${item.agentVersion}`;
  }

  return `${agent.name} · ${agent.role}`;
}

function rowState(item: CatalogItem): string {
  if (item.ended) {
    return lifecycleOutcomeLabel(item.lifecycleStatus);
  }
  if (item.archived) {
    return "Archived";
  }
  if (item.status === "attached") {
    return "Open";
  }
  return "Paused";
}

function mutationBusy(mutation: CatalogMutation | null, sessionId: string, kind: CatalogMutation["kind"]): boolean {
  return mutation?.sessionId === sessionId && mutation.kind === kind;
}

function rowBusy(mutation: CatalogMutation | null, sessionId: string): boolean {
  if (mutation?.kind === "deleteAll") {
    return true;
  }

  return mutation?.sessionId === sessionId;
}

function OverflowIconButton({
  title,
  ariaLabel,
  icon,
  loading,
  disabled,
  onClick
}: {
  title: string;
  ariaLabel: string;
  icon: ReactNode;
  loading?: boolean;
  disabled?: boolean;
  onClick?: () => void;
}) {
  const control = (
    <Button
      type="text"
      size="small"
      className="session-overflow"
      aria-label={ariaLabel}
      icon={icon}
      loading={loading}
      disabled={disabled}
      onClick={onClick}
    />
  );

  return (
    <Tooltip title={title}>
      {disabled ? <span className="session-overflow-wrap">{control}</span> : control}
    </Tooltip>
  );
}

function latestCatalogError(): string {
  return useSessionStore.getState().catalogError ?? "Unable to update the session catalog.";
}

export function SessionRail({
  items,
  agents,
  activeSessionId,
  includeArchived,
  hasMore,
  capabilityLost,
  error,
  mutation,
  showHeading = true,
  showNewChat = true,
  onNewChat,
  onOpen
}: {
  items: CatalogItem[];
  agents: AgentDescriptor[];
  activeSessionId: string | null;
  includeArchived: boolean;
  hasMore: boolean;
  capabilityLost: boolean;
  error: string | null;
  mutation: CatalogMutation | null;
  showHeading?: boolean;
  showNewChat?: boolean;
  onNewChat: (options?: { urlMode?: "push" | "replace" }) => void;
  onOpen: (item: CatalogItem) => void;
}) {
  const { message, modal } = App.useApp();
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [draftTitle, setDraftTitle] = useState("");
  const [refreshing, setRefreshing] = useState(false);
  const catalogBusy = mutation != null;
  const listActionsDisabled = catalogBusy || refreshing || capabilityLost;

  function latestItem(sessionId: string, fallback: CatalogItem): CatalogItem {
    return useSessionStore.getState().catalogItems.find((row) => row.sessionId === sessionId) ?? fallback;
  }

  async function notifyMutation(
    ok: boolean,
    success: string,
    options?: { rejectOnFailure?: boolean }
  ): Promise<void> {
    if (ok) {
      void message.success(success);
      return;
    }

    const errorMessage = latestCatalogError();
    void message.error(errorMessage);
    if (options?.rejectOnFailure) {
      return Promise.reject(new Error(errorMessage));
    }
  }

  function confirmDeleteAll(): void {
    modal.confirm({
      title: "Delete all chats?",
      content: includeArchived
        ? "This removes every session in your catalog, including archived chats. This cannot be undone."
        : "This removes every visible chat in your catalog. Archived chats are kept unless you show archived first. This cannot be undone.",
      okText: "Delete all",
      cancelText: "Cancel",
      okType: "danger",
      centered: true,
      mask: { closable: true },
      onOk: async () => {
        const hadActive = activeSessionId != null;
        const deletedCount = await deleteAllCatalogItems();
        if (deletedCount === false) {
          await notifyMutation(false, "Chats deleted.", { rejectOnFailure: true });
          return;
        }

        if (hadActive) {
          onNewChat({ urlMode: "replace" });
        }

        void message.success(
          deletedCount === 0
            ? "No chats to delete."
            : `Deleted ${deletedCount} chat${deletedCount === 1 ? "" : "s"}.`
        );
      }
    });
  }

  function confirmDelete(item: CatalogItem): void {
    modal.confirm({
      title: `Delete “${item.title}”?`,
      content: "This removes the session from the catalog. This cannot be undone.",
      okText: "Delete",
      cancelText: "Cancel",
      okType: "danger",
      centered: true,
      mask: { closable: true },
      onOk: async () => {
        const deletingActive = sameSessionId(item.sessionId, activeSessionId);
        const ok = await deleteCatalogItem(latestItem(item.sessionId, item));
        if (!ok) {
          await notifyMutation(false, "Session deleted.", { rejectOnFailure: true });
          return;
        }
        if (deletingActive) {
          onNewChat({ urlMode: "replace" });
        }
        void message.success("Session deleted.");
      }
    });
  }

  function rowMenu(item: CatalogItem): MenuProps["items"] {
    const items: MenuProps["items"] = [];
    if (!item.ended) {
      if (item.archived) {
        items.push({
          key: "unarchive",
          label: "Unarchive",
          disabled: catalogBusy && !mutationBusy(mutation, item.sessionId, "unarchive"),
          onClick: () => {
            void unarchiveCatalogItem(item.sessionId).then(async (ok) => {
              await notifyMutation(ok, "Session restored.");
            });
          }
        });
      } else {
        items.push({
          key: "rename",
          label: "Rename",
          disabled: rowBusy(mutation, item.sessionId),
          onClick: () => {
            setRenamingId(item.sessionId);
            setDraftTitle(item.title);
          }
        });
        items.push({
          key: "archive",
          label: "Archive",
          disabled: catalogBusy && !mutationBusy(mutation, item.sessionId, "archive"),
          onClick: () => {
            void archiveCatalogItem(item.sessionId).then(async (ok) => {
              await notifyMutation(ok, "Session archived.");
            });
          }
        });
      }
    }

    items.push({
      key: "delete",
      danger: true,
      label: "Delete",
      disabled: rowBusy(mutation, item.sessionId),
      onClick: () => confirmDelete(item)
    });
    return items;
  }

  const listMenu: MenuProps["items"] = [
    {
      key: "archived",
      label: includeArchived ? "Hide archived" : "Show archived",
      disabled: listActionsDisabled,
      onClick: () => {
        void setIncludeArchived(!includeArchived);
      }
    },
    { type: "divider" },
    {
      key: "delete-all",
      danger: true,
      label: "Delete all chats",
      disabled: listActionsDisabled,
      onClick: () => confirmDeleteAll()
    }
  ];

  function refreshList(): void {
    setRefreshing(true);
    void refreshCatalog(true).finally(() => {
      setRefreshing(false);
    });
  }

  return (
    <nav className="session-rail" aria-label="Chats" data-testid="session-rail">
      {showNewChat ? (
        <Button
          type="text"
          className="session-new-chat"
          aria-label="Start a new chat"
          icon={<PlusOutlined />}
          onClick={() => {
            setRenamingId(null);
            onNewChat();
          }}
          disabled={catalogBusy}
          block
        >
          New chat
        </Button>
      ) : null}
      <div className="session-rail-catalog">
        <Flex justify={showHeading ? "space-between" : "flex-end"} align="center" gap={8} className="session-rail-heading">
          {showHeading ? <Typography.Text type="secondary">Chats</Typography.Text> : null}
          <div className="session-rail-heading-actions">
            <OverflowIconButton
              title="Refresh chats"
              ariaLabel="Refresh chats"
              icon={<ReloadOutlined />}
              loading={refreshing}
              disabled={listActionsDisabled}
              onClick={refreshList}
            />
            <Dropdown menu={{ items: listMenu }} trigger={["click"]} placement="bottomRight" disabled={listActionsDisabled}>
              <Button
                type="text"
                size="small"
                className="session-overflow"
                aria-label="Chat list options"
                icon={<MoreOutlined />}
                disabled={listActionsDisabled}
              />
            </Dropdown>
          </div>
        </Flex>
        {capabilityLost ? (
          <Alert type="error" showIcon title={error ?? "Local owner access is unavailable."} />
        ) : null}
        {!capabilityLost && error ? <Alert type="error" showIcon title={error} /> : null}
      <ul className="session-rail-list">
        {items.length === 0 && !capabilityLost ? (
          <li className="session-rail-empty">
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No chats yet." />
          </li>
        ) : null}
        {items.map((item) => {
          const inactive = item.archived;
          const busy = rowBusy(mutation, item.sessionId);
          const updatedLabel = formatChatTime(item.updatedAt);
          return (
            <li
              key={item.sessionId}
              className={
                sameSessionId(item.sessionId, activeSessionId) ? "session-row session-row-active" : "session-row"
              }
            >
              <div className="session-row-body">
                {renamingId === item.sessionId ? (
                  <form
                    className="session-row-rename"
                    onSubmit={(event) => {
                      event.preventDefault();
                      void renameCatalogItem(item.sessionId, draftTitle).then(async (ok) => {
                        if (ok) {
                          setRenamingId(null);
                        }
                        await notifyMutation(ok, "Session renamed.");
                      });
                    }}
                  >
                    <Flex gap={8} align="center">
                      <Input
                        aria-label="Session title"
                        value={draftTitle}
                        onChange={(event) => setDraftTitle(event.target.value)}
                        disabled={mutationBusy(mutation, item.sessionId, "rename")}
                        onKeyDown={(event) => {
                          if (event.key === "Escape" && !mutationBusy(mutation, item.sessionId, "rename")) {
                            setRenamingId(null);
                          }
                        }}
                      />
                      <Button
                        htmlType="submit"
                        loading={mutationBusy(mutation, item.sessionId, "rename")}
                        disabled={catalogBusy && !mutationBusy(mutation, item.sessionId, "rename")}
                      >
                        Save
                      </Button>
                    </Flex>
                  </form>
                ) : (
                  <>
                    <Button
                      type="text"
                      className="session-row-open"
                      aria-label={item.title}
                      disabled={inactive || busy}
                      onClick={() => onOpen(item)}
                    >
                      <Flex vertical align="flex-start" gap={0} className="session-row-copy">
                        <Flex align="baseline" gap={8} className="session-row-title">
                          <Typography.Text ellipsis={{ tooltip: item.title }} className="session-row-name">
                            {item.title}
                          </Typography.Text>
                          {updatedLabel ? (
                            <time dateTime={item.updatedAt} className="session-row-time">
                              {updatedLabel}
                            </time>
                          ) : null}
                        </Flex>
                        <Typography.Text type="secondary" ellipsis className="session-row-meta">
                          {`${agentLabel(agents, item)} · ${rowState(item)}`}
                        </Typography.Text>
                      </Flex>
                    </Button>
                    <Dropdown menu={{ items: rowMenu(item) }} trigger={["click"]} placement="bottomRight">
                      <Button
                        type="text"
                        size="small"
                        className="session-overflow session-row-more"
                        aria-label={`Actions for ${item.title}`}
                        icon={<MoreOutlined />}
                        disabled={busy}
                      />
                    </Dropdown>
                  </>
                )}
              </div>
            </li>
          );
        })}
      </ul>
      {hasMore ? (
        <Button disabled={catalogBusy} onClick={() => void refreshCatalog(false)}>
          Load more
        </Button>
      ) : null}
      </div>
    </nav>
  );
}
