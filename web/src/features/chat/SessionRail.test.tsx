import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { CatalogItem } from "../../services/api";
import {
  archiveCatalogItem,
  deleteCatalogItem,
  renameCatalogItem,
  unarchiveCatalogItem
} from "../../services/catalog";
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

const archived: CatalogItem = {
  ...live,
  sessionId: "s3",
  title: "Shelved notes",
  archived: true,
  status: "paused"
};

const agents = [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }];

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
        mutation={null}
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
        mutation={null}
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
        mutation={null}
        onNewChat={vi.fn()}
        onOpen={vi.fn()}
      />
    );
    expect(screen.getByRole("alert")).toHaveTextContent("Local owner access is unavailable.");
    expect(screen.queryByText("No sessions yet.")).not.toBeInTheDocument();
  });

  it("rename save, archive, unarchive, and confirm delete call versioned catalog APIs", async () => {
    vi.mocked(renameCatalogItem).mockResolvedValue(true);
    vi.mocked(archiveCatalogItem).mockResolvedValue(true);
    vi.mocked(unarchiveCatalogItem).mockResolvedValue(true);
    vi.mocked(deleteCatalogItem).mockResolvedValue(true);

    const { rerender } = render(
      <SessionRail
        items={[live]}
        agents={agents}
        activeSessionId="s1"
        includeArchived={false}
        hasMore={false}
        capabilityLost={false}
        error={null}
        mutation={null}
        onNewChat={vi.fn()}
        onOpen={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    fireEvent.change(screen.getByLabelText("Session title"), { target: { value: "Planning notes v2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(renameCatalogItem).toHaveBeenCalledWith("s1", "Planning notes v2"));

    fireEvent.click(screen.getByRole("button", { name: "Archive" }));
    await waitFor(() => expect(archiveCatalogItem).toHaveBeenCalledWith("s1"));

    rerender(
      <SessionRail
        items={[archived]}
        agents={agents}
        activeSessionId={null}
        includeArchived
        hasMore={false}
        capabilityLost={false}
        error={null}
        mutation={null}
        onNewChat={vi.fn()}
        onOpen={vi.fn()}
      />
    );
    fireEvent.click(screen.getByRole("button", { name: "Unarchive" }));
    await waitFor(() => expect(unarchiveCatalogItem).toHaveBeenCalledWith("s3"));

    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm delete" }));
    await waitFor(() => expect(deleteCatalogItem).toHaveBeenCalledWith(archived));
  });

  it("does not offer rename, archive, unarchive, or delete on Ended rows", () => {
    render(
      <SessionRail
        items={[ended]}
        agents={agents}
        activeSessionId={null}
        includeArchived={false}
        hasMore={false}
        capabilityLost={false}
        error={null}
        mutation={null}
        onNewChat={vi.fn()}
        onOpen={vi.fn()}
      />
    );
    expect(screen.queryByRole("button", { name: "Rename" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Archive" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Unarchive" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Delete" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Confirm delete" })).not.toBeInTheDocument();
  });
});
