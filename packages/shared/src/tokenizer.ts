export const TOKENIZER_ID = "js_length_div_4";

export function jsLengthDiv4(text: string): number {
  return Math.ceil(text.length / 4);
}
