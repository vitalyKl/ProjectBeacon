declare module "tree-sitter-javascript" {
  const language: unknown;
  export default language;
}

declare module "tree-sitter-python" {
  const language: unknown;
  export default language;
}

declare module "tree-sitter-typescript" {
  const typescript: unknown;
  const tsx: unknown;
  const languages: { typescript: unknown; tsx: unknown };
  export default languages;
  export { typescript, tsx };
}
