import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { AdminApiService, ApiPark } from './admin-api.service';
import { AdminAuthService } from './admin-auth.service';

/**
 * Tracks "which park is the admin console currently viewing?" — and which
 * parks are visible at all (the backend already filters /api/admin/parks
 * for park-admins, so this just reflects what we got).
 *
 * Park-admin: forced to their own park (only one returned by the API).
 * Super-admin: defaults to the first park; the topbar switcher updates it.
 *
 * Reactive: pages bind to `currentParkId()` and re-fetch when it changes.
 */
@Injectable({ providedIn: 'root' })
export class AdminParkContextService {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AdminAuthService);

  readonly parks = signal<ApiPark[]>([]);
  readonly currentParkId = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly currentPark = computed<ApiPark | null>(() => {
    const id = this.currentParkId();
    return this.parks().find(p => p.id === id) ?? null;
  });

  readonly canSwitchPark = computed(() => this.parks().length > 1);

  private bootstrapping: Promise<void> | null = null;

  constructor() {
    // When the admin logs out (token cleared), drop our cached parks so a
    // subsequent login re-fetches fresh.
    effect(() => {
      if (!this.auth.token()) {
        this.parks.set([]);
        this.currentParkId.set(null);
        this.bootstrapping = null;
        this.loading.set(true);
      }
    });
  }

  ensureLoaded(): Promise<void> {
    if (this.bootstrapping) return this.bootstrapping;
    this.bootstrapping = this.loadParks();
    return this.bootstrapping;
  }

  setCurrentPark(parkId: string) {
    if (this.parks().some(p => p.id === parkId)) {
      this.currentParkId.set(parkId);
    }
  }

  private async loadParks(): Promise<void> {
    try {
      const parks = await this.api.listParks();
      this.parks.set(parks);
      if (parks.length === 0) {
        this.error.set('No parks accessible.');
        return;
      }
      // Prefer the park from the admin's session (park-admin), else first.
      const sessionParkId = this.auth.admin()?.parkId;
      const initial = parks.find(p => p.id === sessionParkId) ?? parks[0];
      this.currentParkId.set(initial.id);
    } catch (err: any) {
      this.error.set(`Could not load parks: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }
}
