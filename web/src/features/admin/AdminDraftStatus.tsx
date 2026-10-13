import { createContext, useContext, useEffect, useId } from 'react';
export const AdminDraftStatus = createContext<(source: string, pages: readonly string[]) => void>(() => {});
export function useAdminDraftStatus(pages: readonly string[]) {
  const report = useContext(AdminDraftStatus); const source = useId();
  const key = pages.join(',');
  useEffect(() => { report(source, pages); return () => report(source, []); }, [report, source, key]);
}
