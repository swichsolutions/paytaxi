import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminAlertsService, AdminAlert } from '../../services/admin-alerts.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminAuthService } from '../../services/admin-auth.service';

/**
 * The red strip at the top of every admin page while money needs a human: failed or stuck
 * settlements, cashouts parked for review, open reconciliation items. One line per park and
 * kind, each with a link to the page that fixes it. Dismiss hides this set; new trouble returns.
 */
@Component({
  selector: 'app-admin-alert-banner',
  imports: [RouterLink],
  templateUrl: './alert-banner.html',
  styleUrl: './alert-banner.scss',
})
export class AdminAlertBannerComponent {
  readonly alerts = inject(AdminAlertsService);
  private i18n = inject(AdminI18nService);
  private fmt = inject(AdminMockService);
  private auth = inject(AdminAuthService);

  get t() { return this.i18n.t; }

  /** Park managers see one park only; the park name on each line would be noise for them. */
  readonly showParkName = computed(() => this.auth.admin()?.role !== 'park_admin');
  readonly isParkAdmin = computed(() => this.auth.admin()?.role === 'park_admin');

  readonly severity = computed(() => this.alerts.dangerAlerts().length > 0 ? 'danger' : 'warning');

  /** Link with its query string split for routerLink. */
  linkParts(a: AdminAlert): { path: string; query: Record<string, string> } {
    const [path, qs] = a.link.split('?');
    const query: Record<string, string> = {};
    if (qs) for (const kv of qs.split('&')) { const [k, v] = kv.split('='); if (k) query[k] = v ?? ''; }
    return { path, query };
  }

  headline(a: AdminAlert): string {
    const n = String(a.count);
    const amount = a.amount != null ? this.fmt.formatGel(a.amount, 2) : '';
    switch (a.kind) {
      case 'settlement_failed':    return (a.count === 1 ? this.t.alertSettlementFailedOne : this.t.alertSettlementFailedMany).replace('{n}', n).replace('{amount}', amount);
      case 'settlement_stuck':     return (a.count === 1 ? this.t.alertSettlementStuckOne : this.t.alertSettlementStuckMany).replace('{n}', n).replace('{amount}', amount);
      case 'cashout_review':       return (a.count === 1 ? this.t.alertCashoutReviewOne : this.t.alertCashoutReviewMany).replace('{n}', n).replace('{amount}', amount);
      case 'reconciliation_open':  return (a.count === 1 ? this.t.alertReconOpenOne : this.t.alertReconOpenMany).replace('{n}', n);
    }
  }

  /** What to do about it — the park manager and Swich get different advice for the same failure. */
  advice(a: AdminAlert): string | null {
    if (a.kind === 'settlement_failed') {
      switch (a.code) {
        case 'INSUFFICIENT_PARK_BALANCE': return this.isParkAdmin() ? this.t.alertAdviceTopUpPark : this.t.alertAdviceTopUpSwich;
        case 'SWICH_IBAN_NOT_CONFIGURED': return this.t.alertAdviceSwichIban;
        case 'NO_PARK_ACCOUNT':           return this.t.alertAdviceNoAccount;
        default:                          return this.t.alertAdviceRetryTonight;
      }
    }
    if (a.kind === 'settlement_stuck') return this.t.alertAdviceStuck;
    if (a.kind === 'cashout_review')   return this.t.alertAdviceReview;
    return null;
  }

  since(a: AdminAlert): string {
    return this.fmt.formatRelTime(new Date(a.since));
  }
}
