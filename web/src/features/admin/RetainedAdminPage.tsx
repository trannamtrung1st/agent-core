import { useState, type ReactNode } from 'react';
/** Lazy first mount, then retain private editor state for this exact owner. */
export function RetainedAdminPage({ active, children }: { active: boolean; children: ReactNode }) {
  const [visited, setVisited] = useState(active);
  if (active && !visited) setVisited(true);
  return visited || active ? <div hidden={!active}>{children}</div> : null;
}
