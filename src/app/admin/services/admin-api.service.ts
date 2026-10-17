import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

/**
 * Typed HTTP client for the .NET backend admin endpoints.
 *
 * Base URL is the .NET dev server. CORS is configured on the backend for
 * localhost:4200. Auth is deferred to Phase 8 — for now the endpoints are open.
 */
@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/admin`;

  listParks(): Promise<ApiPark[]> {
    return firstValueFrom(this.http.get<ApiPark[]>(`${this.base}/parks`));
  }

  /** Create a new park + optional first manager login (super-admin only). */
  createPark(body: CreateParkBody): Promise<CreateParkResult> {
    return firstValueFrom(this.http.post<CreateParkResult>(`${this.base}/parks`, body));
  }

  /** The park's Yandex roster, flagged with who's already onboarded. */
  getYandexRoster(parkId: string): Promise<YandexRosterResponse> {
    return firstValueFrom(this.http.get<YandexRosterResponse>(
      `${this.base}/parks/${parkId}/yandex-roster`));
  }

  /** Bulk-onboard selected drivers straight from the Yandex roster. */
  bulkOnboard(parkId: string, yandexProfileIds: string[]): Promise<BulkOnboardResult> {
    return firstValueFrom(this.http.post<BulkOnboardResult>(
      `${this.base}/parks/${parkId}/drivers/bulk`, { yandexProfileIds }));
  }

  listDrivers(parkId: string): Promise<ApiDriversResponse> {
    return firstValueFrom(this.http.get<ApiDriversResponse>(`${this.base}/parks/${parkId}/drivers`));
  }

  createCashout(parkId: string, body: CreateCashoutBody): Promise<CashoutSagaResult> {
    return firstValueFrom(this.http.post<CashoutSagaResult>(
      `${this.base}/parks/${parkId}/cashouts`, body));
  }

  listCashouts(parkId: string, take = 20): Promise<ApiCashoutsResponse> {
    return firstValueFrom(this.http.get<ApiCashoutsResponse>(
      `${this.base}/parks/${parkId}/cashouts?take=${take}`));
  }

  getKpis(parkId: string): Promise<ApiKpis> {
    return firstValueFrom(this.http.get<ApiKpis>(`${this.base}/parks/${parkId}/kpis`));
  }

  /**
   * Retry a failed cashout. Server creates a fresh cashout row (new
   * idempotency key) and returns the saga result.
   */
  retryCashout(parkId: string, cashoutId: string): Promise<CashoutSagaResult> {
    return firstValueFrom(this.http.post<CashoutSagaResult>(
      `${this.base}/parks/${parkId}/cashouts/${cashoutId}/retry`, {}));
  }

  /** Look up a Yandex driver profile under a park (onboarding step 1). */
  yandexLookup(parkId: string, profileId: string): Promise<ApiYandexLookup> {
    return firstValueFrom(this.http.get<ApiYandexLookup>(
      `${this.base}/parks/${parkId}/drivers/yandex-lookup?profileId=${encodeURIComponent(profileId)}`));
  }

  /** Create a driver under a park (onboarding step 2). */
  createDriver(parkId: string, body: CreateDriverBody): Promise<ApiNewDriver> {
    return firstValueFrom(this.http.post<ApiNewDriver>(
      `${this.base}/parks/${parkId}/drivers`, body));
  }

  /** Partial update — only the fields you pass are changed. */
  updateDriver(parkId: string, driverId: string, body: UpdateDriverBody): Promise<ApiNewDriver> {
    return firstValueFrom(this.http.patch<ApiNewDriver>(
      `${this.base}/parks/${parkId}/drivers/${driverId}`, body));
  }

  /** Update the park's account/billing details (Settings page). */
  updatePark(parkId: string, body: UpdateParkBody): Promise<ApiPark> {
    return firstValueFrom(this.http.patch<ApiPark>(
      `${this.base}/parks/${parkId}`, body));
  }

  /** Suspend or reactivate a park (Swich and the operator only; a reason is required to suspend). */
  setParkStatus(parkId: string, status: 'Active' | 'Suspended', reason?: string): Promise<ParkStatusResult> {
    return firstValueFrom(this.http.post<ParkStatusResult>(
      `${this.base}/parks/${parkId}/status`, { status, reason }));
  }

  /**
   * Annex 3 §7 — record the park's written revocation of the platform's authorisations (Swich and the operator).
   * `revokedFrom` must be at least 30 days after `noticeOn`, and not already past.
   */
  recordAuthorisationRevocation(parkId: string, body: { noticeOn: string; revokedFrom: string; reason: string }): Promise<ApiRevocationResult> {
    return firstValueFrom(this.http.put<ApiRevocationResult>(`${this.base}/parks/${parkId}/authorisation-revocation`, body));
  }

  /** Withdraw a recorded revocation — only before its day (409 revocation_in_effect after). */
  withdrawAuthorisationRevocation(parkId: string, reason?: string): Promise<ApiRevocationResult> {
    return firstValueFrom(this.http.delete<ApiRevocationResult>(`${this.base}/parks/${parkId}/authorisation-revocation`, { body: { reason } }));
  }

  /** Nudge a Queued cashout so the payout worker retries it immediately. */
  processCashoutNow(parkId: string, cashoutId: string): Promise<CashoutSagaResult> {
    return firstValueFrom(this.http.post<CashoutSagaResult>(
      `${this.base}/parks/${parkId}/cashouts/${cashoutId}/process-now`, {}));
  }

  /**
   * Cancel a payout waiting in the queue: the gross amount goes back on the driver's Yandex balance.
   * Reason required. 409 `already_cancelled` / `not_cancellable` (the bank already accepted it) /
   * `outcome_unknown` (the bank has not answered the last attempt — it may have been paid).
   */
  cancelCashout(parkId: string, cashoutId: string, reason: string): Promise<CashoutSagaResult> {
    return firstValueFrom(this.http.post<CashoutSagaResult>(
      `${this.base}/parks/${parkId}/cashouts/${cashoutId}/cancel`, { reason }));
  }

  // ── Park payout accounts ──────────────────────────────────────────
  listBankAccounts(parkId: string): Promise<ApiBankAccountsResponse> {
    return firstValueFrom(this.http.get<ApiBankAccountsResponse>(
      `${this.base}/parks/${parkId}/bank-accounts`));
  }

  createBankAccount(parkId: string, body: UpsertBankAccountBody): Promise<ApiBankAccount> {
    return firstValueFrom(this.http.post<ApiBankAccount>(
      `${this.base}/parks/${parkId}/bank-accounts`, body));
  }

  updateBankAccount(parkId: string, accountId: string, body: UpsertBankAccountBody): Promise<ApiBankAccount> {
    return firstValueFrom(this.http.patch<ApiBankAccount>(
      `${this.base}/parks/${parkId}/bank-accounts/${accountId}`, body));
  }

  // ── Driver payout destinations (operator-side) ────────────────────
  addDriverCard(parkId: string, driverId: string, body: { iban: string; holderName?: string; makeDefault?: boolean; reason?: string }): Promise<ApiCard> {
    return firstValueFrom(this.http.post<ApiCard>(
      `${this.base}/parks/${parkId}/drivers/${driverId}/cards`, body));
  }

  removeDriverCard(parkId: string, driverId: string, cardId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(
      `${this.base}/parks/${parkId}/drivers/${driverId}/cards/${cardId}`));
  }

  /** Recent activity for the overview feed. */
  getActivity(parkId: string, take = 12): Promise<ApiActivityResponse> {
    return firstValueFrom(this.http.get<ApiActivityResponse>(
      `${this.base}/parks/${parkId}/activity?take=${take}`));
  }

  /** 12 buckets of cashout volume over the past 12 hours. */
  getHourly(parkId: string): Promise<ApiHourlyResponse> {
    return firstValueFrom(this.http.get<ApiHourlyResponse>(
      `${this.base}/parks/${parkId}/hourly`));
  }

  // ── Reconciliation ────────────────────────────────────────────────
  /** Run reconciliation for a park now (Swich + operator). Resolves to the run that was written. */
  runReconciliation(parkId: string): Promise<ApiRecRun> {
    return firstValueFrom(this.http.post<ApiRecRun>(`${this.base}/parks/${parkId}/reconciliation/run`, {}));
  }

  listRecRuns(parkId: string, take = 20): Promise<ApiRecRunsResponse> {
    return firstValueFrom(this.http.get<ApiRecRunsResponse>(
      `${this.base}/parks/${parkId}/reconciliation/runs?take=${take}`));
  }

  listRecDiscrepancies(parkId: string, all = false): Promise<ApiRecDiscrepanciesResponse> {
    return firstValueFrom(this.http.get<ApiRecDiscrepanciesResponse>(
      `${this.base}/parks/${parkId}/reconciliation/discrepancies?all=${all}&take=100`));
  }

  /** Swich, or the operator of the park's licensee: closes the item. `notes` is the reason (required, a few characters at least). */
  resolveDiscrepancy(parkId: string, id: string, notes: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(
      `${this.base}/parks/${parkId}/reconciliation/discrepancies/${id}/resolve`,
      { notes }));
  }

  /** The park's manager: an explanation on an open item. It resolves nothing — Swich or the operator still does. */
  noteDiscrepancy(parkId: string, id: string, note: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(
      `${this.base}/parks/${parkId}/reconciliation/discrepancies/${id}/note`,
      { note }));
  }

  /**
   * Open the per-cashout invoice PDF in a new browser tab. The fetch goes
   * through the auth interceptor (so the bearer header is attached); the
   * resulting blob is opened via blob URL so the browser renders the PDF
   * inline in its built-in viewer — same UX as a plain link, without
   * giving up JWT auth.
   */
  async openInvoice(parkId: string, cashoutId: string): Promise<void> {
    const blob = await firstValueFrom(this.http.get(
      `${this.base}/parks/${parkId}/cashouts/${cashoutId}/invoice.pdf`,
      { responseType: 'blob' }));
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank');
    // Delay revoke so the new tab has time to fetch the blob content. 1 minute
    // is plenty even on slow machines and well before the user closes the tab.
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  }

  // ── Settlements ───────────────────────────────────────────────────
  listSettlements(parkId: string | null, take = 60, status?: string): Promise<ApiSettlementsResponse> {
    const q = new URLSearchParams({ take: String(take) });
    if (parkId) q.set('parkId', parkId);
    if (status) q.set('status', status);
    return firstValueFrom(this.http.get<ApiSettlementsResponse>(`${this.base}/settlements?${q}`));
  }

  listSettlementCashouts(settlementId: string): Promise<ApiSettlementCashoutsResponse> {
    return firstValueFrom(this.http.get<ApiSettlementCashoutsResponse>(
      `${this.base}/settlements/${settlementId}/cashouts`));
  }

  listSettlementMonths(parkId: string): Promise<ApiSettlementMonthsResponse> {
    return firstValueFrom(this.http.get<ApiSettlementMonthsResponse>(
      `${this.base}/parks/${parkId}/settlements/months`));
  }

  /** Open the monthly invoice PT-YYYY-MM (Swich → park) in a new tab; same blob-URL trick as openInvoice. */
  async openMonthlyInvoice(parkId: string, month: string): Promise<void> {
    const blob = await firstValueFrom(this.http.get(
      `${this.base}/parks/${parkId}/settlements/invoice.pdf?month=${encodeURIComponent(month)}`,
      { responseType: 'blob' }));
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank');
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  }

  retrySettlement(settlementId: string): Promise<ApiSettlement> {
    return firstValueFrom(this.http.post<ApiSettlement>(`${this.base}/settlements/${settlementId}/retry`, {}));
  }

  /**
   * Swich only: record that a FAILED park transfer was paid outside the platform (§2.5 — by the licensee when the
   * park's account could not cover it, or by the park by hand). Full amount only; the row is never collected again.
   */
  settleSettlementExternally(settlementId: string, body: SettleExternallyBody): Promise<ApiSettlement> {
    return firstValueFrom(this.http.post<ApiSettlement>(`${this.base}/settlements/${settlementId}/settle-externally`, body));
  }

  /**
   * One month as the console shows it (Swich + operator): each licensee's statement — stored, or a
   * live preview — and, for Swich, the Swich-direct parks. Default month: the current one.
   */
  getSettlementMonth(month?: string): Promise<ApiSettlementMonthView> {
    const q = month ? `?month=${encodeURIComponent(month)}` : '';
    return firstValueFrom(this.http.get<ApiSettlementMonthView>(`${this.base}/settlements/month${q}`));
  }

  /** "Settle now" (Swich only): compute + execute a closed month, for one licensee or direct park, or all. */
  runSettlementMonth(month: string, opts: { licenseeId?: string; parkId?: string } = {}): Promise<ApiSettlementRunResult> {
    const q = new URLSearchParams({ month });
    if (opts.licenseeId) q.set('licenseeId', opts.licenseeId);
    if (opts.parkId) q.set('parkId', opts.parkId);
    return firstValueFrom(this.http.post<ApiSettlementRunResult>(`${this.base}/settlements/run?${q}`, {}));
  }

  /** The licensee's monthly report (§2.6) as PDF in a new tab — same blob-URL trick as the invoices. */
  async openStatementReport(statementId: string): Promise<void> {
    const blob = await firstValueFrom(this.http.get(
      `${this.base}/settlements/statements/${statementId}/report.pdf`, { responseType: 'blob' }));
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank');
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  }

  // ── Licensee + shared costs (licence agreement §2.4) ──────────────
  listLicensees(): Promise<{ count: number; licensees: ApiLicensee[] }> {
    return firstValueFrom(this.http.get<{ count: number; licensees: ApiLicensee[] }>(`${this.base}/licensees`));
  }

  /** Swich only: register the licensee (production starts without one). */
  createLicensee(body: UpdateLicenseeBody & { name: string }): Promise<ApiLicensee> {
    return firstValueFrom(this.http.post<ApiLicensee>(`${this.base}/licensees`, body));
  }

  /** Swich only. Partial: omitted fields stay; launchDate "" clears it. */
  updateLicensee(id: string, body: UpdateLicenseeBody): Promise<ApiLicensee> {
    return firstValueFrom(this.http.patch<ApiLicensee>(`${this.base}/licensees/${id}`, body));
  }

  /** Who paid is decided by the caller's role: Swich → Swich, operator → the licensee. */
  createCost(licenseeId: string, body: CreateCostBody): Promise<ApiCost> {
    return firstValueFrom(this.http.post<ApiCost>(`${this.base}/licensees/${licenseeId}/costs`, body));
  }

  /** The cost's own side only, before a statement includes it. Soft: the row stays, struck through, with the reason. */
  deleteCost(licenseeId: string, costId: string, reason: string): Promise<ApiCost> {
    return firstValueFrom(this.http.delete<ApiCost>(`${this.base}/licensees/${licenseeId}/costs/${costId}`, { body: { reason } }));
  }

  /** The OTHER side disputes a cost: no statement counts it until that side releases it. */
  holdCost(licenseeId: string, costId: string, reason: string): Promise<ApiCost> {
    return firstValueFrom(this.http.post<ApiCost>(`${this.base}/licensees/${licenseeId}/costs/${costId}/hold`, { reason }));
  }

  releaseCost(licenseeId: string, costId: string, reason?: string): Promise<ApiCost> {
    return firstValueFrom(this.http.post<ApiCost>(`${this.base}/licensees/${licenseeId}/costs/${costId}/release`, { reason }));
  }

  /** The owner corrects an already-included cost: a signed entry linked to it that the next statement counts. */
  addCostCorrection(licenseeId: string, costId: string, body: { amount: number; reason: string; note?: string }): Promise<ApiCost> {
    return firstValueFrom(this.http.post<ApiCost>(`${this.base}/licensees/${licenseeId}/costs/${costId}/corrections`, body));
  }

  /** The agreement's change history, newest first — the same for Swich and the operator (§2.6). */
  listAgreementEvents(licenseeId: string, kind?: string): Promise<ApiAgreementEventsResponse> {
    const q = kind ? `?kind=${encodeURIComponent(kind)}` : '';
    return firstValueFrom(this.http.get<ApiAgreementEventsResponse>(`${this.base}/licensees/${licenseeId}/events${q}`));
  }

  /** Swich only: put a park under a licensee's agreement, or make it Swich-direct (null). */
  assignParkLicensee(parkId: string, licenseeId: string | null): Promise<ApiParkAssignment> {
    return firstValueFrom(this.http.put<ApiParkAssignment>(`${this.base}/parks/${parkId}/licensee`, { licenseeId }));
  }

  // ── Reports ───────────────────────────────────────────────────────
  getReport(parkId: string, fromIso: string, toIso: string, topDrivers = 10): Promise<ApiReport> {
    const q = new URLSearchParams({ from: fromIso, to: toIso, topDrivers: String(topDrivers) }).toString();
    return firstValueFrom(this.http.get<ApiReport>(
      `${this.base}/parks/${parkId}/reports?${q}`));
  }

  // ── Incident log (licence agreement Annex 1) ──────────────────────
  /** The log, open first. Not tied to the topbar park: Swich and the licensee see every park, managers their own + platform-wide. */
  listIncidents(filter: IncidentFilter = {}): Promise<ApiIncidentsResponse> {
    const q = new URLSearchParams();
    if (filter.state) q.set('state', filter.state);
    if (filter.level) q.set('level', filter.level);
    if (filter.month) q.set('month', filter.month);
    if (filter.parkId) q.set('parkId', filter.parkId);
    const qs = q.toString();
    return firstValueFrom(this.http.get<ApiIncidentsResponse>(`${this.base}/incidents${qs ? '?' + qs : ''}`));
  }

  getIncident(id: string): Promise<ApiIncidentDetail> {
    return firstValueFrom(this.http.get<ApiIncidentDetail>(`${this.base}/incidents/${id}`));
  }

  reportIncident(body: ReportIncidentBody): Promise<ApiIncidentDetail> {
    return firstValueFrom(this.http.post<ApiIncidentDetail>(`${this.base}/incidents`, body));
  }

  /** Swich only. Only the fields passed change; one timeline entry lists them. */
  updateIncident(id: string, body: UpdateIncidentBody): Promise<{ changed: boolean; incident: ApiIncidentDetail }> {
    return firstValueFrom(this.http.patch<{ changed: boolean; incident: ApiIncidentDetail }>(`${this.base}/incidents/${id}`, body));
  }

  /** acknowledge | external-restored | resolve | reopen (Swich) and notes (Swich + licensee). */
  incidentAction(id: string, action: IncidentAction, text?: string): Promise<ApiIncidentDetail> {
    const path = action === 'note' ? 'notes' : action;
    return firstValueFrom(this.http.post<ApiIncidentDetail>(`${this.base}/incidents/${id}/${path}`, text ? { text } : {}));
  }

  /** Annex 1 §5–§7 for one Tbilisi calendar month (yyyy-MM; default the current one). Swich + licensee. */
  getIncidentMetrics(month?: string): Promise<ApiIncidentMetrics> {
    const qs = month ? `?month=${encodeURIComponent(month)}` : '';
    return firstValueFrom(this.http.get<ApiIncidentMetrics>(`${this.base}/incidents/metrics${qs}`));
  }
}

/**
 * §4.1.2 late-payment information for one park transfer — informational, never charged automatically.
 * The penalty fields come only for a licensee park and only to Swich and the operator: it is the licensee's
 * to Swich — a park manager (and a Swich-direct park) sees the days overdue only.
 */
export interface ApiOverdue {
  dueDate: string;          // YYYY-MM-DD
  daysOverdue: number;
  paid: boolean;            // paid late (by the bank or outside the platform): the figure is final
  penaltyDays?: number;     // days whose cause was the park's (insufficient balance / no account)
  technicalDays?: number;   // days of a platform or bank fault, or before anything was attempted — no penalty
  accrued?: number;         // 0.1 % a day on the unpaid amount, over penaltyDays
  percentPerDay?: number;
}

/** A failed transfer recorded as paid outside the platform. Park managers get payer + paidOn only. */
export interface ApiSettlementExternal {
  payer: 'licensee' | 'park' | string | null;
  paidOn: string | null;      // YYYY-MM-DD
  reference?: string | null;
  note?: string | null;
  recordedBy?: string | null;
  recordedAt?: string | null;
}

export interface SettleExternallyBody {
  payer: 'licensee' | 'park';
  amount: number;
  paidOn: string;             // YYYY-MM-DD
  reference: string;
  note?: string;
}

export interface ApiSettlement {
  id: string;
  parkId: string;
  parkName?: string;
  /** Monthly row (licence agreement) vs a legacy nightly row. */
  isMonthly?: boolean;
  /** The licensee statement this row is the park's part of; null for Swich-direct parks. */
  monthlyStatementId?: string | null;
  month?: string;                  // YYYY-MM
  settlementDate: string;          // YYYY-MM-DD (monthly rows: the month's last day)
  periodFromUtc?: string;
  periodToUtc?: string;
  cashoutCount: number;
  feeTotal: number;
  /** Null for a park manager on a licensee park's row: the split is the licensee's barrier phase (Swich and the operator see it). */
  phase1Fees: number | null;
  phase2Fees: number | null;
  swichShare: number;
  parkShare: number;
  cumulativeFeesBefore: number;
  status: string;                  // Pending | Processing | Completed | Failed | SettledExternally
  bankTransferId: string | null;
  failureReason: string | null;
  attemptCount: number;
  lastAttemptAt?: string | null;
  completedAt: string | null;
  description: string;
  invoiceRef: string;
  sourceIban?: string | null;
  swichIban?: string | null;
  initiatedBy?: string | null;
  createdAt?: string;
  failureCode?: string | null;
  overdue?: ApiOverdue | null;
  external?: ApiSettlementExternal | null;
}

export interface ApiSettlementsResponse {
  count: number;
  totals: { completedSwichShare: number; paidExternallySwichShare?: number; failedCount: number; failedSwichShare: number };
  settlements: ApiSettlement[];
}

export interface ApiSettlementMonth {
  month: string;          // "2026-09"
  invoiceRef: string;     // "PT-2026-09"
  isCurrent: boolean;     // the month is still running → the PDF is marked interim
  settlements: number;
  cashouts: number;
  feeTotal: number;
  swichShare: number;
  transferred: number;    // Completed settlements
  paidExternally?: number; // paid outside the platform (§2.5)
  outstanding: number;    // Failed / Pending / Processing
  failedCount: number;
  pendingCount: number;
  /** The park's monthly row for that month (null for legacy nightly months). */
  settlementId?: string | null;
  status?: string | null;
  viaLicensee?: boolean;
  /** The park changed arrangement during the month: a licensee row and a Swich-direct row, summed above (§2.2). */
  splitRegimes?: boolean;
  failureCode?: string | null;
  completedAt?: string | null;
  bankTransferId?: string | null;
  overdue?: ApiOverdue | null;
  external?: ApiSettlementExternal | null;
}

export interface ApiSettlementMonthsResponse {
  parkId: string;
  count: number;
  months: ApiSettlementMonth[];
}

export type ApiCostCategory = 'Bank' | 'Sms' | 'PaymentProvider' | 'SupportComms' | 'Hosting' | 'Domain';

/**
 * Where a shared-cost entry stands: included in a statement (locked) · deleted by its owner (kept, struck through) ·
 * held by the other side (disputed, not counted) · waiting (entered less than the cut-off window ago) · pending
 * (counted if a statement were computed now).
 */
export type ApiCostState = 'included' | 'deleted' | 'held' | 'waiting' | 'pending';

/** One third-party invoice for a shared cost (§2.4) — or a correction of one already included. */
export interface ApiCost {
  id: string;
  licenseeId: string;
  paidBy: 'Swich' | 'Licensee';
  category: ApiCostCategory;
  vendor: string;
  invoiceNumber: string;
  invoiceDate: string;        // YYYY-MM-DD
  /** Signed for a correction; above zero for an invoice. */
  amount: number;
  note: string | null;
  createdBy: string;
  createdAt: string;
  /** When its owner last entered it; it counts from eligibleFrom (the cut-off window after that). */
  enteredAt: string;
  eligibleFrom: string;
  statementId: string | null;
  /** "PT-YYYY-MM" once a statement included it — then it is locked. */
  includedIn: string | null;
  state: ApiCostState;
  /** Waiting, and the next scheduled statement comes before its window ends: it goes into the one after. */
  missesNextRun: boolean;
  isCorrection: boolean;
  correctsId: string | null;
  /** A correction's reason. */
  reason: string | null;
  /** On a correction: the original it corrects. */
  corrects: { id: string; amount: number; invoiceDate: string; includedIn: string | null } | null;
  /** On an original: its corrections, oldest first. */
  corrections: Array<{ id: string; amount: number; reason: string | null; state: ApiCostState; includedIn: string | null; createdBy: string; createdAt: string }>;
  held: { at: string; by: string; reason: string } | null;
  deleted: { at: string; by: string; reason: string } | null;
  /** The cost's own side, before inclusion. */
  canEdit: boolean;
  canDelete: boolean;
  /** The other side: hold a cost it disputes / release its own hold. */
  canHold: boolean;
  canRelease: boolean;
  /** The owner, on an included original. */
  canCorrect: boolean;
}

export type ApiAgreementEventKind =
  'launch_date' | 'terms' | 'cost_added' | 'cost_edited' | 'cost_deleted' | 'cost_held' | 'cost_released' | 'correction_added';

/** One entry of the licence agreement's change history (append-only). old/new are the recorded snapshots. */
export interface ApiAgreementEvent {
  id: number;
  at: string;
  actor: string;
  kind: ApiAgreementEventKind;
  referenceId: string | null;
  old: Record<string, any> | null;
  new: Record<string, any> | null;
  reason: string | null;
  cost: { id: string; vendor: string; invoiceNumber: string; paidBy: 'Swich' | 'Licensee'; isCorrection: boolean } | null;
}

export interface ApiAgreementEventsResponse {
  count: number;
  kinds: ApiAgreementEventKind[];
  events: ApiAgreementEvent[];
}

export interface CreateCostBody {
  category: ApiCostCategory;
  vendor: string;
  invoiceNumber: string;
  invoiceDate: string;
  amount: number;
  note?: string;
}

/** One park's part of a licensee statement. */
export interface ApiStatementPark {
  settlementId: string | null;
  parkId: string;
  parkName: string;
  cashoutCount: number;
  fees: number;
  feeSharePercent: number;
  barrierFees: number;
  swichShare: number;
  status: string | null;          // null while the month is only a preview
  bankTransferId: string | null;
  failureReason: string | null;
  failureCode: string | null;
  attemptCount: number;
  lastAttemptAt: string | null;
  completedAt: string | null;
  description: string | null;
  overdue: ApiOverdue | null;
  external?: ApiSettlementExternal | null;
}

export type ApiStatementPhase = 'Barrier' | 'Crossing' | 'Normal';

/** The licensee's statement for a month — stored (frozen) or a live preview. */
export interface ApiStatement {
  isPreview: boolean;
  statementId: string | null;
  licenseeId: string;
  licenseeName: string;
  launchDate: string | null;
  month: string;
  invoiceRef: string;
  periodFromUtc: string;
  periodToUtc: string;
  isClosed: boolean;
  dueDate: string;
  cashoutCount: number;
  feeIncome: number;
  licenseeCosts: number;
  swichCosts: number;
  carryIn: number;
  barrierGel: number;
  swichSharePercent: number;
  barrierBefore: number;
  /** An earlier month already reached the barrier: it stays reached even if R is back under B (§2.3). */
  barrierFilledBefore?: boolean;
  barrierAfter: number;
  barrierFilled?: boolean;
  barrierRemaining: number;
  phase: ApiStatementPhase;
  barrierFraction: number;
  barrierEntitlement: number;
  normalEntitlement: number;
  entitlement: number;
  due: number;
  carryOut: number;
  status: string | null;
  computedAt: string | null;
  executedAt: string | null;
  initiatedBy: string | null;
  transferred: number;
  paidExternally?: number;
  outstanding: number;
  /** Why "Settle now" would be refused now (month_not_closed, launch_date_not_set, …). */
  blockedReason: string | null;
  parks: ApiStatementPark[];
  /** The entries this statement counts (CL / CS). */
  costs: ApiCost[];
  /**
   * Dated in (or before) the month but not counted here: for a settled month, entered afterwards — they go into the
   * next statement; for a preview, held or still inside their cut-off window.
   */
  lateCosts: ApiCost[];
  /** Entries of the month deleted by their owner — shown struck through, with the reason. */
  deletedCosts: ApiCost[];
  /** Every entry on hold (disputed), whatever its month. */
  heldCosts: ApiCost[];
}

/** A Swich-direct park's month (stored row, or a live preview). */
export interface ApiDirectSettlement {
  isPreview: boolean;
  settlementId: string | null;
  parkId: string;
  parkName: string;
  month: string;
  swichSharePercent: number;
  phase1SharePercent: number | null;
  phase1CapGel: number | null;
  cashoutCount: number;
  feeTotal: number;
  phase1Fees: number;
  phase2Fees: number;
  swichShare: number;
  cumulativeFeesBefore: number;
  status: string | null;
  bankTransferId: string | null;
  failureReason: string | null;
  failureCode: string | null;
  completedAt: string | null;
  overdue: ApiOverdue | null;
  external?: ApiSettlementExternal | null;
  blockedReason: string | null;
}

export interface ApiSettlementMonthView {
  month: string;
  invoiceRef: string;
  isCurrent: boolean;
  isClosed: boolean;
  dueDate: string;
  runDayOfMonth: number;
  allowOpenMonthRun: boolean;
  /** A shared cost counts once this many hours have passed since its entry (48). */
  costCutoffHours: number;
  /** When the worker next computes a statement (UTC). */
  nextStatementRunAt: string;
  statements: ApiStatement[];
  direct: ApiDirectSettlement[];
}

export interface ApiSettlementRunResult {
  month: string;
  statements: ApiStatement[];
  direct: ApiSettlement[];
  skipped: Array<{ kind: 'licensee' | 'park'; id: string; name: string; code: string }>;
}

export interface ApiLicensee {
  id: string;
  name: string;
  taxId: string | null;
  email: string | null;
  iban: string | null;
  launchDate: string | null;
  /** The launch date has passed (Tbilisi day): it can no longer move. */
  launchDateLocked: boolean;
  /** Every set / move of the launch date, newest first. */
  launchDateHistory: Array<{ id: number; at: string; actor: string; from: string | null; to: string | null; reason: string | null }>;
  barrierGel: number;
  swichSharePercent: number;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  parks: Array<{ id: string; name: string; slug: string; status: string }>;
  statementCount: number;
  lastStatement: string | null;
  carryOut: number;
  barrier: { entitled: number; transferred: number; paidExternally?: number; remaining: number; filled: boolean };
}

export interface UpdateLicenseeBody {
  name?: string;
  taxId?: string;
  email?: string;
  iban?: string;
  launchDate?: string;
  barrierGel?: number;
  swichSharePercent?: number;
  isActive?: boolean;
  /** Required when the launch date changes; recorded with any change of the launch date or the terms. */
  reason?: string;
}

export interface ApiParkAssignment {
  parkId: string;
  parkName: string;
  licenseeId: string | null;
  licenseeName: string | null;
  changed: boolean;
  unsettledFees: number;
  /** Earned under an earlier arrangement and not settled yet — stays with it (fees follow the regime they were earned under). */
  unsettledEarnedBefore: number;
  /** From before regimes were recorded — follows the assignment at settlement time, i.e. this one. */
  unsettledUnstamped: number;
}

/** Annex 3 §7: the park's revocation of the platform's authorisations, and what still waits on its account. */
export interface ApiRevocationResult {
  parkId: string;
  authorisation: {
    noticeOn: string;
    revokedFrom: string;
    inEffect: boolean;
    reason: string;
    recordedBy: string | null;
    recordedAt: string | null;
  } | null;
  waitingPayouts: number;
  unpaidSettlements: number;
}

export interface ApiSettlementCashoutsResponse {
  settlementId: string;
  count: number;
  cashouts: Array<{
    id: string; driverName: string | null; amount: number; fee: number;
    completedAt: string | null; bankTransferId: string | null; invoiceNumber: number | null;
  }>;
}

export interface ApiReport {
  parkId: string;
  windowFrom: string;
  windowTo: string;
  summary: {
    count: number;
    value: number;
    fees: number;
    net: number;
    failedCount: number;
    attempted: number;
    successRate: number;
    uniqueDrivers: number;
  };
  daily: Array<{
    date: string;
    cashoutsCount: number;
    value: number;
    fees: number;
    failedCount: number;
  }>;
  topDrivers: Array<{
    driverId: string;
    driverName: string | null;
    cashoutsCount: number;
    totalValue: number;
    totalFees: number;
  }>;
  byBank: Array<{
    bankType: string;
    cashoutsCount: number;
    value: number;
  }>;
}

export interface ApiRecRunsResponse {
  parkId: string;
  count: number;
  runs: ApiRecRun[];
}

export interface ApiRecRun {
  id: string;
  windowFrom: string;
  windowTo: string;
  startedAt: string;
  finishedAt: string | null;
  status: string;
  cashoutsScanned: number;
  bankTransfersScanned: number;
  yandexTxScanned: number;
  discrepanciesFound: number;
  error: string | null;
}

export interface ApiRecDiscrepanciesResponse {
  parkId: string;
  openCount: number;
  count: number;
  discrepancies: ApiRecDiscrepancy[];
  /** Swich and the operator of the park's licensee resolve; the park's manager notes instead. */
  canResolve?: boolean;
  canNote?: boolean;
}

export interface ApiRecDiscrepancy {
  id: string;
  runId: string;
  cashoutId: string | null;
  kind: string;
  paytaxiAmount: number | null;
  externalAmount: number | null;
  bankTransferId: string | null;
  yandexTransactionId: string | null;
  notes: string | null;
  isResolved: boolean;
  resolvedAt: string | null;
  resolvedBy: string | null;
  resolutionNotes: string | null;
  /** The park's own explanation (it does not resolve the item). */
  parkNote?: string | null;
  parkNoteAt?: string | null;
  /** Who wrote it — shown to Swich and the operator only (null for the park). */
  parkNoteBy?: string | null;
  createdAt: string;
}

export interface ApiActivityResponse {
  parkId: string;
  count: number;
  events: ApiActivityEvent[];
}

export interface ApiActivityEvent {
  id: string;
  at: string;
  type: string;       // cashout_completed | cashout_failed | cashout_review | cashout_submitted | driver_joined
  severity: string;   // info | success | warning | error
  message: string;
  driverName: string | null;
  amount: number | null;
}

export interface ApiHourlyResponse {
  parkId: string;
  asOf: string;
  buckets: ApiHourBucket[];
}

export interface ApiHourBucket {
  hour: string;
  value: number;
  count: number;
}

export interface ApiYandexLookup {
  yandexProfileId: string;
  name: string | null;
  carPlate: string | null;
  balance: number;
  currency: string;
  alreadyLinked: boolean;
}

export interface CreateDriverBody {
  phone: string;
  yandexProfileId: string;
  name: string;
  consentGiven: boolean;
  iban?: string;
  holderName?: string;
  /** Required when holderName is not the driver (third-party account). */
  reason?: string;
}

export interface ApiBankAccount {
  id: string;
  bankCode: string;      // "TB" | "BG" | …
  bankLabel: string;     // "TBC" | "BOG" | …
  provider: string;      // "tbc" | "bog" | "mock"
  iban: string;
  holderName: string | null;
  label: string | null;
  isActive: boolean;
  isPrimary: boolean;
  credentialsSet?: boolean;
  balance?: number | null;
  balanceError?: string | null;
  queuedCount?: number;
  queuedNet?: number;
  createdAt?: string;
}

export interface ApiBankAccountsResponse {
  parkId: string;
  count: number;
  accounts: ApiBankAccount[];
  /** The providers this server accepts — no "mock" on the live server (test rails are for testing only). */
  allowedProviders?: string[];
}

export interface UpsertBankAccountBody {
  iban?: string;
  provider?: string;
  holderName?: string;
  label?: string;
  credentialsJson?: string;
  isPrimary?: boolean;
  isActive?: boolean;
}

export interface UpdateDriverBody {
  name?: string;
  phone?: string;
  yandexProfileId?: string;
  status?: string; // "Active" | "Suspended" | "Pending"
}

export interface UpdateParkBody {
  legalEntityName?: string;
  taxId?: string;
  phone?: string;
  bankAccountIban?: string;
  yandexClientId?: string;
  yandexApiKey?: string;
  yandexParkId?: string;
  cashoutFee?: number;
  minCashoutAmount?: number;
  maxCashoutAmount?: number;             // 0 = no limit
  dailyCashoutLimitPerDriver?: number;   // 0 = no limit
  swichSharePercent?: number;            // Swich only
  phase1SharePercent?: number;           // Swich only; -1 = clear
  phase1CapGel?: number;                 // Swich only; 0 = clear
}

export interface CreateParkBody {
  name: string;
  slug?: string;
  operatingModel?: string;       // "ModelA" (launch) — legacy values still accepted
  bankProvider?: string;         // "tbc" | "bog" | "mock" — inferred from IBAN when omitted
  bankCredentialsJson?: string;
  yandexClientId?: string;
  yandexApiKey?: string;
  yandexParkId: string;
  legalEntityName?: string;
  taxId?: string;
  phone?: string;
  bankAccountIban: string;       // required: the park's primary payout account
  managerEmail?: string;
  managerName?: string;
  managerPassword?: string;
  cashoutFee?: number;
  minCashoutAmount?: number;
  maxCashoutAmount?: number | null;
  dailyCashoutLimitPerDriver?: number | null;
  /** The licensee whose agreement settles the park; omitted = the one licensee in force (required when several). */
  licenseeId?: string;
  /** True = Swich connected the park directly: outside the licence agreement, its own split. */
  direct?: boolean;
}

export interface CreateParkResult {
  id: string;
  name: string;
  slug: string;
  operatingModel: string;
  managerEmail: string | null;
  licenseeId?: string | null;
  licenseeName?: string | null;
}

export interface YandexRosterDriver {
  yandexProfileId: string;
  name: string | null;
  carPlate: string | null;
  phone: string | null;
  balance: number;
  currency: string;
  alreadyOnboarded: boolean;
}

export interface YandexRosterResponse {
  parkId: string;
  count: number;
  drivers: YandexRosterDriver[];
}

export interface BulkOnboardResult {
  createdCount: number;
  skippedCount: number;
  created: { id: string; yandexProfileId: string; name: string; phone: string }[];
  skipped: { yandexProfileId: string; reason: string }[];
}

export interface ApiNewDriver {
  id: string;
  name: string;
  yandexProfileId: string;
  status: string;
  phone: string;
  parkId: string;
}

export interface ApiKpis {
  park: { id: string; name: string; operatingModel: string };
  cashoutsToday: { count: number; value: number };
  feesToday: { value: number };
  pendingQueue: { count: number; value: number };
  failedToday: { count: number };
  activeDrivers: { count: number; total: number };
  cashoutFee: number;
  queued: { count: number; value: number; oldestAt: string | null };
  asOf: string;
}

export interface ApiPark {
  id: string;
  name: string;
  slug: string;
  yandexParkId: string;
  bankProvider: string;
  operatingModel: string;
  cashoutFee: number;
  minCashoutAmount: number;
  maxCashoutAmount: number | null;
  dailyCashoutLimitPerDriver: number | null;
  swichSharePercent: number;
  phase1SharePercent: number | null;
  phase1CapGel: number | null;
  /** Settled under the licence agreement with this licensee; null = Swich-direct (own split above). */
  licenseeId?: string | null;
  licenseeName?: string | null;
  status: string;
  /** Why the status last changed (set on suspension). */
  statusReason: string | null;
  statusChangedAt: string | null;
  /** "admin:{email}" — only returned to Swich and the operator. */
  statusChangedBy: string | null;
  /**
   * Annex 3 §7 — the park's written revocation of the platform's authorisations, when recorded: the day the notice was
   * received, the first day without them ("YYYY-MM-DD"), whether that day has come, and the reason. From that day nothing
   * is paid or collected from the park's account. Who recorded it is only returned to Swich and the operator.
   */
  authorisationNoticeOn?: string | null;
  authorisationRevokedFrom?: string | null;
  authorisationRevoked?: boolean;
  authorisationRevocationReason?: string | null;
  authorisationRevocationRecordedBy?: string | null;
  authorisationRevocationRecordedAt?: string | null;
  driverCount: number;
  legalEntityName: string | null;
  taxId: string | null;
  phone: string | null;
  bankAccountIban: string | null;
  yandexClientId: string | null;
  yandexApiKeySet: boolean;
  bankAccounts: ApiBankAccount[];
}

export interface ParkStatusResult {
  id: string;
  name: string;
  status: string;
  statusReason: string | null;
  statusChangedAt: string | null;
  statusChangedBy: string | null;
  /** False when the park already had the requested status (nothing was changed). */
  changed: boolean;
  /** Payouts already debited from Yandex — they still complete while the park is suspended. */
  inFlightCount: number;
}

export interface ApiDriversResponse {
  park: { id: string; name: string; yandexParkId: string };
  driverCount: number;
  drivers: ApiDriver[];
}

export interface ApiDriver {
  id: string;
  name: string | null;
  phone: string;
  yandexProfileId: string | null;
  status: string;
  yandex: {
    balance: number;
    currency: string;
    carPlate: string | null;
    name: string | null;
  } | null;
  cards: ApiCard[];
}

export interface ApiCard {
  id: string;
  maskedPan: string;
  bankType: string;
  bankCode?: string;
  iban?: string;
  holderName?: string | null;
  isDefault: boolean;
  /** Holder name does not match the driver's registered name; park added it with a reason. */
  isThirdPartyAccount?: boolean;
  thirdPartyReason?: string | null;
  /** "driver:{id}" or "admin:{email}". */
  addedBy?: string | null;
}

/** The server records the initiating admin from the bearer token — never from the body. */
export interface CreateCashoutBody {
  driverId: string;
  cardId: string;
  amount: number;
  idempotencyKey: string;
}

export interface CashoutSagaResult {
  cashoutId: string;
  status: string;
  amount: number;
  fee: number;
  net: number;
  bankTransferId: string | null;
  yandexTransactionId: string | null;
  failureReason: string | null;
  wasDeduped: boolean;
  nextAttemptAt?: string | null;
  attemptCount?: number;
}

export interface ApiCashoutsResponse {
  parkId: string;
  count: number;
  cashouts: ApiCashout[];
}

export interface ApiCashout {
  id: string;
  driverId: string;
  driverName: string | null;
  amount: number;
  fee: number;
  status: string;
  bankTransferId: string | null;
  yandexTransactionId: string | null;
  yandexReversalTransactionId?: string | null;
  failureReason: string | null;
  initiatedBy?: string | null;
  attemptCount?: number;
  nextAttemptAt?: string | null;
  /** Unknown-outcome attempts since the bank last answered about the payout's current document. */
  unresolvedAttempts?: number;
  /**
   * A document of this payout went to the bank and the bank never said what became of it: it may be paid,
   * so it cannot be cancelled until the bank answers about that document. Sticky — a later, unrelated
   * answer (an expired password, "no money") does not clear it.
   */
  outcomeUnknown?: boolean;
  /** Which document that is (our payment-order id) — what to look up at the bank. */
  unknownOutcomeDocument?: string | null;
  createdAt: string;
  completedAt: string | null;
  bankType: string;
  maskedPan: string;
  destinationIban?: string;
  sourceIban?: string | null;
  /** Account holder as registered on the destination; differs from the driver for third-party accounts. */
  holderName?: string | null;
  isThirdPartyAccount?: boolean;
  /** Set on a cashout created by "Retry": the Failed cashout it retries. */
  retryOfCashoutId?: string | null;
  /** Set on a Failed cashout that already has a live (non-Failed) retry — the console hides Retry. */
  retriedByCashoutId?: string | null;
}

// ── Incident log (licence agreement Annex 1) ────────────────────────
export type IncidentLevel = 'P1' | 'P2' | 'P3';
export type IncidentStatus = 'Reported' | 'Acknowledged' | 'Resolved';
export type IncidentFault = 'Platform' | 'External' | 'Park' | 'Unknown';
export type SlaState = 'Pending' | 'Met' | 'Overdue' | 'Missed' | 'Paused' | 'NoTarget';
export type IncidentAction = 'acknowledge' | 'external-restored' | 'resolve' | 'reopen' | 'note';

export interface IncidentFilter {
  state?: 'open' | 'resolved' | 'all';
  level?: IncidentLevel;
  month?: string;                  // yyyy-MM
  parkId?: string;                 // a park id or 'platform'
}

export interface ApiSlaClock {
  state: SlaState;
  startsAt: string | null;
  dueAt: string | null;
  doneAt: string | null;
  /** due − (done ?? now) in minutes: positive = left / early, negative = overdue / late. */
  marginMinutes: number | null;
  /** Paused resolution only: what the clock waits for. */
  waitingFor: 'bank' | 'yandex' | 'park' | 'external' | null;
}

export interface ApiIncident {
  id: string;
  number: number;
  ref: string;                     // INC-0042
  level: IncidentLevel;
  status: IncidentStatus;
  title: string;
  cause: string | null;
  fault: IncidentFault;
  source: 'Manual' | 'Auto';
  autoKind: 'payout_queue' | 'payout_queue_wide' | 'yandex_outage' | null;
  /** When it reached its level — its report, or a later raise (the first one): its clocks count from here. */
  levelSince: string;
  parkId: string | null;           // null = all parks
  parkName: string | null;
  startedAt: string;
  reportedAt: string;
  reportedBy: string;              // "admin:{email}", "system", or "admin" (hidden from park managers)
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
  externalRestoredAt: string | null;
  externalRestoredBy: string | null;
  resolvedAt: string | null;
  resolvedBy: string | null;
  durationMinutes: number;
  sla: { response: ApiSlaClock; resolution: ApiSlaClock };
}

export interface ApiIncidentsResponse {
  asOf: string;
  count: number;
  openCount: number;
  incidents: ApiIncident[];
}

/**
 * What changed in one "updated" entry. Dates are ISO instants; a scope `to` of null means all parks.
 * The console words it in its own language; the entry's English text is the record.
 */
export interface IncidentChange {
  field: 'title' | 'description' | 'cause' | 'level' | 'fault' | 'scope' | 'started' | string;
  from?: string | null;
  to?: string | null;
  /** Scope only: the move took the incident away from the monitor (it is no longer closed automatically). */
  detached?: boolean;
}

/**
 * A timeline entry the system worded (the monitor, or a fixed phrase such as "Resolved."): its code and
 * figures, so the console can show it in Georgian. Null on a person's own words, which are shown as typed.
 */
export interface IncidentTimelineData {
  code: string;
  level?: IncidentLevel;
  park?: string | null;
  title?: string;
  impactSince?: string | null;
  payouts?: number;
  drivers?: number;
  paid?: number;
  minutes?: number;
  failures?: number;
  /** Platform-wide auto incidents: how many parks the cause holds (Yandex: null = every park). */
  parks?: number | null;
  /** The bank a platform-wide queue incident waits for ("TBC"), null when not known. */
  bank?: string | null;
  /** Merge links: the park's own incident closed as a duplicate (`incident`, `park`), and the one it duplicates (`of`). */
  incident?: string;
  of?: string;
  fault?: IncidentFault;
  failureCode?: string | null;
  from?: string | null;
  to?: string;
  since?: string | null;
  note?: string | null;
  reason?: string;
  changes?: IncidentChange[];
}

export interface ApiIncidentTimelineEntry {
  at: string;
  by: string;
  kind: string;
  text: string;
  data?: IncidentTimelineData | null;
}

export interface ApiIncidentDetail {
  incident: ApiIncident;
  description: string | null;
  timeline: ApiIncidentTimelineEntry[];
  can: { acknowledge: boolean; restoreExternal: boolean; resolve: boolean; reopen: boolean; edit: boolean; note: boolean };
  asOf: string;
}

export interface ReportIncidentBody {
  title: string;
  level: IncidentLevel;
  description?: string;
  parkId?: string | null;
  startedAt?: string;              // ISO
  fault?: IncidentFault;           // Swich only
  cause?: string;                  // Swich only
  acknowledge?: boolean;           // Swich only
}

export interface UpdateIncidentBody {
  title?: string;
  description?: string;
  cause?: string;
  level?: IncidentLevel;
  fault?: IncidentFault;
  parkId?: string;
  platformWide?: boolean;
}

export interface ApiIncidentCitation {
  id: string;
  ref: string;
  level: IncidentLevel;
  fault: IncidentFault;
  reportedAt: string;
  resolvedAt: string | null;
  resolution: SlaState;
  hours: number | null;
}

export interface ApiIncidentMetrics {
  month: string;
  from: string;
  to: string;
  asOf: string;
  isCurrent: boolean;
  thresholds: {
    chronicP1Count: number; chronicP1Hours: number; abandonmentResponseHours: number;
    abandonmentSupportDays: number; abandonmentCureDays: number;
  };
  /** outsideAgreementP1: P1s reported this month at parks Swich connected directly. Shown apart, never counted. */
  counts: { p1: number; p2: number; p3: number; ownerP1: number; undeterminedP1: number; outsideAgreementP1: number };
  ownerP1Missed: ApiIncidentCitation[];
  ownerP1Hours: number;
  chronicBreach: { byCount: boolean; byHours: boolean; flag: boolean };
  abandonment: { flag: boolean; unanswered: ApiIncidentCitation[] };
  trial: {
    state: 'not_set' | 'upcoming' | 'running' | 'ended';
    launchDate: string | null;
    trialDays: number;
    from: string | null;
    to: string | null;
    day: number | null;
    licenseeReleased: boolean;
    qualifying: ApiIncidentCitation[];
    ownerP1Open: number;
    /** P1s of the trial whose fault is not determined and that missed their target: they may still qualify. */
    undeterminedMissed: ApiIncidentCitation[];
    undeterminedOpen: number;
    /** Not released, but an undetermined P1 missed its target: the card must not say "none qualifies". */
    undetermined: boolean;
  };
}
