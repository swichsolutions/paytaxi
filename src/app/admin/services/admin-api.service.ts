import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

/**
 * Typed HTTP client for the .NET backend admin endpoints.
 *
 * Base URL is the .NET dev server. CORS is configured on the backend for
 * localhost:4200. Auth is deferred to Phase 8 — for now the endpoints are open.
 */
@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);
  private readonly base = 'http://localhost:5196/api/admin';

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

  // ── Reports ───────────────────────────────────────────────────────
  getReport(parkId: string, fromIso: string, toIso: string, topDrivers = 10): Promise<ApiReport> {
    const q = new URLSearchParams({ from: fromIso, to: toIso, topDrivers: String(topDrivers) }).toString();
    return firstValueFrom(this.http.get<ApiReport>(
      `${this.base}/parks/${parkId}/reports?${q}`));
  }
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
}

export interface UpdateDriverBody {
  name?: string;
  phone?: string;
  yandexProfileId?: string;
  status?: string; // "Active" | "Inactive" | "Suspended"
}

export interface UpdateParkBody {
  legalEntityName?: string;
  taxId?: string;
  phone?: string;
  bankAccountIban?: string;
  yandexClientId?: string;
  yandexApiKey?: string;
  yandexParkId?: string;
}

export interface CreateParkBody {
  name: string;
  slug?: string;
  operatingModel?: string;       // "ModelA5" | "ModelA" | "ModelB"
  authorizationLimit?: number | null;
  bankProvider?: string;
  yandexClientId?: string;
  yandexApiKey?: string;
  yandexParkId: string;
  legalEntityName?: string;
  taxId?: string;
  phone?: string;
  bankAccountIban?: string;
  managerEmail?: string;
  managerName?: string;
  managerPassword?: string;
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
  authorizationLimit: number | null;
  asOf: string;
}

export interface ApiPark {
  id: string;
  name: string;
  slug: string;
  yandexParkId: string;
  bankProvider: string;
  operatingModel: string;
  authorizationLimit: number | null;
  status: string;
  driverCount: number;
  legalEntityName: string | null;
  taxId: string | null;
  phone: string | null;
  bankAccountIban: string | null;
  yandexClientId: string | null;
  yandexApiKeySet: boolean;
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
  isDefault: boolean;
}

export interface CreateCashoutBody {
  driverId: string;
  cardId: string;
  amount: number;
  idempotencyKey: string;
  initiatedBy?: string;
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
  failureReason: string | null;
  createdAt: string;
  completedAt: string | null;
  bankType: string;
  maskedPan: string;
}
