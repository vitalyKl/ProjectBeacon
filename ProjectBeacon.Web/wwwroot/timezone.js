(() => {
    "use strict";

    const storageKey = "projectbeacon.timeZone";
    const systemZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
    const selectedZone = () => localStorage.getItem(storageKey) || systemZone();

    function formatElement(element) {
        const raw = element.dataset.localTime;
        if (!raw) return;
        const date = new Date(raw);
        if (Number.isNaN(date.getTime())) return;

        const format = element.dataset.localTimeFormat || "g";
        const locale = document.documentElement.lang || undefined;
        const options = format === "d"
            ? { dateStyle: "short" }
            : format === "D"
                ? { dateStyle: "long" }
                : format === "u"
                    ? { dateStyle: "medium", timeStyle: "medium" }
                    : { dateStyle: "short", timeStyle: "short" };

        try {
            element.textContent = new Intl.DateTimeFormat(locale, {
                ...options,
                timeZone: selectedZone()
            }).format(date);
            element.title = new Intl.DateTimeFormat(locale, {
                dateStyle: "full",
                timeStyle: "long",
                timeZone: selectedZone()
            }).format(date);
            element.dateTime = date.toISOString();
        } catch {
            element.textContent = new Intl.DateTimeFormat(locale, {
                ...options,
                timeZone: systemZone()
            }).format(date);
        }
    }

    function refresh(root = document) {
        if (root.matches?.("[data-local-time]")) formatElement(root);
        root.querySelectorAll?.("[data-local-time]").forEach(formatElement);
    }

    window.beaconTimeZone = {
        getPreference: () => localStorage.getItem(storageKey) || "",
        getCurrent: selectedZone,
        getSystem: systemZone,
        setPreference: value => {
            if (!value) {
                localStorage.removeItem(storageKey);
            } else {
                // Validate the IANA identifier before persisting it.
                new Intl.DateTimeFormat(undefined, { timeZone: value });
                localStorage.setItem(storageKey, value);
            }
            refresh();
            window.dispatchEvent(new CustomEvent("beacon-timezone-changed", {
                detail: { timeZone: selectedZone() }
            }));
            return selectedZone();
        }
    };

    const observer = new MutationObserver(records => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node.nodeType === Node.ELEMENT_NODE) refresh(node);
            }
            if (record.type === "characterData" && record.target.parentElement?.matches("[data-local-time]")) {
                formatElement(record.target.parentElement);
            }
        }
    });

    function start() {
        refresh();
        observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["data-local-time", "data-local-time-format"] });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start, { once: true });
    } else {
        start();
    }
})();
