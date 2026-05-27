import { Component, inject, signal, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AdminAuthService } from '../services/admin-auth.service';

@Component({
  selector: 'app-admin-login',
  imports: [FormsModule],
  templateUrl: './admin-login.html',
  styleUrl: './admin-login.scss',
})
export class AdminLoginComponent {
  private router = inject(Router);
  private auth = inject(AdminAuthService);

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
        code === 'invalid_credentials' ? 'Wrong email or password.'
        : code === 'email_and_password_required' ? 'Both fields are required.'
        : err?.error?.message ?? err?.message ?? 'Login failed.'
      );
    } finally {
      this.submitting.set(false);
    }
  }
}
