import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

/**
 * Holds the "currently logged-in driver" context for the driver app.
 *
 * Until real driver-auth (Phase 8) lands, this service auto-discovers a
 * usable driver on bootstrap — first active driver in the first park that
 * has at least one bank card. That's enough to drive the cashout flow
 * end-to-end against real backend data.
 *
 * When real auth arrives, this becomes a thin wrapper over the JWT payload
 * and the auto-discovery can be deleted.
 */
@Injectable({ providedIn: 'root' })
export class DriverSessionService {
  private readonly http = inject(HttpClient);
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
    this.bootstrapping = this.discover();
    return this.bootstrapping;
  }

  /** Re-fetch the current driver's balance and cards (after a cashout, for instance). */
  async refresh(): Promise<void> {
    const parkId = this.parkId();
    const driverId = this.driver()?.id;
    if (!parkId || !driverId) return;

    const resp = await firstValueFrom(
      this.http.get<DriversResponse>(`${this.base}/parks/${parkId}/drivers`));
    const fresh = resp.drivers.find(d => d.id === driverId);
    if (fresh) {
      this.driver.set({
        id: fresh.id,
        name: fresh.name ?? '(unnamed)',
        yandexProfileId: fresh.yandexProfileId,
        carPlate: fresh.yandex?.carPlate ?? null,
        balance: fresh.yandex?.balance ?? 0,
      });
      this.cards.set(fresh.cards);
    }
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
          this.driver.set({
            id: candidate.id,
            name: candidate.name ?? '(unnamed)',
            yandexProfileId: candidate.yandexProfileId,
            carPlate: candidate.yandex?.carPlate ?? null,
            balance: candidate.yandex?.balance ?? 0,
          });
          this.cards.set(candidate.cards);
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
