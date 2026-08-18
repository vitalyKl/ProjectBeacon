import { describe, expect, it } from "vitest";

import type { PublicMe, PublicOrg, PublicProject } from "@/lib/api";

import { pickOrg, pickProject } from "./selection";

const personal: PublicOrg = {
  id: "org-personal",
  slug: "me",
  name: "Personal",
  kind: "personal",
};

const team: PublicOrg = {
  id: "org-team",
  slug: "acme",
  name: "Acme",
  kind: "team",
};

const beacon: PublicProject = {
  id: "proj-beacon",
  org_id: team.id,
  slug: "beacon",
  name: "Beacon",
  description: "",
  visibility: "private",
  default_repo_id: null,
};

const other: PublicProject = {
  ...beacon,
  id: "proj-other",
  slug: "other",
  name: "Other",
};

function me(orgs: PublicOrg[], personalOrg: PublicOrg | null = personal): PublicMe {
  return {
    id: "user-1",
    login: "admin",
    email: null,
    name: null,
    avatar_url: null,
    personal_org: personalOrg,
    orgs,
  };
}

describe("pickOrg", () => {
  it("prefers a stored org that still exists", () => {
    expect(pickOrg(me([personal, team]), team.id)).toEqual(team);
  });

  it("falls back to the personal org when the stored id is gone", () => {
    expect(pickOrg(me([personal, team]), "missing")).toEqual(personal);
  });

  it("returns null when the user has no orgs", () => {
    expect(pickOrg(me([], null), null)).toBeNull();
  });
});

describe("pickProject", () => {
  it("prefers a stored project that still exists", () => {
    expect(pickProject([beacon, other], other.id)).toEqual(other);
  });

  it("falls back to the first project after login when storage is empty", () => {
    expect(pickProject([beacon, other], null)).toEqual(beacon);
  });

  it("returns null so Home can show the first-project form", () => {
    expect(pickProject([], "proj-beacon")).toBeNull();
  });
});
