import { isInaccessible, within, waitFor } from "@testing-library/react";
import { expect } from "vitest";

/** Locate known controls cheaply, then assert their actual accessibility semantics. */
export function exposedRoles(container: HTMLElement = document.body) {
  const queries = within(container);
  const getByRole = (...[role, options]: Parameters<typeof queries.getByRole>) => {
    const selectors: Record<string, string> = {
      button: "button, [role=button]",
      tab: "[role=tab]",
      radio: "input[type=radio], [role=radio]",
    };
    const selector = typeof role === "string" ? selectors[role] : undefined;
    const name = options?.name;
    if (selector && typeof name === "string" && Object.keys(options ?? {}).every(key => key === "name" || key === "exact")) {
      const candidates = Array.from(container.querySelectorAll<HTMLElement>(selector))
        .filter(element => element.getAttribute("aria-label") === name ||
          (role === "radio" ? element.closest("label")?.textContent : element.textContent)?.trim() === name)
        .filter(element => !isInaccessible(element));
      if (candidates.length === 1) {
        expect(candidates[0]).toHaveRole(role as string);
        expect(candidates[0]).toHaveAccessibleName(name);
        return candidates[0];
      }
    }
    const matches = queries.getAllByRole(role, { ...options, hidden: true })
      .filter(element => !isInaccessible(element));
    expect(matches).toHaveLength(1);
    return matches[0];
  };
  const findByRole = (...[role, options, waitOptions]: Parameters<typeof queries.findByRole>) =>
    waitFor(() => getByRole(role, options), waitOptions);
  return { getByRole, findByRole };
}
