import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService } from '../../core/services/driver-session.service';
import { Ride } from '../../core/mock/data';
import { environment } from '../../../environments/environment';

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

interface ApiCashout {
  id: string;
  driverId: string;
  amount: number;
  fee: number;
  status: string;
  bankTransferId: string | null;
  createdAt: string;
}

interface ApiCashoutsResponse {
  cashouts: ApiCashout[];
}

@Component({
  selector: 'app-history',
  templateUrl: './history.html',
  styleUrl: './history.scss',
})
export class HistoryComponent implements OnInit {
  readonly svc = inject(MockDataService); // i18n + rides mock; no rides endpoint yet
  readonly session = inject(DriverSessionService);
  private http = inject(HttpClient);

  filter = signal<Filter>('all');
  cashouts = signal<ApiCashout[]>([]);
  loading = signal(true);
  loadError = signal<string | null>(null);

  async ngOnInit() {
    await this.session.ensureLoaded();
    try {
      const resp = await firstValueFrom(this.http.get<ApiCashoutsResponse>(
        `${environment.apiBase}/api/driver/me/cashouts?take=50`));
      this.cashouts.set(resp.cashouts);
    } catch (err: any) {
      this.loadError.set(`Could not load cashouts: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }

  get t() { return this.svc.t; }

  private allItems = computed<TxItem[]>(() => {
    const driverId = this.session.driver()?.id;
    const cashoutItems: TxItem[] = this.cashouts()
      .filter(c => !driverId || c.driverId === driverId)
      .map(c => {
        const created = new Date(c.createdAt);
        return {
          id: c.id,
          type: 'cashout' as const,
          title: `Cashout · ${c.bankTransferId ?? '—'}`,
          subtitle: this.svc.formatDateTime(created),
          amount: c.amount,
          date: created,
          status: c.status.toLowerCase(),
        };
      });

    const rideItems: TxItem[] = this.svc.rides().map((r: Ride) => ({
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
