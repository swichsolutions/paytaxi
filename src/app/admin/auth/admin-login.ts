import { Component, inject, signal, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AdminAuthService } from '../services/admin-auth.service';
import { AdminI18nService } from '../services/admin-i18n.service';

@Component({
  selector: 'app-admin-login',
  imports: [FormsModule],
  templateUrl: './admin-login.html',
  styleUrl: './admin-login.scss',
})
export class AdminLoginComponent {
  private router = inject(Router);
  private auth = inject(AdminAuthService);
  readonly i18n = inject(AdminI18nService);

  get t() { return this.i18n.t; }

  constructor() {
    // Already signed in (token in localStorage) → skip the login form. No-op during SSR.
    if (this.auth.isAuthenticated()) this.router.navigate(['/admin/overview']);
  }

  email = signal('');
  password = signal('');
  submitting = signal(false);
  error = signal('');

  canSubmit = computed(() =>
    this.email().includes('@') && this.password().length >= 4 && !this.submitting()
  );

  async submit() {
    if (!this.canSubmit()) return;
    this.submitting.set(true);
    this.error.set('');
    try {
      await this.auth.login(this.email().trim(), this.password());
      this.router.navigate(['/admin/overview']);
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
