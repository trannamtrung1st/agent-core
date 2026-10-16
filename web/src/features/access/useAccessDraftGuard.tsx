import { useEffect, useRef } from 'react';
import { Modal } from 'antd';
import { registerNavigationGuard } from '../../app/appRoute';
import { confirmAction } from '../../app/confirmAction';

export function useAccessDraftGuard(dirty: boolean, busy: boolean, onClose: () => void, keepWithin?: string) {
  const [modal, holder] = Modal.useModal();
  const pending = useRef<Promise<boolean> | null>(null);
  const released = useRef(false);
  useEffect(() => { if (dirty && !busy) released.current = false; }, [dirty, busy]);
  const ask = () => {
    if (pending.current) return pending.current;
    let decide!: (discard: boolean) => void;
    const answer = new Promise<boolean>(resolve => { decide = resolve; });
    pending.current = answer;
    let outcome = false;
    confirmAction(modal, { title: 'Discard unsaved changes?', content: 'Your changes have not been saved.', okText: 'Discard changes', cancelText: 'Keep editing', danger: true,
      onOk: () => { outcome = true; }, onCancel: () => { outcome = false; }, afterClose: () => { pending.current = null; decide(outcome); }
    });
    return answer;
  };
  useEffect(() => {
    if (!dirty && !busy) return;
    return registerNavigationGuard(path => released.current || !busy && !!keepWithin && path.startsWith(keepWithin) ? true : busy ? false : ask());
  }, [dirty, busy, modal, keepWithin]);
  useEffect(() => {
    if (!dirty) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);
  const close = () => {
    // Escape is delivered to nested native overlays too. Keep one confirmation owner until its
    // close animation finishes; a single key must never stack discard dialogs behind one another.
    if (busy || pending.current) return;
    if (!dirty) { onClose(); return; }
    void ask().then(discard => { if (discard) onClose(); });
  };
  return { close, holder, release: () => { released.current = true; } };
}
