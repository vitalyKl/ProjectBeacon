import { homedir } from "node:os";
import { join } from "node:path";

export const CONFIG_FILE_NAME = "config.toml";

export function resolveBeaconHome(env: NodeJS.ProcessEnv = process.env): string {
  const override = env["BEACON_HOME"]?.trim();
  if (override) {
    return override;
  }
  return join(homedir(), ".beacon");
}

export function resolveConfigPath(home: string = resolveBeaconHome()): string {
  return join(home, CONFIG_FILE_NAME);
}
