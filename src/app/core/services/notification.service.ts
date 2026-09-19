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
  /** True until the first successful fetch of this session. */
  readonly loadedOnce = signal(false);
  /** The most recent fetch failed (translate in the UI; no server text here). */
  readonly loadFailed = signal(false);

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
        data: n.data ?? null,
        payload: parsePayload(n.data),
        createdAt: new Date(n.createdAt),
      })));
      this.unreadCount.set(resp.unreadCount);
      this.loadFailed.set(false);
      this.loadedOnce.set(true);
    } catch {
      this.loadFailed.set(true);
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
    this.loadedOnce.set(false);
    this.loadFailed.set(false);
  }
}

/** Parse the server's `data` JSON once; null for old rows / malformed payloads. */
function parsePayload(raw: string | null | undefined): NotificationPayload | null {
  if (!raw) return null;
  try {
    const obj = JSON.parse(raw);
    if (!obj || typeof obj !== 'object') return null;
    return {
      amount: typeof obj.amount === 'number' ? obj.amount : null,
      destination: typeof obj.destination === 'string' ? obj.destination : null,
      reason: typeof obj.reason === 'string' && obj.reason.trim() !== '' ? obj.reason.trim() : null,
    };
  } catch {
    return null;
  }
}

/** Structured fields behind a notification (amount is net for completed/queued, gross for failed/review). */
export interface NotificationPayload {
  amount: number | null;
  destination: string | null;
  reason: string | null;
}

export interface DriverNotification {
  id: string;
  /** cashout_completed | cashout_queued | cashout_failed | cashout_review | … */
  type: string;
  /** Server-rendered English fallbacks — used only when `payload` is null (old rows). */
  title: string;
  body: string;
  link: string | null;
  isRead: boolean;
  createdAt: Date;
  /** Raw JSON string from the server, kept for debugging; `payload` is the parsed form. */
  data: string | null;
  payload: NotificationPayload | null;
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
    data?: string | null;
  }>;
}
