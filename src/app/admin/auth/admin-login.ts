import { Component, inject, signal, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AdminAuthService } from '../services/admin-auth.service';
import { AdminI18nService } from '../services/admin-i18n.service';

/** Only relative console paths may be used as a post-login destination. */
const SAFE_NEXT = /^\/admin(\/[A-Za-z0-9\-_/]*)?(\?[^#\s]*)?$/;

@Component({
  selector: 'app-admin-login',
  imports: [FormsModule],
  templateUrl: './admin-login.html',
  styleUrl: './admin-login.scss',
})
export class AdminLoginComponent {
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private auth = inject(AdminAuthService);
  readonly i18n = inject(AdminI18nService);

  get t() { return this.i18n.t; }

  constructor() {
    // Already signed in (token in localStorage) → skip the login form. No-op during SSR.
    if (this.auth.isAuthenticated()) this.router.navigateByUrl(this.nextUrl());
  }

  email = signal('');
  password = signal('');
  submitting = signal(false);
  error = signal('');

  /** Set when the interceptor sent us here after a 401 (`?reason=expired`). */
  readonly sessionExpired = signal(this.route.snapshot.queryParamMap.get('reason') === 'expired');

  canSubmit = computed(() =>
    this.email().includes('@') && this.password().length >= 4 && !this.submitting()
  );

  /**
   * Honour `?next=` set by the auth guard, but only for relative /admin/… paths
   * (never the login page itself, never an absolute or protocol-relative URL).
   */
  private nextUrl(): string {
    const raw = this.route.snapshot.queryParamMap.get('next');
    if (raw && SAFE_NEXT.test(raw) && !raw.startsWith('/admin/login') && !raw.startsWith('//')) {
      return raw;
    }
    return '/admin/overview';
  }

  async submit() {
    if (!this.canSubmit()) return;
    this.submitting.set(true);
    this.error.set('');
    try {
      await this.auth.login(this.email().trim(), this.password());
      this.sessionExpired.set(false);
      this.router.navigateByUrl(this.nextUrl());
    } catch (err: any) {
      const code = err?.error?.error;
      this.error.set(
        code === 'invalid_credentials' ? this.t['errWrongCredentials']
        : code === 'email_and_password_required' ? this.t['errBothRequired']
        : err?.error?.message ?? err?.message ?? this.t['errLoginFailed']
      );
    } finally {
      this.submitting.set(false);
    }
  }
}
