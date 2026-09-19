import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService } from '../../core/services/driver-session.service';
import { ActivityCashout, ActivityRide, DriverActivityService } from '../../core/services/driver-activity.service';

type Filter = 'all' | 'cashouts' | 'rides';

interface TxItem {
  id: string;
  type: 'cashout' | 'ride';
  title: string;
  subtitle: string;
  amount: number;
  date: Date;
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

  filter = signal<Filter>('all');
  cashouts = signal<ActivityCashout[]>([]);
  rides = signal<ActivityRide[]>([]);
  loading = signal(true);
  loadError = signal<string | null>(null);

  async ngOnInit() {
    await this.session.ensureLoaded();
    const [cashouts, rides] = await Promise.allSettled([
      this.activity.listCashouts(50),
      this.activity.listRides(RIDE_DAYS),
    ]);
    if (cashouts.status === 'fulfilled') this.cashouts.set(cashouts.value);
    else this.loadError.set(`Could not load cashouts: ${cashouts.reason?.message ?? cashouts.reason}`);
    // Rides are best-effort: if Yandex is unreachable the cashout history still shows.
    if (rides.status === 'fulfilled') this.rides.set(rides.value);
    this.loading.set(false);
  }

  get t() { return this.svc.t; }

  private allItems = computed<TxItem[]>(() => {
    const cashoutItems: TxItem[] = this.cashouts().map(c => ({
      id: c.id,
      type: 'cashout' as const,
      title: `${c.card.bankType} ${c.card.maskedPan}`,
      subtitle: this.svc.formatDateTime(c.createdAt),
      amount: c.amount,
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
      status: 'completed',
    }));

    return [...cashoutItems, ...rideItems].sort((a, b) => b.date.getTime() - a.date.getTime());
  });

  items = computed(() => {
    const f = this.filter();
    if (f === 'cashouts') return this.allItems().filter(i => i.type === 'cashout');
    if (f === 'rides')    return this.allItems().filter(i => i.type === 'ride');
    return this.allItems();
  });

  formatGel(n: number) { return this.svc.formatGel(n); }

  statusLabel(status: string): string {
    return (this.t as Record<string, string>)[status] ?? status;
  }
}
