import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { DriverNotificationService, DriverNotification } from '../../core/services/notification.service';
import { MockDataService } from '../../core/services/mock-data.service';

type Filter = 'all' | 'unread';
type Tone = 'success' | 'pending' | 'warning' | 'error' | 'info';

/** A notification with its title/body rendered in the driver's language. */
interface NotificationView {
  n: DriverNotification;
  title: string;
  body: string;
  tone: Tone;
}

@Component({
  selector: 'app-notifications',
  imports: [RouterLink],
  templateUrl: './notifications.html',
  styleUrl: './notifications.scss',
})
export class NotificationsComponent implements OnInit {
  private notifications = inject(DriverNotificationService);
  private router = inject(Router);
  readonly svc = inject(MockDataService); // i18n + formatters

  filter = signal<Filter>('all');

  /**
   * Localised view of the inbox. When the server sent structured `data` we render the title/body
   * ourselves from `type` + payload; old rows without data fall back to the server's English text.
   */
  list = computed<NotificationView[]>(() => {
    this.svc.lang(); // re-render on language switch
    const all = this.notifications.notifications();
    const filtered = this.filter() === 'unread' ? all.filter(n => !n.isRead) : all;
    return filtered.map(n => this.render(n));
  });

  unreadCount = computed(() => this.notifications.unreadCount());

  /** First-load skeleton only — background polls must not blank the list. */
  initialLoading = computed(() =>
    this.notifications.loading() && !this.notifications.loadedOnce() && !this.notifications.loadFailed());

  /** Hard error only when we have nothing to show; otherwise the stale list stays visible. */
  loadError = computed(() =>
    this.notifications.loadFailed() && !this.notifications.loadedOnce());

  async ngOnInit() {
    // Force a fresh fetch on page open so the user sees the latest.
    await this.notifications.refresh();
  }

  async retry() {
    await this.notifications.refresh();
  }

  async open(v: NotificationView) {
    if (!v.n.isRead) await this.notifications.markRead(v.n.id);
    if (v.n.link) this.router.navigateByUrl(v.n.link);
  }

  async markAllRead() {
    await this.notifications.markAllRead();
  }

  setFilter(f: Filter) { this.filter.set(f); }

  toneFor(type: string): Tone {
    switch (type) {
      case 'cashout_completed': return 'success';
      case 'cashout_queued':    return 'pending';
      case 'cashout_failed':    return 'error';
      case 'cashout_review':    return 'warning';
      default:                  return 'info';
    }
  }

  private render(n: DriverNotification): NotificationView {
    const tone = this.toneFor(n.type);
    const p = n.payload;
    if (!p || p.amount === null) {
      return { n, title: n.title, body: n.body, tone };
    }
    const params = { amount: this.svc.formatGel(p.amount), destination: p.destination ?? '—' };
    switch (n.type) {
      case 'cashout_completed':
        return { n, tone, title: this.t.cashoutCompletedTitle, body: this.svc.tr('notifCashoutCompletedBody', params) };
      case 'cashout_queued':
        return { n, tone, title: this.t.cashoutQueuedTitle, body: this.svc.tr('notifCashoutQueuedBody', params) };
      case 'cashout_failed': {
        const base = this.svc.tr('notifCashoutFailedBody', params);
        return { n, tone, title: this.t.cashoutFailedTitle, body: p.reason ? `${base} ${p.reason}` : base };
      }
      case 'cashout_review':
        return { n, tone, title: this.t.cashoutReviewTitle, body: this.svc.tr('notifCashoutReviewBody', params) };
      default:
        return { n, title: n.title, body: n.body, tone };
    }
  }

  get t() { return this.svc.t; }

  readonly skeletonRows = [0, 1, 2];

  formatRel(d: Date) { return this.svc.formatDateTime(d); }
}
