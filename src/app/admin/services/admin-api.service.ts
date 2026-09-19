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

  /** Nudge a Queued cashout so the payout worker retries it immediately. */
  processCashoutNow(parkId: string, cashoutId: string): Promise<CashoutSagaResult> {
    return firstValueFrom(this.http.post<CashoutSagaResult>(
      `${this.base}/parks/${parkId}/cashouts/${cashoutId}/process-now`, {}));
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
  addDriverCard(parkId: string, driverId: string, body: { iban: string; holderName?: string; makeDefault?: boolean }): Promise<ApiCard> {
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
  listRecRuns(parkId: string, take = 20): Promise<ApiRecRunsResponse> {
    return firstValueFrom(this.http.get<ApiRecRunsResponse>(
      `${this.base}/parks/${parkId}/reconciliation/runs?take=${take}`));
  }

  listRecDiscrepancies(parkId: string, all = false): Promise<ApiRecDiscrepanciesResponse> {
    return firstValueFrom(this.http.get<ApiRecDiscrepanciesResponse>(
      `${this.base}/parks/${parkId}/reconciliation/discrepancies?all=${all}&take=100`));
  }

  resolveDiscrepancy(parkId: string, id: string, notes: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(
      `${this.base}/parks/${parkId}/reconciliation/discrepancies/${id}/resolve`,
      { notes }));
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

  getSettlementSummary(parkId: string, days = 14): Promise<ApiSettlementSummary> {
    return firstValueFrom(this.http.get<ApiSettlementSummary>(
      `${this.base}/parks/${parkId}/settlements/summary?days=${days}`));
  }

  listSettlementCashouts(settlementId: string): Promise<ApiSettlementCashoutsResponse> {
    return firstValueFrom(this.http.get<ApiSettlementCashoutsResponse>(
      `${this.base}/settlements/${settlementId}/cashouts`));
  }

  retrySettlement(settlementId: string): Promise<ApiSettlement> {
    return firstValueFrom(this.http.post<ApiSettlement>(`${this.base}/settlements/${settlementId}/retry`, {}));
  }

  runSettlementNow(parkId: string, date?: string): Promise<any> {
    const q = new URLSearchParams({ parkId });
    if (date) q.set('date', date);
    return firstValueFrom(this.http.post<any>(`${this.base}/settlements/run?${q}`, {}));
  }

  // ── Reports ───────────────────────────────────────────────────────
  getReport(parkId: string, fromIso: string, toIso: string, topDrivers = 10): Promise<ApiReport> {
    const q = new URLSearchParams({ from: fromIso, to: toIso, topDrivers: String(topDrivers) }).toString();
    return firstValueFrom(this.http.get<ApiReport>(
      `${this.base}/parks/${parkId}/reports?${q}`));
  }
}

export interface ApiSettlement {
  id: string;
  parkId: string;
  parkName?: string;
  settlementDate: string;          // YYYY-MM-DD
  periodFromUtc?: string;
  periodToUtc?: string;
  cashoutCount: number;
  feeTotal: number;
  phase1Fees: number;
  phase2Fees: number;
  swichShare: number;
  parkShare: number;
  cumulativeFeesBefore: number;
  status: string;                  // Pending | Processing | Completed | Failed
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
}

export interface ApiSettlementsResponse {
  count: number;
  totals: { completedSwichShare: number; failedCount: number; failedSwichShare: number };
  settlements: ApiSettlement[];
}

export interface ApiSettlementSummary {
  park: {
    id: string; name: string; cashoutFee: number;
    swichSharePercent: number; phase1SharePercent: number | null; phase1CapGel: number | null;
  };
  cumulativeFees: number;
  phase1Progress: number | null;
  phase1Remaining: number | null;
  inPhase1: boolean;
  settledFees: number;
  settledToSwich: number;
  failedToSwich: number;
  unsettled: { count: number; fees: number };
  daily: Array<{
    date: string; cashouts: number; volume: number; fees: number;
    settlementStatus: string | null; swichShare: number | null;
  }>;
  asOf: string;
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
}

export interface CreateParkResult {
  id: string;
  name: string;
  slug: string;
  operatingModel: string;
  managerEmail: string | null;
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
  status: string;
  driverCount: number;
  legalEntityName: string | null;
  taxId: string | null;
  phone: string | null;
  bankAccountIban: string | null;
  yandexClientId: string | null;
  yandexApiKeySet: boolean;
  bankAccounts: ApiBankAccount[];
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
  createdAt: string;
  completedAt: string | null;
  bankType: string;
  maskedPan: string;
  destinationIban?: string;
  sourceIban?: string | null;
}
