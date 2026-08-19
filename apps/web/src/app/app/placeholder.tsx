import { t } from "@/lib/i18n";

export function Placeholder({ title }: { title: string }) {
  return (
    <section className="space-y-2">
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      <p className="max-w-xl text-sm leading-6 text-muted">{t("placeholder.stub")}</p>
    </section>
  );
}
