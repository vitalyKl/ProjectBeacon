"use client";

import { useRouter } from "next/navigation";
import {
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
} from "react";

import { buildCommandPaletteItems, filterCommandPaletteItems } from "@/lib/command-palette";
import { BUTTON_VARIANT_CLASS, FIELD_INPUT_CLASS, cx } from "@/lib/ui";
import { useT } from "@/lib/use-locale";

const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function CommandPalette({ hasProject }: { hasProject: boolean }) {
  const router = useRouter();
  const label = useT();
  const titleId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [activeIndex, setActiveIndex] = useState(0);

  const items = useMemo(
    () => filterCommandPaletteItems(buildCommandPaletteItems({ hasProject }), query, label),
    [hasProject, label, query],
  );

  const close = useCallback(() => {
    setOpen(false);
    setQuery("");
    setActiveIndex(0);
  }, []);

  const run = useCallback(
    (href: string) => {
      close();
      router.push(href);
    },
    [close, router],
  );

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setOpen((current) => {
          if (current) {
            setQuery("");
            setActiveIndex(0);
            return false;
          }
          return true;
        });
        return;
      }
      if (event.key === "Escape") {
        setOpen((current) => {
          if (!current) {
            return current;
          }
          event.preventDefault();
          setQuery("");
          setActiveIndex(0);
          return false;
        });
      }
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, []);

  const selectedIndex = items.length === 0 ? 0 : Math.min(activeIndex, items.length - 1);

  useEffect(() => {
    if (!open) {
      return;
    }
    const id = window.requestAnimationFrame(() => {
      inputRef.current?.focus();
    });
    return () => window.cancelAnimationFrame(id);
  }, [open]);

  function focusables(): HTMLElement[] {
    const root = dialogRef.current;
    if (!root) {
      return [];
    }
    return Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
      (node) => !node.hasAttribute("disabled") && node.tabIndex !== -1,
    );
  }

  function onDialogKeyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (event.key === "Escape") {
      event.preventDefault();
      close();
      return;
    }
    if (event.key === "Tab") {
      const nodes = focusables();
      if (nodes.length === 0) {
        event.preventDefault();
        return;
      }
      const first = nodes[0];
      const last = nodes[nodes.length - 1];
      if (!first || !last) {
        event.preventDefault();
        return;
      }
      const active = document.activeElement;
      if (event.shiftKey) {
        if (active === first || !rootContains(active)) {
          event.preventDefault();
          last.focus();
        }
      } else if (active === last || !rootContains(active)) {
        event.preventDefault();
        first.focus();
      }
      return;
    }
    if (event.key === "ArrowDown") {
      event.preventDefault();
      if (items.length === 0) {
        return;
      }
      setActiveIndex((current) => (current + 1) % items.length);
      return;
    }
    if (event.key === "ArrowUp") {
      event.preventDefault();
      if (items.length === 0) {
        return;
      }
      setActiveIndex((current) => (current - 1 + items.length) % items.length);
      return;
    }
    if (event.key === "Enter") {
      const item = items[selectedIndex];
      if (!item) {
        return;
      }
      event.preventDefault();
      run(item.href);
    }
  }

  function rootContains(node: Element | null): boolean {
    return Boolean(node && dialogRef.current?.contains(node));
  }

  return (
    <>
      <button
        className={BUTTON_VARIANT_CLASS.secondary}
        type="button"
        aria-label={label("command.open")}
        onClick={() => {
          setQuery("");
          setActiveIndex(0);
          setOpen(true);
        }}
      >
        {label("command.open")}
      </button>
      {open ? (
        <div className="fixed inset-0 z-50 flex items-start justify-center px-4 pt-[15vh]">
          <button
            className="absolute inset-0 bg-foreground/20"
            type="button"
            tabIndex={-1}
            aria-label={label("common.cancel")}
            onClick={close}
          />
          <div
            ref={dialogRef}
            className="relative z-10 w-full max-w-lg overflow-hidden rounded-lg border border-border bg-surface shadow-sm"
            role="dialog"
            aria-modal="true"
            aria-labelledby={titleId}
            onKeyDown={onDialogKeyDown}
          >
            <h2 id={titleId} className="sr-only">
              {label("command.open")}
            </h2>
            <input
              ref={inputRef}
              className={cx(FIELD_INPUT_CLASS, "h-11 w-full rounded-none border-0 border-b")}
              placeholder={label("command.placeholder")}
              value={query}
              onChange={(event) => {
                setQuery(event.target.value);
                setActiveIndex(0);
              }}
              aria-label={label("command.placeholder")}
              autoComplete="off"
              spellCheck={false}
            />
            {items.length === 0 ? (
              <p className="px-3 py-4 text-sm text-muted">{label("command.noResults")}</p>
            ) : (
              <ul className="max-h-80 overflow-y-auto py-1" role="listbox">
                {items.map((item, index) => {
                  const active = index === selectedIndex;
                  return (
                    <li key={item.id} role="option" aria-selected={active}>
                      <button
                        className={cx(
                          "block w-full px-3 py-2 text-left text-sm",
                          active ? "bg-background font-medium" : "text-muted hover:text-foreground",
                        )}
                        type="button"
                        tabIndex={-1}
                        onMouseEnter={() => setActiveIndex(index)}
                        onClick={() => run(item.href)}
                      >
                        {label(item.message)}
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>
        </div>
      ) : null}
    </>
  );
}
