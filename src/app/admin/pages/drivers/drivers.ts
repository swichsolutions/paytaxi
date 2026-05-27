import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminDriver, DriverStatus } from '../../mock/admin-data';
import { QueueCashout } from '../../mock/admin-data';

type StatusFilter = 'all' | DriverStatus;
type SortKey = 'name' | 'balance' | 'lastSeen' | 'cashedOut';

@Component({
  selector: 'app-admin-drivers',
  imports: [FormsModule],
  templateUrl: './drivers.html',
  styleUrl: './drivers.scss',
})
export class DriversComponent {
  svc = inject(AdminMockService);

  search       = signal('');
  status       = signal<StatusFilter>('all');
  sortKey      = signal<SortKey>('balance');
  sortDir      = signal<'asc' | 'desc'>('desc');
  selectedId   = signal<string | null>(null);

  drivers = computed(() => this.svc.drivers());

  filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    const status = this.status();
    let list = this.drivers().slice();
    if (status !== 'all') list = list.filter(d => d.status === status);
    if (term) {
      list = list.filter(d =>
        d.name.toLowerCase().includes(term) ||
        d.phone.toLowerCase().includes(term) ||
        d.carPlate.toLowerCase().includes(term)
      );
    }
    const key = this.sortKey();
    const dir = this.sortDir() === 'asc' ? 1 : -1;
    list.sort((a, b) => {
      switch (key) {
        case 'name':       return a.name.localeCompare(b.name) * dir;
        case 'balance':    return (a.balance - b.balance) * dir;
        case 'cashedOut':  return (a.totalCashedOutMonth - b.totalCashedOutMonth) * dir;
        case 'lastSeen':   return (a.lastSeenAt.getTime() - b.lastSeenAt.getTime()) * dir;
      }
    });
    return list;
  });

  counts = computed(() => {
    const all = this.drivers();
    return {
      all:       all.length,
      active:    all.filter(d => d.status === 'active').length,
      inactive:  all.filter(d => d.status === 'inactive').length,
      suspended: all.filter(d => d.status === 'suspended').length,
    };
  });

  selected = computed<AdminDriver | null>(() => {
    const id = this.selectedId();
    return id ? this.drivers().find(d => d.id === id) ?? null : null;
  });

  selectedCashouts = computed<QueueCashout[]>(() => {
    const id = this.selectedId();
    if (!id) return [];
    return this.svc.queue()
      .filter(c => c.driverId === id)
      .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime())
      .slice(0, 6);
  });

  sumThisMonth = computed(() =>
    this.filtered().reduce((s, d) => s + d.totalCashedOutMonth, 0)
  );

  setSort(key: SortKey) {
    if (this.sortKey() === key) {
      this.sortDir.update(d => d === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortKey.set(key);
      this.sortDir.set(key === 'name' ? 'asc' : 'desc');
    }
  }

  setStatus(s: StatusFilter) {
    this.status.set(s);
  }

  open(id: string) {
    this.selectedId.set(id);
  }

  closeDetail() {
    this.selectedId.set(null);
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatRel(d: Date)          { return this.svc.formatRelTime(d); }
  initials(n: string)         { return this.svc.initials(n); }
}
