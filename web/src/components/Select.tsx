import { useEffect, useId, useRef, useState } from "react";

export type SelectOption = {
  value: string;
  label: string;
};

export function Select({
  value,
  options,
  onChange,
  "aria-label": ariaLabel,
  disabled = false
}: {
  value: string;
  options: SelectOption[];
  onChange: (value: string) => void;
  "aria-label": string;
  disabled?: boolean;
}) {
  const listId = useId();
  const rootRef = useRef<HTMLDivElement>(null);
  const optionRefs = useRef<Array<HTMLLIElement | null>>([]);
  const [open, setOpen] = useState(false);
  const [highlightedIndex, setHighlightedIndex] = useState(-1);

  const selectedIndex = options.findIndex((option) => option.value === value);
  const selected = selectedIndex >= 0 ? options[selectedIndex] : options[0];

  useEffect(() => {
    if (!open) {
      return;
    }

    const handlePointerDown = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };

    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  }, [open]);

  useEffect(() => {
    if (!open) {
      setHighlightedIndex(-1);
    }
  }, [open]);

  useEffect(() => {
    if (!open || highlightedIndex < 0) {
      return;
    }
    optionRefs.current[highlightedIndex]?.scrollIntoView?.({ block: "nearest" });
  }, [highlightedIndex, open]);

  useEffect(() => {
    const root = rootRef.current;
    if (!open || !root) {
      return;
    }

    const handleFocusOut = (event: FocusEvent) => {
      const next = event.relatedTarget;
      if (next instanceof Node && root.contains(next)) {
        return;
      }
      setOpen(false);
    };

    root.addEventListener("focusout", handleFocusOut);
    return () => root.removeEventListener("focusout", handleFocusOut);
  }, [open]);

  const selectAt = (index: number) => {
    const option = options[index];
    if (!option) {
      return;
    }
    onChange(option.value);
    setOpen(false);
  };

  const moveHighlight = (delta: number) => {
    if (options.length === 0) {
      return;
    }
    const start = highlightedIndex >= 0 ? highlightedIndex : Math.max(selectedIndex, 0);
    const next = (start + delta + options.length) % options.length;
    setHighlightedIndex(next);
  };

  const handleTriggerKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    if (disabled) {
      return;
    }

    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        if (!open) {
          setOpen(true);
          setHighlightedIndex(Math.max(selectedIndex, 0));
          return;
        }
        moveHighlight(1);
        break;
      case "ArrowUp":
        event.preventDefault();
        if (!open) {
          setOpen(true);
          setHighlightedIndex(Math.max(selectedIndex, 0));
          return;
        }
        moveHighlight(-1);
        break;
      case "Enter":
      case " ":
        event.preventDefault();
        if (!open) {
          setOpen(true);
          setHighlightedIndex(Math.max(selectedIndex, 0));
          return;
        }
        if (highlightedIndex >= 0) {
          selectAt(highlightedIndex);
        }
        break;
      case "Escape":
        if (open) {
          event.preventDefault();
          setOpen(false);
        }
        break;
      case "Home":
        if (open) {
          event.preventDefault();
          setHighlightedIndex(0);
        }
        break;
      case "End":
        if (open) {
          event.preventDefault();
          setHighlightedIndex(options.length - 1);
        }
        break;
      default:
        break;
    }
  };

  return (
    <div className="select" ref={rootRef}>
      <button
        type="button"
        className="select-trigger"
        aria-label={ariaLabel}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        disabled={disabled || options.length === 0}
        onClick={() => {
          if (disabled || options.length === 0) {
            return;
          }
          setOpen((current) => !current);
          if (!open) {
            setHighlightedIndex(Math.max(selectedIndex, 0));
          }
        }}
        onKeyDown={handleTriggerKeyDown}
      >
        <span className="select-value">{selected?.label ?? ""}</span>
        <span className="select-chevron" aria-hidden="true" data-open={open ? "true" : "false"} />
      </button>
      {open ? (
        <ul className="select-list" id={listId} role="listbox" aria-label={ariaLabel}>
          {options.map((option, index) => {
            const isSelected = option.value === value;
            const isHighlighted = index === highlightedIndex;
            return (
              <li
                key={option.value}
                ref={(element) => {
                  optionRefs.current[index] = element;
                }}
                role="option"
                aria-selected={isSelected}
                className={[
                  "select-option",
                  isSelected ? "is-selected" : "",
                  isHighlighted ? "is-highlighted" : ""
                ]
                  .filter(Boolean)
                  .join(" ")}
                onMouseEnter={() => setHighlightedIndex(index)}
                onPointerDown={(event) => {
                  event.preventDefault();
                  selectAt(index);
                }}
              >
                <span className="select-marker" aria-hidden="true" data-selected={isSelected ? "true" : "false"} />
                <span className="select-option-label">{option.label}</span>
              </li>
            );
          })}
        </ul>
      ) : null}
    </div>
  );
}
