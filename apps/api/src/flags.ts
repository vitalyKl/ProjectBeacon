export function envFlagEnabled(env: NodeJS.ProcessEnv, name: string): boolean {
  const dotted = env[name];
  if (dotted !== undefined) {
    return dotted === "true" || dotted === "1";
  }
  const underscored = env[name.replaceAll(".", "_").toUpperCase()];
  return underscored === "true" || underscored === "1";
}

export function isHostedCloneEnabled(env: NodeJS.ProcessEnv = process.env): boolean {
  return envFlagEnabled(env, "ff.hosted_clone");
}
