import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';
import { environment } from '../../../environments/environment';

/**
 * Driver in-app notification client. Polls /api/driver/me/notifications
 * on a coarse interval (default 25s — well clear of any sane rate limit)
 * and exposes the inbox + unread count as reactive signals.
 *
 * Real-time (SSE/WebSocket) push is a future improvement; polling is
 * fine for the demo and means one fewer moving part.
 */
@Injectable({ providedIn: 'root' })
export class DriverNotificationService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly base = `${environment.apiBase}/api/driver/me/notifications`;
  private readonly pollIntervalMs = 25_000;

  readonly notifications = signal<DriverNotification[]>([]);
  readonly unreadCount = signal(0);
  readonly loading = signal(false);
  readonly lastError = signal<string | null>(null);

  private pollHandle: ReturnType<typeof setInterval> | null = null;

  startPolling(): void {
    if (this.pollHandle) return;
    void this.refresh();
    this.pollHandle = setInterval(() => void this.refresh(), this.pollIntervalMs);
  }

  stopPolling(): void {
    if (this.pollHandle) {
      clearInterval(this.pollHandle);
      this.pollHandle = null;
    }
  }

  async refresh(): Promise<void> {
    if (!this.auth.token()) return;
    this.loading.set(true);
    try {
      const resp = await firstValueFrom(this.http.get<ApiNotificationsResponse>(`${this.base}?take=30`));
      this.notifications.set(resp.notifications.map(n => ({
        ...n,
        createdAt: new Date(n.createdAt),
      })));
      this.unreadCount.set(resp.unreadCount);
      this.lastError.set(null);
    } catch (err: any) {
      this.lastError.set(err?.message ?? 'Failed to load notifications');
    } finally {
      this.loading.set(false);
    }
  }

  async markRead(id: string): Promise<void> {
    try {
      await firstValueFrom(this.http.post(`${this.base}/${id}/read`, {}));
      // Optimistic update so the badge changes immediately.
      this.notifications.update(list =>
        list.map(n => n.id === id ? { ...n, isRead: true } : n));
      this.unreadCount.update(c => Math.max(0, c - 1));
    } catch {
      // Server is the source of truth — refresh.
      await this.refresh();
    }
  }

  async markAllRead(): Promise<void> {
    try {
      await firstValueFrom(this.http.post(`${this.base}/read-all`, {}));
      this.notifications.update(list => list.map(n => ({ ...n, isRead: true })));
      this.unreadCount.set(0);
    } catch {
      await this.refresh();
    }
  }

  clear(): void {
    this.notifications.set([]);
    this.unreadCount.set(0);
  }
}

export interface DriverNotification {
  id: string;
  type: string;
  title: string;
  body: string;
  link: string | null;
  isRead: boolean;
  createdAt: Date;
}

interface ApiNotificationsResponse {
  count: number;
  unreadCount: number;
  notifications: Array<{
    id: string;
    type: string;
    title: string;
    body: string;
    link: string | null;
    isRead: boolean;
    createdAt: string;
  }>;
}
