import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminApiService, ApiReport } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminMockService } from '../../services/admin-mock.service';

type Preset = 'today' | 'week' | 'month' | '30d' | 'custom';

@Component({
  selector: 'app-admin-reports',
  imports: [FormsModule],
  templateUrl: './reports.html',
  styleUrl: './reports.scss',
})
export class ReportsComponent {
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  svc = inject(AdminMockService); // formatGel

  preset = signal<Preset>('week');
  customFrom = signal<string>(this.daysAgoIso(7));
  customTo = signal<string>(this.todayIso());

  loading = signal(false);
  loadError = signal<string | null>(null);
  report = signal<ApiReport | null>(null);

  readonly chartMax = computed(() => {
    const r = this.report();
    if (!r) return 1;
    return Math.max(1, ...r.daily.map(d => d.value));
  });

  constructor() {
    this.parkCtx.ensureLoaded();
    effect(() => {
      const parkId = this.parkCtx.currentParkId();
      if (!parkId) return;
      const { from, to } = this.currentWindow();
      void this.fetchFor(parkId, from, to);
    });
  }

  private currentWindow(): { from: Date; to: Date } {
    const now = new Date();
    const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    switch (this.preset()) {
      case 'today':
        return { from: startOfToday, to: now };
      case 'week': {
        const from = new Date(startOfToday);
        from.setDate(from.getDate() - 7);
        return { from, to: now };
      }
      case 'month': {
        const from = new Date(now.getFullYear(), now.getMonth(), 1);
        return { from, to: now };
      }
      case '30d': {
        const from = new Date(startOfToday);
        from.setDate(from.getDate() - 30);
        return { from, to: now };
      }
      case 'custom': {
        // Local-date strings; treat as start-of-day in local zone.
        const [yF, mF, dF] = this.customFrom().split('-').map(Number);
        const [yT, mT, dT] = this.customTo().split('-').map(Number);
        return {
          from: new Date(yF, (mF ?? 1) - 1, dF ?? 1),
          to:   new Date(yT, (mT ?? 1) - 1, (dT ?? 1) + 1), // include the end day
        };
      }
    }
  }

  private async fetchFor(parkId: string, from: Date, to: Date) {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const r = await this.api.getReport(parkId, from.toISOString(), to.toISOString(), 10);
      this.report.set(r);
    } catch (err: any) {
      this.loadError.set(`Could not load report: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }

  setPreset(p: Preset) { this.preset.set(p); }

  refresh() {
    const parkId = this.parkCtx.currentParkId();
    if (parkId) {
      const { from, to } = this.currentWindow();
      void this.fetchFor(parkId, from, to);
    }
  }

  exportCsv() {
    const r = this.report();
    if (!r) return;
    const header = ['Date', 'Cashouts', 'Value (GEL)', 'Fees (GEL)', 'Failed'];
    const rows = r.daily.map(d => [
      d.date.slice(0, 10),
      d.cashoutsCount,
      d.value.toFixed(2),
      d.fees.toFixed(2),
      d.failedCount,
    ]);
    const csv = [header, ...rows]
      .map(row => row.map(cell => this.csvEscape(String(cell))).join(','))
      .join('\r\n');

    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    const park = this.parkCtx.currentPark();
    a.href = url;
    a.download = `paytaxi-${park?.slug ?? 'park'}-${r.windowFrom.slice(0, 10)}_to_${r.windowTo.slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }

  private csvEscape(s: string): string {
    if (s.includes('"') || s.includes(',') || s.includes('\n')) {
      return `"${s.replace(/"/g, '""')}"`;
    }
    return s;
  }

  private todayIso(): string {
    const d = new Date();
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }
  private daysAgoIso(days: number): string {
    const d = new Date();
    d.setDate(d.getDate() - days);
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-GB', { month: 'short', day: '2-digit', weekday: 'short' });
  }
  formatRange(): string {
    const r = this.report();
    if (!r) return '';
    const from = new Date(r.windowFrom).toLocaleDateString('en-GB');
    const to   = new Date(r.windowTo).toLocaleDateString('en-GB');
    return `${from} → ${to}`;
  }

  barWidth(value: number): string {
    return ((value / this.chartMax()) * 100).toFixed(1) + '%';
  }

  initials(name: string | null): string {
    if (!name) return '??';
    return name.split(' ').slice(0, 2).map(w => w[0] ?? '').join('').toUpperCase();
  }
}

function pad(n: number): string { return n < 10 ? '0' + n : String(n); }
