import { codeIndexUnavailable, type CodeSource } from "@beacon/mcp-tools";

const UNAVAILABLE = "Code index is not available. Code tools need a local index.";

export function unavailableCodeSource(): CodeSource {
  return {
    getTree: async () => {
      throw codeIndexUnavailable(UNAVAILABLE);
    },
    searchCode: async () => {
      throw codeIndexUnavailable(UNAVAILABLE);
    },
    getFile: async () => {
      throw codeIndexUnavailable(UNAVAILABLE);
    },
    getSymbol: async () => {
      throw codeIndexUnavailable(UNAVAILABLE);
    },
    getOwners: async () => {
      throw codeIndexUnavailable(UNAVAILABLE);
    },
    getRelatedFiles: async () => {
      throw codeIndexUnavailable(UNAVAILABLE);
    },
    getChangedScope: async () => {
      throw codeIndexUnavailable(UNAVAILABLE);
    },
  };
}
