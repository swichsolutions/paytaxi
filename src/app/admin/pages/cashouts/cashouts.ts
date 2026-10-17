import { Component, HostListener, computed, effect, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService, ApiCashout } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AdminAuthService } from '../../services/admin-auth.service';
import { ManualCashoutComponent } from './manual-cashout/manual-cashout';
import { downloadCsv, isoDateLocal } from '../../shared/csv';
import { WaitCause, reasonCode, waitCause } from '../../shared/payout-wait';

/** UI status, mapped 1:1 from the backend enum. */
export type UiCashoutStatus = 'queued' | 'processing' | 'completed' | 'failed' | 'review';
type StatusTab = 'all' | UiCashoutStatus;
type DateRange = 'today' | 'yesterday' | 'week' | 'all';

interface DisplayCashout {
  id: string;
  driverId: string;
  driverName: string;
  amount: number;
  fee: number;
  net: number;
  status: UiCashoutStatus;
  rawStatus: string;           // backend status: Queued | Processing | Completed | Failed | ReviewRequired
  bankTransferId: string | null;
  yandexTransactionId: string | null;
  errorMessage: string | null;
  initiatedBy: 'driver' | 'manager';
  bankType: string;
  maskedPan: string;
  holderName: string | null;
  isThirdPartyAccount: boolean;
  retryOfCashoutId: string | null;
  retriedByCashoutId: string | null;
  attemptCount: number;
  nextAttemptAt: Date | null;
  /** Queued rows only: what the payout is waiting for (from the failure-reason code). */
  waitCause: WaitCause | null;
  /** A document went to the bank and the bank never said what became of it — it may be paid, so no Cancel. */
  outcomeUnknown: boolean;
  createdAt: Date;
}

@Component({
  selector: 'app-admin-cashouts',
  imports: [FormsModule, RouterLink, ManualCashoutComponent],
  templateUrl: './cashouts.html',
  styleUrl: './cashouts.scss',
})
export class CashoutsComponent {
  svc = inject(AdminMockService); // kept for formatGel/formatRel/initials utilities
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private i18n = inject(AdminI18nService);
  private auth = inject(AdminAuthService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  get t() { return this.i18n.t; }

  search    = signal('');
  status    = signal<StatusTab>('all');
  range     = signal<DateRange>('all'); // backend returns recent rows; let server decide
  expanded  = signal<string | null>(null);
  showManualModal = signal(false);

  /** Driver to preselect in the manual-cashout modal (from ?driverId=, set by the Drivers drawer). */
  presetDriverId = signal<string | null>(null);

  private manual = viewChild(ManualCashoutComponent);

  loading = signal(true);
  loadError = signal<string | null>(null);
  raw = signal<ApiCashout[]>([]);

  readonly firstLoad = computed(() => this.loading() && this.raw().length === 0 && this.loadError() === null);

  constructor() {
    // Lazy-init the park context, then re-fetch whenever the selected park changes.
    this.parkCtx.ensureLoaded();
    effect(() => {
      const parkId = this.parkCtx.currentParkId();
      if (parkId) this.fetchFor(parkId);
    });

    // Alert banner deep links: /admin/cashouts?status=review, ?status=queued. Followed from this page
    // too (the banner sits on every page and Angular reuses the component), so listen, don't snapshot.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(q => {
      const wanted = q.get('status');
      if (wanted && ['queued', 'processing', 'completed', 'failed', 'review'].includes(wanted)) {
        this.status.set(wanted as StatusTab);
        this.range.set('all');
      }
    });

    // "New cashout for {driver}" deep link from the Drivers drawer.
    const driverId = this.route.snapshot.queryParamMap.get('driverId');
    if (driverId) {
      this.presetDriverId.set(driverId);
      this.showManualModal.set(true);
      // Strip the param so a refresh doesn't re-open the modal.
      void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
    }
  }

  refresh() {
    const parkId = this.parkCtx.currentParkId();
    if (parkId && !this.loading()) void this.fetchFor(parkId);
  }

  private async fetchFor(parkId: string) {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const resp = await this.api.listCashouts(parkId, 200);
      this.raw.set(resp.cashouts);
    } catch (err: any) {
      this.loadError.set(err?.error?.message ?? this.t.errLoadCashouts);
    } finally {
      this.loading.set(false);
    }
  }

  private inRange(d: Date): boolean {
    // Computed per call: a page left open overnight must not keep yesterday's "now".
    const ms = Date.now() - d.getTime();
    switch (this.range()) {
      case 'today':     return ms < 24 * 3600 * 1000;
      case 'yesterday': return ms >= 24 * 3600 * 1000 && ms < 48 * 3600 * 1000;
      case 'week':      return ms < 7 * 24 * 3600 * 1000;
      default:          return true;
    }
  }

  /** Backend PascalCase → UI status. Every backend value has its own tab and pill. */
  private mapStatus(s: string): UiCashoutStatus {
    switch (s.toLowerCase()) {
      case 'completed':      return 'completed';
      case 'failed':         return 'failed';
      case 'reviewrequired': return 'review';
      case 'processing':     return 'processing';
      case 'queued':         return 'queued';
      default:               return 'processing';
    }
  }

  statusLabel(s: UiCashoutStatus): string {
    switch (s) {
      case 'queued':     return this.t.queued;
      case 'processing': return this.t.processing;
      case 'completed':  return this.t.completed;
      case 'failed':     return this.t.failed;
      case 'review':     return this.t.reviewrequired;
    }
  }

  all = computed<DisplayCashout[]>(() => this.raw().map(c => ({
    id: c.id,
    driverId: c.driverId,
    driverName: c.driverName ?? this.t.unnamed,
    amount: c.amount,
    fee: c.fee,
    net: c.amount - c.fee,
    status: this.mapStatus(c.status),
    rawStatus: c.status,
    bankTransferId: c.bankTransferId,
    yandexTransactionId: c.yandexTransactionId,
    errorMessage: c.failureReason,
    initiatedBy: (c.initiatedBy ?? '').startsWith('driver') ? 'driver' : 'manager',
    bankType: c.bankType,
    maskedPan: c.maskedPan,
    holderName: c.holderName ?? null,
    isThirdPartyAccount: c.isThirdPartyAccount === true,
    retryOfCashoutId: c.retryOfCashoutId ?? null,
    retriedByCashoutId: c.retriedByCashoutId ?? null,
    attemptCount: c.attemptCount ?? 0,
    nextAttemptAt: c.nextAttemptAt ? new Date(c.nextAttemptAt) : null,
    waitCause: this.mapStatus(c.status) === 'queued' ? waitCause(reasonCode(c.failureReason)) : null,
    outcomeUnknown: c.outcomeUnknown ?? (c.unresolvedAttempts ?? 0) > 0,
    createdAt: new Date(c.createdAt),
  })));

  // ── Queued: what the payout waits for, in plain words ─────────────
  /** Short label under the status pill. */
  waitShort(c: DisplayCashout): string {
    switch (c.waitCause) {
      case 'funds':      return this.t.waitShortFunds;
      case 'no_account': return this.t.waitShortNoAccount;
      case 'setup':      return this.t.waitShortSetup;
      case 'unresolved': return this.t.waitShortUnresolved;
      case 'bank':       return this.t.waitShortBank;
      case 'revoked':    return this.t.waitShortRevoked;
      default:           return this.t.waitShortOther;
    }
  }

  /** The sentence in the expanded row. The park manager is told what they can do about it. */
  waitText(c: DisplayCashout): string {
    const own = this.parkAdmin();
    switch (c.waitCause) {
      case 'funds':      return own ? this.t.waitReasonFundsPark : this.t.waitReasonFunds;
      case 'no_account': return this.t.waitReasonNoAccount;
      case 'setup':      return this.t.waitReasonSetup;
      case 'unresolved': return this.t.waitReasonUnresolved;
      case 'bank':       return this.t.waitReasonBank;
      // Annex 3 §7: never sent from this park's account any more — a person cancels it (Swich or the operator).
      case 'revoked':    return own ? this.t.waitReasonRevokedPark : this.t.waitReasonRevoked;
      default:           return this.t.waitReasonOther;
    }
  }

  private readonly parkAdmin = computed(() => this.auth.admin()?.role === 'park_admin');

  /**
   * Cancelling a waiting payout is for Swich and the operator. The park's manager is the one obliged to
   * keep the account funded (Annex 3 §5): a payout short of funds waits for their top-up, it is not theirs
   * to withdraw. The server refuses them too; the console simply does not offer it.
   */
  readonly canCancel = computed(() => { const r = this.auth.admin()?.role; return r === 'super_admin' || r === 'operator'; });

  filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    const status = this.status();
    let list = this.all().filter(c => this.inRange(c.createdAt));
    if (status !== 'all') list = list.filter(c => c.status === status);
    if (term) {
      list = list.filter(c =>
        c.driverName.toLowerCase().includes(term) ||
        c.id.toLowerCase().includes(term)
      );
    }
    return list.sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime());
  });

  counts = computed(() => {
    const inRange = this.all().filter(c => this.inRange(c.createdAt));
    const by = (s: UiCashoutStatus) => inRange.filter(c => c.status === s).length;
    return {
      all:        inRange.length,
      queued:     by('queued'),
      processing: by('processing'),
      completed:  by('completed'),
      failed:     by('failed'),
      review:     by('review'),
    };
  });

  summary = computed(() => {
    const list = this.filtered();
    const value = list.reduce((s, c) => s + c.amount, 0);
    const fees  = list.reduce((s, c) => s + c.fee, 0);
    return { count: list.length, value, fees };
  });

  toggleExpand(id: string) {
    this.expanded.update(v => v === id ? null : id);
  }

  // ── Export ────────────────────────────────────────────────────────
  exportCsv() {
    const rows = this.filtered();
    if (rows.length === 0) return;
    const park = this.parkCtx.currentPark();
    downloadCsv(
      `paytaxi-cashouts-${park?.slug ?? 'park'}-${isoDateLocal()}`,
      [this.t.cashoutId, this.t.colDriver, this.t.colAmount, this.t.feeWord, this.t.netToDriver, this.t.colStatus,
       this.t.colDestination, this.t.colInitiated, this.t.colWhen, this.t.bankTransfer, this.t.yandexTransaction],
      rows.map(c => [
        c.id, c.driverName, c.amount.toFixed(2), c.fee.toFixed(2), c.net.toFixed(2), c.rawStatus,
        `${c.bankType} ${c.maskedPan}`.trim(), c.initiatedBy, c.createdAt.toISOString(),
        c.bankTransferId ?? '', c.yandexTransactionId ?? '',
      ]),
    );
  }

  // ── Retry / process-now (retry needs an explicit confirmation) ────
  retrying = signal<string | null>(null);
  retryMessage = signal<string | null>(null);
  openingInvoice = signal<string | null>(null);
  confirmRetryId = signal<string | null>(null);

  askRetry(id: string, e: Event) {
    e.stopPropagation();
    if (this.retrying()) return;
    this.confirmCancelId.set(null);
    this.confirmRetryId.set(id);
    this.expanded.set(id);
  }

  cancelRetry(e: Event) {
    e.stopPropagation();
    this.confirmRetryId.set(null);
  }

  async openInvoice(cashoutId: string, e: Event) {
    e.stopPropagation();
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.openingInvoice()) return;
    this.openingInvoice.set(cashoutId);
    try {
      await this.api.openInvoice(parkId, cashoutId);
    } catch (err: any) {
      this.retryMessage.set(err?.error?.message ?? this.t.errOpenInvoice);
    } finally {
      this.openingInvoice.set(null);
    }
  }

  async retry(id: string, e: Event) {
    e.stopPropagation();
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.retrying()) return;
    this.confirmRetryId.set(null);
    this.retrying.set(id);
    this.retryMessage.set(null);
    try {
      const result = await this.api.retryCashout(parkId, id);
      this.retryMessage.set(this.describeRetry(result));
      await this.fetchFor(parkId);
    } catch (err: any) {
      // The saga returns the result body even on 422 (Failed) / 202 (ReviewRequired)
      // — those are legitimate outcomes, not transport errors. Surface them
      // the same way as success.
      const sagaResult = err?.error;
      if (sagaResult && typeof sagaResult.status === 'string' && sagaResult.cashoutId) {
        this.retryMessage.set(this.describeRetry(sagaResult));
        await this.fetchFor(parkId);
      } else if (err?.error?.error === 'cashout_rejected' && err?.error?.code === 'authorisation_revoked') {
        // The park revoked the platform's authorisation (Annex 3 §7): a retry would be a new payout from its account.
        this.retryMessage.set(`${this.t.retryFailed} ${this.t.rejAuthorisationRevoked}`);
      } else if (err?.status === 409 && err?.error?.error === 'already_retried') {
        // Someone (or a second click) already retried it — refresh so the button disappears.
        this.retryMessage.set(this.t.alreadyRetried);
        await this.fetchFor(parkId);
      } else {
        const msg = err?.error?.message ?? err?.error?.error ?? err?.message ?? '';
        this.retryMessage.set(`${this.t.retryFailed} ${msg}`.trim());
      }
    } finally {
      this.retrying.set(null);
    }
  }

  /** Clear a Queued cashout's backoff and run the payout step right now. */
  async processNow(id: string, e: Event) {
    e.stopPropagation();
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.retrying()) return;
    this.retrying.set(id);
    this.retryMessage.set(null);
    try {
      const result = await this.api.processCashoutNow(parkId, id);
      this.retryMessage.set(this.describeRetry(result));
      await this.fetchFor(parkId);
    } catch (err: any) {
      const sagaResult = err?.error;
      if (sagaResult && typeof sagaResult.status === 'string' && sagaResult.cashoutId) {
        this.retryMessage.set(this.describeRetry(sagaResult));
        await this.fetchFor(parkId);
      } else {
        const msg = err?.error?.message ?? err?.error?.error ?? err?.message ?? '';
        this.retryMessage.set(`${this.t.retryFailed} ${msg}`.trim());
      }
    } finally {
      this.retrying.set(null);
    }
  }

  // ── Cancel a waiting payout (inline confirm with a required reason) ─
  // Nothing that waits is given back automatically any more; this is the person deciding to.
  confirmCancelId = signal<string | null>(null);
  cancelReason = signal('');
  cancelError = signal<string | null>(null);
  cancelling = signal(false);
  readonly canSubmitCancel = computed(() => !this.cancelling() && this.cancelReason().trim().length > 0);

  askCancel(id: string, e: Event) {
    e.stopPropagation();
    if (this.cancelling()) return;
    this.confirmRetryId.set(null);
    this.cancelReason.set('');
    this.cancelError.set(null);
    this.confirmCancelId.set(id);
    this.expanded.set(id);
  }

  dismissCancel(e: Event) {
    e.stopPropagation();
    if (this.cancelling()) return;
    this.confirmCancelId.set(null);
    this.cancelError.set(null);
  }

  async submitCancel(c: DisplayCashout, e: Event) {
    e.stopPropagation();
    const parkId = this.parkCtx.currentParkId();
    const reason = this.cancelReason().trim();
    if (!parkId || this.cancelling()) return;
    if (!reason) { this.cancelError.set(this.t.errCancelReasonRequired); return; }
    if (reason.length > 500) { this.cancelError.set(this.t.errReasonTooLong); return; }

    this.cancelling.set(true);
    this.cancelError.set(null);
    this.retryMessage.set(null);
    try {
      const r = await this.api.cancelCashout(parkId, c.id, reason);
      this.retryMessage.set(r.status === 'ReviewRequired'
        ? this.t.cancelNeedsReview
        : this.t.cancelledOk.replace('{amount}', this.formatGel(c.amount, 2)));
      this.confirmCancelId.set(null);
      await this.fetchFor(parkId);
    } catch (err: any) {
      const body = err?.error;
      // 202 ReviewRequired arrives as a body with a status, like the saga's other outcomes.
      if (body && typeof body.status === 'string' && body.cashoutId) {
        this.retryMessage.set(body.status === 'ReviewRequired' ? this.t.cancelNeedsReview : `${this.t.retryReturned} ${body.status}`);
        this.confirmCancelId.set(null);
        await this.fetchFor(parkId);
        return;
      }
      switch (body?.error) {
        case 'reason_required':   this.cancelError.set(this.t.errCancelReasonRequired); return;
        case 'reason_too_long':   this.cancelError.set(this.t.errReasonTooLong); return;
        case 'outcome_unknown':   this.retryMessage.set(this.t.errCancelOutcomeUnknown); break;
        case 'bank_has_order':    this.retryMessage.set(this.t.errCancelBankHasOrder); break;
        case 'bank_unconfirmed':  this.retryMessage.set(this.t.errCancelBankUnconfirmed); break;
        case 'already_cancelled': this.retryMessage.set(this.t.errCancelAlready); break;
        case 'not_cancellable':   this.retryMessage.set(this.t.errCancelNotQueued); break;
        default:                  this.cancelError.set(`${this.t.errCancelFailed} ${body?.message ?? err?.message ?? ''}`.trim()); return;
      }
      // The row moved on under us (sent, cancelled elsewhere, bank unsure): close the strip, show its real state.
      this.confirmCancelId.set(null);
      await this.fetchFor(parkId);
    } finally {
      this.cancelling.set(false);
    }
  }

  private describeRetry(r: { status: string; cashoutId: string; failureReason: string | null }): string {
    const short = r.cashoutId.slice(0, 8);
    if (r.status === 'Completed')      return `${this.t.retrySucceeded} ${short}…`;
    if (r.status === 'ReviewRequired') return `${this.t.retryNeedsReview} ${short}…`;
    if (r.status === 'Queued')         return `${this.t.queued} · ${short}…`;
    if (r.status === 'Failed')         return `${this.t.retryFailedAgain} ${r.failureReason ?? this.t.noReason}`;
    return `${this.t.retryReturned} ${r.status}`;
  }

  // ── Manual cashout modal ─────────────────────────────────────────
  openManual()  {
    this.presetDriverId.set(null);
    this.showManualModal.set(true);
  }

  /** Backdrop click / Escape: ignored while the saga request is in flight (D2). */
  requestCloseManual() {
    if (this.manual()?.submitting()) return;
    this.closeManual();
  }

  @HostListener('document:keydown.escape')
  onEscape() {
    if (this.showManualModal()) this.requestCloseManual();
  }

  closeManual() {
    this.showManualModal.set(false);
    this.presetDriverId.set(null);
    // Refresh the list so the new cashout appears.
    const parkId = this.parkCtx.currentParkId();
    if (parkId) this.fetchFor(parkId);
  }

  setStatus(s: StatusTab) { this.status.set(s); }
  setRange(r: DateRange)  { this.range.set(r); }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatRel(d: Date)          { return this.svc.formatRelTime(d); }
  initials(n: string)         { return this.svc.initials(n); }

  /**
   * "27 May, 17:08" for older rows, or "Today · 17:08" / "Yesterday · 17:08"
   * for recent ones. The relative "X ago" stays as the subline.
   */
  formatTime(d: Date): string {
    const time = d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    const now = new Date();
    const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
    const dayMs = 24 * 3600 * 1000;
    const startOfThis = new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
    const daysAgo = Math.round((startOfToday - startOfThis) / dayMs);
    if (daysAgo === 0) return `${this.t.today} · ${time}`;
    if (daysAgo === 1) return `${this.t.yesterday} · ${time}`;
    return `${d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short' })}, ${time}`;
  }
}
