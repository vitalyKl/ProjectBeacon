import { t, tf } from "./i18n";

export const UNRECOGNIZED_IMPORT_MESSAGE =
  "No recognized context files. Import AGENTS.md, CLAUDE.md, CONVENTIONS.md, Cursor or Grok rules, CODEOWNERS, or a package manifest.";

export type FileInputLike = {
  files: FileList | null;
  value: string;
};

export type ImportPayloadFile = {
  path: string;
  content: string;
};

/** Copy the live FileList before the input is reset. Clearing first empties the list. */
export function takeInputFiles(input: FileInputLike): File[] {
  const files = input.files ? Array.from(input.files) : [];
  input.value = "";
  return files;
}

export function importPathForFile(file: Pick<File, "name" | "webkitRelativePath">): string {
  return file.webkitRelativePath || file.name;
}

export async function readImportPayload(
  files: Iterable<Pick<File, "name" | "webkitRelativePath" | "text">>,
): Promise<ImportPayloadFile[]> {
  const payload: ImportPayloadFile[] = [];
  for (const file of files) {
    payload.push({ path: importPathForFile(file), content: await file.text() });
  }
  return payload;
}

export function importedNodesNotice(count: number): string {
  return count === 1 ? t("context.importedOne") : tf("context.importedMany", { count: String(count) });
}

export function unrecognizedImportMessage(): string {
  return t("context.unrecognized");
}
