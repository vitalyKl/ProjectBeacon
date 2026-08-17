import { drizzle } from "drizzle-orm/postgres-js";
import postgres from "postgres";
import * as schema from "./schema/index.js";

export type Db = ReturnType<typeof createDb>;

export function createDb(url: string) {
  const client = postgres(url);
  return drizzle(client, { schema });
}
