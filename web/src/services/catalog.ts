import {
  OwnerCapabilityError,
  archiveSession,
  durableDeleteSession,
  listCatalog,
  renameSession,
  unarchiveSession,
  type CatalogItem
} from "./api";
import { useSessionStore, type CatalogMutationKind } from "../state/sessionStore";

function sortCatalogItems(items: CatalogItem[]): CatalogItem[] {
  return [...items].sort((left, right) => {
    const byUpdated = Date.parse(right.updatedAt) - Date.parse(left.updatedAt);
    if (byUpdated !== 0) {
      return byUpdated;
    }

    return right.sessionId.localeCompare(left.sessionId);
  });
}

export function catalogShell() {
  const state = useSessionStore.getState();
  return {
    agents: state.agents,
    selectedAgentId: state.selectedAgentId,
    catalogItems: state.catalogItems,
    catalogNextCursor: state.catalogNextCursor,
    catalogHasMore: state.catalogHasMore,
    catalogIncludeArchived: state.catalogIncludeArchived,
    catalogCapabilityLost: state.catalogCapabilityLost,
    catalogError: state.catalogError,
    catalogMutation: state.catalogMutation
  };
}

export async function refreshCatalog(reset = true): Promise<void> {
  const includeArchived = useSessionStore.getState().catalogIncludeArchived;
  try {
    if (reset) {
      const page = await listCatalog({ includeArchived, limit: 50 });
      useSessionStore.setState({
        catalogItems: sortCatalogItems(page.items),
        catalogNextCursor: page.nextCursor,
        catalogHasMore: page.hasMore,
        catalogCapabilityLost: false,
        catalogError: null
      });
      return;
    }

    const cursor = useSessionStore.getState().catalogNextCursor;
    const page = await listCatalog({ cursor, includeArchived, limit: 50 });
    const existing = useSessionStore.getState().catalogItems;
    const seen = new Set(existing.map((item) => item.sessionId));
    useSessionStore.setState({
      catalogItems: sortCatalogItems([
        ...existing,
        ...page.items.filter((item) => !seen.has(item.sessionId))
      ]),
      catalogNextCursor: page.nextCursor,
      catalogHasMore: page.hasMore,
      catalogCapabilityLost: false,
      catalogError: null
    });
  } catch (error) {
    if (error instanceof OwnerCapabilityError) {
      useSessionStore.setState({
        catalogItems: [],
        catalogNextCursor: null,
        catalogHasMore: false,
        catalogCapabilityLost: true,
        catalogError: error.message
      });
      return;
    }

    useSessionStore.setState({
      catalogError: error instanceof Error ? error.message : "Unable to load sessions."
    });
  }
}

export async function setIncludeArchived(includeArchived: boolean): Promise<void> {
  useSessionStore.setState({ catalogIncludeArchived: includeArchived });
  await refreshCatalog(true);
}

function beginCatalogMutation(sessionId: string, kind: CatalogMutationKind): void {
  useSessionStore.setState({
    catalogMutation: { sessionId, kind },
    catalogError: null
  });
}

function endCatalogMutation(): void {
  useSessionStore.setState({ catalogMutation: null });
}

function failCatalogMutation(message: string): void {
  useSessionStore.setState({
    catalogMutation: null,
    catalogError: message
  });
}

async function runCatalogMutation(
  sessionId: string,
  kind: CatalogMutationKind,
  action: () => Promise<void>
): Promise<boolean> {
  beginCatalogMutation(sessionId, kind);
  try {
    await action();
    useSessionStore.setState({ catalogError: null });
    return true;
  } catch (error) {
    failCatalogMutation(error instanceof Error ? error.message : "Unable to update the session catalog.");
    return false;
  } finally {
    endCatalogMutation();
  }
}

export async function renameCatalogItem(sessionId: string, title: string): Promise<boolean> {
  return runCatalogMutation(sessionId, "rename", async () => {
    const item = await renameSession(sessionId, title);
    patchCatalog(item);
  });
}

export async function archiveCatalogItem(sessionId: string): Promise<boolean> {
  return runCatalogMutation(sessionId, "archive", async () => {
    await archiveSession(sessionId);
    await refreshCatalog(true);
  });
}

export async function unarchiveCatalogItem(sessionId: string): Promise<boolean> {
  return runCatalogMutation(sessionId, "unarchive", async () => {
    const item = await unarchiveSession(sessionId);
    patchCatalog(item);
    await refreshCatalog(true);
  });
}

export async function deleteCatalogItem(item: CatalogItem): Promise<boolean> {
  return runCatalogMutation(item.sessionId, "delete", async () => {
    await durableDeleteSession(item.sessionId);
    await refreshCatalog(true);
  });
}

function patchCatalog(item: CatalogItem): void {
  const items = sortCatalogItems(
    useSessionStore.getState().catalogItems.map((row) =>
      row.sessionId === item.sessionId ? item : row
    )
  );
  useSessionStore.setState({ catalogItems: items });
}
