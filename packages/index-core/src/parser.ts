import Parser from "tree-sitter";
import JavaScript from "tree-sitter-javascript";
import Python from "tree-sitter-python";
import TypeScript from "tree-sitter-typescript";
import { hasSymbolGrammar } from "./languages.js";
import type { ExtractedImport, ExtractedSymbol, LanguageId, ParseResult, TreeSitterParser } from "./types.js";

type SyntaxNode = Parser.SyntaxNode;

const JS_LIKE = new Set<LanguageId>(["javascript", "typescript", "tsx"]);

function asLanguage(mod: unknown): Parser.Language {
  return mod as Parser.Language;
}

function nodeName(node: SyntaxNode): string | null {
  const name = node.childForFieldName("name");
  if (name && name.text) {
    return name.text;
  }
  return null;
}

function extractJsLike(root: SyntaxNode): ParseResult {
  const symbols: ExtractedSymbol[] = [];
  const imports: ExtractedImport[] = [];
  const parentStack: string[] = [];

  const pushSymbol = (name: string, kind: string, node: SyntaxNode): void => {
    symbols.push({
      name,
      kind,
      startLine: node.startPosition.row + 1,
      endLine: node.endPosition.row + 1,
      parentName: parentStack.at(-1) ?? null,
    });
  };

  const visit = (node: SyntaxNode): void => {
    switch (node.type) {
      case "function_declaration":
      case "generator_function_declaration": {
        const name = nodeName(node);
        if (name) {
          pushSymbol(name, "function", node);
        }
        break;
      }
      case "class_declaration": {
        const name = nodeName(node);
        if (name) {
          pushSymbol(name, "class", node);
          parentStack.push(name);
          const body = node.childForFieldName("body");
          if (body) {
            for (const child of body.namedChildren) {
              visit(child);
            }
          }
          parentStack.pop();
          return;
        }
        break;
      }
      case "method_definition": {
        const name = nodeName(node);
        if (name) {
          pushSymbol(name, "method", node);
        }
        break;
      }
      case "lexical_declaration":
      case "variable_declaration": {
        for (const child of node.namedChildren) {
          if (child.type !== "variable_declarator") {
            continue;
          }
          const nameNode = child.childForFieldName("name");
          const value = child.childForFieldName("value");
          if (!nameNode || nameNode.type !== "identifier") {
            continue;
          }
          if (value && (value.type === "arrow_function" || value.type === "function_expression")) {
            pushSymbol(nameNode.text, "function", child);
          }
        }
        break;
      }
      case "export_statement": {
        for (const child of node.namedChildren) {
          visit(child);
        }
        return;
      }
      case "import_statement": {
        const source = node.childForFieldName("source");
        if (source) {
          imports.push({ toSpec: unquote(source.text) });
        }
        return;
      }
      case "call_expression": {
        const fn = node.childForFieldName("function");
        const args = node.childForFieldName("arguments");
        if (fn && args && (fn.text === "require" || fn.text === "import")) {
          const first = args.namedChildren[0];
          if (first && (first.type === "string" || first.type === "string_fragment")) {
            imports.push({ toSpec: unquote(first.text) });
          }
        }
        break;
      }
      default:
        break;
    }
    for (const child of node.namedChildren) {
      visit(child);
    }
  };

  visit(root);
  return { symbols, imports };
}

function extractPython(root: SyntaxNode): ParseResult {
  const symbols: ExtractedSymbol[] = [];
  const imports: ExtractedImport[] = [];

  const visit = (node: SyntaxNode, parentName: string | null): void => {
    if (node.type === "function_definition" || node.type === "class_definition") {
      const name = nodeName(node);
      if (name) {
        symbols.push({
          name,
          kind: node.type === "class_definition" ? "class" : "function",
          startLine: node.startPosition.row + 1,
          endLine: node.endPosition.row + 1,
          parentName,
        });
        const body = node.childForFieldName("body");
        if (body) {
          for (const child of body.namedChildren) {
            visit(child, name);
          }
        }
        return;
      }
    }
    if (node.type === "import_statement" || node.type === "import_from_statement") {
      const moduleNode =
        node.childForFieldName("module_name") ??
        node.namedChildren.find((child) => child.type === "dotted_name" || child.type === "relative_import");
      if (moduleNode) {
        imports.push({ toSpec: moduleNode.text });
      }
      return;
    }
    for (const child of node.namedChildren) {
      visit(child, parentName);
    }
  };

  visit(root, null);
  return { symbols, imports };
}

function unquote(text: string): string {
  if (
    (text.startsWith('"') && text.endsWith('"')) ||
    (text.startsWith("'") && text.endsWith("'")) ||
    (text.startsWith("`") && text.endsWith("`"))
  ) {
    return text.slice(1, -1);
  }
  return text;
}

export class NativeTreeSitterParser implements TreeSitterParser {
  private readonly parser = new Parser();
  private readonly languages: Partial<Record<LanguageId, Parser.Language>> = {
    javascript: asLanguage(JavaScript),
    typescript: asLanguage(TypeScript.typescript),
    tsx: asLanguage(TypeScript.tsx),
    python: asLanguage(Python),
  };

  parse(source: string, lang: LanguageId): ParseResult {
    if (!hasSymbolGrammar(lang)) {
      return { symbols: [], imports: [] };
    }
    const grammar = this.languages[lang];
    if (!grammar) {
      return { symbols: [], imports: [] };
    }
    this.parser.setLanguage(grammar);
    const tree = this.parser.parse(source);
    if (JS_LIKE.has(lang)) {
      return extractJsLike(tree.rootNode);
    }
    if (lang === "python") {
      return extractPython(tree.rootNode);
    }
    return { symbols: [], imports: [] };
  }
}

export function createParser(): TreeSitterParser {
  return new NativeTreeSitterParser();
}
