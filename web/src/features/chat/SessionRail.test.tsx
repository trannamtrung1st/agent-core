import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { CatalogItem } from "../../services/api";
import { SessionRail } from "./SessionRail";

vi.mock("../../services/catalog", () => ({
  archiveCatalogItem: vi.fn(),
  deleteCatalogItem: vi.fn(),
  refreshCatalog: vi.fn(),
  renameCatalogItem: vi.fn(),
  setIncludeArchived: vi.fn(),
  unarchiveCatalogItem: vi.fn()
}));

const live: CatalogItem = {
  sessionId: "s1",
  title: "Planning notes",
  agentId: "examiner",
  agentVersion: 1,
  status: "paused",
  archived: false,
  ended: false,
  workspaceOwned: true,
  runtimeEpoch: 0,
  revision: 2,
  createdAt: "2026-09-16T00:00:00.000Z",
  updatedAt: "2026-09-16T00:01:00.000Z"
};

const ended: CatalogItem = {
  ...live,
  sessionId: "s2",
  title: "Closed exam",
  status: "ended",
  ended: true
};

describe("SessionRail", () => {
  it("renders empty state and new chat", () => {
    const onNewChat = vi.fn();
    render(
      <SessionRail
        items={[]}
        agents={[{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }]}
        activeSessionId={null}
        includeArchived={false}
        hasMore={false}
        capabilityLost={false}
        error={null}
        onNewChat={onNewChat}
        onOpen={vi.fn()}
      />
    );
    expect(screen.getByText("No sessions yet.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Start a new chat" }));
    expect(onNewChat).toHaveBeenCalled();
  });

  it("shows agent identity without offering ended actions", () => {
    const onOpen = vi.fn();
    render(
      <SessionRail
        items={[live, ended]}
        agents={[{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "secret", voiceAvailable: true }]}
        activeSessionId={null}
        includeArchived={false}
        hasMore
        capabilityLost={false}
        error={null}
        onNewChat={vi.fn()}
        onOpen={onOpen}
      />
    );
    expect(screen.getByText("Planning notes")).toBeInTheDocument();
    expect(screen.getAllByText(/Alex · Examiner/).length).toBe(2);
    expect(screen.queryByText("secret")).not.toBeInTheDocument();
    expect(screen.getByText(/· Ended/)).toBeInTheDocument();
    const endedButton = screen.getByRole("button", { name: /Closed exam/ });
    expect(endedButton).toBeDisabled();
    fireEvent.click(endedButton);
    expect(onOpen).not.toHaveBeenCalled();
    expect(screen.getAllByRole("button", { name: "Rename" })).toHaveLength(1);
    expect(screen.getByRole("button", { name: "Load more" })).toBeInTheDocument();
  });

  it("fails closed when owner capability is lost", () => {
    render(
      <SessionRail
        items={[]}
        agents={[]}
        activeSessionId={null}
        includeArchived={false}
        hasMore={false}
        capabilityLost
        error="Local owner access is unavailable."
        onNewChat={vi.fn()}
        onOpen={vi.fn()}
      />
    );
    expect(screen.getByRole("alert")).toHaveTextContent("Local owner access is unavailable.");
    expect(screen.queryByText("No sessions yet.")).not.toBeInTheDocument();
  });
});
