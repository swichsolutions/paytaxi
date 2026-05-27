import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminMockService } from '../../services/admin-mock.service';
import { QueueCashout } from '../../mock/admin-data';
import { CashoutStatus } from '../../../core/mock/data';
import { ManualCashoutComponent } from './manual-cashout/manual-cashout';

type StatusTab = 'all' | CashoutStatus;
type DateRange = 'today' | 'yesterday' | 'week' | 'all';

@Component({
  selector: 'app-admin-cashouts',
  imports: [FormsModule, ManualCashoutComponent],
  templateUrl: './cashouts.html',
  styleUrl: './cashouts.scss',
})
export class CashoutsComponent {
  svc = inject(AdminMockService);

  search    = signal('');
  status    = signal<StatusTab>('all');
  range     = signal<DateRange>('today');
  expanded  = signal<string | null>(null);
  showManualModal = signal(false);

  private readonly TODAY_REF = new Date('2026-05-26T18:00:00').getTime();

  private inRange(d: Date): boolean {
    const ms = this.TODAY_REF - d.getTime();
    switch (this.range()) {
      case 'today':     return ms < 24 * 3600 * 1000;
      case 'yesterday': return ms >= 24 * 3600 * 1000 && ms < 48 * 3600 * 1000;
      case 'week':      return ms < 7 * 24 * 3600 * 1000;
      default:          return true;
    }
  }

  all = computed(() => this.svc.queue());

  filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    const status = this.status();
    let list = this.all().filter(c => this.inRange(c.createdAt));
    if (status !== 'all') list = list.filter(c => c.status === status);
    if (term) {
      list = list.filter(c =>
        c.driverName.toLowerCase().includes(term) ||
        c.id.toLowerCase().includes(term) ||
        c.maskedPan.toLowerCase().includes(term)
      );
    }
    return list.sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime());
  });

  counts = computed(() => {
    const inRange = this.all().filter(c => this.inRange(c.createdAt));
    return {
      all:        inRange.length,
      pending:    inRange.filter(c => c.status === 'pending').length,
      processing: inRange.filter(c => c.status === 'processing').length,
      completed:  inRange.filter(c => c.status === 'completed').length,
      failed:     inRange.filter(c => c.status === 'failed').length,
    };
  });

  summary = computed(() => {
    const list = this.filtered();
    const value = list.reduce((s, c) => s + c.amount, 0);
    const fees  = list.reduce((s, c) => s + c.fee, 0);
    return { count: list.length, value, fees };
  });

  toggleExpand(id: string) {
    this.expanded.update(v => v === id ? null : id);
  }

  retry(id: string, e: Event) {
    e.stopPropagation();
    this.svc.retryCashout(id);
  }

  openManual() {
    this.showManualModal.set(true);
  }

  closeManual() {
    this.showManualModal.set(false);
  }

  setStatus(s: StatusTab) { this.status.set(s); }
  setRange(r: DateRange)  { this.range.set(r); }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatRel(d: Date)          { return this.svc.formatRelTime(d); }
  initials(n: string)         { return this.svc.initials(n); }

  formatTime(d: Date): string {
    return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }
}
