import { useState } from "react";
import { Alert, App, Button, Dropdown, Empty, Flex, Input, Typography } from "antd";
import { MoreOutlined, PlusOutlined } from "@ant-design/icons";
import type { MenuProps } from "antd";
import type { AgentDescriptor, CatalogItem } from "../../services/api";
import {
  archiveCatalogItem,
  deleteCatalogItem,
  refreshCatalog,
  renameCatalogItem,
  setIncludeArchived,
  unarchiveCatalogItem
} from "../../services/catalog";
import { useSessionStore, type CatalogMutation } from "../../state/sessionStore";

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
    return "Ended";
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
  return mutation?.sessionId === sessionId;
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
  onNewChat: () => void;
  onOpen: (item: CatalogItem) => void;
}) {
  const { message, modal } = App.useApp();
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [draftTitle, setDraftTitle] = useState("");
  const catalogBusy = mutation != null;

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
        let ok = await deleteCatalogItem(latestItem(item.sessionId, item));
        if (!ok) {
          ok = await deleteCatalogItem(latestItem(item.sessionId, item));
        }
        await notifyMutation(ok, "Session deleted.", { rejectOnFailure: true });
        if (ok && item.sessionId === activeSessionId) {
          onNewChat();
        }
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
      disabled: catalogBusy,
      onClick: () => {
        void setIncludeArchived(!includeArchived);
      }
    }
  ];

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
      <Flex justify={showHeading ? "space-between" : "flex-end"} align="center" gap={8} className="session-rail-heading">
        {showHeading ? <Typography.Text type="secondary">Chats</Typography.Text> : null}
        <Dropdown menu={{ items: listMenu }} trigger={["click"]} placement="bottomRight">
          <Button type="text" size="small" aria-label="Chat list options" icon={<MoreOutlined />} />
        </Dropdown>
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
          const inactive = item.archived || item.ended;
          const busy = rowBusy(mutation, item.sessionId);
          return (
            <li
              key={item.sessionId}
              className={
                item.sessionId === activeSessionId ? "session-row session-row-active" : "session-row"
              }
            >
              <div className="session-row-body">
                {renamingId === item.sessionId ? (
                  <form
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
                  <Flex align="flex-start" gap={8} className="session-row-main">
                    <Button
                      type="text"
                      className="session-row-open"
                      aria-label={item.title}
                      disabled={inactive || busy}
                      onClick={() => onOpen(item)}
                    >
                      <Flex vertical align="flex-start" gap={0} className="session-row-copy">
                        <Typography.Text ellipsis={{ tooltip: item.title }}>{item.title}</Typography.Text>
                        <Typography.Text type="secondary" className="session-row-meta">
                          {agentLabel(agents, item)}
                          <span> · {rowState(item)}</span>
                        </Typography.Text>
                      </Flex>
                    </Button>
                    <Dropdown menu={{ items: rowMenu(item) }} trigger={["click"]} placement="bottomRight">
                      <Button
                        type="text"
                        size="small"
                        className="session-row-more"
                        aria-label={`Actions for ${item.title}`}
                        icon={<MoreOutlined />}
                        disabled={busy}
                      />
                    </Dropdown>
                  </Flex>
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
    </nav>
  );
}
