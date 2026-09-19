import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

/**
 * Read-side driver data for the dashboard and history pages: the driver's own cashouts
 * (our database) and rides (Yandex Fleet via the backend, cached there for a minute).
 * Shapes are normalised here so both pages render the same rows.
 */
@Injectable({ providedIn: 'root' })
export class DriverActivityService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/driver/me`;

  async listCashouts(take = 50): Promise<ActivityCashout[]> {
    const res = await firstValueFrom(this.http.get<{ cashouts: ApiCashout[] }>(`${this.base}/cashouts?take=${take}`));
    return res.cashouts.map(c => ({
      id: c.id,
      amount: c.amount,
      fee: c.fee,
      net: c.amount - c.fee,
      status: c.status.toLowerCase(),
      bankTransferId: c.bankTransferId,
      createdAt: new Date(c.createdAt),
      card: { bankType: c.bankType, maskedPan: c.maskedPan },
    }));
  }

  async listRides(days = 14): Promise<ActivityRide[]> {
    const res = await firstValueFrom(this.http.get<{ rides: ApiRide[] }>(`${this.base}/rides?days=${days}`));
    return res.rides.map(r => ({
      id: r.orderId,
      amount: r.amount,
      from: r.from ?? '—',
      to: r.to ?? '—',
      date: new Date(r.createdAt),
    }));
  }
}

export interface ActivityCashout {
  id: string;
  amount: number;
  fee: number;
  net: number;
  status: string;            // completed | processing | queued | failed | reviewrequired
  bankTransferId: string | null;
  createdAt: Date;
  card: { bankType: string; maskedPan: string };
}

export interface ActivityRide {
  id: string;
  amount: number;
  from: string;
  to: string;
  date: Date;
}

interface ApiCashout {
  id: string;
  amount: number;
  fee: number;
  status: string;
  bankTransferId: string | null;
  createdAt: string;
  bankType: string;
  maskedPan: string;
}

interface ApiRide {
  orderId: string;
  amount: number;
  from: string | null;
  to: string | null;
  createdAt: string;
}
