import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * Holds the "currently logged-in driver" context for the driver app.
 *
 * Sources everything from /api/driver/me, which derives the driver and park
 * from the JWT — clients cannot ask for another driver's data by changing
 * URL parameters.
 *
 * The route guard (authGuard) ensures we never get here without a JWT, so
 * there's no fallback to render — failed loads bubble up to `error`.
 */
@Injectable({ providedIn: 'root' })
export class DriverSessionService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly base = 'http://localhost:5196/api/driver';

  // Reactive session state
  readonly driver = signal<SessionDriver | null>(null);
  readonly parkId = signal<string | null>(null);
  readonly parkName = signal<string | null>(null);
  readonly cards = signal<SessionCard[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly ready = computed(() => this.driver() !== null && this.parkId() !== null);

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
    this.cards.set([]);
    this.bootstrapping = this.fetchMe();
    return this.bootstrapping;
  }

  /** Refresh balance + cards after a side-effecting action (e.g. cashout). */
  refresh(): Promise<void> {
    return this.fetchMe();
  }

  private async fetchMe(): Promise<void> {
    if (!this.auth.token()) {
      // The route guard should have prevented this, but be defensive.
      this.error.set('Not signed in.');
      this.loading.set(false);
      return;
    }
    try {
      const me = await firstValueFrom(this.http.get<MeResponse>(`${this.base}/me`));
      this.parkId.set(me.park.id);
      this.parkName.set(me.park.name);
      this.driver.set({
        id: me.driver.id,
        name: me.driver.name ?? '(unnamed)',
        yandexProfileId: me.driver.yandexProfileId,
        carPlate: me.driver.carPlate ?? null,
        balance: me.driver.balance ?? 0,
      });
      this.cards.set(me.cards);
    } catch (err: any) {
      this.error.set(`Could not load your session: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }
}

export interface SessionDriver {
  id: string;
  name: string;
  yandexProfileId: string | null;
  carPlate: string | null;
  balance: number;
}

export interface SessionCard {
  id: string;
  maskedPan: string;
  bankType: string;
  isDefault: boolean;
}

interface MeResponse {
  driver: {
    id: string;
    name: string | null;
    yandexProfileId: string | null;
    status: string;
    carPlate: string | null;
    balance: number | null;
  };
  park: {
    id: string;
    name: string;
    operatingModel: string;
  };
  cards: SessionCard[];
}
