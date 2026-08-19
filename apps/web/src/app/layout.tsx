import type { Metadata } from "next";
import type { ReactNode } from "react";

import "./globals.css";

import { t } from "@/lib/i18n";

export const metadata: Metadata = {
  title: t("common.brand"),
  description: t("layout.description"),
};

export default function RootLayout({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <html lang="en" className="h-full antialiased">
      <body className="min-h-full bg-background text-foreground">{children}</body>
    </html>
  );
}
