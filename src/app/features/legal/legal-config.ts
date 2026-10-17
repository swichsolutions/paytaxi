import type { LegalDoc, LegalLang, LegalMeta } from './legal-doc';

/** A value that reads differently per language (legal names, addresses); a plain string is the same in all. */
type Localized = string | Record<LegalLang, string>;

/**
 * Everything in the legal documents (privacy policy, terms of use) that only the business or the
 * lawyer can supply. One place to fill in; the pages render any `null` as a highlighted blank and
 * show a "draft — not yet in force" banner while a value a document uses is blank or the document
 * has no effective date (see PRIVACY_META / TERMS_META below).
 *
 * Source of the filled values: the licence agreement drafted by the lawyer (ხელშეკრულება.docx,
 * 09.10.2026) and its annexes 1–3. Annex 3 §6: each taxi park is the CONTROLLER of its drivers'
 * data; Swich is the PROCESSOR (უფლებამოსილი პირი). Agreement §10.1.9: personal data is returned
 * and deleted within 30 days after the relationship ends, except what the law requires keeping.
 */
export const LEGAL_DETAILS = {
  // ── Parties ─────────────────────────────────────────────────────────
  /**
   * Platform owner and processor (agreement preamble and §14). The Russian name ends in a parenthetical,
   * so the Russian texts never put "(…)" straight after {provider}.
   */
  provider: { ka: 'შპს სვიჩსოლუშენსი', en: 'Swich Solutions LLC', ru: 'ООО «Свич Солюшенс» (Swich Solutions LLC)' } as Localized | null,
  providerId: '405848882' as Localized | null,
  providerAddress: {
    ka: 'საქართველო, ქ. თბილისი, დავით გამრეკელის ქ. N 8, ბინა 5',
    en: '8 Davit Gamrekeli St., apt. 5, Tbilisi, Georgia',
    ru: 'Грузия, г. Тбилиси, ул. Давида Гамрекели, д. 8, кв. 5',
  } as Localized | null,
  /** The licensee: operates PayTaxi commercially in Georgia (still blank in the agreement). */
  operator: null as Localized | null,
  /** The licensee's identification code and registered address — the privacy policy names it as a processor (Art. 24(1)(a)). */
  operatorId: null as Localized | null,
  operatorAddress: null as Localized | null,

  // ── Contacts and vendors ────────────────────────────────────────────
  /**
   * Where data-subject requests go — Swich's official e-mail in the agreement (§14). The privacy
   * policy says this is a Google mailbox (recipients); if it moves to another provider, change that line.
   */
  email: 'swichsolutions@gmail.com' as Localized | null,
  /**
   * Drivers' first-line support (phone and/or e-mail), run by the operator — terms of use only.
   * Unknown until the operator is. If it is not ready at launch, put Swich's {email} here.
   */
  support: 'swichsolutions@gmail.com' as Localized | null,   // until the operator has its own support contact
  /** SMS gateway that delivers sign-in codes and account notices: smsoffice.ge, operated by შპს „ირკა“ (chosen 16.10.2026). */
  sms: { ka: 'შპს „ირკა“ (smsoffice.ge)', en: 'Irka LLC (smsoffice.ge)', ru: 'ООО «Ирка» (smsoffice.ge)' } as Localized | null,
  /** Hosting provider for the servers and database (not chosen yet). */
  hosting: null as Localized | null,
  /** Country/region of the hosting data centre. */
  hostingLocation: null as Localized | null,
  /**
   * Country of the Yandex company that receives the Fleet API data. The Fleet API terms the platform works under
   * (yandex.ru/legal/taxi_api_partners, checked 17.10.2026) are an offer of ООО «Яндекс.Такси» (Yandex.Taxi LLC)
   * governed by the law of the Russian Federation (clause 2.4). Lawyer to confirm against the park's own Yandex contract
   * (LAWYER-NOTES item 22).
   */
  yandexCountry: { ka: 'რუსეთი', en: 'Russia', ru: 'Россия' } as Localized | null,
  /**
   * The Art. 37 ground and safeguards of each transfer abroad (Yandex, Google, the host if abroad), e.g.
   * "the country is on the State Audit Office's list of countries with adequate safeguards" — lawyer.
   */
  transferBasis: null as Localized | null,
  /**
   * How long financial records are kept under Georgian tax/accounting law (lawyer to confirm), WITH its
   * unit, so Russian plurals come out right: { ka: '6 წელი', en: '6 years', ru: '6 лет' }.
   */
  financialYears: null as Localized | null,
  /** How long the hosting provider keeps technical (server) logs, with its unit: { ka: '30 დღე', en: '30 days', ru: '30 дней' }. */
  logDays: null as Localized | null,
};

export type LegalKey = keyof typeof LEGAL_DETAILS;

/**
 * Privacy policy version and dates. When the policy text or a value it uses changes after
 * publication, bump `version` and `effectiveDate` (the policy promises that the date and
 * version at the top change with every edit).
 */
export const PRIVACY_META: LegalMeta = {
  version: '1.2',
  effectiveDate: null,
  draftDate: '2026-10-14',
};

/**
 * Terms of use version and dates — separate from the privacy policy, because the terms promise
 * 7 days' notice of a change and a driver's acceptance refers to a terms version.
 */
export const TERMS_META: LegalMeta = {
  version: '1.0',
  effectiveDate: null,
  draftDate: '2026-10-14',
};

const PLACEHOLDER = /\{([a-zA-Z]+)\}/g;

/** Every {placeholder} a document uses, in any of its languages. */
export function placeholdersIn(docs: Record<LegalLang, LegalDoc>): Set<string> {
  const keys = new Set<string>();
  const scan = (s: string | undefined) => { if (s) for (const m of s.matchAll(PLACEHOLDER)) keys.add(m[1]); };
  for (const d of Object.values(docs)) {
    scan(d.footer);
    d.summary?.forEach(scan);
    d.glance?.forEach(row => row.forEach(scan));
    for (const s of d.sections) {
      for (const b of s.blocks) {
        if (b.kind === 'p') scan(b.text);
        else if (b.kind === 'ul') b.items.forEach(scan);
        else b.rows.forEach(row => row.forEach(scan));
      }
    }
  }
  return keys;
}

/**
 * True while a document is not ready to be in force: it has no effective date, or a value it
 * uses is still blank. The page then says it is a draft, so it cannot pass for the version in force.
 */
export function legalIsDraft(meta: LegalMeta, docs: Record<LegalLang, LegalDoc>): boolean {
  if (!meta.effectiveDate) return true;
  for (const key of placeholdersIn(docs)) {
    if (valueFor(key, 'ka') === null || valueFor(key, 'en') === null || valueFor(key, 'ru') === null) return true;
  }
  return false;
}

export interface TextPart { text: string; missing?: LegalKey }

function valueFor(key: string, lang: LegalLang): string | null {
  const v = (LEGAL_DETAILS as Record<string, unknown>)[key];
  if (typeof v === 'string') return v.length > 0 ? v : null;
  if (v && typeof v === 'object') return (v as Record<LegalLang, string>)[lang] ?? null;
  return null;
}

/** "Write to {email}." → [{text:'Write to '}, {text:'privacy@…'}] or a `missing` part when blank. */
export function fillParts(text: string, lang: LegalLang): TextPart[] {
  const parts: TextPart[] = [];
  const re = new RegExp(PLACEHOLDER.source, 'g');
  let last = 0;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    if (m.index > last) parts.push({ text: text.slice(last, m.index) });
    const value = valueFor(m[1], lang);
    parts.push(value !== null ? { text: value } : { text: '', missing: m[1] as LegalKey });
    last = m.index + m[0].length;
  }
  if (last < text.length) parts.push({ text: text.slice(last) });
  return parts;
}
