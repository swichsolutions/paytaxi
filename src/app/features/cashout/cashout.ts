import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService, SessionCard } from '../../core/services/driver-session.service';
import { environment } from '../../../environments/environment';

type Step = 1 | 2 | 3;

interface CashoutSagaResult {
  cashoutId: string;
  status: string;            // Completed | Queued | Failed | ReviewRequired
  amount: number;
  fee: number;
  net: number;
  bankTransferId: string | null;
  yandexTransactionId: string | null;
  failureReason: string | null;
  wasDeduped: boolean;
  nextAttemptAt?: string | null;
  attemptCount?: number;
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

  // ── Inline "add bank account" form (step 2) ──────────────────────
  addingAccount = signal(false);
  newIban = signal('');
  newHolder = signal('');
  addBusy = signal(false);
  addError = signal<string | null>(null);

  // Generated once per visit so a network retry from this page dedupes server-side.
  private readonly idempotencyKey = crypto.randomUUID();

  async ngOnInit() {
    await this.session.ensureLoaded();
    this.pickDefaultCard();
    this.newHolder.set(this.session.driver()?.name ?? '');
  }

  private pickDefaultCard() {
    const cards = this.session.cards();
    const current = cards.find(c => c.id === this.selectedCardId());
    if (current) return;
    const defaultCard = cards.find(c => c.isDefault) ?? cards[0];
    this.selectedCardId.set(defaultCard?.id ?? '');
  }

  get t() { return this.svc.t; }
  get cards(): SessionCard[] { return this.session.cards(); }
  balance = computed(() => this.session.driver()?.balance ?? 0);

  // Fee & limits come from the park's configuration (flat fee, e.g. 0.50 GEL).
  feeAmount = computed(() => this.session.cashoutFee());
  minAmount = computed(() => this.session.minCashout());
  maxAmount = computed(() => this.session.maxCashout());

  amount = computed(() => parseFloat(this.amountStr()) || 0);
  fee = computed(() => this.amount() > 0 ? this.feeAmount() : 0);
  net = computed(() => Math.max(0, this.amount() - this.fee()));

  belowMin = computed(() => this.amount() > 0 && this.amount() < this.minAmount());
  aboveMax = computed(() => this.maxAmount() !== null && this.amount() > (this.maxAmount() as number));
  exceedsBalance = computed(() => this.amount() > this.balance());
  canProceed = computed(() =>
    this.amount() > 0 && !this.belowMin() && !this.aboveMax() && !this.exceedsBalance());

  selectedCard = computed<SessionCard | null>(() =>
    this.cards.find(c => c.id === this.selectedCardId()) ?? this.cards[0] ?? null);

  ibanComplete = computed(() => this.newIban().replace(/\s+/g, '').length === 22);

  supportedBanksLabel = computed(() =>
    this.session.supportedBanks().map(b => b.bankLabel).join(', '));

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

  // ── Add account ──────────────────────────────────────────────────

  openAddAccount() {
    this.addError.set(null);
    this.addingAccount.set(true);
  }

  cancelAddAccount() {
    this.addingAccount.set(false);
    this.newIban.set('');
    this.addError.set(null);
  }

  onIbanInput(v: string) {
    // Group in fours for readability; the backend strips whitespace anyway.
    const raw = v.replace(/\s+/g, '').toUpperCase().slice(0, 22);
    this.newIban.set(raw.replace(/(.{4})/g, '$1 ').trim());
  }

  async saveAccount() {
    if (this.addBusy()) return;
    this.addBusy.set(true);
    this.addError.set(null);
    try {
      const card = await this.session.addCard(this.newIban(), this.newHolder().trim(), true);
      this.selectedCardId.set(card.id);
      this.addingAccount.set(false);
      this.newIban.set('');
    } catch (err: any) {
      this.addError.set(this.mapCardError(err));
    } finally {
      this.addBusy.set(false);
    }
  }

  private mapCardError(err: any): string {
    const code = err?.error?.error;
    const t = this.t as Record<string, string>;
    if (code === 'invalid_iban_format' || code === 'invalid_iban_checksum' || code === 'iban_required')
      return t['errInvalidIban'];
    if (code === 'bank_not_supported') {
      const supported = (err?.error?.supported ?? []).map((b: any) => b.bankLabel).join(', ');
      return `${t['errBankNotSupported']} ${supported || this.supportedBanksLabel()}`;
    }
    if (code === 'iban_already_added') return t['errIbanExists'];
    return err?.error?.message ?? t['errGeneric'];
  }

  // ── Confirm ──────────────────────────────────────────────────────

  async confirm() {
    const card = this.selectedCard();
    if (!card) return;

    this.submitting.set(true);
    this.submitError.set(null);
    try {
      const result = await firstValueFrom(this.http.post<CashoutSagaResult>(
        `${environment.apiBase}/api/driver/cashouts`,
        {
          cardId: card.id,
          amount: this.amount(),
          idempotencyKey: this.idempotencyKey,
        }));
      this.result.set(result);
      this.confirmed.set(true);
      await this.session.refresh();
      setTimeout(() => this.router.navigate(['/history']), result.status === 'Completed' ? 2200 : 4500);
    } catch (err: any) {
      // 422 Failed comes back as an error response carrying the saga result body.
      const body = err?.error;
      if (body && typeof body.status === 'string' && body.cashoutId) {
        this.result.set(body as CashoutSagaResult);
        this.confirmed.set(true);
        await this.session.refresh();
        return;
      }
      const serverMsg = body?.message ?? body?.error ?? err?.message ?? (this.t as Record<string, string>)['errGeneric'];
      this.submitError.set(serverMsg);
    } finally {
      this.submitting.set(false);
    }
  }

  goHistory() { this.router.navigate(['/history']); }

  formatGel(n: number) { return this.svc.formatGel(n); }
}
