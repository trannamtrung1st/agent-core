import { useEffect, useRef, type ReactNode, type RefObject } from 'react';
import { Drawer, Flex, theme } from 'antd';

/** The Session Sandbox inspector's existing shell, inset and opener-focus owner. */
export function SessionSandboxFrame({ open, narrow, openerRef, onClose, blocked = false, children }: { open: boolean; narrow: boolean; openerRef?: RefObject<HTMLButtonElement | null>; onClose: () => void; blocked?: boolean; children: ReactNode }) {
  const { token } = theme.useToken(); const wasOpen = useRef(false);
  useEffect(() => {
    const closing = wasOpen.current && !open; wasOpen.current = open;
    if (!closing || !openerRef) return;
    const frame = requestAnimationFrame(() => { if (!wasOpen.current) openerRef.current?.focus({ preventScroll: true }); });
    return () => cancelAnimationFrame(frame);
  }, [open, openerRef]);
  return <Drawer title="Session sandbox" className="session-sandbox-drawer" open={open} onClose={onClose} size={narrow ? '100vw' : 'min(640px, 100vw)'} styles={{ wrapper: { maxWidth: '100vw' } }} destroyOnHidden focusable={{ focusTriggerAfterClose: !openerRef }} closable={!blocked} mask={{ closable: !blocked }} keyboard={!blocked}
    afterOpenChange={visible => { if (!visible && openerRef?.current?.isConnected) openerRef.current.focus({ preventScroll: true }); }}>
    <Flex vertical gap={token.padding} onKeyDown={event => {
      if (event.key === 'Escape' && !event.defaultPrevented && !blocked) { event.preventDefault(); event.stopPropagation(); onClose(); }
    }}>{children}</Flex>
  </Drawer>;
}
