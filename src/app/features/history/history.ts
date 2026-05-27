import { Component, inject, signal, computed } from '@angular/core';
import { MockDataService } from '../../core/services/mock-data.service';
import { Cashout, Ride } from '../../core/mock/data';

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

@Component({
  selector: 'app-history',
  templateUrl: './history.html',
  styleUrl: './history.scss',
})
export class HistoryComponent {
  readonly svc = inject(MockDataService);
  filter = signal<Filter>('all');

  get t() { return this.svc.t; }

  private allItems = computed<TxItem[]>(() => {
    const cashouts: TxItem[] = this.svc.cashouts().map((c: Cashout) => ({
      id: c.id, type: 'cashout',
      title: `Cashout · ${c.card.bankType} ${c.card.maskedPan}`,
      subtitle: this.svc.formatDateTime(c.createdAt),
      amount: c.amount,
      date: c.createdAt,
      status: c.status,
    }));
    const rides: TxItem[] = this.svc.rides().map((r: Ride) => ({
      id: r.id, type: 'ride',
      title: `${r.from} → ${r.to}`,
      subtitle: this.svc.formatDateTime(r.date),
      amount: r.amount,
      date: r.date,
      status: 'completed',
    }));
    return [...cashouts, ...rides].sort((a, b) => b.date.getTime() - a.date.getTime());
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
