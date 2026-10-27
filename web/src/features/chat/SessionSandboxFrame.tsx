import { useEffect, useRef, type KeyboardEvent, type ReactNode, type RefObject } from 'react';
import { Drawer, Flex, theme } from 'antd';

/** The Session Sandbox inspector's existing shell, inset and opener-focus owner. */
export function SessionSandboxFrame({ open, narrow, openerRef, onClose, blocked = false, title = 'Session sandbox', children }: { open: boolean; narrow: boolean; openerRef?: RefObject<HTMLButtonElement | null>; onClose: () => void; blocked?: boolean; title?: string; children: ReactNode }) {
  const { token } = theme.useToken(); const wasOpen = useRef(false);
  useEffect(() => {
    const closing = wasOpen.current && !open; wasOpen.current = open;
    if (!closing || !openerRef) return;
    const frame = requestAnimationFrame(() => { if (!wasOpen.current) openerRef.current?.focus({ preventScroll: true }); });
    return () => cancelAnimationFrame(frame);
  }, [open, openerRef]);
  useEffect(() => () => {
    // Private scope replacement can unmount an open inspector instead of running
    // its close animation. Restore only when no new modal owns keyboard focus.
    if (wasOpen.current && openerRef) requestAnimationFrame(() => {
      if (!document.querySelector('[role="dialog"][aria-modal="true"]') && openerRef.current?.isConnected)
        openerRef.current.focus({ preventScroll: true });
    });
  }, [openerRef]);
  return <Drawer title={title} className="session-sandbox-drawer" open={open} onClose={onClose} size={narrow ? '100vw' : 'min(640px, 100vw)'} styles={{ wrapper: { maxWidth: '100vw', transitionProperty: 'transform' } }} destroyOnHidden focusable={{ trap: true, focusTriggerAfterClose: !openerRef }} closable={!blocked} mask={{ closable: !blocked }} keyboard={!blocked}
    onKeyDown={event => {
      trapSessionInspectorFocus(event);
    }}
    afterOpenChange={visible => { if (!visible && openerRef?.current?.isConnected) openerRef.current.focus({ preventScroll: true }); }}>
    <Flex vertical gap={token.padding} onKeyDown={event => {
      if (event.key === 'Escape' && !event.defaultPrevented && !blocked) { event.preventDefault(); event.stopPropagation(); onClose(); }
    }}>{children}</Flex>
  </Drawer>;
}

/** Native tab boundary shared by the operational Session inspectors. */
export function trapSessionInspectorFocus(event: KeyboardEvent) {
      if (event.key !== 'Tab' || event.defaultPrevented) return;
      const panel = (event.target as HTMLElement).closest<HTMLElement>('[role="dialog"]');
      if (!panel) return;
      const controls = Array.from(panel.querySelectorAll<HTMLElement>('a[href],button,input,select,textarea,[tabindex]'))
        .filter(node => node.tabIndex >= 0 && !node.matches(':disabled,[aria-disabled="true"]') && node.getClientRects().length > 0 && getComputedStyle(node).visibility !== 'hidden');
      const edge = event.shiftKey ? controls[0] : controls.at(-1);
      if (edge && document.activeElement === edge) {
        event.preventDefault(); (event.shiftKey ? controls.at(-1) : controls[0])?.focus();
      }
}
