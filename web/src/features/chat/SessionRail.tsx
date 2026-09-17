import { useState } from "react";
import { Alert, Button, Checkbox, Flex, Input, Typography } from "antd";
import type { AgentDescriptor, CatalogItem } from "../../services/api";
import {
  archiveCatalogItem,
  deleteCatalogItem,
  refreshCatalog,
  renameCatalogItem,
  setIncludeArchived,
  unarchiveCatalogItem
} from "../../services/catalog";
import type { CatalogMutation } from "../../state/sessionStore";

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

export function SessionRail({
  items,
  agents,
  activeSessionId,
  includeArchived,
  hasMore,
  capabilityLost,
  error,
  mutation,
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
  onNewChat: () => void;
  onOpen: (item: CatalogItem) => void;
}) {
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [draftTitle, setDraftTitle] = useState("");
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const catalogBusy = mutation != null;

  return (
    <nav className="session-rail" aria-label="Sessions" data-testid="session-rail">
      <Flex justify="space-between" align="center" gap={8}>
        <Typography.Text strong>Sessions</Typography.Text>
        <Button type="primary" aria-label="Start a new chat" onClick={onNewChat} disabled={catalogBusy}>
          New chat
        </Button>
      </Flex>
      {capabilityLost ? (
        <Alert type="error" showIcon title={error ?? "Local owner access is unavailable."} />
      ) : null}
      {!capabilityLost && error ? <Alert type="error" showIcon title={error} /> : null}
      <Checkbox
        checked={includeArchived}
        disabled={catalogBusy}
        onChange={(event) => void setIncludeArchived(event.target.checked)}
      >
        Show archived
      </Checkbox>
      <ul className="session-rail-list">
        {items.length === 0 && !capabilityLost ? (
          <li className="session-rail-empty">No sessions yet.</li>
        ) : null}
        {items.map((item) => {
          const locked = item.ended;
          const inactive = item.archived || item.ended;
          const busy = rowBusy(mutation, item.sessionId);
          return (
            <li
              key={item.sessionId}
              className={
                item.sessionId === activeSessionId ? "session-row session-row-active" : "session-row"
              }
            >
              {renamingId === item.sessionId ? (
                <form
                  className="session-rename"
                  onSubmit={(event) => {
                    event.preventDefault();
                    void renameCatalogItem(item.sessionId, draftTitle).then((ok) => {
                      if (ok) {
                        setRenamingId(null);
                      }
                    });
                  }}
                >
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
                </form>
              ) : (
                <Button
                  type="text"
                  block
                  disabled={inactive || busy}
                  onClick={() => onOpen(item)}
                  style={{ height: "auto", textAlign: "start", whiteSpace: "normal" }}
                >
                  <Flex vertical align="flex-start" gap={0}>
                    <Typography.Text>{item.title}</Typography.Text>
                    <Typography.Text type="secondary">
                      {agentLabel(agents, item)}
                      <span> · {rowState(item)}</span>
                    </Typography.Text>
                  </Flex>
                </Button>
              )}
              {locked ? null : (
                <div className="session-rail-actions">
                  {item.archived ? (
                    <Button
                      size="small"
                      loading={mutationBusy(mutation, item.sessionId, "unarchive")}
                      disabled={catalogBusy && !mutationBusy(mutation, item.sessionId, "unarchive")}
                      onClick={() => void unarchiveCatalogItem(item.sessionId)}
                    >
                      Unarchive
                    </Button>
                  ) : (
                    <>
                      <Button
                        size="small"
                        disabled={busy}
                        onClick={() => {
                          setRenamingId(item.sessionId);
                          setDraftTitle(item.title);
                        }}
                      >
                        Rename
                      </Button>
                      <Button
                        size="small"
                        loading={mutationBusy(mutation, item.sessionId, "archive")}
                        disabled={catalogBusy && !mutationBusy(mutation, item.sessionId, "archive")}
                        onClick={() => void archiveCatalogItem(item.sessionId)}
                      >
                        Archive
                      </Button>
                    </>
                  )}
                  {confirmDeleteId === item.sessionId ? (
                    <>
                      <Button
                        size="small"
                        danger
                        loading={mutationBusy(mutation, item.sessionId, "delete")}
                        disabled={catalogBusy && !mutationBusy(mutation, item.sessionId, "delete")}
                        onClick={() => {
                          void deleteCatalogItem(item).then((ok) => {
                            if (ok) {
                              setConfirmDeleteId(null);
                            } else {
                              setConfirmDeleteId(null);
                            }
                          });
                        }}
                      >
                        Confirm delete
                      </Button>
                      <Button
                        size="small"
                        disabled={mutationBusy(mutation, item.sessionId, "delete")}
                        onClick={() => setConfirmDeleteId(null)}
                      >
                        Cancel
                      </Button>
                    </>
                  ) : (
                    <Button size="small" danger disabled={busy} onClick={() => setConfirmDeleteId(item.sessionId)}>
                      Delete
                    </Button>
                  )}
                </div>
              )}
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
