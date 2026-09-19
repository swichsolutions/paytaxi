import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { MockDataService } from '../../core/services/mock-data.service';
import { DriverSessionService, SessionCard } from '../../core/services/driver-session.service';
import { IbanHelpComponent } from '../../shared/components/iban-help/iban-help';
import { environment } from '../../../environments/environment';

type Step = 1 | 2 | 3;
type BalanceState = 'loading' | 'ok' | 'unavailable';

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

/** 400 body for a saga pre-check rejection. `params` are per-code (min, max, fee, limit, usedToday, balance, supported…). */
interface CashoutRejection {
  error: 'cashout_rejected';
  code: string;
  message?: string;
  params?: Record<string, unknown> | null;
}

/**
 * RFC 4122 v4 id. `crypto.randomUUID` only exists in secure contexts (https / localhost) — a phone
 * opening the dev server over plain http on the LAN does not have it, so fall back to getRandomValues.
 */
function newUuid(): string {
  const c = globalThis.crypto;
  if (c && typeof c.randomUUID === 'function') return c.randomUUID();
  const bytes = new Uint8Array(16);
  if (c && typeof c.getRandomValues === 'function') c.getRandomValues(bytes);
  else for (let i = 0; i < 16; i++) bytes[i] = Math.floor(Math.random() * 256);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

@Component({
  selector: 'app-cashout',
  imports: [IbanHelpComponent],
  templateUrl: './cashout.html',
  styleUrl: './cashout.scss',
})
export class CashoutComponent implements OnInit, OnDestroy {
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

  /**
   * Idempotency: one nonce per visit, combined with the amount and destination. A network retry
   * of the *same* request dedupes server-side; changing the amount or account yields a new key so
   * the driver can never get an older cashout back as "their" result.
   */
  private readonly nonce = signal(newUuid());
  readonly idempotencyKey = computed(() =>
    `${this.nonce()}:${this.amount().toFixed(2)}:${this.selectedCardId()}`);

  private redirectTimer: ReturnType<typeof setTimeout> | null = null;

  async ngOnInit() {
    await this.session.ensureLoaded();
    this.pickDefaultCard();
    this.newHolder.set(this.session.driver()?.name ?? '');
  }

  ngOnDestroy() {
    // Leaving the page (e.g. tapping Home on the result screen) must not yank the driver to /history later.
    if (this.redirectTimer) { clearTimeout(this.redirectTimer); this.redirectTimer = null; }
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

  // ── Balance (null = unknown; never treated as 0) ─────────────────
  balance = computed<number | null>(() => this.session.driver()?.balance ?? null);

  balanceState = computed<BalanceState>(() => {
    const d = this.session.driver();
    if (!d) return this.session.loading() ? 'loading' : 'unavailable';
    if (d.balance === null) return this.session.refreshing() ? 'loading' : 'unavailable';
    return 'ok';
  });

  balanceHint = computed(() => {
    const d = this.session.driver();
    if (!d) return this.t.sessionLoadError;
    if (d.balanceError === 'profile_not_found') return this.t.balanceProfileNotFound;
    return this.t.balanceUnavailableHint;
  });

  /** Keypad and Next are locked until we know what the driver can withdraw. */
  amountLocked = computed(() => this.balanceState() !== 'ok');

  async retryBalance() {
    if (this.session.refreshing()) return;
    await this.session.refresh(true);
  }

  // Fee & limits come from the park's configuration (flat fee, e.g. 0.50 GEL).
  feeAmount = computed(() => this.session.cashoutFee());
  minAmount = computed(() => this.session.minCashout());
  maxAmount = computed(() => this.session.maxCashout());

  amount = computed(() => parseFloat(this.amountStr()) || 0);
  fee = computed(() => this.amount() > 0 ? this.feeAmount() : 0);
  net = computed(() => Math.max(0, this.amount() - this.fee()));

  belowMin = computed(() => this.amount() > 0 && this.amount() < this.minAmount());
  aboveMax = computed(() => this.maxAmount() !== null && this.amount() > (this.maxAmount() as number));
  exceedsBalance = computed(() => {
    const b = this.balance();
    return b !== null && this.amount() > b;
  });
  canProceed = computed(() =>
    this.balance() !== null && this.amount() > 0 && !this.belowMin() && !this.aboveMax() && !this.exceedsBalance());

  selectedCard = computed<SessionCard | null>(() =>
    this.cards.find(c => c.id === this.selectedCardId()) ?? this.cards[0] ?? null);

  ibanComplete = computed(() => this.newIban().replace(/\s+/g, '').length === 22);

  supportedBanksLabel = computed(() =>
    this.session.supportedBanks().map(b => b.bankLabel).join(', '));
  supportedBankLabels = computed(() => this.session.supportedBanks().map(b => b.bankLabel));

  keypad = [
    ['1','2','3'],
    ['4','5','6'],
    ['7','8','9'],
    ['.','0','⌫'],
  ];

  pressKey(k: string) {
    if (this.amountLocked()) return;
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

  keyLabel(k: string): string {
    return k === '⌫' ? this.t.backspace : k;
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
    const t = this.t;
    if (code === 'invalid_iban_format' || code === 'invalid_iban_checksum' || code === 'iban_required')
      return t.errInvalidIban;
    if (code === 'bank_not_supported') {
      const supported = (err?.error?.supported ?? []).map((b: any) => b.bankLabel).join(', ');
      return `${t.errBankNotSupported} ${supported || this.supportedBanksLabel()}`;
    }
    if (code === 'iban_already_added') return t.errIbanExists;
    return t.errGeneric;
  }

  // ── Confirm ──────────────────────────────────────────────────────

  async confirm() {
    const card = this.selectedCard();
    if (!card || this.submitting()) return;

    this.submitting.set(true);
    this.submitError.set(null);
    try {
      const result = await firstValueFrom(this.http.post<CashoutSagaResult>(
        `${environment.apiBase}/api/driver/cashouts`,
        {
          cardId: card.id,
          amount: this.amount(),
          idempotencyKey: this.idempotencyKey(),
        }));
      this.showResult(result);
      await this.session.refresh();
      this.scheduleRedirect(result.status === 'Completed' ? 2200 : 4500);
    } catch (err: any) {
      const body = err?.error;
      // 422 Failed comes back as an error response carrying the saga result body.
      if (body && typeof body.status === 'string' && body.cashoutId) {
        this.showResult(body as CashoutSagaResult);
        await this.session.refresh();
        return;
      }
      // 400 pre-check rejection: { error: 'cashout_rejected', code, params }.
      if (body && body.error === 'cashout_rejected') {
        this.submitError.set(this.mapRejection(body as CashoutRejection));
        if (body.code === 'insufficient_balance' || body.code === 'balance_unavailable') {
          void this.session.refresh(true);
        }
        return;
      }
      this.submitError.set(err?.status === 0 ? this.t.errLoginNetwork : this.t.errGeneric);
    } finally {
      this.submitting.set(false);
    }
  }

  private showResult(result: CashoutSagaResult) {
    this.result.set(result);
    this.confirmed.set(true);
    // This visit's request has reached a terminal state — a fresh key for anything that follows.
    this.nonce.set(newUuid());
  }

  private scheduleRedirect(ms: number) {
    if (this.redirectTimer) clearTimeout(this.redirectTimer);
    this.redirectTimer = setTimeout(() => {
      this.redirectTimer = null;
      this.router.navigate(['/history']);
    }, ms);
  }

  /** Translate a saga rejection code with its params; unknown codes fall back to the generic text. */
  private mapRejection(r: CashoutRejection): string {
    const key = `errCashout_${r.code}`;
    const has = Object.prototype.hasOwnProperty.call(this.t, key);
    const p = r.params ?? {};
    const money = (v: unknown) => typeof v === 'number' ? this.svc.formatGel(v)
      : typeof v === 'string' && v !== '' && !Number.isNaN(Number(v)) ? this.svc.formatGel(Number(v)) : String(v ?? '');
    const supportedRaw = p['supported'];
    const supported = Array.isArray(supportedRaw)
      ? supportedRaw.map((b: any) => typeof b === 'string' ? b : b?.bankLabel ?? b?.bankCode ?? '').filter(Boolean).join(', ')
      : this.supportedBanksLabel();
    return this.svc.tr(has ? key : 'errCashout_rejected', {
      min: money(p['min']),
      max: money(p['max']),
      fee: money(p['fee']),
      limit: money(p['limit']),
      usedToday: money(p['usedToday']),
      balance: money(p['balance']),
      supported: supported || '—',
    });
  }

  /** Failed result copy: the bank's reason when we have one, otherwise a translated explanation. */
  failureText(r: CashoutSagaResult): string {
    return r.failureReason?.trim() || this.t.cashoutFailedGeneric;
  }

  goHistory() {
    if (this.redirectTimer) { clearTimeout(this.redirectTimer); this.redirectTimer = null; }
    this.router.navigate(['/history']);
  }

  formatGel(n: number) { return this.svc.formatGel(n); }
}
