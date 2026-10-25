"use client";

import * as React from "react";
import { ChevronDown } from "lucide-react";
import { cn } from "@/lib/utils";

/**
 * A native text input enhanced with a dropdown of suggestions (an editable
 * combobox). Unlike a select, the input stays a free-text field: the user can
 * type any value, or pick one of the provided suggestions.
 *
 * Behaviour:
 *  - Focusing the field opens the suggestion list (filtered by the current value).
 *  - Typing filters the suggestions; the free-text value is always preserved.
 *  - Clicking a suggestion sets the value and fires `onChange` (via `onValueChange`)
 *    then closes the list.
 *  - Arrow keys move the highlighted suggestion, Enter accepts it, Escape closes.
 *  - Clicking outside closes the list.
 */
export interface ComboInputProps
  extends Omit<React.InputHTMLAttributes<HTMLInputElement>, "onChange" | "value"> {
  value: string;
  /** Called with the raw typed text or the chosen suggestion. */
  onValueChange: (value: string) => void;
  /** Suggestion list. Empty/undefined hides the dropdown. */
  options: string[];
  /** Optional className for the outer wrapper (the input keeps `className`). */
  wrapperClassName?: string;
  /** Max height of the dropdown list in pixels. */
  maxListHeight?: number;
}

const ComboInput = React.forwardRef<HTMLInputElement, ComboInputProps>(
  (
    {
      value,
      onValueChange,
      options,
      className,
      wrapperClassName,
      maxListHeight = 240,
      onKeyDown,
      onFocus,
      onBlur,
      disabled,
      ...props
    },
    ref,
  ) => {
    const [open, setOpen] = React.useState(false);
    const [highlighted, setHighlighted] = React.useState(-1);
    const wrapperRef = React.useRef<HTMLDivElement>(null);
    const listRef = React.useRef<HTMLUListElement>(null);

    const filtered = React.useMemo(() => {
      const query = value.trim().toLowerCase();
      if (!query) return options;
      // When the box is pre-filled with an exact known title (e.g. the series title), show the
      // full suggestion list so the user can pick any title instead of being filtered down to
      // the matching one. As soon as they type a partial term, filter normally.
      if (options.some((option) => option.toLowerCase() === query)) return options;
      return options.filter((option) => option.toLowerCase().includes(query));
    }, [options, value]);

    // Close when clicking outside the wrapper.
    React.useEffect(() => {
      if (!open) return;
      const handlePointerDown = (event: MouseEvent) => {
        if (!wrapperRef.current?.contains(event.target as Node)) {
          setOpen(false);
        }
      };
      document.addEventListener("mousedown", handlePointerDown);
      return () => document.removeEventListener("mousedown", handlePointerDown);
    }, [open]);

    // Reset the highlight whenever the filtered list changes.
    React.useEffect(() => {
      setHighlighted(-1);
    }, [value, options.length]);

    // Keep the highlighted row in view while navigating with the keyboard.
    React.useEffect(() => {
      if (highlighted < 0 || !listRef.current) return;
      const item = listRef.current.children[highlighted] as HTMLElement | undefined;
      item?.scrollIntoView({ block: "nearest" });
    }, [highlighted]);

    const select = React.useCallback(
      (option: string) => {
        onValueChange(option);
        setOpen(false);
        setHighlighted(-1);
      },
      [onValueChange],
    );

    const handleKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
      if (event.key === "ArrowDown") {
        if (filtered.length === 0) return;
        event.preventDefault();
        setOpen(true);
        setHighlighted((prev) => (prev + 1) % filtered.length);
        return;
      }
      if (event.key === "ArrowUp") {
        if (filtered.length === 0) return;
        event.preventDefault();
        setOpen(true);
        setHighlighted((prev) => (prev <= 0 ? filtered.length - 1 : prev - 1));
        return;
      }
      if (event.key === "Enter") {
        const choice = highlighted >= 0 ? filtered[highlighted] : undefined;
        if (showList && choice !== undefined) {
          // A suggestion is highlighted: accept it and consume the key so the
          // parent's Enter handler (e.g. "run search") does not also fire.
          event.preventDefault();
          select(choice);
          return;
        }
        setOpen(false);
        // No suggestion chosen — let the parent handle Enter (free-text submit).
      } else if (event.key === "Escape" && open) {
        event.preventDefault();
        setOpen(false);
        return;
      }
      onKeyDown?.(event);
    };

    const showList = open && !disabled && filtered.length > 0;

    return (
      <div ref={wrapperRef} className={cn("relative", wrapperClassName)}>
        <div className="relative">
          <input
            ref={ref}
            value={value}
            disabled={disabled}
            onChange={(e) => {
              onValueChange(e.target.value);
              setOpen(true);
            }}
            onKeyDown={handleKeyDown}
            onFocus={(e) => {
              onFocus?.(e);
              setOpen(true);
            }}
            onBlur={(e) => {
              onBlur?.(e);
            }}
            role="combobox"
            aria-expanded={showList}
            aria-autocomplete="list"
            autoComplete="off"
            className={cn(
              "flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 pr-8 text-sm shadow-sm transition-colors file:border-0 file:bg-transparent file:text-sm file:font-medium placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
              className,
            )}
            {...props}
          />
          {options.length > 0 && !disabled && (
            <button
              type="button"
              tabIndex={-1}
              aria-label="Toggle suggestions"
              onMouseDown={(e) => {
                // Prevent the input blur from closing the list before the toggle.
                e.preventDefault();
                setOpen((prev) => !prev);
              }}
              className="absolute right-1 top-1/2 flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-sm text-muted-foreground hover:text-foreground"
            >
              <ChevronDown className={cn("h-4 w-4 transition-transform", showList && "rotate-180")} />
            </button>
          )}
        </div>

        {showList && (
          <ul
            ref={listRef}
            role="listbox"
            className="absolute z-50 mt-1 w-full overflow-auto rounded-md border bg-popover p-1 text-popover-foreground shadow-md"
            style={{ maxHeight: maxListHeight }}
          >
            {filtered.map((option, idx) => (
              <li
                key={`${option}-${idx}`}
                role="option"
                aria-selected={highlighted === idx}
                onMouseDown={(e) => {
                  // Keep focus on the input so the value is committed cleanly.
                  e.preventDefault();
                  select(option);
                }}
                onMouseEnter={() => setHighlighted(idx)}
                className={cn(
                  "cursor-pointer truncate rounded-sm px-2 py-1.5 text-sm",
                  highlighted === idx ? "bg-accent text-accent-foreground" : "hover:bg-accent hover:text-accent-foreground",
                )}
                title={option}
              >
                {option}
              </li>
            ))}
          </ul>
        )}
      </div>
    );
  },
);
ComboInput.displayName = "ComboInput";

export { ComboInput };
