import { Injectable, signal, computed, effect } from '@angular/core';
import { Lang, T, Translations } from '../mock/data';

/**
 * Driver-app UI service: active language, translations and locale-aware formatters.
 * (The name is historical — it no longer holds any mock data.)
 */
@Injectable({ providedIn: 'root' })
export class MockDataService {
  readonly lang = signal<Lang>(MockDataService.loadLang());

  // Internal computed signal for translations
  private readonly _t = computed(() => T[this.lang()] ?? T['en']);

  // Reactive translation getter
  get t(): Translations { return this._t(); }

  constructor() {
    // Remember the driver's language across visits (per browser); no-op during SSR.
    effect(() => {
      const l = this.lang();
      try { if (typeof localStorage !== 'undefined') localStorage.setItem('paytaxi.lang', l); } catch { /* private mode etc. */ }
    });
  }

  private static loadLang(): Lang {
    try {
      if (typeof localStorage === 'undefined') return 'en';
      const saved = localStorage.getItem('paytaxi.lang');
      return saved === 'ka' || saved === 'ru' || saved === 'en' ? saved : 'en';
    } catch { return 'en'; }
  }

  /** Translate `key` and substitute `{name}` placeholders. Unknown keys fall back to the key itself. */
  tr(key: string, params?: Record<string, string | number>): string {
    let s = (this.t as Record<string, string>)[key] ?? key;
    if (params) {
      for (const [k, v] of Object.entries(params)) s = s.split(`{${k}}`).join(String(v));
    }
    return s;
  }

  /** BCP-47 locale for the active language (dates, times). */
  get locale(): string {
    switch (this.lang()) {
      case 'ka': return 'ka-GE';
      case 'ru': return 'ru-RU';
      default:   return 'en-GB';
    }
  }

  formatGel(amount: number): string {
    return `₾ ${amount.toFixed(2)}`;
  }

  /** "18 Sep" for the current year, "18 Sep 2025" otherwise, in the active language. */
  formatDate(date: Date): string {
    const opts: Intl.DateTimeFormatOptions = { day: '2-digit', month: 'short' };
    if (date.getFullYear() !== new Date().getFullYear()) opts.year = 'numeric';
    try { return date.toLocaleDateString(this.locale, opts); }
    catch { return date.toLocaleDateString('en-GB', opts); }
  }

  formatTime(date: Date): string {
    const opts: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit' };
    try { return date.toLocaleTimeString(this.locale, opts); }
    catch { return date.toLocaleTimeString('en-GB', opts); }
  }

  formatDateTime(date: Date): string {
    return `${this.formatDate(date)}, ${this.formatTime(date)}`;
  }

  /** Up to two initials from the first two words; ignores punctuation-only tokens like "(unnamed)". */
  initials(name: string): string {
    const letters = (name ?? '')
      .split(/\s+/)
      .map(w => w.replace(/^[^\p{L}\p{N}]+/u, ''))
      .filter(w => w.length > 0)
      .slice(0, 2)
      .map(w => w[0]);
    return letters.join('').toUpperCase();
  }
}
