import { useEffect, useRef, useState } from 'react';
import {
  artifactKind,
  artifactSize,
  downloadArtifact,
  getArtifact,
  type ArtifactMetadata,
} from '../../services/artifacts';

import { ChatFileCard } from './ChatFileCard';
import { IdentityError } from '../../services/identity';

export function ArtifactView({
  sessionId,
  artifactId,
  tenantId,
}: {
  sessionId: string | null;
  artifactId: string;
  tenantId?: string;
}) {
  const [file, setFile] = useState<ArtifactMetadata | null>(null);
  const [metadataFailed, setMetadataFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [downloading, setDownloading] = useState(false);
  const [downloadFailed, setDownloadFailed] = useState(false);
  const download = useRef<AbortController | null>(null);

  useEffect(() => {
    let cancelled = false;
    const metadataRequest = new AbortController();
    setFile(null);
    setMetadataFailed(!sessionId);
    setDownloadFailed(false);
    setDownloading(false);
    if (sessionId)
      void (tenantId ? getArtifact(sessionId, artifactId, tenantId, metadataRequest.signal) : getArtifact(sessionId, artifactId))
        .then((value) => {
          if (!cancelled) setFile(value);
        })
        .catch(() => {
          if (!cancelled) setMetadataFailed(true);
        });
    return () => {
      cancelled = true;
      metadataRequest.abort();
      download.current?.abort();
      download.current = null;
    };
  }, [tenantId, sessionId, artifactId, attempt]);

  async function startDownload() {
    if (!sessionId || !file || download.current) return;
    const controller = new AbortController();
    download.current = controller;
    setDownloading(true);
    setDownloadFailed(false);
    try {
      if (tenantId) await downloadArtifact(sessionId, artifactId, controller.signal, tenantId);
      else await downloadArtifact(sessionId, artifactId, controller.signal);
    } catch (error) {
      if (!controller.signal.aborted) {
        if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) { setFile(null); setMetadataFailed(true); }
        else setDownloadFailed(true);
      }
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
          ? { kind: 'artifact', sessionId, artifactId, ...(tenantId ? { tenantId } : {}) }
          : undefined
      }
      downloading={downloading}
      downloadFailed={downloadFailed}
      metadataFailed={metadataFailed}
      onDownload={() => void startDownload()}
      onReadLost={() => { setFile(null); setMetadataFailed(true); }}
      onRetryMetadata={
        sessionId ? () => setAttempt((value) => value + 1) : undefined
      }
    />
  );
}
