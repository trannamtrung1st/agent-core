import { useEffect, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Empty,
  Flex,
  Input,
  Modal,
  Spin,
  Table,
  Typography,
  theme,
} from 'antd';
import {
  getDriveAccounts,
  listDriveFiles,
  safeDriveLink,
  type DriveAccount,
  type DriveFile,
  type DrivePage,
} from '../../services/drive';
import { FilePreviewButton } from '../filePreview/FilePreview';
import { artifactSize } from '../../services/artifacts';
import {
  CloseCircleFilled,
  FolderOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
export function DriveFilesButton({
  instanceId,
  accountRef,
  disabled = false,
}: {
  instanceId: string;
  accountRef: string;
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);
  return (
    <>
      <Button disabled={disabled} onClick={() => setOpen(true)}>
        Browse Drive files
      </Button>
      <DriveFiles
        key={`${instanceId}:${accountRef}`}
        instanceId={instanceId}
        accountRef={accountRef}
        open={open}
        onClose={() => setOpen(false)}
      />
    </>
  );
}
function DriveFiles({
  instanceId,
  accountRef,
  open,
  onClose,
}: {
  instanceId: string;
  accountRef: string;
  open: boolean;
  onClose: () => void;
}) {
  const { token } = theme.useToken();
  const [account, setAccount] = useState<DriveAccount | null>(null);
  const [page, setPage] = useState<DrivePage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [request, setRequest] = useState<{
    folder?: string;
    cursor?: string;
    query?: string;
    attempt: number;
  }>({ attempt: 0 });
  const epoch = useRef(0);
  useEffect(() => {
    const controller = new AbortController();
    const generation = ++epoch.current;
    setPage(null);
    setAccount(null);
    setError(null);
    if (!open) return () => controller.abort();
    void (async () => {
      const accounts = await getDriveAccounts(instanceId, controller.signal);
      const active = accounts.find((a) => a.accountRef === accountRef);
      if (!active?.available)
        throw new Error(
          active?.recovery ??
            'The Drive binding is unavailable. Reload credentials.',
        );
      const data = await listDriveFiles(
        instanceId,
        accountRef,
        request.folder,
        request.cursor,
        request.query,
        controller.signal,
      );
      if (generation === epoch.current) {
        setAccount(active);
        setPage(data);
      }
    })().catch((reason) => {
      if (!controller.signal.aborted && generation === epoch.current)
        setError(
          reason instanceof Error
            ? reason.message
            : 'Drive files could not be loaded. Retry.',
        );
    });
    return () => {
      controller.abort();
      epoch.current++;
    };
  }, [open, instanceId, accountRef, request]);
  return (
    <Modal
      className="file-preview-modal"
      open={open}
      centered
      title="Files available to Agent Core"
      closable={{ 'aria-label': 'Close Drive files' }}
      width="90vw"
      style={{ maxWidth: 1200 }}
      footer={null}
      onCancel={onClose}
      destroyOnHidden
    >
      <Flex
        className="file-preview-shell"
        vertical
        gap={token.paddingSM}
        style={{ minWidth: 0 }}
      >
        <Typography.Text type="secondary">
          App-authorized files only · full Drive search, Google-native editing
          and Shared Drives are deferred.
          {account?.synthetic ? ' Synthetic fixture; no Google call.' : ''}
        </Typography.Text>
        <Flex className="file-preview-toolbar" wrap gap={token.paddingXS}>
          <Input.Search
            className="drive-file-search"
            allowClear={{
              clearIcon: <CloseCircleFilled aria-label="Clear Drive search" />,
            }}
            aria-label="Search app-authorized Drive files"
            placeholder="Search available file names"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            onSearch={(value) =>
              setRequest({
                ...request,
                cursor: undefined,
                query: value,
                attempt: request.attempt + 1,
              })
            }
          />
          <Button
            icon={<ReloadOutlined aria-hidden />}
            onClick={() =>
              setRequest({
                ...request,
                cursor: undefined,
                attempt: request.attempt + 1,
              })
            }
          >
            Reload files
          </Button>
          {request.folder && (
            <Button
              onClick={() => setRequest({ attempt: request.attempt + 1 })}
            >
              All available files
            </Button>
          )}
        </Flex>
        <div className="file-preview-content">
          {error ? (
            <Alert
              type="warning"
              showIcon
              title={error}
              action={
                <Button
                  onClick={() =>
                    setRequest({ ...request, attempt: request.attempt + 1 })
                  }
                >
                  Retry files
                </Button>
              }
            />
          ) : !page ? (
            <Spin aria-label="Loading Drive files" />
          ) : (
            <>
              <Typography.Text
                type="secondary"
                className="drive-file-scroll-hint"
              >
                Scroll horizontally for file details and actions.
              </Typography.Text>
              <Table<DriveFile>
                size="small"
                className="admin-collection-table"
                rowKey="id"
                pagination={false}
                scroll={{ x: 800 }}
                dataSource={page.items}
                locale={{
                  emptyText: (
                    <Empty
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                      description="No files available to Agent Core in this view"
                    />
                  ),
                }}
                columns={[
                  {
                    title: 'Name',
                    dataIndex: 'name',
                    ellipsis: true,
                    width: 300,
                    render: (name: string, file) => (
                      <Typography.Text
                        title={name}
                        className="single-line-ellipsis"
                      >
                        {file.folder ? (
                          <Button
                            type="link"
                            icon={<FolderOutlined aria-hidden />}
                            className="drive-file-folder"
                            onClick={() =>
                              setRequest({
                                folder: file.id,
                                attempt: request.attempt + 1,
                              })
                            }
                          >
                            <span className="single-line-ellipsis">{name}</span>
                          </Button>
                        ) : (
                          name
                        )}
                      </Typography.Text>
                    ),
                  },
                  {
                    title: 'Type',
                    dataIndex: 'mimeType',
                    ellipsis: true,
                    width: 200,
                  },
                  {
                    title: 'Size',
                    dataIndex: 'byteSize',
                    width: 90,
                    render: artifactSize,
                  },
                  {
                    title: 'Actions',
                    width: 180,
                    render: (_, file) => (
                      <Flex gap={token.paddingXS}>
                        {!file.folder && !file.nativeDocument && account && (
                          <FilePreviewButton
                            disabled={!open}
                            file={{
                              kind: 'drive',
                              instanceId,
                              accountRef,
                              fileId: file.id,
                              version: file.version,
                              authEpoch: account.authEpoch,
                              bindingId: account.bindingId,
                              synthetic: account.synthetic,
                              metadata: {
                                displayName: file.name,
                                contentType: file.mimeType,
                                byteSize: file.byteSize,
                              },
                            }}
                          />
                        )}{' '}
                        {safeDriveLink(file.webViewLink) && (
                          <Button
                            type="link"
                            href={safeDriveLink(file.webViewLink)!}
                            target="_blank"
                            rel="noreferrer noopener"
                          >
                            Open in Drive
                          </Button>
                        )}
                      </Flex>
                    ),
                  },
                ]}
              />
            </>
          )}
        </div>
        {page?.nextCursor && (
          <Button
            onClick={() =>
              setRequest({
                ...request,
                cursor: page.nextCursor!,
                attempt: request.attempt + 1,
              })
            }
          >
            Next files
          </Button>
        )}
      </Flex>
    </Modal>
  );
}
