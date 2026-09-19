import { Component, inject, signal, computed, ViewChildren, QueryList, ElementRef, OnDestroy } from '@angular/core';
import { Router, ActivatedRoute } from '@angular/router';
import { MockDataService } from '../../../core/services/mock-data.service';
import { AuthService } from '../../../core/services/auth.service';
import { DriverSessionService } from '../../../core/services/driver-session.service';

type Step = 'phone' | 'otp';

/** Georgian mobile numbers are 9 local digits (5xx xxx xxx); we always send E.164 (+995…). */
const LOCAL_DIGITS = 9;

@Component({
  selector: 'app-login',
  templateUrl: './login.html',
  styleUrl: './login.scss',
})
export class LoginComponent implements OnDestroy {
  private router = inject(Router);
  readonly svc = inject(MockDataService);
  private auth = inject(AuthService);
  private session = inject(DriverSessionService);

  step = signal<Step>('phone');
  /** Local part only: exactly the digits the driver typed, without +995 / leading 0. */
  phone = signal('');
  digits = signal<string[]>(['', '', '', '', '', '']);
  resendSecs = signal(0);
  submitting = signal(false);
  resending = signal(false);
  errorMessage = signal<string | null>(null);
  private readonly route = inject(ActivatedRoute);

  constructor() {
    // Already signed in on this device (90-day trusted session) → skip the OTP screen.
    if (this.auth.isAuthenticated()) {
      const next = this.route.snapshot.queryParamMap.get('next');
      this.router.navigateByUrl(next && next.startsWith('/') ? next : '/dashboard');
      return;
    }
    // Bounced here by the interceptor after the 90-day session ended / was revoked.
    if (this.route.snapshot.queryParamMap.get('reason') === 'expired') {
      this.errorMessage.set(this.t.sessionExpired);
    }
  }
  devCode = signal<string | null>(null);
  private timer: ReturnType<typeof setInterval> | null = null;

  @ViewChildren('digitInput') digitInputs!: QueryList<ElementRef<HTMLInputElement>>;

  get t() { return this.svc.t; }

  otp = computed(() => this.digits().join(''));
  phoneValid = computed(() => /^\d{9}$/.test(this.phone()));
  otpValid = computed(() => /^\d{6}$/.test(this.otp()));

  /** "599 123 456" for display. */
  phoneDisplay = computed(() => this.phone().replace(/(\d{3})(?=\d)/g, '$1 ').trim());

  /** Resend countdown label, e.g. "Resend in 45s" / "ხელახლა გაგზავნა შესაძლებელია 45 წმ-ში". */
  resendLabel = computed(() => this.svc.tr('resendIn', { seconds: this.resendSecs() }));

  /**
   * Normalise whatever was typed or pasted to the 9 local digits:
   * "+995 599 123 456" → 599123456, "0599123456" → 599123456, letters dropped.
   */
  static sanitizePhone(raw: string): string {
    let d = raw.replace(/\D/g, '');
    if (d.length > LOCAL_DIGITS && d.startsWith('995')) d = d.slice(3);
    if (d.startsWith('0')) d = d.replace(/^0+/, '');
    return d.slice(0, LOCAL_DIGITS);
  }

  onPhoneInput(e: Event) {
    const input = e.target as HTMLInputElement;
    const clean = LoginComponent.sanitizePhone(input.value);
    this.phone.set(clean);
    // Write the sanitised value back so the field never shows what we won't send.
    if (input.value !== clean) input.value = clean;
  }

  private fullPhone(): string {
    return `+995${this.phone()}`;
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
    } catch (err: unknown) {
      this.errorMessage.set(this.mapRequestError(err));
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

  digitLabel(idx: number): string {
    return this.svc.tr('digitN', { n: idx + 1 });
  }

  async verify() {
    if (!this.otpValid() || this.submitting()) return;
    this.submitting.set(true);
    this.errorMessage.set(null);
    try {
      await this.auth.verifyOtp(this.fullPhone(), this.otp());
      // Force the session service to re-read its driver context from the new JWT.
      await this.session.reloadForCurrentSession();
      const next = this.route.snapshot.queryParamMap.get('next');
      this.router.navigateByUrl(next && next.startsWith('/') ? next : '/dashboard');
    } catch (err: unknown) {
      this.errorMessage.set(this.mapVerifyError(err));
    } finally {
      this.submitting.set(false);
    }
  }

  async resend() {
    // Double-tap guard: one request at a time, and never inside the cooldown.
    if (this.resending() || this.submitting() || this.resendSecs() > 0) return;
    this.resending.set(true);
    this.digits.set(['', '', '', '', '', '']);
    this.errorMessage.set(null);
    try {
      const { devCode } = await this.auth.requestOtp(this.fullPhone());
      this.devCode.set(devCode);
      this.startResendTimer();
      setTimeout(() => this.focusDigit(0), 100);
    } catch (err: unknown) {
      this.errorMessage.set(this.mapRequestError(err));
    } finally {
      this.resending.set(false);
    }
  }

  // ── Error mapping (never surface Angular's raw "Http failure response…") ──

  private mapRequestError(err: unknown): string {
    const e = err as { status?: number; error?: { error?: string; retryAfterSeconds?: number } | null } | null;
    const status = e?.status;
    const code = e?.error?.error;
    if (status === 0) return this.t.errLoginNetwork;
    if (status === 429) {
      // Per-phone cap returns { error: 'too_many_requests', retryAfterSeconds }; the per-IP limiter returns an empty body.
      const secs = e?.error?.retryAfterSeconds;
      return typeof secs === 'number' && secs > 0
        ? this.svc.tr('errLoginTooManyRequests', { seconds: Math.ceil(secs) })
        : this.t.errLoginTooManyRequestsNoRetry;
    }
    switch (code) {
      case 'driver_not_found': return this.t.errLoginDriverNotFound;
      case 'driver_inactive':  return this.t.errLoginDriverInactive;
      case 'phone_required':   return this.t.errLoginPhoneRequired;
      default:                 return this.t.errCouldNotRequestCode;
    }
  }

  private mapVerifyError(err: unknown): string {
    const e = err as { status?: number; error?: { error?: string } | null } | null;
    if (e?.status === 0) return this.t.errLoginNetwork;
    if (e?.status === 429) return this.t.errLoginTooManyRequestsNoRetry;
    switch (e?.error?.error) {
      case 'invalid_code':      return this.t.errInvalidCode;
      case 'no_active_code':    return this.t.errCodeExpired;
      case 'too_many_attempts': return this.t.errTooManyAttempts;
      case 'driver_not_found':  return this.t.errLoginDriverNotFound;
      case 'driver_inactive':   return this.t.errLoginDriverInactive;
      default:                  return this.t.errVerifyFailed;
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
