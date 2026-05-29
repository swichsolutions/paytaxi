import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminApiService, ApiRecDiscrepancy, ApiRecRun } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminMockService } from '../../services/admin-mock.service';

interface DisplayRun extends ApiRecRun {
  windowFromDate: Date;
  windowToDate: Date;
  startedAtDate: Date;
}

@Component({
  selector: 'app-admin-reconciliation',
  imports: [FormsModule],
  templateUrl: './reconciliation.html',
  styleUrl: './reconciliation.scss',
})
export class ReconciliationComponent {
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  svc = inject(AdminMockService); // formatters

  loading = signal(true);
  loadError = signal<string | null>(null);

  runs = signal<DisplayRun[]>([]);
  discrepancies = signal<ApiRecDiscrepancy[]>([]);
  openCount = signal(0);

  showResolved = signal(false);
  resolving = signal<string | null>(null);
  resolveNotes = signal('');
  openResolveFor = signal<string | null>(null);

  filteredDiscrepancies = computed(() => {
    const list = this.discrepancies();
    return this.showResolved() ? list : list.filter(d => !d.isResolved);
  });

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
      const [runsResp, discResp] = await Promise.all([
        this.api.listRecRuns(parkId, 20),
        this.api.listRecDiscrepancies(parkId, true),
      ]);
      this.runs.set(runsResp.runs.map(r => ({
        ...r,
        windowFromDate: new Date(r.windowFrom),
        windowToDate: new Date(r.windowTo),
        startedAtDate: new Date(r.startedAt),
      })));
      this.discrepancies.set(discResp.discrepancies);
      this.openCount.set(discResp.openCount);
    } catch (err: any) {
      this.loadError.set(`Could not load reconciliation: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }

  async refresh() {
    const parkId = this.parkCtx.currentParkId();
    if (parkId) await this.fetchFor(parkId);
  }

  toggleResolveFor(id: string) {
    if (this.openResolveFor() === id) {
      this.openResolveFor.set(null);
      this.resolveNotes.set('');
    } else {
      this.openResolveFor.set(id);
      this.resolveNotes.set('');
    }
  }

  async submitResolve(id: string) {
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.resolving()) return;
    this.resolving.set(id);
    try {
      await this.api.resolveDiscrepancy(parkId, id, this.resolveNotes());
      this.openResolveFor.set(null);
      this.resolveNotes.set('');
      await this.fetchFor(parkId);
    } catch (err: any) {
      this.loadError.set(`Could not resolve: ${err?.message ?? err}`);
    } finally {
      this.resolving.set(null);
    }
  }

  toneFor(kind: string): 'error' | 'warning' | 'info' {
    if (kind === 'stuck_pending') return 'warning';
    if (kind.startsWith('amount_mismatch')) return 'warning';
    return 'error';
  }

  kindLabel(kind: string): string {
    switch (kind) {
      case 'missing_in_bank':       return 'Missing in bank';
      case 'orphaned_bank_send':    return 'Orphaned bank send';
      case 'missing_in_yandex':     return 'Missing in Yandex';
      case 'orphaned_yandex_debit': return 'Orphaned Yandex debit';
      case 'amount_mismatch_bank':  return 'Bank amount mismatch';
      case 'amount_mismatch_yandex':return 'Yandex amount mismatch';
      case 'stuck_pending':         return 'Stuck pending';
      default:                       return kind;
    }
  }

  formatGel(n: number | null | undefined, d = 2) {
    return n == null ? '—' : this.svc.formatGel(n, d);
  }

  formatTime(d: Date): string {
    return d.toLocaleString('en-GB', {
      year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit',
    });
  }

  asDate(s: string): Date { return new Date(s); }

  formatRel(d: Date) { return this.svc.formatRelTime(d); }
}
