import config from "@beacon/config/eslint.config.js";

export default [
  { ignores: ["fixtures/**", "scripts/**", "_tmp_*.mjs"] },
  ...config,
];
