import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { DriverNotificationService, DriverNotification } from '../../core/services/notification.service';
import { MockDataService } from '../../core/services/mock-data.service';

type Filter = 'all' | 'unread';

@Component({
  selector: 'app-notifications',
  imports: [RouterLink],
  templateUrl: './notifications.html',
  styleUrl: './notifications.scss',
})
export class NotificationsComponent implements OnInit {
  private notifications = inject(DriverNotificationService);
  private router = inject(Router);
  readonly svc = inject(MockDataService); // formatters

  filter = signal<Filter>('all');

  list = computed<DriverNotification[]>(() => {
    const all = this.notifications.notifications();
    return this.filter() === 'unread' ? all.filter(n => !n.isRead) : all;
  });

  unreadCount = computed(() => this.notifications.unreadCount());

  async ngOnInit() {
    // Force a fresh fetch on page open so the user sees the latest.
    await this.notifications.refresh();
  }

  async open(n: DriverNotification) {
    if (!n.isRead) await this.notifications.markRead(n.id);
    if (n.link) this.router.navigateByUrl(n.link);
  }

  async markAllRead() {
    await this.notifications.markAllRead();
  }

  setFilter(f: Filter) { this.filter.set(f); }

  iconFor(type: string): 'success' | 'warning' | 'error' | 'info' {
    switch (type) {
      case 'cashout_completed': return 'success';
      case 'cashout_failed':    return 'error';
      case 'cashout_review':    return 'warning';
      default:                   return 'info';
    }
  }

  get t() { return this.svc.t; }

  formatRel(d: Date) { return this.svc.formatDateTime(d); }
}
