import { AdminRequestError } from '../../services/adminApi';
import { describeAdminError } from './adminErrors';
import { showAdminFailure } from './adminFailure';
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { Alert, App, Button, Flex, Typography } from 'antd';
import { ReloadOutlined, SaveOutlined, UndoOutlined } from '@ant-design/icons';

type Editor = { dirty: boolean; busy: boolean; revision?: number; blocked?: string; save: (revision: number) => Promise<number>; discard: () => void; refresh?: () => Promise<unknown> };
type ConfigurationContext = { register: (key: string, editor: Editor | null) => void; saving: boolean; editors: Record<string, Editor>; revision: number; disabled: boolean; run: (action: 'save' | 'refresh' | 'discard') => Promise<void>; error: string | null; status: string | null };
const Configuration = createContext<ConfigurationContext | null>(null);
/** Coordinates existing Instance configuration commands; each endpoint retains its CAS boundary. */
export function InstanceConfigurationProvider({ revision, disabled, onUpdated, children }: { revision: number; disabled: boolean; onUpdated: () => void | Promise<void>; children: ReactNode }) {
  const { message } = App.useApp();
  const [editors, setEditors] = useState<Record<string, Editor>>({});
  const [saving, setSaving] = useState(false); const lock = useRef(false); const generation = useRef(0);
  useEffect(() => () => { generation.current++; }, []);
  const [error, setError] = useState<string | null>(null); const [status, setStatus] = useState<string | null>(null);
  const register = useCallback((key: string, editor: Editor | null) => setEditors(current => {
    const next = { ...current }; if (editor) next[key] = editor; else delete next[key]; return next;
  }), []);
  async function run(action: 'save' | 'refresh' | 'discard') {
    if (lock.current || disabled && action === 'save') return;
    const pending = Object.entries(editors).filter(([, editor]) => editor.dirty);
    if (action === 'discard') { pending.forEach(([, editor]) => editor.discard()); setError(null); setStatus(null); return; }
    lock.current = true; setSaving(true); setError(null); setStatus(null);
    let committed = 0; const request = generation.current;
    try {
      if (action === 'refresh') {
        for (const editor of Object.values(editors)) { if (request !== generation.current) return; await editor.refresh?.(); }
      } else {
        for (const [key, editor] of pending) {
          if (editor.busy || editor.blocked) throw new Error(editor.blocked ?? `${key} is still loading.`);
          if (editor.revision !== revision) throw new Error(`${key} needs current revisions. Refresh configuration; your edits are retained.`);
        }
        let nextRevision = revision;
        for (const [, editor] of pending) {
          if (request !== generation.current) return;
          nextRevision = await editor.save(nextRevision);
          if (request !== generation.current) return;
          if (!Number.isInteger(nextRevision)) throw new Error('The saved revision could not be confirmed. Refresh configuration before retrying.');
          committed++;
        }
        setStatus('Configuration saved. Changes apply to the next new Run.');
      }
    } catch (reason) {
      if (request !== generation.current) return;
      const failure = describeAdminError(reason, 'Configuration could not be saved.');
      const detail = failure.message;
      const notice = showAdminFailure(message, new AdminRequestError(`${committed ? `${committed} configuration group${committed === 1 ? '' : 's'} saved before the failure. ` : ''}${detail}${/uncommitted edits are retained/i.test(detail) ? '' : ' Uncommitted edits are retained.'}`, failure.diagnosticId), 'Configuration could not be saved.', action === 'save' ? 'Save failed' : 'Refresh failed');
      setError(notice.message);
    } finally {
      if (request !== generation.current) return;
      try { await onUpdated(); } catch { setError(showAdminFailure(message, new Error('Saved state could not be refreshed. Retry the Instance read; uncommitted edits are retained.'), 'Refresh failed.', 'Refresh failed').message); } finally { lock.current = false; setSaving(false); }
    }
  }
  return <Configuration.Provider value={{ register, saving, editors, revision, disabled, run, error, status }}>{children}</Configuration.Provider>;
}
export function useInstanceConfigurationEditor(key: string, editor: Editor) {
  const context = useContext(Configuration); const latest = useRef(editor); latest.current = editor;
  const register = context?.register;
  useEffect(() => {
    if (!register) return;
    register(key, { dirty: editor.dirty, busy: editor.busy, revision: editor.revision, blocked: editor.blocked,
      save: revision => latest.current.save(revision), discard: () => latest.current.discard(), refresh: () => latest.current.refresh?.() ?? Promise.resolve() });
    return () => register(key, null);
  }, [register, key, editor.dirty, editor.busy, editor.revision, editor.blocked]);
  return { managed: !!context, saving: context?.saving ?? false };
}
export function InstanceConfigurationActions() {
  const context = useContext(Configuration); if (!context) return null;
  const pending = Object.values(context.editors).filter(editor => editor.dirty);
  const blockedEntry = Object.entries(context.editors).find(([, editor]) => editor.dirty && (editor.busy || !!editor.blocked || editor.revision !== context.revision));
  const blocked = !!blockedEntry;
  return <section className="admin-owner-actions admin-instance-actions" aria-label="Instance configuration actions">
    <Flex align="center" wrap gap="var(--ac-space-compact)">
      <Button type="primary" icon={<SaveOutlined aria-hidden />} aria-label="Save configuration" loading={context.saving} disabled={context.disabled || context.saving || !pending.length || blocked} onClick={() => void context.run('save')}>Save changes</Button>
      <Button icon={<UndoOutlined aria-hidden />} aria-label="Discard configuration changes" disabled={context.saving || !pending.length} onClick={() => void context.run('discard')}>Discard</Button>
      <Button type="text" icon={<ReloadOutlined aria-hidden />} aria-label="Refresh configuration" disabled={context.saving} onClick={() => void context.run('refresh')}>Refresh</Button>
      <Typography.Text type={pending.length ? 'warning' : context.status ? 'success' : 'secondary'} role={context.status && !pending.length ? "status" : undefined} aria-label="Instance configuration status" aria-live="polite">{pending.length ? `${pending.length} configuration group${pending.length === 1 ? '' : 's'} with unsaved changes` : context.status ?? 'Configuration is saved'}</Typography.Text>
    </Flex>
    {context.error && <Alert showIcon type="error" title={context.error} />}
    {blocked && !context.error && <Typography.Text type="secondary">{blockedEntry?.[1].blocked ?? (blockedEntry?.[1].busy ? 'Wait for configuration loading to finish.' : 'Refresh configuration before saving. Edits stay in place.')}</Typography.Text>}
  </section>;
}
export function InstanceConfigurationContent({ children }: { children: ReactNode }) {
  const context = useContext(Configuration);
  return <div inert={context?.saving}>{children}</div>;
}
