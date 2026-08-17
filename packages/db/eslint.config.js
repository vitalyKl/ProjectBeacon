import config from "@beacon/config/eslint.config.js";

export default [
  ...config,
  {
    ignores: ["drizzle/**"],
  },
];


