import { Component, inject, signal, computed } from '@angular/core';
import { Router } from '@angular/router';
import { MockDataService } from '../../core/services/mock-data.service';
import { BankCard } from '../../core/mock/data';

type Step = 1 | 2 | 3;

@Component({
  selector: 'app-cashout',
  templateUrl: './cashout.html',
  styleUrl: './cashout.scss',
})
export class CashoutComponent {
  private router = inject(Router);
  readonly svc = inject(MockDataService);

  step = signal<Step>(1);
  amountStr = signal('');
  selectedCard = signal<BankCard>(this.svc.cards().find(c => c.isDefault) ?? this.svc.cards()[0]);
  confirmed = signal(false);

  get t() { return this.svc.t; }
  get cards() { return this.svc.cards(); }
  balance = computed(() => this.svc.driver().balance);

  amount = computed(() => parseFloat(this.amountStr()) || 0);
  fee = computed(() => this.amount() > 0 ? this.svc.calcFee(this.amount()) : 0);
  net = computed(() => Math.max(0, this.amount() - this.fee()));
  canProceed = computed(() => this.amount() >= 5 && this.amount() <= this.balance());

  keypad = [
    ['1','2','3'],
    ['4','5','6'],
    ['7','8','9'],
    ['.','0','⌫'],
  ];

  pressKey(k: string) {
    if (k === '⌫') {
      this.amountStr.update(s => s.slice(0, -1));
      return;
    }
    const cur = this.amountStr();
    if (k === '.' && cur.includes('.')) return;
    if (k === '.' && cur === '') { this.amountStr.set('0.'); return; }
    const parts = cur.split('.');
    if (parts[1]?.length >= 2) return;
    if (cur.length >= 7) return;
    this.amountStr.update(s => s + k);
  }

  nextStep() {
    if (this.step() === 1 && this.canProceed()) this.step.set(2);
    else if (this.step() === 2 && this.selectedCard()) this.step.set(3);
  }

  prevStep() {
    if (this.step() > 1) this.step.set((this.step() - 1) as Step);
    else this.router.navigate(['/dashboard']);
  }

  confirm() {
    this.confirmed.set(true);
    // Mock success — navigate to history after brief delay
    setTimeout(() => this.router.navigate(['/history']), 1800);
  }

  formatGel(n: number) { return this.svc.formatGel(n); }
}
