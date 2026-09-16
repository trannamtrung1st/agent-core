import "@testing-library/jest-dom/vitest";

const testViewportWidth = 1280;

function mediaMatches(query: string): boolean {
  const min = /min-width:\s*(\d+)/i.exec(query);
  const max = /max-width:\s*(\d+)/i.exec(query);
  const minPx = min ? Number(min[1]) : 0;
  const maxPx = max ? Number(max[1]) : Number.POSITIVE_INFINITY;
  return testViewportWidth >= minPx && testViewportWidth <= maxPx;
}

Object.defineProperty(window, "matchMedia", {
  writable: true,
  value: (query: string) => ({
    matches: mediaMatches(query),
    media: query,
    onchange: null,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent() {
      return false;
    }
  })
});

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}

window.ResizeObserver = ResizeObserverStub;

const originalGetComputedStyle = window.getComputedStyle.bind(window);
window.getComputedStyle = ((elt: Element, pseudoElt?: string | null) => {
  if (pseudoElt) {
    return originalGetComputedStyle(elt);
  }
  return originalGetComputedStyle(elt, pseudoElt);
}) as typeof window.getComputedStyle;
