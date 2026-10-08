import { Alert, Empty, Flex, Spin, Typography, theme } from "antd";
import { listArtifactPage } from "../../services/artifacts";
import { useCursorPages } from "./useCursorPages";
import { ArtifactView } from "./ArtifactView";
import { DrawerListFooter } from "./DrawerListFooter";

export function SessionArtifacts({ sessionId, open }: { sessionId: string; open: boolean }) {
  const page = useCursorPages(sessionId, open, listArtifactPage);
  const { token } = theme.useToken();
  return <Flex vertical gap={token.padding}>
    <Typography.Text strong>Files</Typography.Text>
    {page.loading ? <Spin aria-label="Loading files" /> : !page.items.length && !page.error ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No files yet" /> : null}
    {page.items.map(file => <ArtifactView key={file.artifactId} sessionId={sessionId} artifactId={file.artifactId} />)}
    {page.error ? <Alert type="error" showIcon title={page.error} /> : null}
    <DrawerListFooter loadingMore={page.loadingMore} hasMore={page.hasMore} error={page.error} count={page.items.length}
      onLoadMore={() => void page.loadMore()} onRetry={() => void page.retry()} />
  </Flex>;
}
