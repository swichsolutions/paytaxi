import type { LegalKey } from './legal-config';

/**
 * Shared shape of the public legal documents (privacy policy, terms of use). One page component
 * (legal-page.ts) renders either of them; the route's `data.doc` says which.
 */
export type LegalLang = 'ka' | 'en' | 'ru';

export type LegalDocKind = 'privacy' | 'terms';

export type Block =
  | { kind: 'p'; text: string }
  | { kind: 'ul'; items: string[] }
  | { kind: 'table'; head: string[]; rows: string[][] };

export interface PolicySection {
  id: string;
  title: string;
  blocks: Block[];
}

export interface LegalDoc {
  lang: LegalLang;
  /** Short language name for the switcher. */
  langName: string;
  eyebrow: string;
  title: string;
  subtitle: string;
  versionLabel: string;
  effectiveLabel: string;
  draftDateLabel: string;
  draftTag: string;
  draftBanner: string;
  blankTitle: string;
  blanks: Record<LegalKey, string>;
  toc: string;
  skipToContent: string;
  print: string;
  backToApp: string;
  /** Shown above the text of a translation; empty for the governing (Georgian) text. */
  governingNote: string;
  /** "In short" card — privacy policy only. */
  summaryTitle?: string;
  summary?: string[];
  /** "At a glance" ledger table — privacy policy only. */
  glanceTitle?: string;
  glanceHead?: [string, string, string];
  glance?: [string, string, string][];
  sections: PolicySection[];
  footer: string;
}

/** Version and dates of one document. Each document has its own, so a change to one never re-dates the other. */
export interface LegalMeta {
  /** Shown at the top. Bump on every change once published. */
  version: string;
  /** ISO date the document takes effect (set when the lawyer signs off). Null = draft. */
  effectiveDate: string | null;
  /** Date of this draft, shown while the document is a draft. */
  draftDate: string;
}

export const LEGAL_LANGS: LegalLang[] = ['ka', 'en', 'ru'];

/** Georgian at /privacy, translations at /privacy/en and /privacy/ru — each a stable, shareable URL. */
export function normalizeLegalLang(raw: string | null | undefined): LegalLang {
  return raw === 'en' || raw === 'ru' ? raw : 'ka';
}

/** Router link to a legal document in a language: Georgian at the bare path, translations below it. */
export function legalLink(kind: LegalDocKind, lang: LegalLang): string[] {
  return lang === 'ka' ? [`/${kind}`] : [`/${kind}`, lang];
}
