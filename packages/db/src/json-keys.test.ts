import { describe, expect, it } from "vitest";

import { duplicateJsonKeys } from "./json-keys.js";

describe("duplicateJsonKeys", () => {
  it("accepts unique keys, including the same key in sibling objects", () => {
    expect(duplicateJsonKeys('{"a":1,"b":{"a":2}}')).toEqual([]);
    expect(duplicateJsonKeys('[{"when":1},{"when":2}]')).toEqual([]);
  });

  it("reports duplicate keys that JSON.parse would silently drop", () => {
    const source = `{
      "idx": 1,
      "when": 1,
      "tag": "0001_tasks_github_issue_unique",
      "when": 2,
      "tag": "0001_sidecar_connections_repo_id_unique"
    }`;
    expect(duplicateJsonKeys(source)).toEqual([
      { key: "when", path: "$" },
      { key: "tag", path: "$" },
    ]);
    expect(JSON.parse(source)).toEqual({
      idx: 1,
      when: 2,
      tag: "0001_sidecar_connections_repo_id_unique",
    });
  });

  it("reports nested duplicates with a path", () => {
    const source = '{"entries":[{"idx":1,"tag":"a","tag":"b"}]}';
    expect(duplicateJsonKeys(source)).toEqual([{ key: "tag", path: "entries[0]" }]);
  });

  it("does not treat string contents as structure", () => {
    expect(duplicateJsonKeys('{"a":"{ \\"a\\": 1, \\"a\\": 2 }"}')).toEqual([]);
  });
});
