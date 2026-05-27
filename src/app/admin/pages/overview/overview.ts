import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminMockService } from '../../services/admin-mock.service';

@Component({
  selector: 'app-admin-overview',
  imports: [RouterLink],
  templateUrl: './overview.html',
  styleUrl: './overview.scss',
})
export class OverviewComponent {
  svc = inject(AdminMockService);

  kpis        = computed(() => this.svc.kpis());
  floatStatus = computed(() => this.svc.floatStatus());
  hourly      = computed(() => this.svc.hourlyVolume());
  activity    = computed(() => this.svc.activity().slice(0, 8));
  failed      = computed(() => this.svc.cashoutsByStatus('failed'));
  pending     = computed(() => this.svc.cashoutsByStatus('pending'));

  // Bar chart geometry helpers
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

  todayTotal = computed(() =>
    this.hourly().reduce((s, h) => s + h.value, 0)
  );

  retry(id: string) {
    this.svc.retryCashout(id);
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatRel(d: Date)          { return this.svc.formatRelTime(d); }
  initials(name: string)      { return this.svc.initials(name); }
}
