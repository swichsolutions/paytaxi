import { Component, inject, signal, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';

@Component({
  selector: 'app-admin-login',
  imports: [FormsModule],
  templateUrl: './admin-login.html',
  styleUrl: './admin-login.scss',
})
export class AdminLoginComponent {
  private router = inject(Router);

  email = signal('');
  password = signal('');
  submitting = signal(false);
  error = signal('');

  canSubmit = computed(() =>
    this.email().includes('@') && this.password().length >= 4 && !this.submitting()
  );

  submit() {
    if (!this.canSubmit()) return;
    this.submitting.set(true);
    this.error.set('');

    // Mock: any credentials work
    setTimeout(() => {
      this.submitting.set(false);
      this.router.navigate(['/admin/overview']);
    }, 600);
  }
}
