import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const TOKEN_KEY = 'paytaxi.admin.token';
const TOKEN_EXP_KEY = 'paytaxi.admin.token.exp';
const SESSION_KEY = 'paytaxi.admin.session';

/** Treat a token as expired this many ms before its real expiry (clock skew, in-flight requests). */
const EXPIRY_SKEW_MS = 30_000;

/**
 * Email + password auth for the admin console. Mirrors the driver-side
 * AuthService but keeps its own localStorage keys so the two sessions
 * are fully independent — you can be logged in as a driver in one tab
 * and as a manager in another.
 */
@Injectable({ providedIn: 'root' })
export class AdminAuthService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/admin/auth`;

  readonly token = signal<string | null>(this.readToken());
  readonly admin = signal<AdminSession | null>(this.readSession());

  /**
   * Signed in AND the token has not expired. Admin tokens live 24 h with no refresh flow,
   * so a stale token from yesterday must count as signed out — otherwise the guard lets it
   * through, the login page bounces the visitor away and every request 401s.
   */
  readonly isAuthenticated = computed(() => {
    const token = this.token();
    if (token === null) return false;
    const exp = this.tokenExpiryMs(token);
    return exp === null || exp - EXPIRY_SKEW_MS > Date.now();
  });

  constructor() {
    // Drop a token that already expired while the tab was closed.
    if (this.token() !== null && !this.isAuthenticated()) this.logout();
  }

  async login(email: string, password: string): Promise<void> {
    const res = await firstValueFrom(
      this.http.post<LoginResponse>(`${this.base}/login`, { email, password }));
    this.setToken(res.token, res.expiresAt);
    this.setSession({
      id: res.admin.id,
      email: res.admin.email,
      name: res.admin.name,
      role: res.admin.role,
      parkId: res.admin.parkId,
      parkName: res.admin.parkName,
    });
  }

  logout() {
    this.setToken(null, null);
    this.setSession(null);
  }

  /** Called by the HTTP interceptor when the backend rejects the token: clear and go to login. */
  expire() {
    this.logout();
  }

  /** Expiry (ms since epoch) from the stored value, else from the JWT `exp` claim; null if unknown. */
  private tokenExpiryMs(token: string): number | null {
    if (typeof localStorage !== 'undefined') {
      const stored = localStorage.getItem(TOKEN_EXP_KEY);
      if (stored) {
        const ms = Date.parse(stored);
        if (!Number.isNaN(ms)) return ms;
      }
    }
    try {
      const payload = token.split('.')[1] ?? '';
      const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
      const exp = JSON.parse(json).exp;
      return typeof exp === 'number' ? exp * 1000 : null;
    } catch {
      return null;
    }
  }

  // ── Storage helpers ────────────────────────────────────────────────
  private setToken(value: string | null, expiresAt: string | null) {
    this.token.set(value);
    if (typeof localStorage === 'undefined') return;
    if (value === null) {
      localStorage.removeItem(TOKEN_KEY);
      localStorage.removeItem(TOKEN_EXP_KEY);
    } else {
      localStorage.setItem(TOKEN_KEY, value);
      if (expiresAt) localStorage.setItem(TOKEN_EXP_KEY, expiresAt);
      else localStorage.removeItem(TOKEN_EXP_KEY);
    }
  }

  private setSession(value: AdminSession | null) {
    this.admin.set(value);
    if (typeof localStorage === 'undefined') return;
    if (value === null) localStorage.removeItem(SESSION_KEY);
    else localStorage.setItem(SESSION_KEY, JSON.stringify(value));
  }

  private readToken(): string | null {
    if (typeof localStorage === 'undefined') return null;
    return localStorage.getItem(TOKEN_KEY);
  }

  private readSession(): AdminSession | null {
    if (typeof localStorage === 'undefined') return null;
    const raw = localStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    try { return JSON.parse(raw) as AdminSession; }
    catch { return null; }
  }
}

export interface AdminSession {
  id: string;
  email: string;
  name: string | null;
  role: string;          // "super_admin" | "park_admin"
  parkId: string | null;
  parkName: string | null;
}

interface LoginResponse {
  token: string;
  expiresAt: string;
  admin: {
    id: string;
    email: string;
    name: string | null;
    role: string;
    parkId: string | null;
    parkName: string | null;
  };
}
