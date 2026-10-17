import { useLayoutEffect } from "react";

type Bounds = Pick<DOMRect, "top" | "right" | "bottom" | "left">;

/** Reserve space only when a visible action surface intersects the default toast. */
export function feedbackBottom(viewportHeight: number, height: number, bounds: Bounds,
  obstacles: Bounds[], edge: number, gap: number) {
  let bottom = edge;
  for (const obstacle of [...obstacles].sort((a, b) => b.top - a.top)) {
    if (obstacle.left >= bounds.right || obstacle.right <= bounds.left) continue;
    const toastBottom = viewportHeight - bottom;
    if (obstacle.top < toastBottom && obstacle.bottom > toastBottom - height) {
      bottom = Math.max(bottom, viewportHeight - obstacle.top + gap);
    }
  }
  return bottom;
}

export function useAppFeedbackPlacement() {
  useLayoutEffect(() => {
    const root = document.documentElement.style;
    const property = "--ac-feedback-clearance";
    const previous = root.getPropertyValue(property);
    let frame = 0;
    const observed = new Set<Element>();
    const resize = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(schedule);

    function visible(selector: string, owner: ParentNode = document) {
      return Array.from(owner.querySelectorAll<HTMLElement>(selector))
        .filter(element => {
          const rect = element.getBoundingClientRect();
          return rect.width > 0 && rect.height > 0 && rect.right > 0 && rect.left < innerWidth
            && rect.bottom > 0 && rect.top < innerHeight;
        });
    }

    function update() {
      frame = 0;
      const list = document.querySelector<HTMLElement>(".app-feedback-list");
      if (!list) {
        resize?.disconnect(); observed.clear(); root.removeProperty(property);
        return;
      }
      // The frontmost dialog owns its actions; covered page controls do not reserve space.
      const modal = visible(".ant-modal").at(-1);
      const drawer = visible(".ant-drawer-section").at(-1);
      const controls = modal ? visible(".ant-modal-footer, .ant-modal-confirm-btns, [data-feedback-actions]", modal)
        : drawer ? visible(".ant-drawer-footer, [data-feedback-actions]", drawer)
        : visible(".composer-shell, .admin-owner-actions, [data-feedback-actions]");
      const notices = Array.from(list.querySelectorAll<HTMLElement>(".ant-message-notice"));
      const targets = new Set<Element>([list, ...controls, ...notices]);
      for (const target of observed) if (!targets.has(target)) { resize?.unobserve(target); observed.delete(target); }
      for (const target of targets) if (!observed.has(target)) { resize?.observe(target); observed.add(target); }
      if (!notices.length) return;
      const theme = getComputedStyle(document.documentElement);
      const section = parseFloat(theme.getPropertyValue("--ac-space-section")) || 16;
      const compact = parseFloat(theme.getPropertyValue("--ac-space-compact")) || 8;
      const current = parseFloat(root.getPropertyValue(property)) || section;
      const safe = Math.max(0, (parseFloat(getComputedStyle(list).bottom) || current) - current);
      const rects = notices.map(element => element.getBoundingClientRect());
      const bounds = { left: Math.min(...rects.map(r => r.left)), right: Math.max(...rects.map(r => r.right)), top: 0, bottom: 0 };
      const naturalHeight = rects.reduce((sum, r) => sum + r.height, 0) + compact * (notices.length - 1);
      const bottom = feedbackBottom(innerHeight, Math.min(innerHeight / 2, naturalHeight), bounds,
        controls.map(element => element.getBoundingClientRect()).filter(r => r.width > 0 && r.height > 0), section + safe, compact);
      const value = `${Math.round(bottom - safe)}px`;
      if (root.getPropertyValue(property) !== value) root.setProperty(property, value);
    }

    function schedule() {
      if (!frame) frame = requestAnimationFrame(update);
    }
    const mutation = new MutationObserver(records => {
      if (records.some(record => record.type === "childList" || record.target instanceof Element
        && record.target.matches(".ant-modal-wrap, .ant-modal, .ant-drawer, .ant-drawer-content-wrapper, [hidden]"))) schedule();
    });
    mutation.observe(document.body, { childList: true, subtree: true, attributes: true,
      attributeFilter: ["class", "style", "hidden", "aria-hidden"] });
    window.addEventListener("resize", schedule);
    document.addEventListener("scroll", schedule, true);
    schedule();
    return () => {
      cancelAnimationFrame(frame); mutation.disconnect(); resize?.disconnect();
      window.removeEventListener("resize", schedule);
      document.removeEventListener("scroll", schedule, true);
      if (previous) root.setProperty(property, previous); else root.removeProperty(property);
    };
  }, []);
}
