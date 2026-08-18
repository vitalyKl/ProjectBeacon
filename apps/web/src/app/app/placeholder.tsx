export function Placeholder({ title }: { title: string }) {
  return (
    <section className="space-y-2">
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      <p className="max-w-xl text-sm leading-6 text-muted">Coming in a later PR. Navigation only.</p>
    </section>
  );
}
