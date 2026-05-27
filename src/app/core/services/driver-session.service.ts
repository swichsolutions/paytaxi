import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * Holds the "currently logged-in driver" context for the driver app.
 *
 * Priority order on bootstrap:
 *   1. If the AuthService has a valid session (JWT-derived driverId + parkId),
 *      load THAT driver from the backend.
 *   2. Otherwise fall back to auto-discovery (first active driver-with-card
 *      in any park) — a dev-only convenience so the demo still runs if you
 *      bypass login.
 *
 * Once real auth is mandatory across all routes, the fallback can be deleted.
 */
@Injectable({ providedIn: 'root' })
export class DriverSessionService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly base = 'http://localhost:5196/api/admin';

  // Reactive session state
  readonly driver = signal<SessionDriver | null>(null);
  readonly parkId = signal<string | null>(null);
  readonly parkName = signal<string | null>(null);
  readonly cards = signal<SessionCard[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly ready = computed(() => this.driver() !== null && this.parkId() !== null);

  private bootstrapping: Promise<void> | null = null;

  /** Lazy bootstrap — first caller triggers the discovery, the rest await it. */
  ensureLoaded(): Promise<void> {
    if (this.bootstrapping) return this.bootstrapping;
    this.bootstrapping = this.bootstrap();
    return this.bootstrapping;
  }

  /** Drop cached session and re-bootstrap from current auth state. Call after login/logout. */
  reloadForCurrentSession(): Promise<void> {
    this.driver.set(null);
    this.parkId.set(null);
    this.parkName.set(null);
    this.cards.set([]);
    this.bootstrapping = this.bootstrap();
    return this.bootstrapping;
  }

  private async bootstrap(): Promise<void> {
    const claims = this.auth.session();
    if (claims) {
      await this.loadAuthenticatedDriver(claims.parkId, claims.driverId);
    } else {
      await this.discover();
    }
  }

  private async loadAuthenticatedDriver(parkId: string, driverId: string): Promise<void> {
    try {
      const parks = await firstValueFrom(this.http.get<ApiPark[]>(`${this.base}/parks`));
      const park = parks.find(p => p.id === parkId);
      this.parkName.set(park?.name ?? null);

      const resp = await firstValueFrom(
        this.http.get<DriversResponse>(`${this.base}/parks/${parkId}/drivers`));
      const me = resp.drivers.find(d => d.id === driverId);
      if (me) {
        this.parkId.set(parkId);
        this.applyDriver(me);
      } else {
        this.error.set('Authenticated driver no longer found on this park.');
      }
    } catch (err: any) {
      this.error.set(`Could not load your session: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }

  /** Re-fetch the current driver's balance and cards (after a cashout, for instance). */
  async refresh(): Promise<void> {
    const parkId = this.parkId();
    const driverId = this.driver()?.id;
    if (!parkId || !driverId) return;

    const resp = await firstValueFrom(
      this.http.get<DriversResponse>(`${this.base}/parks/${parkId}/drivers`));
    const fresh = resp.drivers.find(d => d.id === driverId);
    if (fresh) this.applyDriver(fresh);
  }

  private async discover(): Promise<void> {
    try {
      const parks = await firstValueFrom(
        this.http.get<ApiPark[]>(`${this.base}/parks`));

      for (const park of parks) {
        const resp = await firstValueFrom(
          this.http.get<DriversResponse>(`${this.base}/parks/${park.id}/drivers`));
        const candidate = resp.drivers.find(
          d => d.status?.toLowerCase() === 'active' && d.cards.length > 0);
        if (candidate) {
          this.parkId.set(park.id);
          this.parkName.set(park.name);
          this.applyDriver(candidate);
          return;
        }
      }
      this.error.set('No active driver with a registered card was found on the backend.');
    } catch (err: any) {
      this.error.set(`Could not load driver session: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }

  private applyDriver(d: ApiDriver): void {
    this.driver.set({
      id: d.id,
      name: d.name ?? '(unnamed)',
      yandexProfileId: d.yandexProfileId,
      carPlate: d.yandex?.carPlate ?? null,
      balance: d.yandex?.balance ?? 0,
    });
    this.cards.set(d.cards);
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

// ── Wire types (mirror admin-api.service.ts; could be shared later) ─

interface ApiPark {
  id: string;
  name: string;
}

interface DriversResponse {
  drivers: ApiDriver[];
}

interface ApiDriver {
  id: string;
  name: string | null;
  yandexProfileId: string | null;
  status: string;
  yandex: { balance: number; carPlate: string | null } | null;
  cards: SessionCard[];
}
