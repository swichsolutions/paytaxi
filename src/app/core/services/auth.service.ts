import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpContext, HttpContextToken, HttpErrorResponse } from '@angular/common/http';
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
 * Several tabs share one session through localStorage. Each refresh rotates the refresh token, and the server treats
 * a rotated token coming back as theft (every session of the driver is revoked). So the stored tokens are the truth,
 * not this tab's copy: they are re-read before every use and on the 'storage' event, refreshes of all tabs are
 * serialised with a Web Lock, and a tab that loses a simultaneous refresh (409) adopts the winner's tokens.
 * Only the server saying "this session is over" (401) signs the driver out — a 429, a dropped connection or a 5xx
 * keeps the session for the next try.
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

  constructor() {
    // Another tab refreshed or signed out: take its tokens (or its sign-out) instead of replaying ours.
    if (typeof window !== 'undefined') {
      window.addEventListener('storage', e => {
        if (e.key === null || e.key.startsWith('paytaxi.driver.')) this.syncFromStorage();
      });
    }
  }

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
    this.syncFromStorage();
    const exp = this.readMs(TOKEN_EXP_KEY);
    const fresh = this.token() !== null && exp !== null && exp - Date.now() > 60_000;
    if (fresh) return true;
    return this.refresh();
  }

  /**
   * Rotate the refresh token and mint a new access token. De-duplicates concurrent callers in this tab, and runs
   * under a cross-tab lock where the browser has one (all current ones do), so two tabs never send the same token.
   */
  refresh(): Promise<boolean> {
    if (this.refreshing) return this.refreshing;
    const locks = typeof navigator !== 'undefined' ? (navigator as Navigator & { locks?: LockManager }).locks : undefined;
    const run = locks
      ? locks.request('paytaxi.driver.refresh', () => this.doRefresh())
      : this.doRefresh();
    this.refreshing = Promise.resolve(run).finally(() => { this.refreshing = null; });
    return this.refreshing;
  }

  private async doRefresh(): Promise<boolean> {
    const before = this.refreshToken();
    this.syncFromStorage();
    // Another tab rotated while we waited for the lock: its fresh access token is ours too.
    if (this.refreshToken() !== before && this.hasFreshAccessToken()) return true;
    const rt = this.refreshToken();
    if (!rt || !this.hasLiveRefresh()) { this.clear(); return false; }
    try {
      const res = await firstValueFrom(
        this.http.post<AuthResponse>(`${this.base}/refresh`, { refreshToken: rt },
          { context: new HttpContext().set(SKIP_AUTH, true) }));
      this.apply(res);
      return true;
    } catch (err) {
      const status = err instanceof HttpErrorResponse ? err.status : 0;
      if (status === 409) {
        // A simultaneous refresh with the same token won (a browser without Web Locks): it stores its tokens a
        // moment later. Adopt them; never resend the old token, which the server would take for a stolen copy.
        await new Promise(r => setTimeout(r, 400));
        this.syncFromStorage();
        return this.refreshToken() !== rt && this.hasFreshAccessToken();
      }
      // Expired, revoked (possibly reuse detected) or driver suspended → back to the OTP screen. Anything else
      // (rate limited, offline, a server error) says nothing about the session: keep it and try again later.
      if (status === 401) this.clear();
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

  /** Pick up what another tab stored (new tokens after its refresh, or its sign-out). */
  private syncFromStorage() {
    const token = this.read(TOKEN_KEY);
    const refresh = this.read(REFRESH_KEY);
    if (token !== this.token()) this.token.set(token);
    if (refresh !== this.refreshToken()) this.refreshToken.set(refresh);
    if (token === null && refresh === null && this.session() !== null) this.session.set(null);
  }

  private hasFreshAccessToken(): boolean {
    const exp = this.readMs(TOKEN_EXP_KEY);
    return this.token() !== null && exp !== null && exp - Date.now() > 60_000;
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
