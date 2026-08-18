import { basename } from "./paths.js";

const DENIED_DIR_NAMES = new Set([
  ".git",
  "node_modules",
  "dist",
  ".next",
  ".turbo",
  "coverage",
  "out",
  ".cache",
  ".vercel",
  "target",
  "vendor",
  "__pycache__",
  ".venv",
  "venv",
  "build",
  ".svn",
  ".hg",
  ".idea",
  ".vscode",
  "bower_components",
]);

const DENIED_FILE_NAMES = new Set([
  ".env",
  ".env.local",
  ".env.development",
  ".env.production",
  ".env.test",
  ".env.staging",
  ".env.example",
  "id_rsa",
  "id_dsa",
  "id_ecdsa",
  "id_ed25519",
  "id_rsa.pub",
  ".netrc",
  ".npmrc",
  ".pypirc",
  "credentials",
  "credentials.json",
  "service-account.json",
  "google-services.json",
  "auth.json",
]);

const DENIED_FILE_SUFFIXES = [".pem", ".key", ".p12", ".pfx", ".keystore", ".jks", ".kdbx", ".ppk"];

const DENIED_FILE_PATTERNS = [
  /^\.env\..+$/,
  /.*secret.*/i,
  /.*credentials.*\.json$/i,
  /^id_.*$/,
  /.*\.secret$/i,
  /^.*token.*\.(txt|json|env)$/i,
];

export function isDeniedDirName(name: string): boolean {
  return DENIED_DIR_NAMES.has(name.toLowerCase());
}

export function isDeniedFile(repoPosixPath: string): boolean {
  const name = basename(repoPosixPath);
  const lower = name.toLowerCase();
  if (DENIED_FILE_NAMES.has(lower)) {
    return true;
  }
  if (DENIED_FILE_SUFFIXES.some((suffix) => lower.endsWith(suffix))) {
    return true;
  }
  return DENIED_FILE_PATTERNS.some((pattern) => pattern.test(name));
}

export function pathHasDeniedSegment(repoPosixPath: string): boolean {
  if (repoPosixPath === "." || repoPosixPath === "") {
    return false;
  }
  return repoPosixPath.split("/").some((segment) => isDeniedDirName(segment));
}

export const BINARY_PROBE_BYTES = 8 * 1024;

export function containsNul(buffer: Uint8Array): boolean {
  return buffer.includes(0);
}

// Beacon project tokens are `bcn_` + 43-char base64url (47 chars total).
export const BEACON_TOKEN_RE = /\bbcn_[A-Za-z0-9_-]{43}\b/g;

export function findBeaconTokenHits(content: string): { offset: number }[] {
  const hits: { offset: number }[] = [];
  BEACON_TOKEN_RE.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = BEACON_TOKEN_RE.exec(content)) !== null) {
    hits.push({ offset: match.index });
  }
  return hits;
}

export function containsBeaconToken(content: string): boolean {
  return findBeaconTokenHits(content).length > 0;
}
