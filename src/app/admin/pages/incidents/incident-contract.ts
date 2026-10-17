import { Component, inject, input, output } from '@angular/core';
import { ApiIncidentMetrics } from '../../services/admin-api.service';
import { AdminI18nService } from '../../services/admin-i18n.service';

/**
 * The incident log's monthly contract card — Annex 1 of the licence agreement:
 *   §5(a) owner-fault P1s not resolved within target against the [3] threshold,
 *   §5(b) their total duration against [48] h,
 *   §6(a) P1s left without a response for more than [72] h,
 *   §7    the trial period and whether an owner-fault P1 missed its target in it — or a P1 whose fault is
 *         not determined did, which leaves the answer open rather than "no".
 * Only incidents under the agreement count: the licensee's parks and platform-wide ones (the API filters).
 * Swich and the licensee only (the page does not render it for park managers; the API refuses them too).
 * Presentational: the page owns the month and the drawer.
 */
@Component({
  selector: 'app-incident-contract',
  templateUrl: './incident-contract.html',
  styleUrl: './incident-contract.scss',
})
export class IncidentContractComponent {
  private i18n = inject(AdminI18nService);
  get t() { return this.i18n.t; }

  readonly metrics = input.required<ApiIncidentMetrics>();
  /** Step the month: -1 back, +1 forward. */
  readonly shift = output<number>();
  /** Open a cited incident in the page's drawer. */
  readonly open = output<string>();

  /** One segment per incident up to the threshold. */
  segments(total: number, filled: number): boolean[] {
    return Array.from({ length: Math.max(1, total) }, (_, k) => k < filled);
  }

  pct(value: number, max: number): number { return max > 0 ? Math.min(100, (value / max) * 100) : 0; }

  hours(n: number): string { return n.toLocaleString('en-US', { maximumFractionDigits: 1 }); }

  private locale(): string { return this.i18n.lang() === 'ka' ? 'ka-GE' : 'en-GB'; }

  monthLabel(month: string): string {
    const [y, m] = month.split('-').map(Number);
    return new Date(Date.UTC(y, m - 1, 1)).toLocaleDateString(this.locale(), { month: 'long', year: 'numeric', timeZone: 'UTC' });
  }

  /** A calendar date (yyyy-MM-dd) or an instant, as a Tbilisi date. */
  date(iso: string | null | undefined): string {
    if (!iso) return '—';
    return new Date(iso.length === 10 ? iso + 'T12:00:00Z' : iso)
      .toLocaleDateString(this.locale(), { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'Asia/Tbilisi' });
  }

  /** Last day of the trial (the window ends at the midnight after it). */
  trialLastDay(m: ApiIncidentMetrics): string {
    return m.trial.to ? this.date(new Date(new Date(m.trial.to).getTime() - 60_000).toISOString()) : '—';
  }
}
