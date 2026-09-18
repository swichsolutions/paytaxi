import { Injectable, signal, computed, effect } from '@angular/core';
import {
  MOCK_DRIVER, MOCK_CARDS, MOCK_CASHOUTS, MOCK_RIDES,
  Driver, BankCard, Cashout, Ride, Lang, T, Translations,
} from '../mock/data';

@Injectable({ providedIn: 'root' })
export class MockDataService {
  readonly driver   = signal<Driver>({ ...MOCK_DRIVER });
  readonly cards    = signal<BankCard[]>([...MOCK_CARDS]);
  readonly cashouts = signal<Cashout[]>([...MOCK_CASHOUTS]);
  readonly rides    = signal<Ride[]>([...MOCK_RIDES]);
  readonly lang     = signal<Lang>(MockDataService.loadLang());

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

  formatGel(amount: number): string {
    return `₾ ${amount.toFixed(2)}`;
  }

  formatDate(date: Date): string {
    return date.toLocaleDateString('en-GB', { day: '2-digit', month: 'short' });
  }

  formatTime(date: Date): string {
    return date.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }

  formatDateTime(date: Date): string {
    return `${this.formatDate(date)}, ${this.formatTime(date)}`;
  }

  calcFee(amount: number): number {
    return Math.max(2, parseFloat((amount * 0.01).toFixed(2)));
  }

  initials(name: string): string {
    return name.split(' ').slice(0, 2).map(w => w[0]).join('').toUpperCase();
  }
}
