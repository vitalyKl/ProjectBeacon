import { drizzle } from "drizzle-orm/postgres-js";
import postgres from "postgres";
import * as schema from "./schema/index.js";

export function createDb(url: string) {
  const client = postgres(url);
  const db = drizzle(client, { schema });
  return Object.assign(db, {
    end: (options?: { timeout?: number }) => client.end(options),
  });
}

export type Db = ReturnType<typeof createDb>;
