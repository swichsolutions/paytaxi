import { Injectable, computed, signal } from '@angular/core';
import {
  MOCK_DRIVERS, MOCK_QUEUE, MOCK_ACTIVITY, MOCK_PARK, MOCK_ADMIN_USER, MOCK_HOURLY_VOLUME,
  AdminDriver, QueueCashout, ActivityEvent,
} from '../mock/admin-data';
import { CashoutStatus } from '../../core/mock/data';

const DAY_MS = 24 * 60 * 60 * 1000;
const TODAY_REF = new Date('2026-05-26T18:00:00');

@Injectable({ providedIn: 'root' })
export class AdminMockService {
  // Raw signals
  readonly drivers  = signal<AdminDriver[]>([...MOCK_DRIVERS]);
  readonly queue    = signal<QueueCashout[]>([...MOCK_QUEUE]);
  readonly activity = signal<ActivityEvent[]>([...MOCK_ACTIVITY]);
  readonly park     = signal({ ...MOCK_PARK });
  readonly user     = signal({ ...MOCK_ADMIN_USER });
  readonly hourlyVolume = signal([...MOCK_HOURLY_VOLUME]);

  // ── Helpers ──────────────────────────────────────────────────────
  private isToday(d: Date): boolean {
    return TODAY_REF.getTime() - d.getTime() < DAY_MS;
  }

  // ── Aggregated KPIs ──────────────────────────────────────────────
  readonly kpis = computed(() => {
    const q = this.queue();
    const today = q.filter(c => this.isToday(c.createdAt));
    const todayCompleted = today.filter(c => c.status === 'completed');
    const todayPending   = today.filter(c => c.status === 'pending' || c.status === 'processing');
    const todayFailed    = today.filter(c => c.status === 'failed');

    const completedValue = todayCompleted.reduce((s, c) => s + c.amount, 0);
    const completedFees  = todayCompleted.reduce((s, c) => s + c.fee, 0);
    const pendingValue   = todayPending.reduce((s, c) => s + c.amount, 0);

    const drivers = this.drivers();
    const activeToday = drivers.filter(d => this.isToday(d.lastSeenAt) && d.status === 'active').length;

    return {
      cashoutsToday:      { count: todayCompleted.length, value: completedValue, deltaPct: +12.4 },
      feesCollectedToday: { value: completedFees,           deltaPct:  +8.1 },
      pendingQueue:       { count: todayPending.length, value: pendingValue },
      failedToday:        { count: todayFailed.length },
      activeDriversToday: { count: activeToday, total: drivers.length },
    };
  });

  readonly floatStatus = computed(() => {
    const p = this.park();
    const usedPct = Math.min(100, (p.floatBalance / p.floatTarget) * 100);
    const lowFloat = p.floatBalance < p.floatMinimum * 1.3;
    return {
      balance: p.floatBalance,
      target:  p.floatTarget,
      minimum: p.floatMinimum,
      usedPct,
      lowFloat,
    };
  });

  // ── Cashout queue helpers ────────────────────────────────────────
  cashoutsByStatus(status: CashoutStatus): QueueCashout[] {
    return this.queue().filter(c => c.status === status);
  }

  recentQueue(limit = 10): QueueCashout[] {
    return [...this.queue()]
      .sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime())
      .slice(0, limit);
  }

  retryCashout(id: string) {
    this.queue.update(list =>
      list.map(c => c.id === id ? { ...c, status: 'pending', errorMessage: undefined, updatedAt: new Date() } : c)
    );
  }

  // ── Formatting helpers ───────────────────────────────────────────
  formatGel(amount: number, digits = 2): string {
    return `₾ ${amount.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits })}`;
  }

  formatCount(n: number): string {
    return n.toLocaleString('en-US');
  }

  formatRelTime(d: Date): string {
    const diffSec = Math.max(0, (TODAY_REF.getTime() - d.getTime()) / 1000);
    if (diffSec < 60)      return `${Math.round(diffSec)}s ago`;
    if (diffSec < 3600)    return `${Math.round(diffSec / 60)}m ago`;
    if (diffSec < 86_400)  return `${Math.round(diffSec / 3600)}h ago`;
    return `${Math.round(diffSec / 86_400)}d ago`;
  }

  initials(name: string): string {
    return name.split(' ').slice(0, 2).map(w => w[0]).join('').toUpperCase();
  }
}
