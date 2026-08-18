export function Placeholder({ title }: { title: string }) {
  return (
    <section className="space-y-2">
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      <p className="max-w-xl text-sm leading-6 text-muted">
        This screen is a stub. Context editor, compile preview, and local code tools are available.
      </p>
    </section>
  );
}
