import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService, SessionCard } from '../../core/services/driver-session.service';

type Step = 1 | 2 | 3;

interface CashoutSagaResult {
  cashoutId: string;
  status: string;
  amount: number;
  fee: number;
  net: number;
  bankTransferId: string | null;
  yandexTransactionId: string | null;
  failureReason: string | null;
  wasDeduped: boolean;
}

@Component({
  selector: 'app-cashout',
  templateUrl: './cashout.html',
  styleUrl: './cashout.scss',
})
export class CashoutComponent implements OnInit {
  private router = inject(Router);
  private http = inject(HttpClient);
  readonly svc = inject(MockDataService); // kept for i18n + formatters only
  readonly session = inject(DriverSessionService);

  step = signal<Step>(1);
  amountStr = signal('');
  selectedCardId = signal<string>('');
  confirmed = signal(false);
  submitting = signal(false);
  result = signal<CashoutSagaResult | null>(null);
  submitError = signal<string | null>(null);

  // Generated once per visit so a network retry from this page dedupes server-side.
  private readonly idempotencyKey = crypto.randomUUID();

  async ngOnInit() {
    await this.session.ensureLoaded();
    const defaultCard = this.session.cards().find(c => c.isDefault) ?? this.session.cards()[0];
    if (defaultCard) this.selectedCardId.set(defaultCard.id);
  }

  get t() { return this.svc.t; }
  get cards(): SessionCard[] { return this.session.cards(); }
  balance = computed(() => this.session.driver()?.balance ?? 0);

  amount = computed(() => parseFloat(this.amountStr()) || 0);
  fee = computed(() => this.amount() > 0 ? this.svc.calcFee(this.amount()) : 0);
  net = computed(() => Math.max(0, this.amount() - this.fee()));
  canProceed = computed(() => this.amount() >= 5 && this.amount() <= this.balance());

  selectedCard = computed<SessionCard | null>(() =>
    this.cards.find(c => c.id === this.selectedCardId()) ?? this.cards[0] ?? null);

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

  pickCard(card: SessionCard) {
    this.selectedCardId.set(card.id);
  }

  nextStep() {
    if (this.step() === 1 && this.canProceed()) this.step.set(2);
    else if (this.step() === 2 && this.selectedCard()) this.step.set(3);
  }

  prevStep() {
    if (this.step() > 1) this.step.set((this.step() - 1) as Step);
    else this.router.navigate(['/dashboard']);
  }

  async confirm() {
    const card = this.selectedCard();
    if (!card) return;

    this.submitting.set(true);
    this.submitError.set(null);
    try {
      const result = await firstValueFrom(this.http.post<CashoutSagaResult>(
        `http://localhost:5196/api/driver/cashouts`,
        {
          cardId: card.id,
          amount: this.amount(),
          idempotencyKey: this.idempotencyKey,
        }));
      this.result.set(result);
      this.confirmed.set(true);
      await this.session.refresh();
      setTimeout(() => this.router.navigate(['/history']), 2200);
    } catch (err: any) {
      const serverMsg = err?.error?.message ?? err?.error?.error ?? err?.message ?? 'Unknown error';
      this.submitError.set(serverMsg);
    } finally {
      this.submitting.set(false);
    }
  }

  formatGel(n: number) { return this.svc.formatGel(n); }
}
