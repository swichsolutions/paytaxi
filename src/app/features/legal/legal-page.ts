import { Component, DestroyRef, PLATFORM_ID, afterNextRender, computed, effect, inject, signal } from '@angular/core';
import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Meta, Title } from '@angular/platform-browser';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import { PRIVACY_META, TERMS_META, legalIsDraft } from './legal-config';
import { LEGAL_LANGS, LegalDoc, LegalDocKind, LegalLang, LegalMeta, legalLink, normalizeLegalLang } from './legal-doc';
import { PRIVACY_DOCS } from './privacy-content';
import { TERMS_DOCS } from './terms-content';
import { LegalTextComponent } from './legal-text';

const MONTHS: Record<LegalLang, string[]> = {
  en: ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'],
  ka: ['იანვარი', 'თებერვალი', 'მარტი', 'აპრილი', 'მაისი', 'ივნისი', 'ივლისი', 'აგვისტო', 'სექტემბერი', 'ოქტომბერი', 'ნოემბერი', 'დეკემბერი'],
  ru: ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'],
};

interface LegalDocument {
  docs: Record<LegalLang, LegalDoc>;
  meta: LegalMeta;
  /** id of <main>, the skip link's target. */
  mainId: string;
  /** The other legal document, linked from the footer. */
  related: LegalDocKind;
  /** Computed once: the documents are static. */
  draft: boolean;
}

const DOCUMENTS: Record<LegalDocKind, LegalDocument> = {
  privacy: { docs: PRIVACY_DOCS, meta: PRIVACY_META, mainId: 'policy', related: 'terms', draft: legalIsDraft(PRIVACY_META, PRIVACY_DOCS) },
  terms: { docs: TERMS_DOCS, meta: TERMS_META, mainId: 'terms', related: 'privacy', draft: legalIsDraft(TERMS_META, TERMS_DOCS) },
};

function docKind(raw: unknown): LegalDocKind {
  return raw === 'terms' ? 'terms' : 'privacy';
}

/**
 * Public legal page: the privacy policy (/privacy) or the terms of use (/terms), chosen by the
 * route's `data.doc`. Georgian at the bare path, translations at /…/en and /…/ru. No login,
 * prerendered per language, plain text only. The legal blanks (operator, contact, vendors…) come
 * from LEGAL_DETAILS; while a value a document uses is missing, or it has no effective date, the
 * page says it is a draft, so it cannot be mistaken for the version in force.
 */
@Component({
  selector: 'app-legal-page',
  imports: [RouterLink, LegalTextComponent],
  templateUrl: './legal-page.html',
  styleUrl: './legal-page.scss',
})
export class LegalPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);
  private readonly document = inject(DOCUMENT);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  readonly kind = toSignal(
    this.route.data.pipe(map(data => docKind(data['doc']))),
    { initialValue: docKind(this.route.snapshot.data['doc']) });
  readonly lang = toSignal(
    this.route.paramMap.pipe(map(p => normalizeLegalLang(p.get('lang')))),
    { initialValue: normalizeLegalLang(this.route.snapshot.paramMap.get('lang')) });

  private readonly document$ = computed(() => DOCUMENTS[this.kind()]);
  readonly docs = computed(() => this.document$().docs);
  readonly d = computed(() => this.docs()[this.lang()]);
  readonly draft = computed(() => this.document$().draft);
  readonly version = computed(() => this.document$().meta.version);
  readonly mainId = computed(() => this.document$().mainId);
  readonly langs = LEGAL_LANGS;

  /** Footer link to the other legal document, in the same language. */
  readonly related = computed(() => {
    const kind = this.document$().related;
    return { link: legalLink(kind, this.lang()), title: DOCUMENTS[kind].docs[this.lang()].title };
  });

  /** "In force from 1 November 2026" or, while a draft, "Draft of 13 October 2026". */
  readonly dateLine = computed(() => {
    const d = this.d();
    const meta = this.document$().meta;
    return meta.effectiveDate && !this.draft()
      ? `${d.effectiveLabel} ${this.formatDate(meta.effectiveDate)}`
      : `${d.draftDateLabel} ${this.formatDate(meta.draftDate)}`;
  });

  /** Section the reader is in (desktop contents list highlight). */
  readonly activeId = signal<string | null>(null);
  private stopWatching: (() => void) | null = null;

  constructor() {
    const previousLang = this.document.documentElement.lang;
    effect(() => {
      const d = this.d();
      this.title.setTitle(`${d.title} · PayTaxi`);
      this.meta.updateTag({ name: 'description', content: d.subtitle });
      this.document.documentElement.lang = d.lang;
    });
    inject(DestroyRef).onDestroy(() => {
      this.document.documentElement.lang = previousLang;
      this.stopWatching?.();
    });

    afterNextRender(() => this.watchSections());
  }

  langLink(l: LegalLang): string[] {
    return legalLink(this.kind(), l);
  }

  num(i: number): string {
    return String(i + 1).padStart(2, '0');
  }

  print() {
    if (this.isBrowser) window.print();
  }

  private formatDate(iso: string): string {
    const [y, m, day] = iso.split('-').map(Number);
    const month = MONTHS[this.lang()][m - 1];
    return this.lang() === 'ru' ? `${day} ${month} ${y} г.` : `${day} ${month} ${y}`;
  }

  /** Highlight the last section whose heading has passed the upper third of the screen. Browser only. */
  private watchSections() {
    const win = this.document.defaultView;
    if (!win) return;
    let queued = false;
    const update = () => {
      queued = false;
      const line = win.innerHeight * 0.3;
      let current: string | null = null;
      this.document.querySelectorAll<HTMLElement>('.pp-sec').forEach(el => {
        if (el.getBoundingClientRect().top <= line) current = el.id;
      });
      this.activeId.set(current);
    };
    const onScroll = () => { if (!queued) { queued = true; win.requestAnimationFrame(update); } };
    win.addEventListener('scroll', onScroll, { passive: true });
    this.stopWatching = () => win.removeEventListener('scroll', onScroll);
    update();
  }
}
