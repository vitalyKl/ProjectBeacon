export type DuplicateJsonKey = {
  key: string;
  path: string;
};

type ObjectFrame = {
  kind: "object";
  keys: Set<string>;
  path: string;
  expect: "key" | "colon" | "value" | "comma";
  pendingKey: string | null;
};

type ArrayFrame = {
  kind: "array";
  path: string;
  index: number;
  expect: "value" | "comma";
};

type Frame = ObjectFrame | ArrayFrame;

export function duplicateJsonKeys(source: string): DuplicateJsonKey[] {
  const text = source.charCodeAt(0) === 0xfeff ? source.slice(1) : source;
  const duplicates: DuplicateJsonKey[] = [];
  const stack: Frame[] = [];
  let i = 0;

  const skipWs = (): void => {
    while (i < text.length) {
      const c = text[i];
      if (c === " " || c === "\t" || c === "\n" || c === "\r") {
        i += 1;
        continue;
      }
      break;
    }
  };

  const fail = (message: string): never => {
    throw new Error(`${message} at index ${i}`);
  };

  const peek = (): string => {
    const c = text[i];
    if (c === undefined) {
      return fail("unexpected end of JSON");
    }
    return c;
  };

  const readString = (): string => {
    if (peek() !== '"') {
      return fail("expected string");
    }
    i += 1;
    let out = "";
    while (i < text.length) {
      const c = text[i];
      if (c === undefined) {
        break;
      }
      if (c === '"') {
        i += 1;
        return out;
      }
      if (c === "\\") {
        const next = text[i + 1];
        if (next === undefined) {
          return fail("unterminated escape");
        }
        if (next === "u") {
          const hex = text.slice(i + 2, i + 6);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) {
            return fail("invalid unicode escape");
          }
          out += String.fromCharCode(Number.parseInt(hex, 16));
          i += 6;
          continue;
        }
        const escaped: Record<string, string> = {
          '"': '"',
          "\\": "\\",
          "/": "/",
          b: "\b",
          f: "\f",
          n: "\n",
          r: "\r",
          t: "\t",
        };
        const mapped = escaped[next];
        if (mapped === undefined) {
          return fail("invalid escape");
        }
        out += mapped;
        i += 2;
        continue;
      }
      out += c;
      i += 1;
    }
    return fail("unterminated string");
  };

  const childPath = (key: string): string => {
    const parent = stack.at(-1);
    if (!parent) {
      return key;
    }
    if (parent.kind === "array") {
      return parent.path === "" ? `[${parent.index}]` : `${parent.path}[${parent.index}]`;
    }
    return parent.path === "" ? key : `${parent.path}.${key}`;
  };

  const beginValue = (): void => {
    skipWs();
    const c = peek();
    if (c === "{") {
      const parent = stack.at(-1);
      const path =
        parent?.kind === "object" && parent.pendingKey
          ? childPath(parent.pendingKey)
          : parent?.kind === "array"
            ? childPath("")
            : "";
      i += 1;
      stack.push({ kind: "object", keys: new Set(), path, expect: "key", pendingKey: null });
      return;
    }
    if (c === "[") {
      const parent = stack.at(-1);
      const path =
        parent?.kind === "object" && parent.pendingKey
          ? childPath(parent.pendingKey)
          : parent?.kind === "array"
            ? childPath("")
            : "";
      i += 1;
      stack.push({ kind: "array", path, index: 0, expect: "value" });
      return;
    }
    if (c === '"') {
      readString();
      finishValue();
      return;
    }
    if (c === "t") {
      if (text.slice(i, i + 4) !== "true") {
        fail("expected true");
      }
      i += 4;
      finishValue();
      return;
    }
    if (c === "f") {
      if (text.slice(i, i + 5) !== "false") {
        fail("expected false");
      }
      i += 5;
      finishValue();
      return;
    }
    if (c === "n") {
      if (text.slice(i, i + 4) !== "null") {
        fail("expected null");
      }
      i += 4;
      finishValue();
      return;
    }
    if (c === "-" || (c >= "0" && c <= "9")) {
      const matched = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(text.slice(i));
      const token = matched?.[0];
      if (!token) {
        return fail("expected number");
      }
      i += token.length;
      finishValue();
      return;
    }
    return fail("expected value");
  };

  const finishValue = (): void => {
    const frame = stack.at(-1);
    if (!frame) {
      return;
    }
    if (frame.kind === "object") {
      frame.expect = "comma";
      frame.pendingKey = null;
      return;
    }
    frame.index += 1;
    frame.expect = "comma";
  };

  skipWs();
  beginValue();

  while (stack.length > 0) {
    skipWs();
    const frame = stack.at(-1);
    if (!frame) {
      break;
    }
    const c = peek();

    if (frame.kind === "object") {
      if (frame.expect === "key") {
        if (c === "}") {
          i += 1;
          stack.pop();
          finishValue();
          continue;
        }
        const key = readString();
        if (frame.keys.has(key)) {
          duplicates.push({ key, path: frame.path || "$" });
        }
        frame.keys.add(key);
        frame.pendingKey = key;
        frame.expect = "colon";
        continue;
      }
      if (frame.expect === "colon") {
        if (c !== ":") {
          fail("expected colon");
        }
        i += 1;
        frame.expect = "value";
        continue;
      }
      if (frame.expect === "value") {
        beginValue();
        continue;
      }
      if (c === "}") {
        i += 1;
        stack.pop();
        finishValue();
        continue;
      }
      if (c !== ",") {
        fail("expected comma or end of object");
      }
      i += 1;
      frame.expect = "key";
      continue;
    }

    if (frame.expect === "value") {
      if (c === "]") {
        i += 1;
        stack.pop();
        finishValue();
        continue;
      }
      beginValue();
      continue;
    }
    if (c === "]") {
      i += 1;
      stack.pop();
      finishValue();
      continue;
    }
    if (c !== ",") {
      fail("expected comma or end of array");
    }
    i += 1;
    frame.expect = "value";
  }

  skipWs();
  if (i !== text.length) {
    fail("unexpected trailing JSON");
  }
  return duplicates;
}
