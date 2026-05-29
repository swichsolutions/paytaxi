import { Component, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminMockService } from '../../services/admin-mock.service';
import {
  AdminApiService, ApiCashout, ApiKpis, ApiActivityEvent, ApiHourBucket,
} from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';

interface FailedRow {
  id: string;
  driverName: string;
  amount: number;
  errorMessage: string | null;
  bankType: string;
  maskedPan: string;
  createdAt: Date;
}

@Component({
  selector: 'app-admin-overview',
  imports: [RouterLink],
  templateUrl: './overview.html',
  styleUrl: './overview.scss',
})
export class OverviewComponent {
  svc = inject(AdminMockService); // hourly chart + activity feed still mock
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private i18n = inject(AdminI18nService);

  get t() { return this.i18n.t; }

  loading = signal(true);
  loadError = signal<string | null>(null);
  kpisRaw = signal<ApiKpis | null>(null);
  private cashoutsRaw = signal<ApiCashout[]>([]);
  private activityRaw = signal<ApiActivityEvent[]>([]);
  private hourlyRaw = signal<ApiHourBucket[]>([]);

  constructor() {
    this.parkCtx.ensureLoaded();
    effect(() => {
      const parkId = this.parkCtx.currentParkId();
      if (parkId) this.fetchFor(parkId);
    });
  }

  private async fetchFor(parkId: string) {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [kpis, cashouts, activity, hourly] = await Promise.all([
        this.api.getKpis(parkId),
        this.api.listCashouts(parkId, 50),
        this.api.getActivity(parkId, 12),
        this.api.getHourly(parkId),
      ]);
      this.kpisRaw.set(kpis);
      this.cashoutsRaw.set(cashouts.cashouts);
      this.activityRaw.set(activity.events);
      this.hourlyRaw.set(hourly.buckets);
    } catch (err: any) {
      this.loadError.set(`Could not load overview: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }

  // Adapted to keep the existing template happy (it expects the mock-shape).
  // deltaPct/hourly/activity stay 0 / mocked — visible follow-up.
  kpis = computed(() => {
    const k = this.kpisRaw();
    return {
      cashoutsToday:      { count: k?.cashoutsToday.count ?? 0, value: k?.cashoutsToday.value ?? 0, deltaPct: 0 },
      feesCollectedToday: { value: k?.feesToday.value ?? 0,    deltaPct: 0 },
      pendingQueue:       { count: k?.pendingQueue.count ?? 0,  value: k?.pendingQueue.value ?? 0 },
      failedToday:        { count: k?.failedToday.count ?? 0 },
      activeDriversToday: { count: k?.activeDrivers.count ?? 0, total: k?.activeDrivers.total ?? 0 },
    };
  });

  // Float card now shows Model A.5's authorizationLimit (or hidden block for Model A).
  // usedPct = how much of the limit has been spent today.
  floatStatus = computed(() => {
    const k = this.kpisRaw();
    const limit = k?.authorizationLimit ?? null;
    const spentToday = k?.cashoutsToday.value ?? 0;
    return {
      hasLimit: limit !== null,
      balance:  limit ?? 0,                         // remaining authorization
      target:   limit !== null ? limit + spentToday : 0, // starting authorization
      minimum:  limit !== null ? (limit + spentToday) * 0.2 : 0,
      usedPct:  limit !== null && (limit + spentToday) > 0
        ? (spentToday / (limit + spentToday)) * 100
        : 0,
      lowFloat: limit !== null && limit < (k?.pendingQueue.value ?? 0) * 2,
    };
  });

  operatingModel = computed(() => this.kpisRaw()?.park.operatingModel ?? '');

  failed = computed<FailedRow[]>(() =>
    this.cashoutsRaw()
      .filter(c => c.status === 'Failed' || c.status === 'ReviewRequired')
      .slice(0, 8)
      .map(c => ({
        id: c.id,
        driverName: c.driverName ?? '(unnamed)',
        amount: c.amount,
        errorMessage: c.failureReason,
        bankType: c.bankType,
        maskedPan: c.maskedPan,
        createdAt: new Date(c.createdAt),
      }))
  );

  pending = computed(() =>
    this.cashoutsRaw().filter(c => c.status === 'Queued' || c.status === 'Processing')
  );

  // Hourly chart + activity feed now backed by /api/admin/parks/{id}/{hourly,activity}.
  hourly = computed(() => this.hourlyRaw().map(b => ({
    hour: b.hour,
    value: b.value,
    count: b.count,
  })));

  activity = computed(() => this.activityRaw().map(e => ({
    id: e.id,
    at: new Date(e.at),
    type: e.type,
    severity: e.severity,
    message: e.message,
    driverName: e.driverName,
    amount: e.amount ?? undefined,
  })));

  // Bar chart geometry helpers (unchanged)
  readonly chartH = 80;
  readonly chartW = 280;

  maxHourly = computed(() => Math.max(...this.hourly().map(h => h.value), 1));

  barX(i: number): number {
    const count = this.hourly().length;
    const gap = 6;
    const w = (this.chartW - gap * (count - 1)) / count;
    return i * (w + gap);
  }

  barW(): number {
    const count = this.hourly().length;
    const gap = 6;
    return (this.chartW - gap * (count - 1)) / count;
  }

  barH(value: number): number {
    return Math.max(2, (value / this.maxHourly()) * this.chartH);
  }

  todayTotal = computed(() => this.kpisRaw()?.cashoutsToday.value ?? 0);

  retrying = signal<string | null>(null);

  async retry(id: string) {
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.retrying()) return;
    this.retrying.set(id);
    try {
      await this.api.retryCashout(parkId, id);
      await this.fetchFor(parkId);
    } catch (err: any) {
      // 422/202 carry the saga result — those are real outcomes, refetch the list.
      if (err?.error?.cashoutId) {
        await this.fetchFor(parkId);
      } else {
        const msg = err?.error?.message ?? err?.message ?? 'Retry failed';
        this.loadError.set(`Retry failed: ${msg}`);
      }
    } finally {
      this.retrying.set(null);
    }
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatRel(d: Date)          { return this.svc.formatRelTime(d); }
  initials(name: string)      { return this.svc.initials(name); }
}
