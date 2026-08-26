import { AsyncLocalStorage } from "node:async_hooks";

import type { Db } from "@beacon/db";

export class DbStoreCore {
  readonly writeTx: AsyncLocalStorage<Db> = new AsyncLocalStorage<Db>();

  #orgId: string | null = null;

  constructor(readonly db: Db) {}

  writeDb(): Db {
    return this.writeTx.getStore() ?? this.db;
  }

  setOrgId(orgId: string | null): void {
    if (this.#orgId === orgId) {
      return;
    }
    this.#orgId = orgId;
    if (orgId) {
      void this.db.execute(
        `SET app.actor_org_id = '${orgId}'`,
      );
    } else {
      void this.db.execute(`SET app.actor_org_id = ''`);
    }
  }
}
