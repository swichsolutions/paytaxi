import { Component, computed, effect, inject, signal } from '@angular/core';
import {
  AdminApiService, ApiSettlement, ApiSettlementSummary, ApiSettlementCashoutsResponse, ApiSettlementMonth,
} from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminAuthService } from '../../services/admin-auth.service';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminI18nService } from '../../services/admin-i18n.service';

/**
 * Settlements: the nightly park → Swich fee-share sweep.
 *   - Everyone sees the current park's recovery/progress tiles, daily fee table and history.
 *   - Swich (super_admin) and the operator can widen the history to all parks.
 *   - Swich can retry a failed settlement and run today's settlement on demand.
 */
@Component({
  selector: 'app-admin-settlements',
  templateUrl: './settlements.html',
  styleUrl: './settlements.scss',
})
export class SettlementsComponent {
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private auth = inject(AdminAuthService);
  svc = inject(AdminMockService); // formatters
  private i18n = inject(AdminI18nService);

  get t() { return this.i18n.t; }

  readonly role = computed(() => this.auth.admin()?.role ?? '');
  readonly isSuperAdmin = computed(() => this.role() === 'super_admin');
  readonly seesAllParks = computed(() => this.role() === 'super_admin' || this.role() === 'operator');

  loading = signal(true);
  loadError = signal<string | null>(null);
  summary = signal<ApiSettlementSummary | null>(null);
  settlements = signal<ApiSettlement[]>([]);
  totals = signal<{ completedSwichShare: number; failedCount: number; failedSwichShare: number } | null>(null);
  scopeAll = signal(false);

  /** Monthly invoices PT-YYYY-MM of the current park (one row per month with a settlement). */
  months = signal<ApiSettlementMonth[]>([]);
  openingMonth = signal<string | null>(null);
  monthError = signal<string | null>(null);

  expanded = signal<string | null>(null);
  expandedCashouts = signal<ApiSettlementCashoutsResponse | null>(null);
  expandedLoading = signal(false);

  busy = signal<string | null>(null);   // settlement id or 'run'
  message = signal<string | null>(null);
  /** "Settle now" waits for an explicit confirmation (D17). */
  confirmRun = signal(false);

  /**
   * Failed-settlement tile scoped to the current park, like its siblings —
   * even when the history list is widened to all parks.
   */
  readonly parkFailed = computed(() => {
    const parkId = this.parkCtx.currentParkId();
    const failed = this.settlements().filter(s => s.status === 'Failed' && (!this.scopeAll() || s.parkId === parkId));
    return { count: failed.length, swichShare: failed.reduce((sum, s) => sum + s.swichShare, 0) };
  });

  constructor() {
    this.parkCtx.ensureLoaded();
    effect(() => {
      const parkId = this.parkCtx.currentParkId();
      const all = this.scopeAll();
      if (parkId) this.fetchFor(parkId, all);
    });
  }

  private async fetchFor(parkId: string, all: boolean) {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [summary, list, months] = await Promise.all([
        this.api.getSettlementSummary(parkId, 14),
        this.api.listSettlements(all ? null : parkId, 90),
        this.api.listSettlementMonths(parkId),
      ]);
      this.summary.set(summary);
      this.settlements.set(list.settlements);
      this.totals.set(list.totals);
      this.months.set(months.months);
    } catch (err: any) {
      this.loadError.set(err?.error?.message ?? this.t.errLoadSettlements);
    } finally {
      this.loading.set(false);
    }
  }

  async refresh() {
    const parkId = this.parkCtx.currentParkId();
    if (parkId) await this.fetchFor(parkId, this.scopeAll());
  }

  // ── Derived ──────────────────────────────────────────────────────

  phasePct = computed(() => {
    const s = this.summary();
    if (!s || s.park.phase1CapGel === null || s.phase1Progress === null) return 0;
    return Math.min(100, (s.phase1Progress / s.park.phase1CapGel) * 100);
  });

  hasPhase1 = computed(() => this.summary()?.park.phase1CapGel !== null && this.summary()?.park.phase1CapGel !== undefined);

  // ── Actions ──────────────────────────────────────────────────────

  async toggleExpand(s: ApiSettlement) {
    if (this.expanded() === s.id) { this.expanded.set(null); this.expandedCashouts.set(null); return; }
    this.expanded.set(s.id);
    this.expandedCashouts.set(null);
    this.expandedLoading.set(true);
    try {
      this.expandedCashouts.set(await this.api.listSettlementCashouts(s.id));
    } catch { /* leave empty */ }
    finally { this.expandedLoading.set(false); }
  }

  async retry(s: ApiSettlement, e: Event) {
    e.stopPropagation();
    if (this.busy()) return;
    this.busy.set(s.id);
    this.message.set(null);
    try {
      const r = await this.api.retrySettlement(s.id);
      this.message.set(`${this.t['settlementRetried']} ${r.status}${r.bankTransferId ? ' · ' + r.bankTransferId : ''}`);
    } catch (err: any) {
      const body = err?.error;
      this.message.set(body?.status
        ? `${this.t['settlementRetried']} ${body.status} — ${body.failureReason ?? ''}`
        : (body?.message ?? this.t.retryFailed));
    } finally {
      this.busy.set(null);
      await this.refresh();
    }
  }

  askRunNow() {
    if (this.busy()) return;
    this.confirmRun.set(true);
  }

  cancelRunNow() {
    this.confirmRun.set(false);
  }

  async runNow() {
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.busy()) return;
    this.confirmRun.set(false);
    this.busy.set('run');
    this.message.set(null);
    try {
      const r = await this.api.runSettlementNow(parkId);
      if (r && r.created === false) this.message.set(this.t['nothingToSettle']);
      else this.message.set(`${this.t['settlementRunResult']} ${r.status} · ${this.formatGel(r.swichShare ?? 0, 2)}${r.bankTransferId ? ' · ' + r.bankTransferId : ''}${r.failureReason ? ' — ' + r.failureReason : ''}`);
    } catch (err: any) {
      this.message.set(err?.error?.message ?? this.t.errRunFailed);
    } finally {
      this.busy.set(null);
      await this.refresh();
    }
  }

  setScopeAll(all: boolean) { this.scopeAll.set(all); }

  /** Open the PDF for one month in a new tab (fetched with the bearer token, opened as a blob URL). */
  async openMonthly(m: ApiSettlementMonth, e: Event) {
    e.stopPropagation();
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.openingMonth()) return;
    this.openingMonth.set(m.month);
    this.monthError.set(null);
    try {
      await this.api.openMonthlyInvoice(parkId, m.month);
    } catch (err: any) {
      this.monthError.set(err?.status === 404 ? this.t.errNoMonthlyInvoice : this.t.errOpenInvoice);
    } finally {
      this.openingMonth.set(null);
    }
  }

  /** "Sep 2026" from "2026-09" — the invoice ref stays as the machine label next to it. */
  formatMonth(month: string): string {
    const d = new Date(month + '-01T00:00:00');
    return d.toLocaleDateString('en-GB', { month: 'short', year: 'numeric' });
  }

  /**
   * The settlement dated this day covers a different set than the day's own columns show — an
   * on-demand run left later cashouts for the next day, or the first run swept older history.
   * Shown so "0 cashouts · Completed · ₾1.35" has its explanation right there.
   */
  coversOtherDays(d: { cashouts: number; fees: number; settlementCashouts?: number | null; settlementFees?: number | null }): boolean {
    if (d.settlementCashouts == null || d.settlementFees == null) return false;
    return d.settlementCashouts !== d.cashouts || Math.abs(d.settlementFees - d.fees) >= 0.005;
  }

  statusKey(s: string): string { return s.toLowerCase(); }
  statusLabel(s: string): string { return (this.t as Record<string, string>)[s.toLowerCase()] ?? s; }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatDate(iso: string): string {
    const d = new Date(iso.length === 10 ? iso + 'T00:00:00' : iso);
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  }
  formatDateTime(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short' }) + ' ' +
      d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }
}
