import { Component, inject, signal, computed, ViewChildren, QueryList, ElementRef, AfterViewInit, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { MockDataService } from '../../../core/services/mock-data.service';

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

  step = signal<Step>('phone');
  phone = signal('');
  digits = signal<string[]>(['', '', '', '', '', '']);
  resendSecs = signal(0);
  private timer: ReturnType<typeof setInterval> | null = null;

  @ViewChildren('digitInput') digitInputs!: QueryList<ElementRef<HTMLInputElement>>;

  get t() { return this.svc.t; }

  otp = computed(() => this.digits().join(''));
  phoneValid = computed(() => this.phone().replace(/\D/g, '').length >= 9);
  otpValid = computed(() => this.otp().length === 6 && this.otp().match(/^\d{6}$/) !== null);

  sendCode() {
    if (!this.phoneValid()) return;
    this.step.set('otp');
    this.startResendTimer();
    setTimeout(() => this.focusDigit(0), 100);
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

  verify() {
    if (!this.otpValid()) return;
    // Mock: any 6-digit code works
    this.router.navigate(['/dashboard']);
  }

  resend() {
    this.digits.set(['', '', '', '', '', '']);
    this.startResendTimer();
    setTimeout(() => this.focusDigit(0), 100);
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
  }

  ngOnDestroy() {
    if (this.timer) clearInterval(this.timer);
  }
}
