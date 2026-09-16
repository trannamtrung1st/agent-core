import { useState } from "react";
import type { AgentDescriptor, CatalogItem } from "../../services/api";
import {
  archiveCatalogItem,
  deleteCatalogItem,
  refreshCatalog,
  renameCatalogItem,
  setIncludeArchived,
  unarchiveCatalogItem
} from "../../services/catalog";

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

export function SessionRail({
  items,
  agents,
  activeSessionId,
  includeArchived,
  hasMore,
  capabilityLost,
  error,
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
  onNewChat: () => void;
  onOpen: (item: CatalogItem) => void;
}) {
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [draftTitle, setDraftTitle] = useState("");
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);

  return (
    <nav className="session-rail" aria-label="Sessions" data-testid="session-rail">
      <div className="session-rail-bar">
        <span>Sessions</span>
        <button type="button" className="key key-compact" aria-label="Start a new chat" onClick={onNewChat}>
          New chat
        </button>
      </div>
      {capabilityLost ? (
        <p className="error session-rail-alert" role="alert">
          {error ?? "Local owner access is unavailable."}
        </p>
      ) : null}
      {!capabilityLost && error ? (
        <p className="error session-rail-alert" role="alert">
          {error}
        </p>
      ) : null}
      <label className="session-rail-filter">
        <input
          type="checkbox"
          checked={includeArchived}
          onChange={(event) => void setIncludeArchived(event.target.checked)}
        />
        Show archived
      </label>
      <ul className="session-rail-list">
        {items.length === 0 && !capabilityLost ? (
          <li className="session-rail-empty">No sessions yet.</li>
        ) : null}
        {items.map((item) => {
          const locked = item.ended;
          const inactive = item.archived || item.ended;
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
                    void renameCatalogItem(item.sessionId, draftTitle).then(() => setRenamingId(null));
                  }}
                >
                  <input
                    aria-label="Session title"
                    value={draftTitle}
                    onChange={(event) => setDraftTitle(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === "Escape") {
                        setRenamingId(null);
                      }
                    }}
                  />
                  <button type="submit">Save</button>
                </form>
              ) : (
                <button
                  type="button"
                  className="session-row-main"
                  disabled={inactive}
                  onClick={() => onOpen(item)}
                >
                  <span className="session-row-title">{item.title}</span>
                  <span className="session-row-meta">
                    {agentLabel(agents, item)}
                    <span className="session-row-state"> · {rowState(item)}</span>
                  </span>
                </button>
              )}
              {locked ? null : (
                <div className="session-row-actions">
                  {item.archived ? (
                    <button type="button" onClick={() => void unarchiveCatalogItem(item.sessionId)}>
                      Unarchive
                    </button>
                  ) : (
                    <>
                      <button
                        type="button"
                        onClick={() => {
                          setRenamingId(item.sessionId);
                          setDraftTitle(item.title);
                        }}
                      >
                        Rename
                      </button>
                      <button type="button" onClick={() => void archiveCatalogItem(item.sessionId)}>
                        Archive
                      </button>
                    </>
                  )}
                  {confirmDeleteId === item.sessionId ? (
                    <>
                      <button type="button" onClick={() => void deleteCatalogItem(item).then(() => setConfirmDeleteId(null))}>
                        Confirm delete
                      </button>
                      <button type="button" onClick={() => setConfirmDeleteId(null)}>
                        Cancel
                      </button>
                    </>
                  ) : (
                    <button type="button" onClick={() => setConfirmDeleteId(item.sessionId)}>
                      Delete
                    </button>
                  )}
                </div>
              )}
            </li>
          );
        })}
      </ul>
      {hasMore ? (
        <button type="button" className="session-rail-more" onClick={() => void refreshCatalog(false)}>
          Load more
        </button>
      ) : null}
    </nav>
  );
}
