import { Component, OnInit, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService } from '../../core/services/driver-session.service';
import { DriverNotificationService } from '../../core/services/notification.service';
import { Lang } from '../../core/mock/data';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardComponent implements OnInit {
  readonly svc = inject(MockDataService); // i18n + recent rides/cashouts mock for now
  readonly session = inject(DriverSessionService);
  private notifications = inject(DriverNotificationService);

  readonly unreadCount = computed(() => this.notifications.unreadCount());

  // Language switcher in the header: taps cycle EN → ქა → RU. Same signal the profile page uses.
  private static readonly LANGS: Lang[] = ['en', 'ka', 'ru'];
  private static readonly LANG_LABELS: Record<Lang, string> = { en: 'EN', ka: 'ქა', ru: 'RU' };
  readonly langLabel = computed(() => DashboardComponent.LANG_LABELS[this.svc.lang()]);

  cycleLang() {
    const order = DashboardComponent.LANGS;
    const next = order[(order.indexOf(this.svc.lang()) + 1) % order.length];
    this.svc.lang.set(next);
  }

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
