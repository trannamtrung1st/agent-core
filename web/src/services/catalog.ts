import {
  OwnerCapabilityError,
  archiveSession,
  durableDeleteSession,
  listCatalog,
  renameSession,
  unarchiveSession,
  type CatalogItem
} from "./api";
import { useSessionStore } from "../state/sessionStore";

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
    catalogError: state.catalogError
  };
}

export async function refreshCatalog(reset = true): Promise<void> {
  const includeArchived = useSessionStore.getState().catalogIncludeArchived;
  try {
    if (reset) {
      const page = await listCatalog({ includeArchived, limit: 50 });
      useSessionStore.setState({
        catalogItems: page.items,
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
      catalogItems: [...existing, ...page.items.filter((item) => !seen.has(item.sessionId))],
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

export async function renameCatalogItem(sessionId: string, title: string): Promise<void> {
  const item = await renameSession(sessionId, title);
  patchCatalog(item);
}

export async function archiveCatalogItem(sessionId: string): Promise<void> {
  await archiveSession(sessionId);
  await refreshCatalog(true);
}

export async function unarchiveCatalogItem(sessionId: string): Promise<void> {
  const item = await unarchiveSession(sessionId);
  patchCatalog(item);
  await refreshCatalog(true);
}

export async function deleteCatalogItem(item: CatalogItem): Promise<void> {
  await durableDeleteSession(item.sessionId, item.revision);
  await refreshCatalog(true);
}

function patchCatalog(item: CatalogItem): void {
  const items = useSessionStore.getState().catalogItems.map((row) =>
    row.sessionId === item.sessionId ? item : row
  );
  useSessionStore.setState({ catalogItems: items });
}
