import postgres from "postgres";

export async function checkDatabase(databaseUrl: string | undefined): Promise<boolean> {
  if (!databaseUrl) {
    return false;
  }

  const sql = postgres(databaseUrl, {
    max: 1,
    connect_timeout: 3,
    idle_timeout: 1,
  });

  try {
    await sql`select 1`;
    return true;
  } catch {
    return false;
  } finally {
    await sql.end({ timeout: 1 });
  }
}
