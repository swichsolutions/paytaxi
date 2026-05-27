import { Component, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService } from '../../core/services/driver-session.service';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardComponent implements OnInit {
  readonly svc = inject(MockDataService); // i18n + recent rides/cashouts mock for now
  readonly session = inject(DriverSessionService);

  async ngOnInit() {
    await this.session.ensureLoaded();
  }

  get t() { return this.svc.t; }
  get recentCashouts() { return this.svc.cashouts().slice(0, 3); }
  get recentRides() { return this.svc.rides().slice(0, 3); }

  // Real driver context from the backend session; falls back to mock while loading.
  // Getter (not computed signal) because the template uses `driver.field`, not `driver().field`.
  get driver() {
    const d = this.session.driver();
    return d
      ? { name: d.name, parkName: this.session.parkName() ?? '', balance: d.balance }
      : { name: '…', parkName: '', balance: 0 };
  }

  formatGel(n: number) { return this.svc.formatGel(n); }
  formatDate(d: Date) { return this.svc.formatDate(d); }
  formatTime(d: Date) { return this.svc.formatTime(d); }
}
