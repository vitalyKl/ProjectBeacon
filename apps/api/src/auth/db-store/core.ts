import { AsyncLocalStorage } from "node:async_hooks";

import type { Db } from "@beacon/db";

export class DbStoreCore {
  readonly writeTx: AsyncLocalStorage<Db> = new AsyncLocalStorage<Db>();

  constructor(readonly db: Db) {}

  writeDb(): Db {
    return this.writeTx.getStore() ?? this.db;
  }
}
