import { useEffect, useRef, useState } from "react";
import { Button, Typography } from "antd";
import { DownloadOutlined, FileOutlined } from "@ant-design/icons";
import { artifactKind, artifactSize, downloadArtifact, getArtifact, type ArtifactMetadata } from "../../services/artifacts";

export function ArtifactView({ sessionId, artifactId }: { sessionId: string | null; artifactId: string }) {
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
    if (sessionId) void getArtifact(sessionId, artifactId).then((value) => {
      if (!cancelled) setFile(value);
    }).catch(() => {
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
    <div className="file-chip artifact-card" role="group" aria-label={file?.displayName ?? "File"}
      aria-busy={downloading || (!file && !metadataFailed)}>
      <FileOutlined aria-hidden />
      <div className="artifact-details">
        {file ? <>
          <Typography.Text className="artifact-filename" title={file.displayName}>{file.displayName}</Typography.Text>
          <Typography.Text type="secondary" className="artifact-description">
            {artifactKind(file.contentType)} · {artifactSize(file.byteSize)}
          </Typography.Text>
          {downloadFailed ? <Typography.Text type="danger" role="status">Download failed. Try again.</Typography.Text> : null}
        </> : <Typography.Text type="secondary" role="status">
          {metadataFailed ? "File unavailable" : "Preparing file…"}
        </Typography.Text>}
      </div>
      {file ? <Button type="text" icon={<DownloadOutlined />} loading={downloading} disabled={downloading}
        aria-label={`${downloadFailed ? "Retry download" : "Download"} ${file.displayName}`} onClick={() => void startDownload()}>
        {downloading ? "Downloading…" : downloadFailed ? "Retry" : "Download"}
      </Button> : metadataFailed && sessionId ? <Button type="text" aria-label="Retry file" onClick={() => setAttempt(value => value + 1)}>Retry</Button> : null}
    </div>
  );
}
