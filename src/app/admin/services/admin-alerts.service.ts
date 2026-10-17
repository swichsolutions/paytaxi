import { DestroyRef, Injectable, PLATFORM_ID, computed, effect, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { NavigationEnd, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AdminAuthService } from './admin-auth.service';

export type AlertKind = 'settlement_failed' | 'settlement_stuck' | 'cashout_review' | 'reconciliation_open' | 'park_suspended'
  | 'payout_waiting' | 'incident_open' | 'authorisation_revoked' | 'signin_locked';

export interface AdminAlert {
  kind: AlertKind;
  severity: 'danger' | 'warning';
  /** Null for a platform-wide incident (incident_open only). */
  parkId: string | null;
  parkName: string | null;
  count: number;
  amount: number | null;
  since: string;
  code: string | null;
  detail: string | null;
  link: string;
  /**
   * authorisation_revoked only (Swich and the operator): the park revoked the platform's authorisations (Annex 3 §7)
   * and payouts or shares still wait on its account — payouts to cancel, shares owed outside the platform.
   */
  revocation?: {
    revokedFrom: string;
    payouts: number;
    payoutsNet: number;
    settlements: number;
    owed: number;
    /** The part the licensee answers for (§2.5); the rest is a Swich-direct park's own debt. */
    licenseeOwed: number;
  } | null;
}

interface AlertsResponse {
  generatedAt: string;
  dangerCount: number;
  warningCount: number;
  signature: string;
  alerts: AdminAlert[];
}

const DISMISSED_KEY = 'paytaxi.admin.alerts.dismissed';
const POLL_MS = 60_000;

/**
 * The "something needs a human" feed for the admin console. Polls while an admin is signed in
 * and refreshes on every navigation, so a failed nightly settlement is on screen the moment
 * anyone opens the console. Dismissing hides the banner for THIS set of alerts only: the server
 * signs the set, and a new failure changes the signature and brings the banner back.
 */
@Injectable({ providedIn: 'root' })
export class AdminAlertsService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AdminAuthService);
  private readonly router = inject(Router);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);

  readonly alerts = signal<AdminAlert[]>([]);
  readonly signature = signal<string | null>(null);
  readonly lastError = signal(false);
  private readonly dismissed = signal<string | null>(this.readDismissed());
  private timer: ReturnType<typeof setInterval> | null = null;
  private inFlight = false;

  readonly dangerAlerts = computed(() => this.alerts().filter(a => a.severity === 'danger'));
  readonly warningAlerts = computed(() => this.alerts().filter(a => a.severity === 'warning'));
  readonly dangerCount = computed(() => this.dangerAlerts().reduce((s, a) => s + a.count, 0));
  readonly warningCount = computed(() => this.warningAlerts().reduce((s, a) => s + a.count, 0));

  /** Banner shows when there are alerts and this exact set has not been dismissed. */
  readonly bannerVisible = computed(() =>
    this.alerts().length > 0 && this.signature() !== null && this.dismissed() !== this.signature());

  constructor() {
    if (!isPlatformBrowser(this.platformId)) return;

    // Start/stop with the session.
    effect(() => {
      if (this.auth.isAuthenticated()) this.start();
      else this.stop();
    });

    // Every navigation inside the console is a good moment to re-check.
    this.router.events
      .pipe(filter(e => e instanceof NavigationEnd), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => { if (this.auth.isAuthenticated()) void this.refresh(); });
  }

  /** Alerts whose link points at this console path — for the sidebar badges. */
  countFor(path: string): number {
    const base = path.split('?')[0];
    return this.alerts()
      .filter(a => a.link.split('?')[0] === base)
      .reduce((s, a) => s + a.count, 0);
  }

  severityFor(path: string): 'danger' | 'warning' | null {
    const base = path.split('?')[0];
    const mine = this.alerts().filter(a => a.link.split('?')[0] === base);
    if (mine.some(a => a.severity === 'danger')) return 'danger';
    if (mine.length > 0) return 'warning';
    return null;
  }

  dismiss() {
    const sig = this.signature();
    if (!sig) return;
    this.dismissed.set(sig);
    try { sessionStorage.setItem(DISMISSED_KEY, sig); } catch { /* private mode */ }
  }

  async refresh(): Promise<void> {
    if (this.inFlight) return;
    this.inFlight = true;
    try {
      const res = await firstValueFrom(this.http.get<AlertsResponse>(`${environment.apiBase}/api/admin/alerts`));
      this.alerts.set(res.alerts ?? []);
      this.signature.set(res.signature ?? null);
      this.lastError.set(false);
    } catch {
      // Keep whatever we last knew; a transient API hiccup must not hide a real alert.
      this.lastError.set(true);
    } finally {
      this.inFlight = false;
    }
  }

  private start() {
    if (this.timer) return;
    void this.refresh();
    this.timer = setInterval(() => void this.refresh(), POLL_MS);
  }

  private stop() {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
    this.alerts.set([]);
    this.signature.set(null);
  }

  private readDismissed(): string | null {
    try { return typeof sessionStorage !== 'undefined' ? sessionStorage.getItem(DISMISSED_KEY) : null; }
    catch { return null; }
  }
}
