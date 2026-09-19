import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService } from '../../core/services/driver-session.service';
import { ActivityCashout, ActivityRide, DriverActivityService } from '../../core/services/driver-activity.service';

type Filter = 'all' | 'cashouts' | 'rides';

interface TxItem {
  id: string;
  type: 'cashout' | 'ride';
  title: string;
  subtitle: string;
  /** Net for cashouts (what the driver actually receives), gross for rides. */
  amount: number;
  date: Date;
  /** Cashouts only — rides have no lifecycle to show. */
  status?: string;
}

/** Rides come from Yandex; keep the window short so the park's API budget isn't spent on history scrolling. */
const RIDE_DAYS = 14;

@Component({
  selector: 'app-history',
  templateUrl: './history.html',
  styleUrl: './history.scss',
})
export class HistoryComponent implements OnInit {
  readonly svc = inject(MockDataService); // i18n + formatters
  readonly session = inject(DriverSessionService);
  private activity = inject(DriverActivityService);
  private route = inject(ActivatedRoute);
  private destroyRef = inject(DestroyRef);

  filter = signal<Filter>('all');
  cashouts = signal<ActivityCashout[]>([]);
  rides = signal<ActivityRide[]>([]);
  loading = signal(true);
  /** Cashouts (our DB) failed to load — the page is not usable without them. */
  loadError = signal(false);
  /** Rides (Yandex) failed — best effort, shown as a soft notice. */
  ridesError = signal(false);

  constructor() {
    // Deep link from the dashboard: /history?tab=rides|cashouts
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(q => {
      const tab = q.get('tab');
      if (tab === 'rides' || tab === 'cashouts' || tab === 'all') this.filter.set(tab);
    });
  }

  async ngOnInit() {
    await this.session.ensureLoaded();
    await this.load();
  }

  async load() {
    this.loading.set(true);
    this.loadError.set(false);
    this.ridesError.set(false);
    const [cashouts, rides] = await Promise.allSettled([
      this.activity.listCashouts(50),
      this.activity.listRides(RIDE_DAYS),
    ]);
    if (cashouts.status === 'fulfilled') this.cashouts.set(cashouts.value);
    else this.loadError.set(true);
    // Rides are best-effort: if Yandex is unreachable the cashout history still shows.
    if (rides.status === 'fulfilled') this.rides.set(rides.value);
    else this.ridesError.set(true);
    this.loading.set(false);
  }

  get t() { return this.svc.t; }

  private allItems = computed<TxItem[]>(() => {
    // Read the language so subtitles re-format when the driver switches it.
    this.svc.lang();

    const cashoutItems: TxItem[] = this.cashouts().map(c => ({
      id: c.id,
      type: 'cashout' as const,
      title: `${c.card.bankType} ${c.card.maskedPan}`,
      subtitle: `${this.svc.formatDateTime(c.createdAt)} · ${this.t.fee} ${this.svc.formatGel(c.fee)}`,
      amount: c.net,
      date: c.createdAt,
      status: c.status,
    }));

    const rideItems: TxItem[] = this.rides().map(r => ({
      id: r.id,
      type: 'ride' as const,
      title: `${r.from} → ${r.to}`,
      subtitle: this.svc.formatDateTime(r.date),
      amount: r.amount,
      date: r.date,
    }));

    return [...cashoutItems, ...rideItems].sort((a, b) => b.date.getTime() - a.date.getTime());
  });

  items = computed(() => {
    const f = this.filter();
    if (f === 'cashouts') return this.allItems().filter(i => i.type === 'cashout');
    if (f === 'rides')    return this.allItems().filter(i => i.type === 'ride');
    return this.allItems();
  });

  /** Soft notice when the rides tab (or All) is missing Yandex data. */
  showRidesNotice = computed(() => this.ridesError() && !this.loading() && this.filter() !== 'cashouts');

  readonly skeletonRows = [0, 1, 2, 3, 4];

  formatGel(n: number) { return this.svc.formatGel(n); }

  statusLabel(status: string): string {
    return (this.t as Record<string, string>)[status] ?? status;
  }
}
