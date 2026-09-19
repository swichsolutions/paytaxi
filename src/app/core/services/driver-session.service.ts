import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';
import { environment } from '../../../environments/environment';

/**
 * Holds the "currently logged-in driver" context for the driver app.
 *
 * Sources everything from /api/driver/me, which derives the driver and park
 * from the JWT — clients cannot ask for another driver's data by changing
 * URL parameters.
 *
 * Also owns the driver's payout destinations (bank accounts / IBANs): add,
 * remove, make default — each call refreshes the session afterwards so every
 * page sees the same list.
 *
 * Balance semantics: `driver().balance === null` means "unknown" (Yandex unreachable or the
 * profile is missing) — it is never collapsed to 0. `balanceStale` marks a cached value that
 * may be outdated; `balanceAsOf` says when it was read.
 */
@Injectable({ providedIn: 'root' })
export class DriverSessionService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly base = `${environment.apiBase}/api/driver`;

  // Reactive session state
  readonly driver = signal<SessionDriver | null>(null);
  readonly parkId = signal<string | null>(null);
  readonly parkName = signal<string | null>(null);
  readonly park = signal<SessionPark | null>(null);
  readonly cards = signal<SessionCard[]>([]);
  /** True until the first /me round-trip finishes (success or failure). */
  readonly loading = signal(true);
  /** True while any /me fetch is in flight (including balance refreshes). */
  readonly refreshing = signal(false);
  /** Error code for the last /me attempt: 'not_signed_in' | 'load_failed' | null. Translate in the UI. */
  readonly error = signal<SessionError | null>(null);

  readonly ready = computed(() => this.driver() !== null && this.parkId() !== null);

  /** Park fee config with safe fallbacks while the session loads. */
  readonly cashoutFee = computed(() => this.park()?.cashoutFee ?? 0.5);
  readonly minCashout = computed(() => this.park()?.minCashoutAmount ?? 5);
  readonly maxCashout = computed(() => this.park()?.maxCashoutAmount ?? null);
  readonly supportedBanks = computed(() => this.park()?.supportedBanks ?? []);

  private bootstrapping: Promise<void> | null = null;

  /** Lazy bootstrap — first caller triggers the fetch, the rest await it. */
  ensureLoaded(): Promise<void> {
    if (this.bootstrapping) return this.bootstrapping;
    this.bootstrapping = this.fetchMe();
    return this.bootstrapping;
  }

  /** Drop cached session and refetch. Call after login/logout. */
  reloadForCurrentSession(): Promise<void> {
    this.driver.set(null);
    this.parkId.set(null);
    this.parkName.set(null);
    this.park.set(null);
    this.cards.set([]);
    this.error.set(null);
    this.loading.set(true);
    this.bootstrapping = this.fetchMe();
    return this.bootstrapping;
  }

  /**
   * Refresh balance + cards after a side-effecting action (e.g. cashout).
   * `fresh = true` forces a live Yandex read instead of the server-side cache.
   */
  refresh(fresh = false): Promise<void> {
    return this.fetchMe(fresh);
  }

  // ── Payout destinations ──────────────────────────────────────────

  /** Add an IBAN. Throws the HttpErrorResponse on failure so the caller can map `error.error`. */
  async addCard(iban: string, holderName?: string, makeDefault = true): Promise<SessionCard> {
    const card = await firstValueFrom(this.http.post<SessionCard>(`${this.base}/me/cards`, {
      iban, holderName: holderName || undefined, makeDefault,
    }));
    await this.fetchMe();
    return card;
  }

  async removeCard(cardId: string): Promise<void> {
    await firstValueFrom(this.http.delete<void>(`${this.base}/me/cards/${cardId}`));
    await this.fetchMe();
  }

  async setDefaultCard(cardId: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`${this.base}/me/cards/${cardId}/default`, {}));
    await this.fetchMe();
  }

  private async fetchMe(fresh = false): Promise<void> {
    if (!this.auth.token()) {
      // The route guard should have prevented this, but be defensive.
      this.error.set('not_signed_in');
      this.loading.set(false);
      return;
    }
    this.refreshing.set(true);
    try {
      const url = fresh ? `${this.base}/me?fresh=true` : `${this.base}/me`;
      const me = await firstValueFrom(this.http.get<MeResponse>(url));
      this.parkId.set(me.park.id);
      this.parkName.set(me.park.name);
      this.park.set(me.park);
      this.driver.set({
        id: me.driver.id,
        // Empty when the park never entered a name — the UI shows a translated fallback.
        name: me.driver.name?.trim() ?? '',
        phone: me.driver.phone ?? '',
        yandexProfileId: me.driver.yandexProfileId,
        carPlate: me.driver.carPlate ?? null,
        balance: typeof me.driver.balance === 'number' ? me.driver.balance : null,
        balanceAsOf: me.driver.balanceAsOf ? new Date(me.driver.balanceAsOf) : null,
        balanceStale: me.driver.balanceStale === true,
        balanceError: me.driver.balanceError ?? null,
      });
      this.cards.set(me.cards);
      this.error.set(null);
    } catch {
      // Keep whatever we already have (a stale balance beats a blank screen); flag the failure.
      this.error.set('load_failed');
    } finally {
      this.refreshing.set(false);
      this.loading.set(false);
    }
  }
}

export type SessionError = 'not_signed_in' | 'load_failed';
export type BalanceError = 'yandex_unavailable' | 'profile_not_found';

export interface SessionDriver {
  id: string;
  /** May be '' — render `t.unnamedDriver` in that case. */
  name: string;
  phone: string;
  yandexProfileId: string | null;
  carPlate: string | null;
  /** null = unknown (never 0). */
  balance: number | null;
  balanceAsOf: Date | null;
  balanceStale: boolean;
  balanceError: BalanceError | null;
}

export interface SessionBank {
  bankCode: string;   // "TB" | "BG" | …
  bankLabel: string;  // "TBC" | "BOG" | …
}

export interface SessionPark {
  id: string;
  name: string;
  operatingModel: string;
  cashoutFee: number;
  minCashoutAmount: number;
  maxCashoutAmount: number | null;
  dailyCashoutLimitPerDriver: number | null;
  supportedBanks: SessionBank[];
}

export interface SessionCard {
  id: string;
  maskedPan: string;
  bankType: string;
  bankCode: string;
  iban: string;
  holderName: string | null;
  isDefault: boolean;
}

interface MeResponse {
  driver: {
    id: string;
    name: string | null;
    phone: string | null;
    yandexProfileId: string | null;
    status: string;
    carPlate: string | null;
    balance: number | null;
    balanceAsOf: string | null;
    balanceStale: boolean;
    balanceError: BalanceError | null;
  };
  park: SessionPark;
  cards: SessionCard[];
}
