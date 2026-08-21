import { drizzle } from "drizzle-orm/postgres-js";
import postgres from "postgres";
import * as schema from "./schema/index.js";

export function createDb(url: string) {
  const client = postgres(url);
  const db = drizzle(client, { schema });
  Object.assign(db, {
    end: (options?: { timeout?: number }) => client.end(options),
  });
  return db;
}

export type Db = ReturnType<typeof createDb>;
export type ClosableDb = Db & { end: (options?: { timeout?: number }) => Promise<void> };
