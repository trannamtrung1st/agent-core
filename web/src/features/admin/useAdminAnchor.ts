import { useEffect, useRef } from 'react';
/** Focus a newly requested anchor once, after lazy/async content mounts. */
export function useAdminAnchor(page: string, ready: boolean) {
  const handled = useRef<string | null>(null);
  useEffect(() => {
    let observer: MutationObserver | null = null;
    let frame = 0;
    const request = () => {
      observer?.disconnect();
      if (!ready || !window.location.hash) { if (!window.location.hash) handled.current = null; return; }
      const href = window.location.pathname + window.location.hash;
      if (handled.current === href) return;
      let id: string; try { id = decodeURIComponent(window.location.hash.slice(1)); } catch { return; }
      const focus = () => {
        const target = document.getElementById(id);
        if (!target || !target.getClientRects().length) return false;
        target.scrollIntoView?.({ block: 'start' }); target.focus({ preventScroll: true }); handled.current = href; return true;
      };
      if (focus()) return;
      observer = new MutationObserver(() => { if (focus()) observer?.disconnect(); });
      observer.observe(document.body, { subtree: true, childList: true, attributes: true });
    };
    const pop = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(request); };
    request(); window.addEventListener('popstate', pop);
    return () => { cancelAnimationFrame(frame); observer?.disconnect(); window.removeEventListener('popstate', pop); };
  }, [page, ready]);
}
