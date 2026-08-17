import { customType, timestamp } from "drizzle-orm/pg-core";

export const bytea = customType<{ data: Buffer; driverData: Buffer }>({
  dataType() {
    return "bytea";
  },
});

export const timestamptz = (name: string) =>
  timestamp(name, { withTimezone: true, mode: "date" });
