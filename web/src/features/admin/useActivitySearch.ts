import { useEffect, useState } from "react";
import { navigateToAppPath } from "../../app/appRoute";

/** Search is explicitly limited to the loaded cursor pages; the URL retains it across inspection/history. */
export function useActivitySearch() {
  const read = () => new URLSearchParams(window.location.search).get("q") ?? "";
  const [search, update] = useState(read);
  useEffect(() => { const sync = () => update(read()); window.addEventListener("popstate", sync); return () => window.removeEventListener("popstate", sync); }, []);
  const setSearch = (value: string) => {
    const params = new URLSearchParams(window.location.search);
    if (value) params.set("q", value); else params.delete("q");
    navigateToAppPath(`${window.location.pathname}${params.size ? `?${params}` : ""}`, true);
    update(value);
  };
  return { search, setSearch };
}
