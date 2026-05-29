import { Injectable, PLATFORM_ID, computed, effect, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import en from '../i18n/en.json';
import ka from '../i18n/ka.json';

export type AdminLang = 'en' | 'ka';
export type AdminTranslations = typeof en & Record<string, string>;

const DICT: Record<AdminLang, AdminTranslations> = {
  en: en as AdminTranslations,
  ka: ka as AdminTranslations,
};

const STORAGE_KEY = 'paytaxi.admin.lang';

/**
 * Admin-console i18n. Mirrors the driver app's MockDataService translation
 * approach but is scoped to the admin namespace and only ships English +
 * Georgian (no Russian for the admin console). Default is English; the choice
 * is persisted to localStorage under `paytaxi.admin.lang`.
 */
@Injectable({ providedIn: 'root' })
export class AdminI18nService {
  private platformId = inject(PLATFORM_ID);
  readonly lang = signal<AdminLang>('en');

  constructor() {
    if (isPlatformBrowser(this.platformId)) {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved === 'en' || saved === 'ka') this.lang.set(saved);
      effect(() => localStorage.setItem(STORAGE_KEY, this.lang()));
    }
  }

  private readonly _t = computed(() => DICT[this.lang()] ?? DICT.en);
  get t(): AdminTranslations { return this._t(); }

  setLang(l: AdminLang) { this.lang.set(l); }
  toggle() { this.lang.update(l => (l === 'en' ? 'ka' : 'en')); }
}
