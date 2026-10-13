import { useEffect, useState } from 'react';
import { AdminApp as AdminView } from '../features/admin/AdminApp';
import { adminRoutePath, parseAppRoute, type AdminRoute } from '../app/appRoute';
/** Test the real canonical route outlet; production AppRouter owns the same popstate contract. */
export function AdminTestWorkspace({ route }: { route: AdminRoute }) {
  const [current, setCurrent] = useState(route);
  useEffect(() => { setCurrent(route); const path = adminRoutePath(route); if (path) window.history.replaceState(null, '', path); }, [route]);
  useEffect(() => { const pop = () => { const next = parseAppRoute(window.location.pathname); if (next.area === 'admin') setCurrent(next); }; window.addEventListener('popstate', pop); return () => window.removeEventListener('popstate', pop); }, []);
  return <AdminView route={current} />;
}
