import { Component, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminMockService } from '../../services/admin-mock.service';
import {
  AdminApiService, ApiCashout, ApiKpis, ApiActivityEvent, ApiHourBucket, ApiBankAccount,
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
  accounts = signal<ApiBankAccount[]>([]);

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
      const [kpis, cashouts, activity, hourly, accounts] = await Promise.all([
        this.api.getKpis(parkId),
        this.api.listCashouts(parkId, 50),
        this.api.getActivity(parkId, 12),
        this.api.getHourly(parkId),
        this.api.listBankAccounts(parkId).catch(() => ({ parkId, count: 0, accounts: [] as ApiBankAccount[] })),
      ]);
      this.kpisRaw.set(kpis);
      this.cashoutsRaw.set(cashouts.cashouts);
      this.activityRaw.set(activity.events);
      this.hourlyRaw.set(hourly.buckets);
      this.accounts.set(accounts.accounts);
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

  // Model A: the park pays from its own bank account(s). The float card lists the
  // park's payout accounts with a live balance where the rail exposes one, plus
  // the payouts currently queued behind the bank.
  activeAccounts = computed(() => this.accounts().filter(a => a.isActive));

  knownBalance = computed(() => {
    const withBalance = this.activeAccounts().filter(a => typeof a.balance === 'number');
    return withBalance.length === 0 ? null : withBalance.reduce((s, a) => s + (a.balance ?? 0), 0);
  });

  queuedRows = computed(() =>
    this.cashoutsRaw()
      .filter(c => c.status === 'Queued')
      .sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime())
      .slice(0, 6)
      .map(c => ({
        id: c.id,
        driverName: c.driverName ?? '(unnamed)',
        net: c.amount - c.fee,
        attemptCount: c.attemptCount ?? 0,
        nextAttemptAt: c.nextAttemptAt ? new Date(c.nextAttemptAt) : null,
        lastError: c.failureReason,
        createdAt: new Date(c.createdAt),
      })));

  queuedTotal = computed(() => this.kpisRaw()?.queued ?? { count: 0, value: 0, oldestAt: null });

  lowFloat = computed(() => {
    const bal = this.knownBalance();
    const q = this.queuedTotal().value;
    return bal !== null && q > 0 && bal < q * 2;
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
  formatTime(d: Date)         { return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }); }
  initials(name: string)      { return this.svc.initials(name); }
}
