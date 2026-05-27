import { Component, inject, signal, computed, ViewChildren, QueryList, ElementRef, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { MockDataService } from '../../../core/services/mock-data.service';
import { AuthService } from '../../../core/services/auth.service';
import { DriverSessionService } from '../../../core/services/driver-session.service';

type Step = 'phone' | 'otp';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  templateUrl: './login.html',
  styleUrl: './login.scss',
})
export class LoginComponent implements OnDestroy {
  private router = inject(Router);
  readonly svc = inject(MockDataService);
  private auth = inject(AuthService);
  private session = inject(DriverSessionService);

  step = signal<Step>('phone');
  phone = signal('');
  digits = signal<string[]>(['', '', '', '', '', '']);
  resendSecs = signal(0);
  submitting = signal(false);
  errorMessage = signal<string | null>(null);
  devCode = signal<string | null>(null);
  private timer: ReturnType<typeof setInterval> | null = null;

  @ViewChildren('digitInput') digitInputs!: QueryList<ElementRef<HTMLInputElement>>;

  get t() { return this.svc.t; }

  otp = computed(() => this.digits().join(''));
  phoneValid = computed(() => this.phone().replace(/\D/g, '').length >= 9);
  otpValid = computed(() => this.otp().length === 6 && this.otp().match(/^\d{6}$/) !== null);

  // E.164 normalization (Georgia default; user types digits only).
  private fullPhone(): string {
    const digits = this.phone().replace(/\D/g, '');
    return digits.startsWith('995') ? '+' + digits : '+995' + digits;
  }

  async sendCode() {
    if (!this.phoneValid() || this.submitting()) return;
    this.submitting.set(true);
    this.errorMessage.set(null);
    try {
      const { devCode } = await this.auth.requestOtp(this.fullPhone());
      this.devCode.set(devCode);
      this.step.set('otp');
      this.startResendTimer();
      setTimeout(() => this.focusDigit(0), 100);
    } catch (err: any) {
      this.errorMessage.set(err?.error?.message ?? err?.message ?? 'Could not request code');
    } finally {
      this.submitting.set(false);
    }
  }

  onDigitInput(e: Event, idx: number) {
    const input = e.target as HTMLInputElement;
    const val = input.value.replace(/\D/g, '').slice(-1);
    const arr = [...this.digits()];
    arr[idx] = val;
    this.digits.set(arr);
    if (val && idx < 5) this.focusDigit(idx + 1);
  }

  onDigitKeydown(e: KeyboardEvent, idx: number) {
    if (e.key === 'Backspace' && !this.digits()[idx] && idx > 0) {
      this.focusDigit(idx - 1);
    }
  }

  onDigitPaste(e: ClipboardEvent) {
    const text = e.clipboardData?.getData('text') ?? '';
    const nums = text.replace(/\D/g, '').slice(0, 6).split('');
    if (nums.length > 0) {
      e.preventDefault();
      const arr = [...this.digits()];
      nums.forEach((n, i) => { if (i < 6) arr[i] = n; });
      this.digits.set(arr);
      this.focusDigit(Math.min(nums.length, 5));
    }
  }

  private focusDigit(idx: number) {
    const inputs = this.digitInputs?.toArray();
    inputs?.[idx]?.nativeElement.focus();
  }

  async verify() {
    if (!this.otpValid() || this.submitting()) return;
    this.submitting.set(true);
    this.errorMessage.set(null);
    try {
      await this.auth.verifyOtp(this.fullPhone(), this.otp());
      // Force the session service to re-read its driver context from the new JWT.
      await this.session.reloadForCurrentSession();
      this.router.navigate(['/dashboard']);
    } catch (err: any) {
      const code = err?.error?.error;
      const msg = code === 'invalid_code'      ? 'Wrong code. Try again.'
                : code === 'no_active_code'    ? 'Code expired. Request a new one.'
                : code === 'too_many_attempts' ? 'Too many attempts. Request a new code.'
                : err?.error?.message ?? err?.message ?? 'Verification failed';
      this.errorMessage.set(msg);
    } finally {
      this.submitting.set(false);
    }
  }

  async resend() {
    this.digits.set(['', '', '', '', '', '']);
    this.errorMessage.set(null);
    try {
      const { devCode } = await this.auth.requestOtp(this.fullPhone());
      this.devCode.set(devCode);
      this.startResendTimer();
      setTimeout(() => this.focusDigit(0), 100);
    } catch (err: any) {
      this.errorMessage.set(err?.error?.message ?? err?.message ?? 'Could not request code');
    }
  }

  private startResendTimer() {
    this.resendSecs.set(60);
    if (this.timer) clearInterval(this.timer);
    this.timer = setInterval(() => {
      const s = this.resendSecs() - 1;
      this.resendSecs.set(s);
      if (s <= 0 && this.timer) { clearInterval(this.timer); this.timer = null; }
    }, 1000);
  }

  goBack() {
    this.step.set('phone');
    this.digits.set(['', '', '', '', '', '']);
    this.errorMessage.set(null);
    this.devCode.set(null);
  }

  ngOnDestroy() {
    if (this.timer) clearInterval(this.timer);
  }
}
