import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App, ConfigProvider } from "antd";
import * as antd from "antd";
import type { ComponentProps } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { antdTheme } from "../../app/antdTheme";
import type { CatalogItem } from "../../services/api";
import {
  archiveCatalogItem,
  deleteAllCatalogItems,
  deleteCatalogItem,
  refreshCatalog,
  renameCatalogItem,
  unarchiveCatalogItem
} from "../../services/catalog";
import { useSessionStore } from "../../state/sessionStore";
import { SessionRail } from "./SessionRail";

vi.mock("../../services/catalog", () => ({
  archiveCatalogItem: vi.fn(),
  deleteAllCatalogItems: vi.fn(),
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

function renderRail(props: ComponentProps<typeof SessionRail>) {
  return render(
    <ConfigProvider theme={antdTheme}>
      <App>
        <SessionRail {...props} />
      </App>
    </ConfigProvider>
  );
}

async function openRowMenu(title: string, action: string) {
  fireEvent.click(screen.getByRole("button", { name: `Actions for ${title}` }));
  fireEvent.click(await screen.findByRole("menuitem", { name: action }));
}

describe("SessionRail", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });
  it("renders empty state and new chat", () => {
    const onNewChat = vi.fn();
    renderRail({
      items: [],
      agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
      activeSessionId: null,
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat,
      onOpen: vi.fn()
    });
    expect(screen.getByText("No chats yet.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Start a new chat" }));
    expect(onNewChat).toHaveBeenCalled();
  });

  it("can hide the heading and new-chat control for a drawer chrome", () => {
    renderRail({
      items: [],
      agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
      activeSessionId: null,
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      showHeading: false,
      showNewChat: false,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });
    expect(screen.queryByText("Chats")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Start a new chat" })).not.toBeInTheDocument();
    expect(screen.getByText("No chats yet.")).toBeInTheDocument();
  });

  it("shows agent identity and delete-only actions for ended sessions", async () => {
    const onOpen = vi.fn();
    renderRail({
      items: [live, ended],
      agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "secret", voiceAvailable: true }],
      activeSessionId: null,
      includeArchived: false,
      hasMore: true,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat: vi.fn(),
      onOpen
    });
    expect(screen.getByText("Planning notes")).toBeInTheDocument();
    expect(screen.getAllByText(/Alex · Examiner/).length).toBe(2);
    expect(screen.queryByText("secret")).not.toBeInTheDocument();
    expect(screen.getByText(/· Ended/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Planning notes" }).querySelector("time")).toHaveAttribute(
      "dateTime",
      live.updatedAt
    );
    const endedButton = screen.getByRole("button", { name: "Closed exam" });
    expect(endedButton).toBeEnabled();
    fireEvent.click(endedButton);
    expect(onOpen).toHaveBeenCalledWith(ended);
    expect(screen.getAllByRole("button", { name: /Actions for / })).toHaveLength(2);
    fireEvent.click(screen.getByRole("button", { name: "Actions for Planning notes" }));
    expect(await screen.findByRole("menuitem", { name: "Rename" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Actions for Planning notes" }));
    fireEvent.click(screen.getByRole("button", { name: "Actions for Closed exam" }));
    expect(await screen.findByRole("menuitem", { name: "Delete" })).toBeInTheDocument();
    expect(screen.queryByRole("menuitem", { name: "Rename" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Load more" })).toBeInTheDocument();
  });

  it("labels completed catalog rows without renaming them Ended", () => {
    renderRail({
      items: [{ ...ended, lifecycleStatus: "completed", title: "Done exam" }],
      agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
      activeSessionId: null,
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });
    expect(screen.getByText(/· Completed/)).toBeInTheDocument();
    expect(screen.queryByText(/· Ended/)).not.toBeInTheDocument();
  });

  it("fails closed when owner capability is lost", () => {
    renderRail({
      items: [],
      agents: [],
      activeSessionId: null,
      includeArchived: false,
      hasMore: false,
      capabilityLost: true,
      error: "Local owner access is unavailable.",
      mutation: null,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });
    expect(screen.getByRole("alert")).toHaveTextContent("Local owner access is unavailable.");
    expect(screen.queryByText("No chats yet.")).not.toBeInTheDocument();
  });

  it("rename save, archive, unarchive, and confirm delete call versioned catalog APIs", async () => {
    vi.mocked(renameCatalogItem).mockResolvedValue(true);
    vi.mocked(archiveCatalogItem).mockResolvedValue(true);
    vi.mocked(unarchiveCatalogItem).mockResolvedValue(true);
    vi.mocked(deleteCatalogItem).mockResolvedValue(true);

    const { rerender } = renderRail({
      items: [live],
      agents,
      activeSessionId: "s1",
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });

    await openRowMenu("Planning notes", "Rename");
    fireEvent.change(screen.getByLabelText("Session title"), { target: { value: "Planning notes v2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(renameCatalogItem).toHaveBeenCalledWith("s1", "Planning notes v2"));
    expect(await screen.findByText("Session renamed.")).toBeInTheDocument();

    await openRowMenu("Planning notes", "Archive");
    await waitFor(() => expect(archiveCatalogItem).toHaveBeenCalledWith("s1"));
    expect(await screen.findByText("Session archived.")).toBeInTheDocument();

    rerender(
      <ConfigProvider theme={antdTheme}>
        <App>
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
        </App>
      </ConfigProvider>
    );
    await openRowMenu("Shelved notes", "Unarchive");
    await waitFor(() => expect(unarchiveCatalogItem).toHaveBeenCalledWith("s3"));
    expect(await screen.findByText("Session restored.")).toBeInTheDocument();

    await openRowMenu("Shelved notes", "Delete");
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Delete “Shelved notes”?");
    expect(screen.queryByRole("button", { name: "Confirm delete" })).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(deleteCatalogItem).toHaveBeenCalledWith(archived));
    expect(await screen.findByText("Session deleted.")).toBeInTheDocument();
  });

  it("cancels delete without calling the catalog API", async () => {
    renderRail({
      items: [live],
      agents,
      activeSessionId: "s1",
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });

    fireEvent.click(screen.getByRole("button", { name: "Actions for Planning notes" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: "Delete" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(deleteCatalogItem).not.toHaveBeenCalled();
  });

  it("offers delete but not rename, archive, or unarchive on Ended rows", async () => {
    vi.mocked(deleteCatalogItem).mockResolvedValue(true);
    const onNewChat = vi.fn();
    renderRail({
      items: [ended],
      agents,
      activeSessionId: null,
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat,
      onOpen: vi.fn()
    });
    fireEvent.click(screen.getByRole("button", { name: "Actions for Closed exam" }));
    expect(screen.queryByRole("menuitem", { name: "Rename" })).not.toBeInTheDocument();
    expect(screen.queryByRole("menuitem", { name: "Archive" })).not.toBeInTheDocument();
    expect(screen.queryByRole("menuitem", { name: "Unarchive" })).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole("menuitem", { name: "Delete" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(deleteCatalogItem).toHaveBeenCalledWith(ended));
    expect(onNewChat).not.toHaveBeenCalled();
  });

  it("returns to new chat after deleting the attached session", async () => {
    vi.mocked(deleteCatalogItem).mockResolvedValue(true);
    const onNewChat = vi.fn();
    renderRail({
      items: [live],
      agents,
      activeSessionId: "s1",
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat,
      onOpen: vi.fn()
    });

    await openRowMenu("Planning notes", "Delete");
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(deleteCatalogItem).toHaveBeenCalledWith(live));
    expect(onNewChat).toHaveBeenCalledTimes(1);
    expect(onNewChat).toHaveBeenCalledWith({ urlMode: "replace" });
    expect(await screen.findByText("Session deleted.")).toBeInTheDocument();
  });

  it("rejects delete confirmation when durable delete fails", async () => {
    const messageApi = { success: vi.fn(), error: vi.fn() };
    let onOk: (() => Promise<void>) | undefined;
    vi.spyOn(antd.App, "useApp").mockReturnValue({
      message: messageApi,
      modal: {
        confirm: (config: { onOk?: () => Promise<void> }) => {
          onOk = config.onOk;
        }
      }
    } as unknown as ReturnType<typeof antd.App.useApp>);

    vi.mocked(deleteCatalogItem).mockImplementation(async () => {
      const { useSessionStore } = await import("../../state/sessionStore");
      useSessionStore.setState({ catalogError: "Delete failed." });
      return false;
    });

    renderRail({
      items: [live],
      agents,
      activeSessionId: "s1",
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });

    await openRowMenu("Planning notes", "Delete");
    expect(onOk).toBeTypeOf("function");
    await expect(onOk!()).rejects.toThrow("Delete failed.");
    expect(messageApi.error).toHaveBeenCalledWith("Delete failed.");
    expect(deleteCatalogItem).toHaveBeenCalledTimes(1);
    expect(deleteCatalogItem).toHaveBeenCalledWith(live);
  });

  it("does not retry delete after a revision conflict refresh", async () => {
    const messageApi = { success: vi.fn(), error: vi.fn() };
    let onOk: (() => Promise<void>) | undefined;
    vi.spyOn(antd.App, "useApp").mockReturnValue({
      message: messageApi,
      modal: {
        confirm: (config: { onOk?: () => Promise<void> }) => {
          onOk = config.onOk;
        }
      }
    } as unknown as ReturnType<typeof antd.App.useApp>);

    const refreshed = { ...live, revision: 5, title: "Changed title" };
    vi.mocked(deleteCatalogItem).mockImplementation(async (item) => {
      const { useSessionStore } = await import("../../state/sessionStore");
      if (item.revision === live.revision) {
        useSessionStore.setState({
          catalogItems: [refreshed],
          catalogError: "Session changed. Review it and confirm delete again."
        });
        return false;
      }

      return true;
    });

    renderRail({
      items: [live],
      agents,
      activeSessionId: "s1",
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });

    await openRowMenu("Planning notes", "Delete");
    await expect(onOk!()).rejects.toThrow("Session changed. Review it and confirm delete again.");
    expect(deleteCatalogItem).toHaveBeenCalledTimes(1);
    expect(deleteCatalogItem).toHaveBeenCalledWith(live);
    expect(useSessionStore.getState().catalogItems[0]?.revision).toBe(5);
  });

  it("refreshes the catalog from the heading control", async () => {
    vi.mocked(refreshCatalog).mockResolvedValue();
    renderRail({
      items: [live],
      agents,
      activeSessionId: null,
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });

    fireEvent.click(screen.getByRole("button", { name: "Refresh chats" }));
    await waitFor(() => expect(refreshCatalog).toHaveBeenCalledWith(true));
  });

  it("does not leave an archived active chat when delete all keeps archived rows", async () => {
    vi.mocked(deleteAllCatalogItems).mockResolvedValue(1);
    const onNewChat = vi.fn();
    renderRail({
      items: [archived],
      agents,
      activeSessionId: "s3",
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat,
      onOpen: vi.fn()
    });
    useSessionStore.setState({ catalogItems: [archived] });

    fireEvent.click(screen.getByRole("button", { name: "Chat list options" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: "Delete all chats" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete all" }));
    await waitFor(() => expect(deleteAllCatalogItems).toHaveBeenCalled());
    expect(onNewChat).not.toHaveBeenCalled();
  });

  it("confirms delete all and returns to new chat when the active session is removed", async () => {
    vi.mocked(deleteAllCatalogItems).mockResolvedValue(1);
    const onNewChat = vi.fn();
    renderRail({
      items: [live],
      agents,
      activeSessionId: "s1",
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat,
      onOpen: vi.fn()
    });

    fireEvent.click(screen.getByRole("button", { name: "Chat list options" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: "Delete all chats" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Delete all chats?");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete all" }));
    await waitFor(() => expect(deleteAllCatalogItems).toHaveBeenCalled());
    expect(onNewChat).toHaveBeenCalledWith({ urlMode: "replace" });
    expect(await screen.findByText("Deleted 1 chat.")).toBeInTheDocument();
  });

  it("toasts catalog mutation failures", async () => {
    vi.mocked(renameCatalogItem).mockImplementation(async () => {
      const { useSessionStore } = await import("../../state/sessionStore");
      useSessionStore.setState({ catalogError: "Rename failed." });
      return false;
    });

    renderRail({
      items: [live],
      agents,
      activeSessionId: "s1",
      includeArchived: false,
      hasMore: false,
      capabilityLost: false,
      error: null,
      mutation: null,
      onNewChat: vi.fn(),
      onOpen: vi.fn()
    });

    await openRowMenu("Planning notes", "Rename");
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Rename failed.")).toBeInTheDocument();
  });
});
