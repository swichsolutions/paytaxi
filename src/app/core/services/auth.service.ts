import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpContext, HttpContextToken } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const TOKEN_KEY = 'paytaxi.driver.token';
const TOKEN_EXP_KEY = 'paytaxi.driver.token.exp';
const REFRESH_KEY = 'paytaxi.driver.refresh';
const REFRESH_EXP_KEY = 'paytaxi.driver.refresh.exp';
const SESSION_KEY = 'paytaxi.driver.session';

/** Marks requests the interceptor must not touch (the auth endpoints themselves). */
export const SKIP_AUTH = new HttpContextToken<boolean>(() => false);

/**
 * Driver auth client with trusted-device sessions.
 *
 *   verify-otp  → short access JWT (~60 min) + 90-day refresh token, both kept in localStorage
 *   refresh     → rotates the refresh token, mints a new JWT — no SMS
 *   logout      → revokes this device's refresh token server-side, clears storage
 *
 * The interceptor calls ensureValidToken() before driver API calls and refresh() once
 * on a 401, so a returning driver never sees the OTP screen while the 90-day session lives.
 *
 * SSR note: localStorage is browser-only; every access is guarded.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/driver/auth`;

  readonly token = signal<string | null>(this.read(TOKEN_KEY));
  readonly session = signal<DriverSessionClaims | null>(this.readSession());
  private readonly refreshToken = signal<string | null>(this.read(REFRESH_KEY));

  /** Signed in = a live refresh session (even if the short access token has lapsed). */
  readonly isAuthenticated = computed(() => this.token() !== null || this.hasLiveRefresh());

  private refreshing: Promise<boolean> | null = null;

  async requestOtp(phone: string): Promise<{ devCode: string | null }> {
    const res = await firstValueFrom(
      this.http.post<{ sent: boolean; expiresInSeconds: number; devCode: string | null }>(
        `${this.base}/request-otp`, { phone }, { context: new HttpContext().set(SKIP_AUTH, true) }));
    return { devCode: res.devCode };
  }

  async verifyOtp(phone: string, code: string): Promise<void> {
    const res = await firstValueFrom(
      this.http.post<AuthResponse>(`${this.base}/verify-otp`, { phone, code },
        { context: new HttpContext().set(SKIP_AUTH, true) }));
    this.apply(res);
  }

  /**
   * Make sure the access token is usable for the next ~60 s; refresh it from the trusted-device
   * session if not. Returns false when the driver must sign in again.
   */
  async ensureValidToken(): Promise<boolean> {
    const exp = this.readMs(TOKEN_EXP_KEY);
    const fresh = this.token() !== null && exp !== null && exp - Date.now() > 60_000;
    if (fresh) return true;
    return this.refresh();
  }

  /** Rotate the refresh token and mint a new access token. De-duplicates concurrent callers. */
  refresh(): Promise<boolean> {
    if (this.refreshing) return this.refreshing;
    this.refreshing = this.doRefresh().finally(() => { this.refreshing = null; });
    return this.refreshing;
  }

  private async doRefresh(): Promise<boolean> {
    const rt = this.refreshToken();
    if (!rt || !this.hasLiveRefresh()) { this.clear(); return false; }
    try {
      const res = await firstValueFrom(
        this.http.post<AuthResponse>(`${this.base}/refresh`, { refreshToken: rt },
          { context: new HttpContext().set(SKIP_AUTH, true) }));
      this.apply(res);
      return true;
    } catch {
      // Expired, revoked (possibly reuse detected) or driver suspended → back to the OTP screen.
      this.clear();
      return false;
    }
  }

  /** Revoke this device server-side (best effort) and forget everything locally. */
  logout() {
    const rt = this.refreshToken();
    if (rt) {
      this.http.post(`${this.base}/logout`, { refreshToken: rt },
        { context: new HttpContext().set(SKIP_AUTH, true) }).subscribe({ error: () => { /* offline: local clear is enough */ } });
    }
    this.clear();
  }

  // ── Internals ──────────────────────────────────────────────────────

  private apply(res: AuthResponse) {
    this.write(TOKEN_KEY, res.token);
    this.write(TOKEN_EXP_KEY, String(new Date(res.expiresAt).getTime()));
    this.write(REFRESH_KEY, res.refreshToken);
    this.write(REFRESH_EXP_KEY, String(new Date(res.refreshExpiresAt).getTime()));
    this.token.set(res.token);
    this.refreshToken.set(res.refreshToken);
    this.setSession({
      driverId: res.driver.id,
      parkId: res.driver.parkId,
      name: res.driver.name,
      yandexProfileId: res.driver.yandexProfileId,
    });
  }

  private clear() {
    for (const k of [TOKEN_KEY, TOKEN_EXP_KEY, REFRESH_KEY, REFRESH_EXP_KEY]) this.write(k, null);
    this.token.set(null);
    this.refreshToken.set(null);
    this.setSession(null);
  }

  private hasLiveRefresh(): boolean {
    const exp = this.readMs(REFRESH_EXP_KEY);
    return this.refreshToken() !== null && exp !== null && exp > Date.now();
  }

  private setSession(value: DriverSessionClaims | null) {
    this.session.set(value);
    this.write(SESSION_KEY, value === null ? null : JSON.stringify(value));
  }

  private readSession(): DriverSessionClaims | null {
    const raw = this.read(SESSION_KEY);
    if (!raw) return null;
    try { return JSON.parse(raw) as DriverSessionClaims; } catch { return null; }
  }

  private read(key: string): string | null {
    try { return typeof localStorage === 'undefined' ? null : localStorage.getItem(key); } catch { return null; }
  }

  private readMs(key: string): number | null {
    const v = this.read(key);
    const n = v === null ? NaN : Number(v);
    return Number.isFinite(n) ? n : null;
  }

  private write(key: string, value: string | null) {
    try {
      if (typeof localStorage === 'undefined') return;
      if (value === null) localStorage.removeItem(key);
      else localStorage.setItem(key, value);
    } catch { /* private mode / quota */ }
  }
}

export interface DriverSessionClaims {
  driverId: string;
  parkId: string;
  name: string | null;
  yandexProfileId: string | null;
}

interface AuthResponse {
  token: string;
  expiresAt: string;
  refreshToken: string;
  refreshExpiresAt: string;
  driver: {
    id: string;
    parkId: string;
    name: string | null;
    yandexProfileId: string | null;
  };
}
