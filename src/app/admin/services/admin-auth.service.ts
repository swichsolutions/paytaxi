import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const TOKEN_KEY = 'paytaxi.admin.token';
const SESSION_KEY = 'paytaxi.admin.session';

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
  readonly isAuthenticated = computed(() => this.token() !== null);

  async login(email: string, password: string): Promise<void> {
    const res = await firstValueFrom(
      this.http.post<LoginResponse>(`${this.base}/login`, { email, password }));
    this.setToken(res.token);
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
