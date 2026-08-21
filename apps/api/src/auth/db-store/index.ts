import { DbStoreCore } from "./core.js";
import { withDbContext } from "./context.js";
import { withDbGithub } from "./github.js";
import { withDbIdentity } from "./identity.js";
import { withDbJobs } from "./jobs.js";
import { withDbOrgs } from "./orgs.js";
import { withDbReports } from "./reports.js";
import { withDbRepos } from "./repos.js";
import { withDbRoadmap } from "./roadmap.js";
import { withDbTokens } from "./tokens.js";
import { withDbWork } from "./work.js";

const DbAuthStoreBase = withDbJobs(
  withDbGithub(
    withDbRepos(
      withDbReports(
        withDbWork(
          withDbTokens(
            withDbContext(withDbRoadmap(withDbOrgs(withDbIdentity(DbStoreCore)))),
          ),
        ),
      ),
    ),
  ),
);

export class DbAuthStore extends DbAuthStoreBase {}
