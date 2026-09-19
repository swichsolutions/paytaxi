import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService } from '../../core/services/driver-session.service';
import { DriverNotificationService } from '../../core/services/notification.service';
import { ActivityCashout, ActivityRide, DriverActivityService } from '../../core/services/driver-activity.service';
import { Lang } from '../../core/mock/data';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardComponent implements OnInit {
  readonly svc = inject(MockDataService); // i18n + formatters
  readonly session = inject(DriverSessionService);
  private notifications = inject(DriverNotificationService);
  private activity = inject(DriverActivityService);

  readonly unreadCount = computed(() => this.notifications.unreadCount());

  // Real data: our own cashouts + the driver's recent Yandex rides (last 7 days, 3 shown).
  private cashouts = signal<ActivityCashout[]>([]);
  private rides = signal<ActivityRide[]>([]);

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
    const [cashouts, rides] = await Promise.allSettled([
      this.activity.listCashouts(3),
      this.activity.listRides(7),
    ]);
    if (cashouts.status === 'fulfilled') this.cashouts.set(cashouts.value);
    if (rides.status === 'fulfilled') this.rides.set(rides.value.slice(0, 3));
  }

  get t() { return this.svc.t; }
  get recentCashouts() { return this.cashouts(); }
  get recentRides() { return this.rides(); }

  // Real driver context from the backend session; falls back to placeholders while loading.
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
