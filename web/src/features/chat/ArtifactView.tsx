import { useEffect, useRef, useState } from 'react';
import {
  artifactKind,
  artifactSize,
  downloadArtifact,
  getArtifact,
  type ArtifactMetadata,
} from '../../services/artifacts';

import { ChatFileCard } from './ChatFileCard';

export function ArtifactView({
  sessionId,
  artifactId,
}: {
  sessionId: string | null;
  artifactId: string;
}) {
  const [file, setFile] = useState<ArtifactMetadata | null>(null);
  const [metadataFailed, setMetadataFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [downloading, setDownloading] = useState(false);
  const [downloadFailed, setDownloadFailed] = useState(false);
  const download = useRef<AbortController | null>(null);

  useEffect(() => {
    let cancelled = false;
    setFile(null);
    setMetadataFailed(!sessionId);
    setDownloadFailed(false);
    setDownloading(false);
    if (sessionId)
      void getArtifact(sessionId, artifactId)
        .then((value) => {
          if (!cancelled) setFile(value);
        })
        .catch(() => {
          if (!cancelled) setMetadataFailed(true);
        });
    return () => {
      cancelled = true;
      download.current?.abort();
      download.current = null;
    };
  }, [sessionId, artifactId, attempt]);

  async function startDownload() {
    if (!sessionId || !file || download.current) return;
    const controller = new AbortController();
    download.current = controller;
    setDownloading(true);
    setDownloadFailed(false);
    try {
      await downloadArtifact(sessionId, artifactId, controller.signal);
    } catch {
      if (!controller.signal.aborted) setDownloadFailed(true);
    } finally {
      if (!controller.signal.aborted) {
        download.current = null;
        setDownloading(false);
      }
    }
  }

  return (
    <ChatFileCard
      displayName={file?.displayName}
      description={
        file
          ? `${artifactKind(file.contentType)} · ${artifactSize(file.byteSize)}`
          : undefined
      }
      fileRef={
        file && sessionId
          ? { kind: 'artifact', sessionId, artifactId }
          : undefined
      }
      downloading={downloading}
      downloadFailed={downloadFailed}
      metadataFailed={metadataFailed}
      onDownload={() => void startDownload()}
      onRetryMetadata={
        sessionId ? () => setAttempt((value) => value + 1) : undefined
      }
    />
  );
}
