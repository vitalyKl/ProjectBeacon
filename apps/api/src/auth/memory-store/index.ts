import { withMemoryContext } from "./context.js";
import { MemoryStoreCore } from "./core.js";
import { withMemoryGithub } from "./github.js";
import { withMemoryIdentity } from "./identity.js";
import { withMemoryJobs } from "./jobs.js";
import { withMemoryOrgs } from "./orgs.js";
import { withMemoryReports } from "./reports.js";
import { withMemoryRepos } from "./repos.js";
import { withMemoryRoadmap } from "./roadmap.js";
import { withMemoryTokens } from "./tokens.js";
import { withMemoryWork } from "./work.js";

const MemoryAuthStoreBase = withMemoryJobs(
  withMemoryGithub(
    withMemoryRepos(
      withMemoryReports(
        withMemoryWork(
          withMemoryTokens(
            withMemoryContext(withMemoryRoadmap(withMemoryOrgs(withMemoryIdentity(MemoryStoreCore)))),
          ),
        ),
      ),
    ),
  ),
);

export class MemoryAuthStore extends MemoryAuthStoreBase {}
