import { useCallback, useEffect, useRef } from 'react';
import { App } from 'antd';
import { parseAppRoute, registerNavigationGuard } from '../../app/appRoute';
import { confirmAction } from '../../app/confirmAction';

/** Exact-owner protection; retained editors reset via onDiscard before departure is approved. */
export function useAdminUnsavedChanges(owner: string, dirty: boolean, onDiscard?: () => void) {
  const { modal } = App.useApp();
  const current = useRef({ owner, dirty, onDiscard }); current.current = { owner, dirty, onDiscard };
  const pending = useRef(false);
  useEffect(() => registerNavigationGuard(path => {
    if (!current.current.dirty) return true;
    const route = parseAppRoute(new URL(path, window.location.origin).pathname);
    const next = route.area === 'admin' ? route.view === 'instance' ? `instance:${route.instanceId}`
      : route.view === 'draft' ? `draft:${route.definitionId}:${route.draftId}` : route.view === 'home' ? `global:${route.collection}` : '' : '';
    if (next === current.current.owner) return true;
    if (pending.current) return false;
    pending.current = true;
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const fromMenu = !!previousFocus?.closest('.admin-navigation-drawer');
    let stayed = false;
    return new Promise<boolean>(resolve => {
      confirmAction(modal, { title: 'Discard unsaved changes?', content: 'Only unsaved edits in this workspace will be discarded. Saved data remains available.', okText: 'Discard and leave', cancelText: 'Stay', onOk: () => { current.current.onDiscard?.(); current.current.dirty = false; resolve(true); }, onCancel: () => { stayed = true; resolve(false); }, afterClose: () => {
        pending.current = false;
        if (stayed) {
          const target = previousFocus?.isConnected && previousFocus !== document.body && previousFocus.getClientRects().length && getComputedStyle(previousFocus).visibility !== 'hidden'
            ? previousFocus : document.querySelector<HTMLElement>(`button[aria-label="${fromMenu ? 'Open Admin navigation' : 'Search Admin'}"]`);
          target?.focus({ preventScroll: true });
        }
      } });
    });
  }), [modal, owner]);
  useEffect(() => {
    const unload = (event: BeforeUnloadEvent) => { if (current.current.dirty) { event.preventDefault(); event.returnValue = ''; } };
    window.addEventListener('beforeunload', unload);
    return () => window.removeEventListener('beforeunload', unload);
  }, []);
  // Call only after a confirmed operation has consumed/deleted this workspace.
  return useCallback(() => { current.current.dirty = false; }, []);
}
