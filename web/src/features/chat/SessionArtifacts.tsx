import { useCallback } from "react";
import { Alert, Empty, Flex, Spin, Typography, theme } from "antd";
import { listArtifactPage } from "../../services/artifacts";
import { useCursorPages } from "./useCursorPages";
import { ArtifactView } from "./ArtifactView";
import { DrawerListFooter } from "./DrawerListFooter";

export function SessionArtifacts({ sessionId, open, agentRunId }: { sessionId: string; open: boolean; agentRunId?: string }) {
  const fetchPage = useCallback((_scope: string, before?: string) => agentRunId ? listArtifactPage(sessionId, before, agentRunId) : listArtifactPage(sessionId, before), [sessionId, agentRunId]);
  const page = useCursorPages(`${sessionId}:${agentRunId ?? "all"}`, open, fetchPage);
  const { token } = theme.useToken();
  return <Flex vertical gap={token.padding}>
    <Typography.Text strong>{agentRunId ? "Original task files" : "Files"}</Typography.Text>
    {agentRunId ? <Typography.Text type="secondary">Only files attributed to this run are shown. Older files without run ownership remain available in chat.</Typography.Text> : null}
    {page.loading ? <Spin aria-label="Loading files" /> : !page.items.length && !page.error ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No files yet" /> : null}
    {page.items.map(file => <ArtifactView key={file.artifactId} sessionId={sessionId} artifactId={file.artifactId} />)}
    {page.error ? <Alert type="error" showIcon title={page.error} /> : null}
    <DrawerListFooter loadingMore={page.loadingMore} hasMore={page.hasMore} error={page.error} count={page.items.length}
      onLoadMore={() => void page.loadMore()} onRetry={() => void page.retry()} />
  </Flex>;
}
