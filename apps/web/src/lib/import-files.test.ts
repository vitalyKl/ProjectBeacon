import { describe, expect, it } from "vitest";

import {
  importedNodesNotice,
  importPathForFile,
  readImportPayload,
  takeInputFiles,
  unrecognizedImportMessage,
} from "./import-files";

function fakeFile(
  name: string,
  content: string,
  webkitRelativePath = "",
): Pick<File, "name" | "webkitRelativePath" | "text"> {
  return {
    name,
    webkitRelativePath,
    text: async () => content,
  };
}

describe("takeInputFiles", () => {
  it("copies files before the input is cleared", () => {
    const agents = fakeFile("AGENTS.md", "## Goals\nShip it.\n");
    const listed = [agents];
    const input = {
      files: {
        0: agents,
        length: 1,
        item(index: number) {
          return listed[index] ?? null;
        },
        *[Symbol.iterator]() {
          yield* listed;
        },
      } as unknown as FileList,
      value: "C:\\fakepath\\AGENTS.md",
    };

    const files = takeInputFiles(input);

    expect(files).toHaveLength(1);
    expect(files[0]).toBe(agents);
    expect(input.value).toBe("");
  });

  it("does not send an empty payload after the live FileList is reset", () => {
    const agents = fakeFile("AGENTS.md", "## Goals\nShip it.\n");
    const listed: Array<typeof agents> = [agents];
    const live = {
      length: 1,
      item(index: number) {
        return listed[index] ?? null;
      },
      *[Symbol.iterator]() {
        yield* listed;
      },
    };
    const input = {
      files: live as unknown as FileList,
      value: "C:\\fakepath\\AGENTS.md",
      stored: "C:\\fakepath\\AGENTS.md",
    };

    Object.defineProperty(input, "value", {
      get() {
        return input.stored;
      },
      set(next: string) {
        input.stored = next;
        if (next === "") {
          listed.splice(0, listed.length);
          live.length = 0;
        }
      },
      configurable: true,
    });

    const files = takeInputFiles(input);
    expect(files).toEqual([agents]);
    expect(Array.from(input.files ?? [])).toEqual([]);
  });
});

describe("readImportPayload", () => {
  it("prefers the relative picker path and keeps AGENTS.md content", async () => {
    await expect(
      readImportPayload([
        fakeFile("AGENTS.md", "## Goals\nShip it.\n", "repo/AGENTS.md"),
        fakeFile("notes.txt", "ignored"),
      ]),
    ).resolves.toEqual([
      { path: "repo/AGENTS.md", content: "## Goals\nShip it.\n" },
      { path: "notes.txt", content: "ignored" },
    ]);
    expect(importPathForFile({ name: "AGENTS.md", webkitRelativePath: "" })).toBe("AGENTS.md");
  });
});

describe("importedNodesNotice", () => {
  it("does not claim a successful import when nothing was recognized", () => {
    expect(importedNodesNotice(0)).toBe("Imported 0 files for review.");
    expect(importedNodesNotice(1)).toBe("Imported 1 file for review.");
    expect(unrecognizedImportMessage()).toContain("AGENTS.md");
  });
});
