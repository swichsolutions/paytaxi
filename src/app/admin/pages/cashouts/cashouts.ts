import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService, ApiCashout } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { CashoutStatus } from '../../../core/mock/data';
import { ManualCashoutComponent } from './manual-cashout/manual-cashout';

type StatusTab = 'all' | CashoutStatus;
type DateRange = 'today' | 'yesterday' | 'week' | 'all';

interface DisplayCashout {
  id: string;
  driverId: string;
  driverName: string;
  amount: number;
  fee: number;
  net: number;
  status: CashoutStatus;
  bankTransferId: string | null;
  yandexTransactionId: string | null;
  errorMessage: string | null;
  initiatedBy: 'driver' | 'manager';
  bankType: string;
  maskedPan: string;
  createdAt: Date;
}

@Component({
  selector: 'app-admin-cashouts',
  imports: [FormsModule, ManualCashoutComponent],
  templateUrl: './cashouts.html',
  styleUrl: './cashouts.scss',
})
export class CashoutsComponent {
  svc = inject(AdminMockService); // kept for formatGel/formatRel/initials utilities
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);

  search    = signal('');
  status    = signal<StatusTab>('all');
  range     = signal<DateRange>('all'); // backend returns recent rows; let server decide
  expanded  = signal<string | null>(null);
  showManualModal = signal(false);

  loading = signal(true);
  loadError = signal<string | null>(null);
  raw = signal<ApiCashout[]>([]);

  private readonly TODAY_REF = Date.now();

  constructor() {
    // Lazy-init the park context, then re-fetch whenever the selected park changes.
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
      const resp = await this.api.listCashouts(parkId, 200);
      this.raw.set(resp.cashouts);
    } catch (err: any) {
      this.loadError.set(`Could not load cashouts: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }

  private inRange(d: Date): boolean {
    const ms = this.TODAY_REF - d.getTime();
    switch (this.range()) {
      case 'today':     return ms < 24 * 3600 * 1000;
      case 'yesterday': return ms >= 24 * 3600 * 1000 && ms < 48 * 3600 * 1000;
      case 'week':      return ms < 7 * 24 * 3600 * 1000;
      default:          return true;
    }
  }

  /** Map backend's PascalCase status to the lowercase CashoutStatus used by the UI. */
  private mapStatus(s: string): CashoutStatus {
    const lower = s.toLowerCase();
    if (lower === 'completed') return 'completed';
    if (lower === 'failed' || lower === 'reviewrequired') return 'failed';
    if (lower === 'processing') return 'processing';
    return 'pending';
  }

  all = computed<DisplayCashout[]>(() => this.raw().map(c => ({
    id: c.id,
    driverId: c.driverId,
    driverName: c.driverName ?? '(unnamed)',
    amount: c.amount,
    fee: c.fee,
    net: c.amount - c.fee,
    status: this.mapStatus(c.status),
    bankTransferId: c.bankTransferId,
    yandexTransactionId: c.yandexTransactionId,
    errorMessage: c.failureReason,
    initiatedBy: 'manager', // backend doesn't track this yet — TODO when InitiatedBy is exposed
    bankType: c.bankType,
    maskedPan: c.maskedPan,
    createdAt: new Date(c.createdAt),
  })));

  filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    const status = this.status();
    let list = this.all().filter(c => this.inRange(c.createdAt));
    if (status !== 'all') list = list.filter(c => c.status === status);
    if (term) {
      list = list.filter(c =>
        c.driverName.toLowerCase().includes(term) ||
        c.id.toLowerCase().includes(term)
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

  retrying = signal<string | null>(null);
  retryMessage = signal<string | null>(null);

  async retry(id: string, e: Event) {
    e.stopPropagation();
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.retrying()) return;
    this.retrying.set(id);
    this.retryMessage.set(null);
    try {
      const result = await this.api.retryCashout(parkId, id);
      this.retryMessage.set(this.describeRetry(result));
      await this.fetchFor(parkId);
    } catch (err: any) {
      // The saga returns the result body even on 422 (Failed) / 202 (ReviewRequired)
      // — those are legitimate outcomes, not transport errors. Surface them
      // the same way as success.
      const sagaResult = err?.error;
      if (sagaResult && typeof sagaResult.status === 'string' && sagaResult.cashoutId) {
        this.retryMessage.set(this.describeRetry(sagaResult));
        await this.fetchFor(parkId);
      } else {
        const msg = err?.error?.message ?? err?.error?.error ?? err?.message ?? 'Retry failed';
        this.retryMessage.set(`Retry failed: ${msg}`);
      }
    } finally {
      this.retrying.set(null);
    }
  }

  private describeRetry(r: { status: string; cashoutId: string; failureReason: string | null }): string {
    const short = r.cashoutId.slice(0, 8);
    if (r.status === 'Completed')      return `Retry succeeded — new cashout ${short}…`;
    if (r.status === 'ReviewRequired') return `Retry needs review — new cashout ${short}…`;
    if (r.status === 'Failed')         return `Retry failed again: ${r.failureReason ?? 'no reason'}`;
    return `Retry returned ${r.status}`;
  }

  openManual()  { this.showManualModal.set(true); }
  closeManual() {
    this.showManualModal.set(false);
    // Refresh the list so the new cashout appears.
    const parkId = this.parkCtx.currentParkId();
    if (parkId) this.fetchFor(parkId);
  }

  setStatus(s: StatusTab) { this.status.set(s); }
  setRange(r: DateRange)  { this.range.set(r); }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatRel(d: Date)          { return this.svc.formatRelTime(d); }
  initials(n: string)         { return this.svc.initials(n); }

  /**
   * "27 May, 17:08" for older rows, or "Today · 17:08" / "Yesterday · 17:08"
   * for recent ones. The relative "X ago" stays as the subline.
   */
  formatTime(d: Date): string {
    const time = d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    const now = new Date();
    const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
    const dayMs = 24 * 3600 * 1000;
    const startOfThis = new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
    const daysAgo = Math.round((startOfToday - startOfThis) / dayMs);
    if (daysAgo === 0) return `Today · ${time}`;
    if (daysAgo === 1) return `Yesterday · ${time}`;
    return `${d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short' })}, ${time}`;
  }
}
