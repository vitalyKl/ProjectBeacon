import { localeKeyCoverage } from "./i18n";

const rows = localeKeyCoverage();
for (const row of rows) {
  const pct = row.total === 0 ? 100 : Math.round((row.present / row.total) * 100);
  console.log(`${row.locale}\t${row.present}/${row.total}\t${pct}%`);
  for (const key of row.missingKeys) {
    console.log(`  ${key}`);
  }
}
