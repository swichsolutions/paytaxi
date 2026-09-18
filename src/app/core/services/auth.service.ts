import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const TOKEN_KEY = 'paytaxi.driver.token';
const SESSION_KEY = 'paytaxi.driver.session';

/**
 * Driver auth client. Talks to /api/driver/auth/* and stores the JWT in
 * localStorage so it survives page reloads. The HTTP interceptor pulls
 * the token from here on each outbound request.
 *
 * SSR note: localStorage is browser-only; we guard reads with a typeof check
 * so server-side renders don't blow up.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/driver/auth`;

  readonly token = signal<string | null>(this.readToken());
  readonly session = signal<DriverSessionClaims | null>(this.readSession());
  readonly isAuthenticated = computed(() => this.token() !== null);

  async requestOtp(phone: string): Promise<{ devCode: string | null }> {
    const res = await firstValueFrom(
      this.http.post<{ sent: boolean; expiresInSeconds: number; devCode: string | null }>(
        `${this.base}/request-otp`, { phone }));
    return { devCode: res.devCode };
  }

  async verifyOtp(phone: string, code: string): Promise<void> {
    const res = await firstValueFrom(
      this.http.post<VerifyOtpResponse>(`${this.base}/verify-otp`, { phone, code }));
    this.setToken(res.token);
    this.setSession({
      driverId: res.driver.id,
      parkId: res.driver.parkId,
      name: res.driver.name,
      yandexProfileId: res.driver.yandexProfileId,
    });
  }

  logout() {
    this.setToken(null);
    this.setSession(null);
  }

  // ── Storage helpers ────────────────────────────────────────────────
  private setToken(value: string | null) {
    this.token.set(value);
    if (typeof localStorage === 'undefined') return;
    if (value === null) localStorage.removeItem(TOKEN_KEY);
    else localStorage.setItem(TOKEN_KEY, value);
  }

  private setSession(value: DriverSessionClaims | null) {
    this.session.set(value);
    if (typeof localStorage === 'undefined') return;
    if (value === null) localStorage.removeItem(SESSION_KEY);
    else localStorage.setItem(SESSION_KEY, JSON.stringify(value));
  }

  private readToken(): string | null {
    if (typeof localStorage === 'undefined') return null;
    return localStorage.getItem(TOKEN_KEY);
  }

  private readSession(): DriverSessionClaims | null {
    if (typeof localStorage === 'undefined') return null;
    const raw = localStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    try { return JSON.parse(raw) as DriverSessionClaims; }
    catch { return null; }
  }
}

export interface DriverSessionClaims {
  driverId: string;
  parkId: string;
  name: string | null;
  yandexProfileId: string | null;
}

interface VerifyOtpResponse {
  token: string;
  expiresAt: string;
  driver: {
    id: string;
    parkId: string;
    name: string | null;
    yandexProfileId: string | null;
  };
}
