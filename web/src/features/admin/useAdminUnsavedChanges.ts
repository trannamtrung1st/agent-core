import { useCallback, useEffect, useRef } from 'react';
import { App } from 'antd';
import { parseAppRoute, registerNavigationGuard } from '../../app/appRoute';
import { confirmAction } from '../../app/confirmAction';

/** Exact-owner draft protection; same-owner section changes retain the workspace. */
export function useAdminUnsavedChanges(owner: string, dirty: boolean) {
  const { modal } = App.useApp();
  const current = useRef({ owner, dirty }); current.current = { owner, dirty };
  const pending = useRef(false);
  useEffect(() => registerNavigationGuard(path => {
    if (!current.current.dirty) return true;
    const route = parseAppRoute(new URL(path, window.location.origin).pathname);
    const next = route.area === 'admin' ? route.view === 'instance' ? `instance:${route.instanceId}`
      : route.view === 'draft' ? `draft:${route.definitionId}:${route.draftId}` : route.view === 'home' ? `global:${route.collection}` : '' : '';
    if (next === current.current.owner) return true;
    if (pending.current) return false;
    pending.current = true;
    return new Promise<boolean>(resolve => {
      confirmAction(modal, { title: 'Discard unsaved changes?', content: 'Only unsaved edits in this workspace will be discarded. Saved data remains available.', okText: 'Discard and leave', cancelText: 'Stay', onOk: () => { current.current.dirty = false; resolve(true); }, onCancel: () => resolve(false), afterClose: () => { pending.current = false; } });
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
