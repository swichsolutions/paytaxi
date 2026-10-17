import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminAlertsService, AdminAlert } from '../../services/admin-alerts.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminAuthService } from '../../services/admin-auth.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { waitCause } from '../payout-wait';

/**
 * The red strip at the top of every admin page while money needs a human: failed or stuck
 * settlements, cashouts parked for review, payouts waiting for the bank or a top-up, open
 * reconciliation items, open P1/P2 incidents, and (for Swich and the operator) what still waits on a
 * park that revoked the platform's authorisation (Annex 3 §7). One line per park (or one for all parks) and
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
  private parkCtx = inject(AdminParkContextService);

  get t() { return this.i18n.t; }

  /** Park managers see one park only; the park name on each line would be noise for them. */
  readonly showParkName = computed(() => this.auth.admin()?.role !== 'park_admin');
  readonly isParkAdmin = computed(() => this.auth.admin()?.role === 'park_admin');
  readonly isSuperAdmin = computed(() => this.auth.admin()?.role === 'super_admin');

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
      case 'park_suspended':       return this.isParkAdmin() ? this.t.alertParkSuspendedOwn : this.t.alertParkSuspended;
      case 'payout_waiting':       return (a.count === 1 ? this.t.alertPayoutWaitingOne : this.t.alertPayoutWaitingMany).replace('{n}', n).replace('{amount}', amount);
      case 'incident_open': {
        // Code is "<worst level>_<REPORTED|ACKNOWLEDGED>" — see AlertsController.
        const p1 = (a.code ?? '').startsWith('P1');
        if (a.count === 1) return p1 ? this.t.alertIncidentP1One : this.t.alertIncidentP2One;
        return (p1 ? this.t.alertIncidentP1Many : this.t.alertIncidentP2Many).replace('{n}', n);
      }
      case 'signin_locked':        return (a.count === 1 ? this.t.alertSigninLockedOne : this.t.alertSigninLockedMany).replace('{n}', n);
      case 'authorisation_revoked': {
        // Annex 3 §7, Swich and the operator only: what still waits on a park whose authorisation is revoked.
        const r = a.revocation;
        const parts = [
          r && r.payouts > 0 ? this.t.alertRevokedPayouts.replace('{n}', String(r.payouts)).replace('{amount}', this.fmt.formatGel(r.payoutsNet, 2)) : '',
          r && r.settlements > 0 ? this.t.alertRevokedOwed.replace('{n}', String(r.settlements)).replace('{amount}', this.fmt.formatGel(r.owed, 2)) : '',
        ].filter(Boolean).join(' · ');
        return `${this.t.alertRevoked.replace('{date}', this.day(r?.revokedFrom))} ${parts}`.trim();
      }
    }
  }

  /** "2026-11-13" → "13 Nov 2026" (a calendar day — no time zone to convert). */
  private day(iso: string | null | undefined): string {
    if (!iso) return '';
    return new Date(`${iso}T00:00:00Z`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
  }

  /** What to do about it — the park manager and Swich get different advice for the same failure. */
  advice(a: AdminAlert): string | null {
    if (a.kind === 'settlement_failed') {
      switch (a.code) {
        case 'INSUFFICIENT_PARK_BALANCE':
        case 'INSUFFICIENT_FUNDS':        return this.isParkAdmin() ? this.t.alertAdviceTopUpPark : this.t.alertAdviceTopUpSwich;
        case 'SWICH_IBAN_NOT_CONFIGURED': return this.t.alertAdviceSwichIban;
        case 'NO_PARK_ACCOUNT':           return this.t.alertAdviceNoAccount;
        default:                          return this.t.alertAdviceRetryTonight;
      }
    }
    if (a.kind === 'settlement_stuck') return this.t.alertAdviceStuck;
    if (a.kind === 'cashout_review')   return this.t.alertAdviceReview;
    if (a.kind === 'signin_locked')    return this.t.alertAdviceSigninLocked;
    if (a.kind === 'park_suspended')   return a.detail ? this.t.alertAdviceSuspendedReason.replace('{reason}', a.detail) : null;
    if (a.kind === 'payout_waiting') {
      // The park manager is the one who can top up; Swich and the operator can only wait or chase.
      switch (waitCause(a.code)) {
        case 'funds':      return this.isParkAdmin()
          ? this.t.alertAdviceWaitTopUpPark.replace('{amount}', a.amount != null ? this.fmt.formatGel(a.amount, 2) : '')
          : this.t.alertAdviceWaitTopUpSwich;
        case 'no_account': return this.t.alertAdviceWaitNoAccount;
        case 'setup':      return this.t.alertAdviceWaitSetup;
        case 'bank':
        case 'unresolved': return this.t.alertAdviceWaitBank;
        // Annex 3 §7: never sent from the park's account any more — Swich or the operator cancels them.
        case 'revoked':    return this.isParkAdmin() ? this.t.alertAdviceWaitRevokedPark : this.t.alertAdviceWaitRevoked;
        // Cancel is Swich's and the operator's (the park is the party that must fund the payouts).
        default:           return this.isParkAdmin() ? this.t.alertAdviceWaitOtherPark : this.t.alertAdviceWaitOther;
      }
    }
    if (a.kind === 'authorisation_revoked') {
      const r = a.revocation;
      return [
        r && r.payouts > 0 ? this.t.alertAdviceRevokedPayouts : '',
        r && r.settlements > 0 ? (r.licenseeOwed > 0 ? this.t.alertAdviceRevokedLicensee : this.t.alertAdviceRevokedPark) : '',
      ].filter(Boolean).join(' ') || null;
    }
    if (a.kind === 'incident_open') {
      // Has the owner responded yet? Swich is told to; the licensee and the park are told whether it has.
      const waiting = (a.code ?? '').endsWith('_REPORTED');
      if (this.isSuperAdmin()) return waiting ? this.t.alertAdviceIncidentSwichAck : this.t.alertAdviceIncidentSwichWorking;
      if (this.isParkAdmin()) return waiting ? this.t.alertAdviceIncidentParkWaiting : this.t.alertAdviceIncidentParkWorking;
      return waiting ? this.t.alertAdviceIncidentOpWaiting : this.t.alertAdviceIncidentOpWorking;
    }
    return null;
  }

  /**
   * Settings, Cashouts and the Settlements history show the park picked in the topbar: point it at the alert's
   * park before following the link (a settlement alert's link also carries the month the failed transfer is in).
   */
  open(a: AdminAlert) {
    // A platform-wide incident has no park (parkId null) — nothing to switch to.
    const parkScoped = ['park_suspended', 'payout_waiting', 'settlement_failed', 'settlement_stuck', 'authorisation_revoked', 'signin_locked'];
    if (parkScoped.includes(a.kind) && a.parkId) this.parkCtx.setCurrentPark(a.parkId);
  }

  since(a: AdminAlert): string {
    return this.fmt.formatRelTime(new Date(a.since));
  }
}
