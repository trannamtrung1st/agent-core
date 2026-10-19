import { useState } from 'react';
import { Alert, Button, Flex, Typography } from 'antd';
import { ArtifactView } from '../chat/ArtifactView';

export interface BrowserArtifactResult {
  status: 'published' | 'empty' | 'unavailable';
  reasonCode: string | null;
  files: { kind: 'capture' | 'download'; artifactId: string }[];
}
const reasons: Record<string, string> = {
  browser_command_not_completed: 'The Browser command did not confirm completion, so no files were published.',
  artifact_publication_unavailable: 'File publication could not be completed.',
  permission_denied: 'Current permission no longer allowed file publication.',
  artifact_quota_exceeded: 'The Session file storage limit prevented publication.',
  capture_limit: 'The capture size or task limit prevented publication of every image.',
  download_limit: 'The download size or task limit prevented publication of every download.',
  capture_invalid: 'A captured image could not be verified for publication.',
  download_invalid: 'A downloaded file could not be verified for publication.',
};

/** Publication facts never confer recipient read authority. File cards use canonical scoped reads. */
export function BrowserArtifactPublication({ result, tenantId, sessionId }: {
  result: BrowserArtifactResult; tenantId: string; sessionId: string;
}) {
  const [inspect, setInspect] = useState(false);
  const published = result.status === 'published' && result.files.length > 0;
  const partial = published && result.reasonCode !== null;
  const captures = result.files.filter(file => file.kind === 'capture').length;
  const downloads = result.files.filter(file => file.kind === 'download').length;
  const counts = [captures > 0 ? `${captures} captured ${captures === 1 ? 'image' : 'images'}` : null,
    downloads > 0 ? `${downloads} downloaded ${downloads === 1 ? 'file' : 'files'}` : null].filter(Boolean).join(' and ');
  return <section aria-label="Browser files">
    <Typography.Title level={5}>Browser files</Typography.Title>
    <Flex vertical align="stretch" gap="var(--ac-space-compact)">
      <Alert showIcon type={partial || result.status === 'unavailable' ? 'warning' : published ? 'success' : 'info'}
        title={published ? partial ? 'Some files published' : 'Files published' : result.status === 'empty' ? 'No files published' : 'File publication unavailable'}
        description={<Flex vertical gap="var(--ac-space-tight)">
          {published && <span>{counts} saved to this conversation.</span>}
          {result.status === 'empty' && <span>The command produced no files for publication.</span>}
          {result.reasonCode && <span>{reasons[result.reasonCode] ?? 'Some requested files could not be published.'}</span>}
          {(partial || result.status === 'unavailable') && <span>This request will not run again.</span>}
        </Flex>} />
      {published && <>
        <Typography.Text type="secondary">Viewing or downloading these files requires your current file access.</Typography.Text>
        {!inspect ? <div><Button onClick={() => setInspect(true)}>Inspect published files</Button></div>
          : <Flex vertical gap="var(--ac-space-compact)">{result.files.map(file => <ArtifactView key={file.artifactId} tenantId={tenantId} sessionId={sessionId} artifactId={file.artifactId} />)}</Flex>}
      </>}
    </Flex>
  </section>;
}
