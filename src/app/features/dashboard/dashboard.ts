import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MockDataService } from '../../core/services/mock-data.service';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardComponent {
  readonly svc = inject(MockDataService);

  get t() { return this.svc.t; }
  get driver() { return this.svc.driver(); }
  get recentCashouts() { return this.svc.cashouts().slice(0, 3); }
  get recentRides() { return this.svc.rides().slice(0, 3); }

  formatGel(n: number) { return this.svc.formatGel(n); }
  formatDate(d: Date) { return this.svc.formatDate(d); }
  formatTime(d: Date) { return this.svc.formatTime(d); }
}
