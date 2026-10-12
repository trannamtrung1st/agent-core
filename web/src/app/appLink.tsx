import type { MouseEvent, ReactNode } from "react";
import { navigateToAppPath } from "./appRoute";

/** Keep native new-tab/window gestures; only ordinary activation uses SPA navigation. */
export function followAppLink(event: MouseEvent<HTMLElement>, navigate?: () => void): void {
  // Route labels live inside AntD tabs; modified clicks must not select the source tab.
  event.stopPropagation();
  const href = event.currentTarget.getAttribute("href");
  const target = event.currentTarget.getAttribute("target");
  if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey
    || (target && target !== "_self") || event.currentTarget.hasAttribute("download") || !href) return;
  event.preventDefault();
  if (navigate) navigate();
  else navigateToAppPath(href);
}

/** The AntD tab remains the keyboard stop and owns arrow-key navigation. */
export function routeTabLabel(label: ReactNode, href: string, navigate?: () => void): ReactNode {
  return <a className="app-route-tab-link" href={href} tabIndex={-1} onClick={event => followAppLink(event, () => {
    if (window.location.pathname + window.location.search === href) return;
    if (navigate) navigate();
    else navigateToAppPath(href);
  })}>{label}</a>;
}
