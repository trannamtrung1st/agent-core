import { useEffect, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Flex,
  Form,
  Input,
  Modal,
  Select,
  Spin,
  Typography,
  theme,
} from 'antd';
import { CloudUploadOutlined } from '@ant-design/icons';
import {
  DrivePendingError,
  getDriveOptions,
  getSaveAccounts,
  inspectDriveOperation,
  listDriveFiles,
  saveToDrive,
  type DriveAccount,
  type DriveReceipt,
  type DriveFile,
} from '../../services/drive';
import {
  resolveFile,
  sourcePath,
  type FileMetadata,
  type FileRef,
} from './fileSource';
export function SaveToDriveButton({
  file,
  displayName,
  iconOnly = false,
}: {
  file: FileRef;
  displayName: string;
  iconOnly?: boolean;
}) {
  return (
    <SaveEntry
      key={JSON.stringify(file)}
      file={file}
      displayName={displayName}
      iconOnly={iconOnly}
    />
  );
}
function SaveEntry({
  file,
  displayName,
  iconOnly,
}: {
  file: FileRef;
  displayName: string;
  iconOnly: boolean;
}) {
  const [available, setAvailable] = useState(false);
  const [open, setOpen] = useState(false);
  useEffect(() => {
    let cancelled = false;
    void getDriveOptions()
      .then((value) => {
        if (!cancelled) setAvailable(value.enabled);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);
  if (!available || (file.kind !== 'home' && file.kind !== 'artifact'))
    return null;
  return (
    <>
      <Button
        type="text"
        icon={<CloudUploadOutlined aria-hidden />}
        aria-label={`Save ${displayName} to Drive`}
        onClick={() => setOpen(true)}
      >
        {iconOnly ? null : 'Save to Drive'}
      </Button>
      <SaveDialog
        key={JSON.stringify(file)}
        file={file}
        open={open}
        onClose={() => setOpen(false)}
      />
    </>
  );
}
function SaveDialog({
  file,
  open,
  onClose,
}: {
  file: FileRef;
  open: boolean;
  onClose: () => void;
}) {
  const { token } = theme.useToken();
  const [metadata, setMetadata] = useState<FileMetadata | null>(null);
  const [accounts, setAccounts] = useState<DriveAccount[] | null>(null);
  const [instance, setInstance] = useState('');
  const [alias, setAlias] = useState<string>();
  const [folder, setFolder] = useState('root');
  const [name, setName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<DriveReceipt | null>(null);
  const [pending, setPending] = useState<DrivePendingError | null>(null);
  const [busy, setBusy] = useState(false);
  const work = useRef<AbortController | null>(null);
  const operationKey = useRef<string | null>(null);
  const identity = JSON.stringify(file);
  useEffect(() => {
    const controller = new AbortController();
    setMetadata(null);
    setAccounts(null);
    setError(null);
    setReceipt(null);
    setPending(null);
    setFolder('root');
    setBusy(false);
    if (!open) return () => controller.abort();
    void (async () => {
      const [source, targets] = await Promise.all([
        resolveFile(file, controller.signal),
        getSaveAccounts(file, controller.signal),
      ]);
      if (!source.sha256)
        throw new Error(
          'The source revision is unavailable. Reload the file before saving.',
        );
      controller.signal.throwIfAborted();
      setMetadata(source);
      setName(source.displayName);
      setAccounts(targets.accounts);
      setInstance(targets.instanceId);
      const ready = targets.accounts.filter((a) => a.available);
      setAlias(ready.length === 1 ? ready[0].accountRef : undefined);
    })().catch((reason) => {
      if (!controller.signal.aborted)
        setError(
          reason instanceof Error
            ? reason.message
            : 'Drive destinations could not be loaded. Retry.',
        );
    });
    return () => {
      controller.abort();
      work.current?.abort();
    };
  }, [open, identity]);
  const account = accounts?.find((a) => a.accountRef === alias);
  const [folders, setFolders] = useState<DriveFile[]>([]);
  const [folderQuery, setFolderQuery] = useState('');
  const [folderCursor, setFolderCursor] = useState<string | null>(null);
  const folderWork = useRef<AbortController | null>(null);
  useEffect(() => {
    setFolder('root');
  }, [account?.bindingId]);
  useEffect(() => {
    const controller = new AbortController();
    folderWork.current = controller;
    setFolderCursor(null);
    if (!open || !account?.available) return () => controller.abort();
    const timer = setTimeout(() => {
      void listDriveFiles(
        instance,
        account.accountRef,
        undefined,
        undefined,
        folderQuery,
        controller.signal,
      )
        .then((page) => {
          if (!controller.signal.aborted) {
            setFolders((previous) =>
              [
                ...previous.filter((item) => item.id === folder),
                ...page.items.filter(
                  (file) => file.folder && file.id !== folder,
                ),
              ].slice(-100),
            );
            setFolderCursor(page.nextCursor);
          }
        })
        .catch(() => {
          if (!controller.signal.aborted)
            setError(
              'Available folders could not be read. Retry the search or use root.',
            );
        });
    }, 200);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [open, instance, account?.bindingId, account?.authEpoch, folderQuery]);
  async function moreFolders() {
    const controller = folderWork.current;
    if (!account || !folderCursor) return;
    try {
      const page = await listDriveFiles(
        instance,
        account.accountRef,
        undefined,
        folderCursor,
        folderQuery,
        controller?.signal,
      );
      if (controller?.signal.aborted || controller !== folderWork.current)
        return;
      setFolders((previous) =>
        [...previous, ...page.items.filter((file) => file.folder)].slice(-100),
      );
      setFolderCursor(page.nextCursor);
    } catch {
      setError('Available folders could not be read. Retry the search.');
    }
  }
  async function save() {
    if (!metadata?.sha256 || !account || busy) return;
    const controller = new AbortController();
    work.current = controller;
    setBusy(true);
    setError(null);
    setReceipt(null);
    // Persist only opaque operation identity to recover a lost HTTP response across reload.
    const key = `agent-core.drive-upload:${sourcePath(file)}:${account.bindingId}:${folder}:${name}:${metadata.sha256}`;
    operationKey.current = key;
    let id = sessionStorage.getItem(key);
    if (!id) {
      id = crypto.randomUUID();
      sessionStorage.setItem(key, id);
    }
    try {
      const result = await saveToDrive(
        file,
        {
          accountRef: account.accountRef,
          authEpoch: account.authEpoch,
          bindingId: account.bindingId,
          expectedSha256: metadata.sha256,
          operationId: id,
          folderId: folder,
          name,
        },
        controller.signal,
      );
      controller.signal.throwIfAborted();
      if (result.status !== 'Succeeded')
        throw new Error(
          'Drive completion is not verified. Check the operation.',
        );
      setReceipt(result);
      sessionStorage.removeItem(key);
      setPending(null);
    } catch (reason) {
      if (!controller.signal.aborted) {
        if (reason instanceof DrivePendingError) setPending(reason);
        setError(
          reason instanceof Error
            ? reason.message
            : 'Save failed. Read the current state before retrying.',
        );
      }
    } finally {
      if (!controller.signal.aborted) {
        setBusy(false);
        work.current = null;
      }
    }
  }
  async function inspect() {
    if (!pending || !account || busy) return;
    const controller = new AbortController();
    work.current = controller;
    setBusy(true);
    setError(null);
    try {
      const result = await inspectDriveOperation(
        instance,
        account.accountRef,
        pending.operationId,
        controller.signal,
      );
      controller.signal.throwIfAborted();
      setReceipt(result);
      setPending(null);
      if (operationKey.current) sessionStorage.removeItem(operationKey.current);
    } catch (reason) {
      if (!controller.signal.aborted)
        setError(
          reason instanceof Error
            ? reason.message
            : 'The operation remains uncertain. Do not repeat the upload.',
        );
    } finally {
      if (!controller.signal.aborted) {
        setBusy(false);
        work.current = null;
      }
    }
  }
  return (
    <Modal
      open={open}
      closable={{ 'aria-label': 'Close Drive save' }}
      centered
      title="Save as new file in Drive"
      onCancel={onClose}
      footer={null}
      destroyOnHidden
    >
      <Flex vertical gap={token.paddingSM}>
        <Typography.Text type="secondary">
          Choose a bound account and an app-authorized folder. The original file
          is kept.
        </Typography.Text>
        {metadata && (
          <Typography.Text className="single-line-ellipsis" title={metadata.displayName}>
            {metadata.displayName}
          </Typography.Text>
        )}
        {error && <Alert type="warning" showIcon title={error} />}
        {receipt && (
          <Alert
            type="success"
            showIcon
            title={
              receipt.synthetic
                ? 'Verified in Synthetic Drive'
                : 'Verified in Google Drive'
            }
            description={
              <Typography.Text style={{ overflowWrap: 'anywhere' }}>
                {receipt.file.name} · {receipt.file.id}
              </Typography.Text>
            }
          />
        )}
        {!accounts && !error ? (
          <Spin aria-label="Reading Drive destinations" />
        ) : (
          <Form layout="vertical" disabled={busy || !!pending || !!receipt}>
            <Form.Item label="Drive account">
              <Select
                aria-label="Drive account"
                value={alias}
                onChange={setAlias}
                options={accounts
                  ?.filter((a) => a.available)
                  .map((a) => ({
                    value: a.accountRef,
                    label: `${a.displayName} · ${a.emailAddress}`,
                  }))}
              />
            </Form.Item>
            {accounts && !accounts.some((a) => a.available) && (
              <Alert
                type="info"
                showIcon
                title="No connected Drive account is bound"
                description={
                  accounts[0]?.recovery ??
                  'Connect a Google Drive Account in Admin, then bind it to this Instance.'
                }
              />
            )}
            <Form.Item label="File name">
              <Input
                aria-label="Drive file name"
                maxLength={255}
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
            </Form.Item>
            <Form.Item
              label="Folder"
              extra="Search only app-authorized folders. Root creates a new file at the root."
            >
              <Select
                aria-label="Drive folder"
                showSearch={{ filterOption: false, onSearch: setFolderQuery }}
                value={folder}
                onChange={setFolder}
                options={[
                  { value: 'root', label: 'Root' },
                  ...folders.map((item) => ({
                    value: item.id,
                    label: item.name,
                  })),
                ]}
              />
              {folderCursor && (
                <Button type="link" onClick={() => void moreFolders()}>
                  More available folders
                </Button>
              )}
            </Form.Item>
          </Form>
        )}
        <Flex wrap gap={token.paddingXS}>
          {pending ? (
            <Button loading={busy} onClick={() => void inspect()}>
              Check operation
            </Button>
          ) : (
            <Button
              type="primary"
              loading={busy}
              disabled={
                !account?.available ||
                !metadata ||
                !name ||
                !folder ||
                !!receipt
              }
              onClick={() => void save()}
            >
              Save new file
            </Button>
          )}
          <Button onClick={onClose}>Close</Button>
        </Flex>
      </Flex>
    </Modal>
  );
}
