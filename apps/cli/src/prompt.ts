import { createInterface } from "node:readline";
import type { Readable, Writable } from "node:stream";

export type PromptIo = {
  stdin: NodeJS.ReadableStream & {
    isTTY?: boolean;
    setRawMode?(mode: boolean): void;
    setEncoding?(encoding: BufferEncoding): void;
  };
  stdout: { write(chunk: string): void };
};

function asReadable(input: PromptIo["stdin"]): Readable {
  return input as Readable;
}

function asWritable(output: PromptIo["stdout"]): Writable {
  return output as Writable;
}

export type SetupPrompt = {
  line(question: string, defaultValue?: string): Promise<string>;
  secret(question: string): Promise<string>;
};

export function isInteractiveIo(io: PromptIo): boolean {
  return Boolean(io.stdin.isTTY && io.stdout);
}

export function createSetupPrompt(io: PromptIo): SetupPrompt {
  return {
    line(question, defaultValue) {
      return promptLine(io, question, defaultValue);
    },
    secret(question) {
      return promptSecret(io, question);
    },
  };
}

export async function promptLine(
  io: PromptIo,
  question: string,
  defaultValue?: string,
): Promise<string> {
  const suffix = defaultValue ? ` [${defaultValue}]` : "";
  const rl = createInterface({ input: asReadable(io.stdin), output: asWritable(io.stdout) });
  try {
    const answer = await new Promise<string>((resolve) => {
      rl.question(`${question}${suffix}: `, resolve);
    });
    const trimmed = answer.trim();
    return trimmed.length > 0 ? trimmed : (defaultValue ?? "");
  } finally {
    rl.close();
  }
}

export async function promptSecret(io: PromptIo, question: string): Promise<string> {
  io.stdout.write(`${question}: `);
  if (typeof io.stdin.setRawMode === "function") {
    return readHiddenLine(io);
  }
  const rl = createInterface({ input: asReadable(io.stdin), output: undefined, terminal: false });
  try {
    return await new Promise<string>((resolve) => {
      rl.once("line", (line) => resolve(line.trim()));
    });
  } finally {
    rl.close();
    io.stdout.write("\n");
  }
}

function readHiddenLine(io: PromptIo): Promise<string> {
  return new Promise((resolve, reject) => {
    const stdin = io.stdin;
    stdin.setRawMode?.(true);
    if ("resume" in stdin && typeof stdin.resume === "function") {
      stdin.resume();
    }
    stdin.setEncoding?.("utf8");
    let value = "";
    const onData = (chunk: string | Buffer) => {
      const text = typeof chunk === "string" ? chunk : chunk.toString("utf8");
      for (const char of text) {
        if (char === "\n" || char === "\r") {
          cleanup();
          io.stdout.write("\n");
          resolve(value.trim());
          return;
        }
        if (char === "\u0003") {
          cleanup();
          io.stdout.write("\n");
          reject(new Error("cancelled"));
          return;
        }
        if (char === "\u007f" || char === "\b") {
          value = value.slice(0, -1);
          continue;
        }
        if (char >= " ") {
          value += char;
        }
      }
    };
    const cleanup = () => {
      stdin.off("data", onData);
      stdin.setRawMode?.(false);
      if ("pause" in stdin && typeof stdin.pause === "function") {
        stdin.pause();
      }
    };
    stdin.on("data", onData);
  });
}
