import { Injectable, inject, signal } from '@angular/core';
import { MOCK_HOURLY_VOLUME } from '../mock/admin-data';
import { AdminI18nService } from './admin-i18n.service';

/**
 * Formatting helpers shared by the admin pages (GEL amounts, relative time,
 * initials). Historically this also held the Phase-5 mock dataset; every page
 * now reads the backend, so only the formatters (and the hourly-volume stub
 * kept for the chart's fallback) remain.
 */
@Injectable({ providedIn: 'root' })
export class AdminMockService {
  private i18n = inject(AdminI18nService);

  readonly hourlyVolume = signal([...MOCK_HOURLY_VOLUME]);

  // ── Formatting helpers ───────────────────────────────────────────
  formatGel(amount: number, digits = 2): string {
    return `₾ ${amount.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits })}`;
  }

  formatCount(n: number): string {
    return n.toLocaleString('en-US');
  }

  formatRelTime(d: Date): string {
    const t = this.i18n.t;
    const diffSec = Math.max(0, (Date.now() - d.getTime()) / 1000);
    if (diffSec < 60)      return `${Math.round(diffSec)}${t.relSec}`;
    if (diffSec < 3600)    return `${Math.round(diffSec / 60)}${t.relMin}`;
    if (diffSec < 86_400)  return `${Math.round(diffSec / 3600)}${t.relHour}`;
    return `${Math.round(diffSec / 86_400)}${t.relDay}`;
  }

  initials(name: string): string {
    return name.split(' ').filter(Boolean).slice(0, 2).map(w => w[0]).join('').toUpperCase() || '??';
  }
}
