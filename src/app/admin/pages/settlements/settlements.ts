import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import {
  AdminApiService, ApiCost, ApiCostCategory, ApiDirectSettlement, ApiLicensee, ApiOverdue, ApiSettlement,
  ApiSettlementCashoutsResponse, ApiSettlementExternal, ApiSettlementMonth, ApiSettlementMonthView, ApiStatement,
  ApiStatementPark, UpdateLicenseeBody,
} from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminAuthService } from '../../services/admin-auth.service';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AgreementHistoryComponent } from './agreement-history/agreement-history';

interface CostForm { category: ApiCostCategory; vendor: string; invoiceNumber: string; invoiceDate: string; amount: string; note: string; }
interface LicenseeForm {
  name: string; taxId: string; email: string; iban: string; launchDate: string; barrierGel: string; swichSharePercent: string;
  reason: string;
}
interface FormulaLine { label: string; expr: string; strong?: boolean; }

/**
 * The one inline action open on a shared-cost entry: delete (its owner, with a reason — soft), hold (the other side,
 * with a reason), release (the side that held it) or correct (its owner, once included: a signed amount and a reason).
 */
type CostActionKind = 'delete' | 'hold' | 'release' | 'correct';
interface CostAction { costId: string; kind: CostActionKind; reason: string; amount: string; }

type ExternalOrigin = 'statement' | 'direct' | 'history';

/** A failed park transfer Swich records as paid outside the platform (§2.5). */
interface ExternalForm {
  settlementId: string;
  origin: ExternalOrigin;      // where it was opened — one transfer can be listed both in the statement and the history
  share: number;               // the transfer's full amount — the only amount the API accepts
  payers: Array<'licensee' | 'park'>;
  payer: 'licensee' | 'park';
  amount: string;
  paidOn: string;
  reference: string;
  note: string;
}

const CATEGORIES: ApiCostCategory[] = ['Bank', 'Sms', 'PaymentProvider', 'SupportComms', 'Hosting', 'Domain'];

/** "2026-10" for the local month of a date. */
function monthKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}
function shiftMonth(month: string, delta: number): string {
  const [y, m] = month.split('-').map(Number);
  return monthKey(new Date(y, m - 1 + delta, 1));
}
function todayIso(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}
/**
 * A number typed into a field, or null when there is none. A type=number input reports '' for a blank (or
 * unparsable) value, and Number('') is 0 — a money term must never become 0 because a field was left empty.
 */
function parseNumber(v: string): number | null {
  // A correction may be negative: accept the typographic minus too.
  const s = v.trim().replace(',', '.').replace('−', '-');
  if (!s) return null;
  const n = Number(s);
  return Number.isFinite(n) ? n : null;
}

/**
 * Settlements — the licence agreement's monthly settlement (§2.2–2.6).
 *   - Swich and the operator see the same month (§2.6: identical data for both sides): the licensee's
 *     statement — income, each side's shared costs, the formula with the real numbers, barrier progress,
 *     the per-park split with transfer status and §4.1.2 overdue information — the shared-cost
 *     invoices, and the licensee's details. Swich also sees the Swich-direct parks.
 *   - Swich settles a closed month now (inline confirmation) — the licensee's statement or a Swich-direct
 *     park's row — retries a failed park transfer or records that it was paid outside the platform (§2.5:
 *     the licensee pays when a park's account cannot), and edits the licensee (barrier, share, and the launch
 *     date — only before it passes, with a reason).
 *   - Each side enters, deletes (with a reason; the row stays struck through) and corrects (once included) only the
 *     cost invoices it paid, and can hold one of the other side's it disputes. An entry counts 48 hours after it was
 *     entered; held entries are shown at the top to both sides. The change history lists every change, newest first.
 *   - Park managers see only their own park: its monthly rows and invoices — never the licensee's costs,
 *     and never the §4.1.2 penalty (the licensee's to Swich): only that a transfer was late.
 * A settlement alert links here with ?month=YYYY-MM: a month is settled from the run day of the following
 * one, so the failed transfer is in a past month, not in the running month's preview.
 */
@Component({
  selector: 'app-admin-settlements',
  imports: [NgTemplateOutlet, AgreementHistoryComponent],
  templateUrl: './settlements.html',
  styleUrl: './settlements.scss',
})
export class SettlementsComponent {
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private auth = inject(AdminAuthService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  svc = inject(AdminMockService); // formatters
  private i18n = inject(AdminI18nService);

  get t() { return this.i18n.t; }
  private get tt() { return this.i18n.t as Record<string, string>; }

  readonly categories = CATEGORIES;
  readonly role = computed(() => this.auth.admin()?.role ?? '');
  readonly isSuperAdmin = computed(() => this.role() === 'super_admin');
  readonly isOperator = computed(() => this.role() === 'operator');
  readonly seesAllParks = computed(() => this.isSuperAdmin() || this.isOperator());
  readonly currentParkName = computed(() => this.parkCtx.currentPark()?.name ?? '');

  // ── Month view (Swich + operator) ────────────────────────────────
  readonly month = signal(monthKey(new Date()));
  readonly view = signal<ApiSettlementMonthView | null>(null);
  readonly licensee = signal<ApiLicensee | null>(null);
  readonly viewLoading = signal(false);
  readonly viewError = signal<string | null>(null);
  readonly statement = computed<ApiStatement | null>(() => this.view()?.statements[0] ?? null);
  /** Only the id: a refreshed licensee object must not reload the change history. */
  readonly licenseeId = computed(() => this.licensee()?.id ?? null);

  /** Months to pick from: the launch month (or a year back) up to the current month, newest first. */
  readonly monthOptions = computed(() => {
    const current = monthKey(new Date());
    const launch = this.licensee()?.launchDate?.slice(0, 7);
    let first = launch && launch < current ? launch : shiftMonth(current, -11);
    if (this.month() < first) first = this.month();
    const out: string[] = [];
    for (let m = current; m >= first && out.length < 60; m = shiftMonth(m, -1)) out.push(m);
    return out;
  });
  readonly canGoNext = computed(() => this.month() < monthKey(new Date()));

  // ── Park scope (everyone) ────────────────────────────────────────
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly settlements = signal<ApiSettlement[]>([]);
  readonly scopeAll = signal(false);
  readonly months = signal<ApiSettlementMonth[]>([]);
  readonly openingMonth = signal<string | null>(null);
  readonly monthError = signal<string | null>(null);
  readonly expanded = signal<string | null>(null);
  readonly expandedCashouts = signal<ApiSettlementCashoutsResponse | null>(null);
  readonly expandedLoading = signal(false);

  // ── Actions ──────────────────────────────────────────────────────
  readonly busy = signal<string | null>(null);
  readonly message = signal<{ text: string; error: boolean } | null>(null);
  readonly confirmRun = signal<string | null>(null);          // licensee id awaiting confirmation
  readonly confirmDirect = signal<string | null>(null);       // Swich-direct park id awaiting confirmation
  readonly extForm = signal<ExternalForm | null>(null);       // the open "paid outside the platform" form
  readonly extError = signal<string | null>(null);
  readonly costAction = signal<CostAction | null>(null);
  readonly costActionError = signal<string | null>(null);
  readonly showCostForm = signal(false);
  readonly costForm = signal<CostForm>(this.emptyCostForm());
  readonly costError = signal<string | null>(null);
  readonly licEditing = signal(false);
  readonly licForm = signal<LicenseeForm>({ name: '', taxId: '', email: '', iban: '', launchDate: '', barrierGel: '', swichSharePercent: '', reason: '' });
  readonly licError = signal<string | null>(null);

  /** Bumped after a change made here, so the change history (a child card) shows the new entry. */
  readonly historyKey = signal(0);

  constructor() {
    this.parkCtx.ensureLoaded();
    effect(() => {
      const parkId = this.parkCtx.currentParkId();
      const all = this.scopeAll();
      if (parkId) this.fetchPark(parkId, all);
    });
    effect(() => {
      const m = this.month();
      if (this.seesAllParks()) this.fetchMonth(m);
    });

    // Alert banner deep link: /admin/settlements?month=2026-09 (the banner has already pointed the topbar at the
    // alert's park, so the transfer history below shows it too). Followed from this page as well — the banner
    // sits on every page and Angular reuses the component — so listen, don't snapshot. A future month is ignored.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(q => {
      const m = q.get('month');
      if (m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) && m <= monthKey(new Date())) this.setMonth(m);
    });
  }

  // ── Loading ──────────────────────────────────────────────────────

  private async fetchPark(parkId: string, all: boolean) {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [list, months] = await Promise.all([
        this.api.listSettlements(all ? null : parkId, 90),
        this.api.listSettlementMonths(parkId),
      ]);
      this.settlements.set(list.settlements);
      this.months.set(months.months);
    } catch (err: any) {
      this.loadError.set(err?.error?.message ?? this.t.errLoadSettlements);
    } finally {
      this.loading.set(false);
    }
  }

  private async fetchMonth(month: string) {
    this.viewLoading.set(true);
    this.viewError.set(null);
    try {
      const [view, lic] = await Promise.all([this.api.getSettlementMonth(month), this.api.listLicensees()]);
      if (this.month() !== month) return;   // the picker moved on while this was loading
      this.view.set(view);
      this.licensee.set(lic.licensees[0] ?? null);
    } catch (err: any) {
      this.viewError.set(err?.error?.message ?? this.t.errLoadSettlements);
    } finally {
      this.viewLoading.set(false);
    }
  }

  async refresh() {
    const parkId = this.parkCtx.currentParkId();
    this.historyKey.update(k => k + 1);
    await Promise.all([
      parkId ? this.fetchPark(parkId, this.scopeAll()) : Promise.resolve(),
      this.seesAllParks() ? this.fetchMonth(this.month()) : Promise.resolve(),
    ]);
  }

  /** After a change to the agreement's records: the month's figures and the change history both moved. */
  private async reloadRecords() {
    this.historyKey.update(k => k + 1);
    await this.fetchMonth(this.month());
  }

  private setMonth(m: string) {
    if (!m || m === this.month()) return;
    this.confirmRun.set(null);
    this.confirmDirect.set(null);
    this.cancelExternal();
    this.cancelCostAction();
    this.message.set(null);
    this.month.set(m);
  }
  /**
   * The picker: the URL follows the month, so the address can be shared and the alert banner's link — which may
   * name the month the page was opened with — still moves the page when followed again after browsing elsewhere.
   */
  pickMonth(m: string) {
    if (!m || m === this.month()) return;
    this.setMonth(m);
    void this.router.navigate([], { relativeTo: this.route, queryParams: { month: m }, replaceUrl: true });
  }
  prevMonth() { this.pickMonth(shiftMonth(this.month(), -1)); }
  nextMonth() { if (this.canGoNext()) this.pickMonth(shiftMonth(this.month(), 1)); }

  // ── Statement: derived text ──────────────────────────────────────

  phaseLabel(p: string): string {
    return p === 'Barrier' ? this.t.stlPhaseBarrier : p === 'Crossing' ? this.t.stlPhaseCrossing : this.t.stlPhaseNormal;
  }

  /** The note under the statement header: preview of the running month, a not-yet-settled month, or why it cannot run. */
  previewNote(s: ApiStatement): string | null {
    if (!s.isPreview) return null;
    const day = String(this.view()?.runDayOfMonth ?? 3);
    if (s.blockedReason && s.blockedReason !== 'month_not_closed')
      return this.tt['stlBlocked_' + s.blockedReason] ?? s.blockedReason;
    return (s.isClosed ? this.t.stlPreviewClosed : this.t.stlPreviewRunning).replace('{day}', day);
  }

  canSettle(s: ApiStatement): boolean {
    return this.isSuperAdmin() && s.isPreview && !s.blockedReason;
  }

  /**
   * The phase formula written out with the statement's own numbers — the same lines the licensee's
   * PDF report prints, so both sides read one calculation.
   */
  formulaLines(s: ApiStatement): FormulaLine[] {
    const n = (v: number) => this.num(v);
    // CL or CS is negative in a month whose only entry of that side is a correction down: bracketed, as in the report.
    const o = (v: number) => (v < 0 ? `(${this.num(v)})` : this.num(v));
    const g = (v: number) => this.formatGel(v);
    const barrierExpr = `F − CL = ${n(s.feeIncome)} − ${o(s.licenseeCosts)} = ${g(s.barrierEntitlement)}`;
    const normalExpr = s.swichSharePercent === 50
      ? `(F − CL + CS) ÷ 2 = (${n(s.feeIncome)} − ${o(s.licenseeCosts)} + ${o(s.swichCosts)}) ÷ 2 = ${g(s.normalEntitlement)}`
      : `s × (F − CL − CS) + CS = ${s.swichSharePercent}% × (${n(s.feeIncome)} − ${o(s.licenseeCosts)} − ${o(s.swichCosts)}) + ${o(s.swichCosts)} = ${g(s.normalEntitlement)}`;
    const lines: FormulaLine[] = [];
    if (s.phase === 'Barrier') {
      lines.push({ label: this.t.stlRuleBarrier, expr: barrierExpr });
    } else if (s.phase === 'Normal') {
      // R can sit under B after a loss month — the barrier, once filled, stays filled (§2.3); the report says the same.
      if (s.barrierFilledBefore && s.barrierBefore < s.barrierGel)
        lines.push({ label: this.t.stlBarrierStaysFilled, expr: `R = ${n(s.barrierBefore)} < B = ${n(s.barrierGel)}` });
      lines.push({ label: this.t.stlRuleNormal, expr: normalExpr });
    } else {
      const room = s.barrierGel - s.barrierBefore;
      const phi = this.phi(s.barrierFraction);
      lines.push({ label: this.t.stlBarrierLeft, expr: `${n(s.barrierGel)} − ${n(s.barrierBefore)} = ${g(room)}` });
      lines.push({ label: this.t.stlRuleBarrier, expr: barrierExpr });
      lines.push({ label: this.t.stlRuleNormal, expr: normalExpr });
      lines.push({ label: this.t.stlBarrierShare, expr: `φ = ${n(room)} ÷ ${n(s.barrierEntitlement)} = ${phi}` });
      lines.push({ label: this.t.stlEntitlement, expr: `${n(room)} + (1 − ${phi}) × ${n(s.normalEntitlement)} = ${g(s.entitlement)}` });
    }
    lines.push(s.carryIn !== 0
      ? { label: this.t.stlDueFormula, expr: `${n(s.entitlement)} + (${n(s.carryIn)}) = ${g(s.entitlement + s.carryIn)}`, strong: true }
      : { label: this.t.stlDueFormula, expr: g(s.entitlement), strong: true });
    return lines;
  }

  carryNote(s: ApiStatement): string | null {
    if (s.carryOut < 0) return this.t.stlCreditNote.replace('{amount}', this.formatGel(-s.carryOut));
    if (s.carryOut > 0) return this.t.stlOwedNote.replace('{amount}', this.formatGel(s.carryOut));
    return null;
  }

  /**
   * How much of the barrier is filled — past it the entitlement keeps growing, the barrier is simply full. Once
   * reached it stays full (§2.3), even when a later loss month has pulled the running total back under it.
   */
  barrierFilled(s: ApiStatement): number {
    return s.barrierFilled || s.barrierFilledBefore ? s.barrierGel : Math.min(Math.max(s.barrierAfter, 0), s.barrierGel);
  }

  /** Barrier bar: [entitled before this month | this month] of the barrier. */
  barrierBefore(s: ApiStatement): number {
    return s.barrierFilledBefore ? 100 : this.pct(Math.max(0, s.barrierBefore), s.barrierGel);
  }
  barrierThisMonth(s: ApiStatement): number {
    if (s.barrierFilledBefore) return 0;
    const after = s.barrierFilled ? 100 : this.pct(Math.max(0, s.barrierAfter), s.barrierGel);
    return Math.max(0, after - this.barrierBefore(s));
  }

  /**
   * "N days overdue". The §4.1.2 penalty figure comes only for a licensee park's transfer and only to Swich and
   * the operator — it is the licensee's to Swich; a park manager and a Swich-direct park get the days alone.
   */
  overdueText(o: ApiOverdue | null | undefined): string | null {
    if (!o) return null;
    if (o.accrued == null)
      return (o.paid ? this.t.stlOverduePaidDays : this.t.stlOverdueDays).replace('{days}', String(o.daysOverdue));
    return (o.paid ? this.t.stlOverduePaid : this.t.stlOverdue)
      .replace('{days}', String(o.daysOverdue))
      .replace('{amount}', this.formatGel(o.accrued));
  }

  /** The overdue days that were not the park's side (a technical fault, or nothing attempted yet): no penalty (§4.1.2). */
  overdueTechnicalText(o: ApiOverdue | null | undefined): string | null {
    if (!o || o.accrued == null || !o.technicalDays) return null;
    return this.t.stlOverdueTechnical.replace('{days}', String(o.technicalDays));
  }

  parkStatusKey(p: ApiStatementPark): string { return (p.status ?? 'preview').toLowerCase(); }
  parkStatusLabel(p: ApiStatementPark, s: ApiStatement): string {
    if (!p.status) return s.isPreview ? this.t.stlNotSettledYet : '—';
    if (p.status === 'Completed' && !p.bankTransferId) return this.t.stlNoTransferNeeded;
    return this.statusLabel(p.status);
  }

  /**
   * Annex 3 §7: a transfer the park's revoked authorisation stopped. It is never collected from the park's account —
   * no Retry; it is closed by recording the payment made outside the platform (the licensee's under §2.5 for a park of its
   * statement, the park's own for a Swich-direct one). A park manager is told only that it was not collected from them.
   */
  isRevoked(failureCode: string | null | undefined): boolean { return failureCode === 'AUTHORISATION_REVOKED'; }

  revokedNote(failureCode: string | null | undefined, viaLicensee: boolean): string | null {
    if (!this.isRevoked(failureCode)) return null;
    if (!this.seesAllParks()) return this.t.stlRevokedNoteManager;
    return viaLicensee ? this.t.stlRevokedNote : this.t.stlRevokedNoteDirect;
  }

  /** "Paid by the licensee on 20 Dec 2020" — the reference is added for Swich and the operator (park managers get none). */
  externalText(e: ApiSettlementExternal | null | undefined): string | null {
    if (!e) return null;
    const who = e.payer === 'licensee' ? this.t.stlExtPaidByLicensee : this.t.stlExtPaidByPark;
    return who.replace('{date}', e.paidOn ? this.formatDate(e.paidOn) : '—');
  }

  /** The statement's "transferred · outstanding" line, with what was paid outside the platform when there is any. */
  transferredText(s: ApiStatement): string {
    const base = this.t.stlTransferredOutstanding
      .replace('{done}', this.formatGel(s.transferred)).replace('{left}', this.formatGel(s.outstanding));
    return s.paidExternally ? `${base} · ${this.t.stlPaidOutsideShort.replace('{amount}', this.formatGel(s.paidExternally))}` : base;
  }

  // ── Statement actions ────────────────────────────────────────────

  askSettle(s: ApiStatement) {
    if (this.busy()) return;
    this.confirmRun.set(s.licenseeId);
  }
  cancelSettle() { this.confirmRun.set(null); }

  async settle(s: ApiStatement) {
    if (this.busy()) return;
    this.confirmRun.set(null);
    this.busy.set('run');
    this.message.set(null);
    try {
      const r = await this.api.runSettlementMonth(s.month, { licenseeId: s.licenseeId });
      const st = r.statements[0];
      if (st) {
        const done = st.parks.filter(p => p.status === 'Completed').length;
        const failed = st.parks.filter(p => p.status === 'Failed').length;
        // Never "settled" while transfers are outstanding: the failed ones are named and retried every night.
        const tpl = failed > 0 ? this.t.stlSettledWithFailures : this.t.stlSettledResult;
        this.message.set({
          text: tpl.replace('{ref}', st.invoiceRef).replace('{done}', String(done))
            .replace('{total}', String(st.parks.length)).replace('{failed}', String(failed)),
          error: failed > 0,
        });
      }
    } catch (err: any) {
      const code = err?.error?.error as string | undefined;
      this.message.set({ text: (code && this.tt['stlBlocked_' + code]) || err?.error?.message || this.t.errRunFailed, error: true });
    } finally {
      this.busy.set(null);
      await this.refresh();
    }
  }

  // ── Swich-direct parks ───────────────────────────────────────────

  /** A closed month the worker has not settled for this park yet (or skipped): Swich can settle it now. */
  canSettleDirect(d: ApiDirectSettlement): boolean {
    return this.isSuperAdmin() && d.isPreview && !d.blockedReason && d.cashoutCount > 0;
  }
  directBlockedText(d: ApiDirectSettlement): string | null {
    return d.blockedReason ? (this.tt['stlBlocked_' + d.blockedReason] ?? d.blockedReason) : null;
  }
  askSettleDirect(d: ApiDirectSettlement) { if (!this.busy()) this.confirmDirect.set(d.parkId); }
  cancelSettleDirect() { this.confirmDirect.set(null); }

  async settleDirect(d: ApiDirectSettlement) {
    if (this.busy()) return;
    this.confirmDirect.set(null);
    this.busy.set('direct:' + d.parkId);
    this.message.set(null);
    try {
      const r = await this.api.runSettlementMonth(d.month, { parkId: d.parkId });
      const row = r.direct[0];
      if (row) {
        const status = this.statusLabel(row.status) + (row.failureReason ? ` — ${row.failureReason}` : '');
        this.message.set({
          text: this.t.stlDirectRunResult.replace('{park}', d.parkName).replace('{ref}', row.invoiceRef).replace('{status}', status),
          error: row.status === 'Failed',
        });
      }
    } catch (err: any) {
      const code = err?.error?.error as string | undefined;
      this.message.set({ text: (code && this.tt['stlBlocked_' + code]) || err?.error?.message || this.t.errRunFailed, error: true });
    } finally {
      this.busy.set(null);
      await this.refresh();
    }
  }

  // ── Paid outside the platform (Swich) ─────────────────────────────

  /**
   * Open the form for a FAILED transfer: §2.5 — when a park's account cannot cover its part, the licensee pays
   * Swich itself (or the park pays by hand). Recorded, the row is closed for good: never retried, so the park's
   * account is never debited for it once topped up. A Swich-direct park has no licensee — only the park can pay.
   */
  openExternal(origin: ExternalOrigin, settlementId: string, share: number, viaLicensee: boolean) {
    if (this.busy()) return;
    const payers: Array<'licensee' | 'park'> = viaLicensee ? ['licensee', 'park'] : ['park'];
    this.extForm.set({
      settlementId, origin, share, payers, payer: payers[0],
      amount: share.toFixed(2), paidOn: todayIso(), reference: '', note: '',
    });
    this.extError.set(null);
  }
  cancelExternal() { this.extForm.set(null); this.extError.set(null); }
  extOpenAt(origin: ExternalOrigin, settlementId: string | null): boolean {
    const f = this.extForm();
    return !!settlementId && f?.origin === origin && f.settlementId === settlementId;
  }
  setExtField<K extends 'payer' | 'amount' | 'paidOn' | 'reference' | 'note'>(k: K, v: ExternalForm[K]) {
    this.extForm.update(f => (f ? { ...f, [k]: v } : f));
  }
  extPayerLabel(p: string): string { return p === 'licensee' ? this.t.stlExtPayerLicensee : this.t.stlExtPayerPark; }

  async saveExternal() {
    const f = this.extForm();
    if (!f || this.busy()) return;
    // The API refuses anything but the full amount; checking it here too keeps a typo from reaching it.
    const amount = parseNumber(f.amount);
    if (amount === null || Math.round(amount * 100) !== Math.round(f.share * 100)) {
      this.extError.set(this.t.stlExtErr_amount_mismatch.replace('{amount}', this.formatGel(f.share)));
      return;
    }
    if (!f.reference.trim()) { this.extError.set(this.t.stlExtErr_reference_required); return; }
    if (!f.paidOn || f.paidOn > todayIso()) { this.extError.set(this.t.stlExtErr_invalid_paid_on); return; }

    this.busy.set('ext:' + f.settlementId);
    this.extError.set(null);
    try {
      const r = await this.api.settleSettlementExternally(f.settlementId, {
        payer: f.payer, amount, paidOn: f.paidOn, reference: f.reference.trim(), note: f.note.trim() || undefined,
      });
      this.extForm.set(null);
      this.message.set({ text: this.t.stlExtRecorded.replace('{ref}', r.invoiceRef ?? '').replace('{amount}', this.formatGel(r.swichShare)), error: false });
      await this.refresh();
    } catch (err: any) {
      const body = err?.error;
      const tpl = this.tt['stlExtErr_' + (body?.error ?? '')];
      this.extError.set(tpl ? tpl.replace('{amount}', this.formatGel(f.share)) : (body?.message ?? this.t.stlExtErrGeneric));
    } finally {
      this.busy.set(null);
    }
  }

  async retryPark(p: ApiStatementPark) {
    if (!p.settlementId || this.busy()) return;
    await this.retryById(p.settlementId);
  }

  async retry(s: ApiSettlement, e: Event) {
    e.stopPropagation();
    if (this.busy()) return;
    await this.retryById(s.id);
  }

  async retryById(id: string) {
    this.busy.set(id);
    this.message.set(null);
    try {
      const r = await this.api.retrySettlement(id);
      this.message.set({ text: `${this.t.settlementRetried} ${this.statusLabel(r.status)}${r.bankTransferId ? ' · ' + r.bankTransferId : ''}`, error: false });
    } catch (err: any) {
      const body = err?.error;
      this.message.set({
        text: body?.status ? `${this.t.settlementRetried} ${this.statusLabel(body.status)} — ${body.failureReason ?? ''}`
          // A tab opened before the park's revocation took effect (Annex 3 §7): this share is never collected from its account.
          : body?.error === 'authorisation_revoked' ? this.t.stlRevokedNote
          : (body?.message ?? this.t.stlRetryFailed),
        error: true,
      });
    } finally {
      this.busy.set(null);
      await this.refresh();
    }
  }

  async openReport(s: ApiStatement) {
    if (!s.statementId || this.busy()) return;
    this.busy.set('report');
    try { await this.api.openStatementReport(s.statementId); }
    catch { this.message.set({ text: this.t.errOpenInvoice, error: true }); }
    finally { this.busy.set(null); }
  }

  async openParkInvoice(parkId: string, month: string) {
    if (this.openingMonth()) return;
    this.openingMonth.set(`${parkId}:${month}`);
    this.monthError.set(null);
    try { await this.api.openMonthlyInvoice(parkId, month); }
    catch (err: any) { this.message.set({ text: err?.status === 404 ? this.t.errNoMonthlyInvoice : this.t.errOpenInvoice, error: true }); }
    finally { this.openingMonth.set(null); }
  }

  // ── Costs ────────────────────────────────────────────────────────

  private emptyCostForm(): CostForm {
    return { category: 'Sms', vendor: '', invoiceNumber: '', invoiceDate: todayIso(), amount: '', note: '' };
  }

  readonly today = todayIso();
  categoryLabel(c: string): string { return this.tt['stlCat_' + c] ?? c; }
  setCostField<K extends keyof CostForm>(k: K, v: CostForm[K]) { this.costForm.update(f => ({ ...f, [k]: v })); }

  toggleCostForm() {
    this.showCostForm.update(v => !v);
    this.costError.set(null);
    if (this.showCostForm()) this.costForm.set(this.emptyCostForm());
  }

  async addCost() {
    const lic = this.licensee();
    if (!lic || this.busy()) return;
    const f = this.costForm();
    const amount = Number(f.amount.replace(',', '.'));
    this.busy.set('cost');
    this.costError.set(null);
    try {
      await this.api.createCost(lic.id, {
        category: f.category, vendor: f.vendor.trim(), invoiceNumber: f.invoiceNumber.trim(),
        invoiceDate: f.invoiceDate, amount: Number.isFinite(amount) ? amount : 0, note: f.note.trim() || undefined,
      });
      this.showCostForm.set(false);
      await this.reloadRecords();
    } catch (err: any) {
      this.costError.set(this.tt['stlCostErr_' + (err?.error?.error ?? '')] ?? this.t.stlCostErrGeneric);
    } finally {
      this.busy.set(null);
    }
  }

  /** "It counts 48 hours after you enter it" — the window the other side has to check (and hold) it. */
  cutoffHint(): string {
    return this.t.stlCostCutoffHint.replace('{hours}', String(this.view()?.costCutoffHours ?? 48));
  }

  costsOf(s: ApiStatement, side: 'Swich' | 'Licensee'): ApiCost[] { return s.costs.filter(c => c.paidBy === side); }

  /** Under a settled month: entered after it, they go into the next statement. Under a preview: not counted (yet). */
  lateCostsTitle(s: ApiStatement): string { return s.isPreview ? this.t.stlNotCountedYet : this.t.stlLateCosts; }

  sideLabel(c: { paidBy: string }): string { return c.paidBy === 'Swich' ? this.t.stlPaidBySwich : this.t.stlPaidByLicensee; }

  /** "admin:levan@operator.local" → "levan@operator.local"; the seed and the system as they are. */
  actorLabel(actor: string | null | undefined): string {
    if (!actor) return '—';
    if (actor === 'seed') return this.t.stlHistActorSeed;
    return actor.startsWith('admin:') ? actor.slice(6) : actor;
  }

  /** Where an entry stands, in one line (the lock, the hold and the deletion have their own marks). */
  costStateText(c: ApiCost): string | null {
    switch (c.state) {
      case 'included': return c.includedIn ? this.t.stlIncludedIn.replace('{ref}', c.includedIn) : null;
      case 'waiting': {
        const hours = String(this.view()?.costCutoffHours ?? 48);
        return c.missesNextRun
          ? this.t.stlCostMissesRun.replace('{hours}', hours).replace('{date}', this.formatDateTime(this.view()?.nextStatementRunAt))
          : this.t.stlCostCountsFrom.replace('{date}', this.formatDateTime(c.eligibleFrom));
      }
      case 'pending': return this.t.stlPendingInclusion;
      default: return null;
    }
  }

  correctionStateText(x: { state: string; includedIn: string | null }): string {
    if (x.state === 'included' && x.includedIn) return this.t.stlIncludedIn.replace('{ref}', x.includedIn);
    if (x.state === 'deleted') return this.t.stlCostDeletedShort;
    if (x.state === 'held') return this.t.stlCostHeldShort;
    return this.t.stlPendingInclusion;
  }

  /** "Correction of № K-1 (PT-2026-09)" on a correction's own row. */
  correctionOfText(c: ApiCost): string {
    return this.t.stlCorrectionOf.replace('{number}', c.invoiceNumber).replace('{ref}', c.corrects?.includedIn ?? '—');
  }

  signedGel(n: number): string { return n > 0 ? `+${this.formatGel(n)}` : this.formatGel(n); }

  // ── Cost actions: delete / hold / release / correct (inline, one at a time) ──

  openCostAction(c: ApiCost, kind: CostActionKind) {
    if (this.busy()) return;
    this.costAction.set({ costId: c.id, kind, reason: '', amount: '' });
    this.costActionError.set(null);
  }
  cancelCostAction() { this.costAction.set(null); this.costActionError.set(null); }
  actionOpenOn(c: ApiCost, where: 'held' | 'list'): boolean {
    // A held entry shows in the hold box and in its month's list: its action form opens only in the hold box.
    const a = this.costAction();
    return !!a && a.costId === c.id && (where === 'held') === (c.state === 'held');
  }
  setCostActionField(k: 'reason' | 'amount', v: string) { this.costAction.update(a => (a ? { ...a, [k]: v } : a)); }

  costActionTitle(kind: CostActionKind): string {
    return kind === 'delete' ? this.t.stlCostDeleteTitle
      : kind === 'hold' ? this.t.stlCostHoldTitle
      : kind === 'release' ? this.t.stlCostReleaseTitle
      : this.t.stlCorrectTitle;
  }
  costActionSubmit(kind: CostActionKind): string {
    return kind === 'delete' ? this.t.stlCostDelete
      : kind === 'hold' ? this.t.stlCostHold
      : kind === 'release' ? this.t.stlCostRelease
      : this.t.stlCorrectSave;
  }

  async submitCostAction(c: ApiCost) {
    const a = this.costAction();
    if (!a || a.costId !== c.id || this.busy()) return;
    const reason = a.reason.trim();
    // A deletion, a hold and a correction are never made without saying why — the other side has to be able to follow it.
    if (a.kind !== 'release' && !reason) { this.costActionError.set(this.t.stlCostErr_reason_required); return; }
    let amount = 0;
    if (a.kind === 'correct') {
      const n = parseNumber(a.amount);
      if (n === null || n === 0) { this.costActionError.set(this.t.stlCostErr_invalid_amount_signed); return; }
      // Tetri-exact, checked on what was typed (0.29 × 100 is not exactly 29 in floating point).
      if (/[.,]\d{3,}\s*$/.test(a.amount)) { this.costActionError.set(this.t.stlCostErr_amount_precision); return; }
      amount = n;
    }

    this.busy.set(c.id);
    this.costActionError.set(null);
    try {
      if (a.kind === 'delete') await this.api.deleteCost(c.licenseeId, c.id, reason);
      else if (a.kind === 'hold') await this.api.holdCost(c.licenseeId, c.id, reason);
      else if (a.kind === 'release') await this.api.releaseCost(c.licenseeId, c.id, reason || undefined);
      else await this.api.addCostCorrection(c.licenseeId, c.id, { amount, reason });
      this.costAction.set(null);
      await this.reloadRecords();
    } catch (err: any) {
      this.costActionError.set(this.tt['stlCostErr_' + (err?.error?.error ?? '')] ?? this.t.stlCostErrGeneric);
    } finally {
      this.busy.set(null);
    }
  }

  // ── Licensee ─────────────────────────────────────────────────────

  startLicEdit() {
    const l = this.licensee();
    if (!l) return;
    this.licForm.set({
      name: l.name, taxId: l.taxId ?? '', email: l.email ?? '', iban: l.iban ?? '',
      launchDate: l.launchDate ?? '', barrierGel: String(l.barrierGel), swichSharePercent: String(l.swichSharePercent), reason: '',
    });
    this.licError.set(null);
    this.licEditing.set(true);
  }
  cancelLicEdit() { this.licEditing.set(false); this.licError.set(null); }
  setLicField<K extends keyof LicenseeForm>(k: K, v: string) { this.licForm.update(f => ({ ...f, [k]: v })); }

  /** No licensee yet (a fresh production): Swich registers it with the agreement's defaults. */
  startLicCreate() {
    this.licForm.set({ name: '', taxId: '', email: '', iban: '', launchDate: '', barrierGel: '20000', swichSharePercent: '50', reason: '' });
    this.licError.set(null);
    this.licEditing.set(true);
  }

  /** The form's launch date differs from the one on record (a new licensee: any date entered). */
  readonly launchChanged = computed(() => this.licForm().launchDate !== (this.licensee()?.launchDate ?? ''));
  /** Shown when the launch date or a money term changes: the reason goes on the record (required for the launch date). */
  readonly reasonShown = computed(() => {
    const f = this.licForm();
    const l = this.licensee();
    if (!l) return !!f.launchDate;
    return this.launchChanged() || parseNumber(f.barrierGel) !== l.barrierGel || parseNumber(f.swichSharePercent) !== l.swichSharePercent;
  });

  /** "Locked — 1 Oct 2026 has passed" / "Can move until it passes" under the launch date. */
  launchLockText(l: ApiLicensee): string | null {
    if (!l.launchDate) return null;
    return l.launchDateLocked ? this.t.stlLicLaunchLocked : this.t.stlLicLaunchMovable;
  }

  launchMoveText(h: { from: string | null; to: string | null }): string {
    const d = (v: string | null) => (v ? this.formatDate(v) : this.t.stlLicNotSet);
    return `${d(h.from)} → ${d(h.to)}`;
  }

  async saveLicensee() {
    const l = this.licensee();
    if (this.busy()) return;
    const f = this.licForm();

    // The barrier and the share are money terms: a cleared field must be refused, never saved as 0 — a 0 barrier
    // would settle every later month 50/50 from the start (§2.3), a 0 % share would leave Swich only its costs.
    const barrier = parseNumber(f.barrierGel);
    const share = parseNumber(f.swichSharePercent);
    if (barrier === null || barrier < 0) { this.licError.set(this.t.stlLicErr_invalid_barrier); return; }
    if (share === null || share < 0 || share > 100) { this.licError.set(this.t.stlLicErr_invalid_share); return; }

    // The launch date starts the trial and the barrier: it moves only to a day still ahead, and never without a reason
    // (the API refuses both too — and a date that has passed, which the form does not offer).
    const reason = f.reason.trim();
    if (this.launchChanged() && f.launchDate && f.launchDate < this.today) { this.licError.set(this.t.stlLicErr_launch_date_in_past); return; }
    if (this.launchChanged() && !reason) { this.licError.set(this.t.stlLicErr_launch_reason_required); return; }

    const full = {
      name: f.name.trim(), taxId: f.taxId.trim(), email: f.email.trim(), iban: f.iban.trim(),
      launchDate: f.launchDate, barrierGel: barrier, swichSharePercent: share, reason: reason || undefined,
    };
    // Editing sends only what changed: correcting the e-mail never re-sends — and so never rewrites — the terms.
    let body: UpdateLicenseeBody = full;
    if (l) {
      const changed: UpdateLicenseeBody = {};
      if (full.name !== l.name) changed.name = full.name;
      if (full.taxId !== (l.taxId ?? '')) changed.taxId = full.taxId;
      if (full.email !== (l.email ?? '')) changed.email = full.email;
      if (full.iban !== (l.iban ?? '')) changed.iban = full.iban;
      if (full.launchDate !== (l.launchDate ?? '')) changed.launchDate = full.launchDate;
      if (barrier !== l.barrierGel) changed.barrierGel = barrier;
      if (share !== l.swichSharePercent) changed.swichSharePercent = share;
      if (Object.keys(changed).length === 0) { this.cancelLicEdit(); return; }
      if (reason) changed.reason = reason;
      body = changed;
    }

    this.busy.set('lic');
    this.licError.set(null);
    try {
      const saved = l ? await this.api.updateLicensee(l.id, body) : await this.api.createLicensee(full);
      this.licensee.set(saved);
      this.licEditing.set(false);
      await this.reloadRecords();
    } catch (err: any) {
      const body = err?.error;
      const tpl = this.tt['stlLicErr_' + (body?.error ?? '')] ?? this.t.stlLicErrGeneric;
      this.licError.set(tpl.replace('{ref}', body?.firstStatement ?? ''));
    } finally {
      this.busy.set(null);
    }
  }

  // ── History (park scope) ─────────────────────────────────────────

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

  setScopeAll(all: boolean) { this.scopeAll.set(all); }

  /** Open the park's monthly invoice PDF (bearer fetch → blob URL in a new tab). */
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

  // ── Formatting ───────────────────────────────────────────────────

  private get locale(): string { return this.i18n.lang() === 'ka' ? 'ka-GE' : 'en-GB'; }

  /** "October 2026" / "ოქტომბერი 2026" — the invoice ref stays as the machine label next to it. */
  formatMonthLong(month: string): string {
    const d = new Date(month + '-01T00:00:00');
    return d.toLocaleDateString(this.locale, { month: 'long', year: 'numeric' });
  }
  formatMonth(month: string): string {
    const d = new Date(month + '-01T00:00:00');
    return d.toLocaleDateString(this.locale, { month: 'short', year: 'numeric' });
  }
  formatGel(n: number, d = 2): string {
    return n < 0 ? `−${this.svc.formatGel(-n, d)}` : this.svc.formatGel(n, d);
  }
  /** Plain number for formulas: "1,234.50", negatives with a real minus sign. */
  num(n: number): string {
    const s = Math.abs(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    return n < 0 ? `−${s}` : s;
  }
  private phi(v: number): string { return String(Number(v.toFixed(6))); }
  pct(part: number, whole: number): number { return whole > 0 ? Math.min(100, Math.max(0, (part / whole) * 100)) : 100; }

  /** The reference a history row shows: the bank's transfer id, an outside payment's reference, or the invoice ref. */
  rowRef(st: ApiSettlement): string { return st.bankTransferId ?? st.external?.reference ?? st.invoiceRef ?? ''; }

  statusKey(s: string): string { return s.toLowerCase(); }
  statusLabel(s: string): string { return this.tt[s.toLowerCase()] ?? s; }

  formatDate(iso: string): string {
    const d = new Date(iso.length === 10 ? iso + 'T00:00:00' : iso);
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  }
  rowDate(s: ApiSettlement): string {
    return s.isMonthly && s.month ? this.formatMonth(s.month) : this.formatDate(s.settlementDate);
  }
  formatDateTime(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short' }) + ' ' +
      d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }
  /** A record's time with the year — the change history spans the whole agreement. */
  formatStamp(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) + ' ' +
      d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }
}
