// copy-code behavior for the chat code-block button, exercised in a node vm with a
// minimal DOM stub (no jsdom): async clipboard, execCommand fallback, failure marking,
// and the atomic followLatest scroll helper.
// Run with: node tests/playwright/chat/copy-code.test.mjs
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import vm from "node:vm";

const chatJs = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "ProjectBeacon.Web", "wwwroot", "chat.js"),
  "utf8",
);

function makeClassList() {
  const classes = new Set();
  return {
    add: (c) => classes.add(c),
    remove: (c) => classes.delete(c),
    contains: (c) => classes.has(c),
    toggle: (c) => (classes.has(c) ? (classes.delete(c), false) : (classes.add(c), true)),
  };
}

function makeDom() {
  const pre = { innerText: "line one\nline two" };
  const box = {
    classList: makeClassList(),
    querySelector: (sel) => (sel === "pre" ? pre : null),
  };
  const btn = {
    classList: makeClassList(),
    closest: (sel) => (sel === ".beacon-code" ? box : null),
    getAttribute: () => null,
    setAttribute: () => {},
  };
  const created = [];
  const timeouts = [];
  const document = {
    createElement: (tag) => {
      const el = { tag, value: "", style: {}, select: () => {} };
      created.push(el);
      return el;
    },
    body: {
      appendChild: (n) => { n.parented = true; },
      removeChild: (n) => { n.parented = false; },
    },
    addEventListener: () => {},
  };
  const context = {
    window: {},
    document,
    navigator: {},
    setTimeout: (fn, ms) => { timeouts.push({ fn, ms }); },
  };
  vm.createContext(context);
  vm.runInContext(chatJs, context);
  return { btn, pre, document, context, created, timeouts };
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 25));

// 1. Async clipboard success: text is copied, success class set, cleared after 1500 ms.
{
  const dom = makeDom();
  let copiedText = null;
  dom.context.navigator.clipboard = { writeText: async (t) => { copiedText = t; } };
  dom.context.window.BeaconChat.copyCode(dom.btn);
  await flush();
  assert.equal(copiedText, dom.pre.innerText);
  assert.ok(dom.btn.classList.contains("beacon-code-copied"));
  assert.ok(!dom.btn.classList.contains("beacon-code-failed"));
  const clear = dom.timeouts.find((t) => t.ms === 1500);
  assert.ok(clear, "clear timer scheduled");
  clear.fn();
  assert.ok(!dom.btn.classList.contains("beacon-code-copied"));
}

// 2. Async clipboard rejection: failure class, no success class.
{
  const dom = makeDom();
  dom.context.navigator.clipboard = { writeText: () => Promise.reject(new Error("denied")) };
  dom.context.window.BeaconChat.copyCode(dom.btn);
  await flush();
  assert.ok(dom.btn.classList.contains("beacon-code-failed"));
  assert.ok(!dom.btn.classList.contains("beacon-code-copied"));
}

// 3. Clipboard still pending: neither class set yet.
{
  const dom = makeDom();
  dom.context.navigator.clipboard = { writeText: () => new Promise(() => {}) };
  dom.context.window.BeaconChat.copyCode(dom.btn);
  await flush();
  assert.ok(!dom.btn.classList.contains("beacon-code-copied"));
  assert.ok(!dom.btn.classList.contains("beacon-code-failed"));
}
// 4. execCommand fallback (no clipboard API): textarea carries the text, removed after.
{
  const dom = makeDom();
  let execCmd = null;
  dom.document.execCommand = (cmd) => { execCmd = cmd; return true; };
  dom.context.window.BeaconChat.copyCode(dom.btn);
  assert.equal(execCmd, "copy");
  assert.equal(dom.created.length, 1);
  assert.equal(dom.created[0].tag, "textarea");
  assert.equal(dom.created[0].value, dom.pre.innerText);
  assert.ok(!dom.created[0].parented, "textarea removed after copy");
  assert.ok(dom.btn.classList.contains("beacon-code-copied"));
  assert.ok(!dom.btn.classList.contains("beacon-code-failed"));
}

// 5. execCommand returning false: failure class.
{
  const dom = makeDom();
  dom.document.execCommand = () => false;
  dom.context.window.BeaconChat.copyCode(dom.btn);
  assert.ok(dom.btn.classList.contains("beacon-code-failed"));
  assert.ok(!dom.btn.classList.contains("beacon-code-copied"));
}

// 6. execCommand throwing: failure class, no crash.
{
  const dom = makeDom();
  dom.document.execCommand = () => { throw new Error("denied"); };
  dom.context.window.BeaconChat.copyCode(dom.btn);
  assert.ok(dom.btn.classList.contains("beacon-code-failed"));
}

// 7. Follow replaces a stale state: a pending clipboard write clears nothing, a later
//    failure marks failed; re-copying marks copied again and clears failed.
{
  const dom = makeDom();
  dom.context.navigator.clipboard = {
    writeText: () => Promise.reject(new Error("denied")),
  };
  dom.context.window.BeaconChat.copyCode(dom.btn);
  await flush();
  assert.ok(dom.btn.classList.contains("beacon-code-failed"));
  dom.context.navigator.clipboard = { writeText: async () => {} };
  dom.context.window.BeaconChat.copyCode(dom.btn);
  await flush();
  assert.ok(dom.btn.classList.contains("beacon-code-copied"));
  assert.ok(!dom.btn.classList.contains("beacon-code-failed"));
}

// 8. followLatest: atomic check-and-snap with the 80 px proximity threshold.
{
  const dom = makeDom();
  const far = { scrollHeight: 1000, scrollTop: 100, clientHeight: 400 };
  assert.equal(dom.context.window.BeaconChat.followLatest(far), false);
  assert.equal(far.scrollTop, 100, "no snap when far");
  const near = { scrollHeight: 1000, scrollTop: 920, clientHeight: 400 };
  assert.equal(dom.context.window.BeaconChat.followLatest(near), true);
  assert.equal(near.scrollTop, 1000, "snapped to bottom");
  assert.equal(dom.context.window.BeaconChat.followLatest(null), true);
}

console.log("copy-code tests passed");