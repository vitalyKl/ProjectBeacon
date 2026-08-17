import { greet } from "./greet.js";
import { helper } from "./util.js";

export function main(): void {
  greet(helper());
}
