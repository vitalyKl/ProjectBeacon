import eslint from "@eslint/js";
import tseslint from "typescript-eslint";

export default tseslint.config(
  {
    ignores: ["**/dist/**", "**/node_modules/**", "**/.turbo/**", "**/coverage/**", "**/.next/**"],
  },
  eslint.configs.recommended,
  tseslint.configs.recommended,
);
