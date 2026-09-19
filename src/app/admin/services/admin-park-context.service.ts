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

  /**
   * Retry after a failed bootstrap. `loadParks` clears the cached promise on
   * failure, so this simply kicks off a fresh load and lets pages' effects
   * re-run when `currentParkId` is finally set.
   */
  retry(): Promise<void> {
    this.bootstrapping = null;
    this.error.set(null);
    this.loading.set(true);
    return this.ensureLoaded();
  }

  setCurrentPark(parkId: string) {
    if (this.parks().some(p => p.id === parkId)) {
      this.currentParkId.set(parkId);
    }
  }

  /** Re-fetch parks from the backend, preserving the current selection. Used after edits. */
  async refresh(): Promise<void> {
    const keep = this.currentParkId();
    try {
      const parks = await this.api.listParks();
      this.parks.set(parks);
      if (keep && parks.some(p => p.id === keep)) {
        this.currentParkId.set(keep);
      } else if (parks.length > 0) {
        this.currentParkId.set(parks[0].id);
      }
    } catch (err: any) {
      this.error.set(err?.error?.message ?? err?.message ?? 'refresh_failed');
    }
  }

  private async loadParks(): Promise<void> {
    this.error.set(null);
    try {
      const parks = await this.api.listParks();
      this.parks.set(parks);
      if (parks.length === 0) {
        // Distinct marker so the layout can show a "no parks" message rather
        // than a generic transport error. Retry is still allowed.
        this.error.set('no_parks');
        this.bootstrapping = null;
        return;
      }
      // Prefer the park from the admin's session (park-admin), else first.
      const sessionParkId = this.auth.admin()?.parkId;
      const initial = parks.find(p => p.id === sessionParkId) ?? parks[0];
      this.currentParkId.set(initial.id);
    } catch (err: any) {
      this.error.set(err?.error?.message ?? err?.message ?? 'load_failed');
      // Drop the cached promise so the next ensureLoaded()/retry() re-fetches.
      this.bootstrapping = null;
    } finally {
      this.loading.set(false);
    }
  }
}
