import { Component, computed, effect, inject, OnInit, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminMockService } from '../../../services/admin-mock.service';
import { AdminApiService, ApiCard, ApiDriver, ApiPark, CashoutSagaResult } from '../../../services/admin-api.service';
import { AdminParkContextService } from '../../../services/admin-park-context.service';
import { AdminI18nService } from '../../../services/admin-i18n.service';
import { AdminAuthService } from '../../../services/admin-auth.service';

interface SubmittedEvent {
  parkId: string;
  driverId: string;
  amount: number;
  cardId: string;
  result: CashoutSagaResult;
}

/** `400 { error: 'cashout_rejected', code, message, params }` from the saga. */
interface CashoutRejection {
  error: 'cashout_rejected';
  code: string;
  message?: string;
  params?: Record<string, unknown>;
}

@Component({
  selector: 'app-manual-cashout',
  imports: [FormsModule],
  templateUrl: './manual-cashout.html',
  styleUrl: './manual-cashout.scss',
})
export class ManualCashoutComponent implements OnInit {
  svc = inject(AdminMockService);
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private i18n = inject(AdminI18nService);
  private auth = inject(AdminAuthService);

  get t() { return this.i18n.t; }

  /** Driver to pre-select (deep link from the Drivers drawer). */
  presetDriverId = input<string | null>(null);

  close = output<void>();
  submitted = output<SubmittedEvent>();

  step = signal<1 | 2 | 3 | 4>(1);
  driverSearch = signal('');
  selectedDriver = signal<ApiDriver | null>(null);
  amountStr = signal('');
  selectedCardId = signal<string>('');
  submitting = signal(false);

  // Backend-loaded state
  loading = signal(true);
  loadError = signal<string | null>(null);
  parks = signal<ApiPark[]>([]);
  selectedParkId = signal<string>('');
  drivers = signal<ApiDriver[]>([]);
  result = signal<CashoutSagaResult | null>(null);
  submitError = signal<string | null>(null);

  /** Who the audit trail will show — the server derives it from the token; this is display only. */
  readonly initiatedByLabel = computed(() => {
    const a = this.auth.admin();
    return a?.name ?? a?.email ?? '—';
  });

  // Idempotency key — generated once per modal lifetime so retries dedupe correctly.
  private readonly idempotencyKey = crypto.randomUUID();

  constructor() {
    // Apply the preset once the roster for the selected park has arrived.
    effect(() => {
      const id = this.presetDriverId();
      const list = this.drivers();
      if (!id || list.length === 0 || this.selectedDriver()) return;
      const d = list.find(x => x.id === id);
      if (d && d.cards.length > 0) this.pickDriver(d);
    });
  }

  async ngOnInit() {
    try {
      const parks = await this.api.listParks();
      this.parks.set(parks);
      if (parks.length > 0) {
        // Default to the park the operator is currently viewing in the topbar
        // — not just the first one alphabetically. Prevents the easy mistake of
        // submitting a cashout to a different park than expected.
        const currentParkId = this.parkCtx.currentParkId();
        const defaultPark = parks.find(p => p.id === currentParkId) ?? parks[0];
        await this.selectPark(defaultPark.id);
      } else {
        this.loadError.set(this.t.noParksConfigured);
      }
    } catch (err: any) {
      this.loadError.set(err?.error?.message ?? this.t.errLoadParks);
    } finally {
      this.loading.set(false);
    }
  }

  async selectPark(parkId: string) {
    this.selectedParkId.set(parkId);
    this.selectedDriver.set(null);
    this.drivers.set([]);
    try {
      const resp = await this.api.listDrivers(parkId);
      this.drivers.set(resp.drivers);
    } catch (err: any) {
      this.loadError.set(err?.error?.message ?? this.t.errLoadDrivers);
    }
  }

  filteredDrivers = computed(() => {
    const term = this.driverSearch().trim().toLowerCase();
    let list = this.drivers().filter(d => d.status?.toLowerCase() === 'active');
    if (term) {
      list = list.filter(d =>
        (d.name ?? '').toLowerCase().includes(term) ||
        (d.yandex?.carPlate ?? '').toLowerCase().includes(term) ||
        (d.yandexProfileId ?? '').toLowerCase().includes(term),
      );
    }
    return list.slice(0, 8);
  });

  selectedPark = computed<ApiPark | null>(() =>
    this.parks().find(p => p.id === this.selectedParkId()) ?? null);

  // Flat per-park fee (0.50 GEL at launch) and limits, mirrored from the backend config.
  parkFee = computed(() => this.selectedPark()?.cashoutFee ?? 0.5);
  parkMin = computed(() => this.selectedPark()?.minCashoutAmount ?? 5);
  parkMax = computed(() => this.selectedPark()?.maxCashoutAmount ?? null);

  amount = computed(() => parseFloat(this.amountStr()) || 0);
  fee    = computed(() => this.amount() > 0 ? this.parkFee() : 0);
  net    = computed(() => Math.max(0, this.amount() - this.fee()));

  driverBalance = computed(() => this.selectedDriver()?.yandex?.balance ?? 0);

  belowMin = computed(() => this.amount() > 0 && this.amount() < this.parkMin());
  aboveMax = computed(() => {
    const max = this.parkMax();
    return max !== null && this.amount() > max;
  });

  canProceedStep2 = computed(() => {
    const d = this.selectedDriver();
    const a = this.amount();
    return d !== null && a > 0 && !this.belowMin() && !this.aboveMax() && a <= this.driverBalance();
  });

  selectedCard = computed<ApiCard | null>(() => {
    const cards = this.selectedDriver()?.cards ?? [];
    return cards.find(c => c.id === this.selectedCardId()) ?? cards[0] ?? null;
  });

  pickDriver(d: ApiDriver) {
    this.selectedDriver.set(d);
    // Default to first card (which is the default-flagged one, ordered server-side)
    this.selectedCardId.set(d.cards[0]?.id ?? '');
    this.step.set(2);
  }

  keypad = [['1','2','3'],['4','5','6'],['7','8','9'],['.','0','⌫']];

  pressKey(k: string) {
    if (k === '⌫') {
      this.amountStr.update(s => s.slice(0, -1));
      return;
    }
    if (k === '.' && this.amountStr().includes('.')) return;
    if (this.amountStr().length >= 7) return;
    this.amountStr.update(s => s + k);
  }

  setMax() {
    const balance = this.driverBalance();
    const cap = this.parkMax();
    const target = cap !== null ? Math.min(balance, cap) : balance;
    if (target > 0) this.amountStr.set(String(Math.floor(target)));
  }

  next() {
    if (this.step() === 2 && this.canProceedStep2()) this.step.set(3);
  }

  back() {
    if (this.submitting()) return;
    if (this.step() === 2) this.step.set(1);
    else if (this.step() === 3) this.step.set(2);
  }

  async confirm() {
    const d = this.selectedDriver();
    const card = this.selectedCard();
    const parkId = this.selectedParkId();
    if (!d || !card || !parkId || this.submitting()) return;

    this.submitting.set(true);
    this.submitError.set(null);
    try {
      // `initiatedBy` is intentionally absent: the server records the admin from the token.
      const result = await this.api.createCashout(parkId, {
        driverId: d.id,
        cardId: card.id,
        amount: this.amount(),
        idempotencyKey: this.idempotencyKey,
      });
      this.result.set(result);
      this.step.set(4);
      this.submitted.emit({
        parkId,
        driverId: d.id,
        amount: this.amount(),
        cardId: card.id,
        result,
      });
    } catch (err: any) {
      const body = err?.error;
      // 422 / 202 carry the saga result (Failed / ReviewRequired / Queued) — show it as an outcome.
      if (body && typeof body.status === 'string' && body.cashoutId) {
        this.result.set(body as CashoutSagaResult);
        this.step.set(4);
      } else {
        this.submitError.set(this.describeError(err));
      }
    } finally {
      this.submitting.set(false);
    }
  }

  /** Translate `cashout_rejected` codes; fall back to the server message, then a generic one. */
  private describeError(err: any): string {
    const body = err?.error as CashoutRejection | undefined;
    if (body?.error === 'cashout_rejected' && body.code) {
      const p = body.params ?? {};
      const gel = (v: unknown) => this.formatGel(Number(v) || 0, 2);
      switch (body.code) {
        case 'below_minimum':          return `${this.t.rejBelowMinimum} ${gel(p['min'])}`;
        case 'above_maximum':          return `${this.t.rejAboveMaximum} ${gel(p['max'])}`;
        case 'daily_limit_reached':    return `${this.t.rejDailyLimit} · ${gel(p['usedToday'])} / ${gel(p['limit'])} ${this.t.rejUsedToday}`;
        case 'insufficient_balance':   return `${this.t.rejInsufficientBalance} · ${gel(p['balance'])}`;
        case 'bank_not_supported': {
          const supported = Array.isArray(p['supported']) ? (p['supported'] as unknown[]).join(', ') : '';
          const detail = [p['bankCode'] ? String(p['bankCode']) : '', supported ? `${this.t.rejSupported}: ${supported}` : '']
            .filter(Boolean).join(' · ');
          return detail ? `${this.t.rejBankNotSupported} (${detail})` : this.t.rejBankNotSupported;
        }
        case 'cashout_in_flight':       return this.t.rejCashoutInFlight;
        case 'driver_not_linked':       return this.t.rejDriverNotLinked;
        case 'driver_inactive':         return this.t.rejDriverInactive;
        case 'park_inactive':           return this.t.rejParkInactive;
        case 'destination_removed':     return this.t.rejDestinationRemoved;
        case 'destination_no_iban':     return this.t.rejDestinationNoIban;
        case 'balance_unavailable':     return this.t.rejBalanceUnavailable;
        case 'yandex_read_only':        return this.t.rejYandexReadOnly;
        case 'idempotency_key_conflict':return this.t.rejIdempotencyConflict;
        case 'driver_not_found':        return this.t.rejDriverNotFound;
        case 'destination_not_found':   return this.t.rejDestinationNotFound;
        case 'amount_precision':        return this.t.rejAmountPrecision;
        case 'rejected':                return this.t.rejRejected;
        default:                        return body.message ?? this.t.rejRejected;
      }
    }
    return err?.error?.message ?? err?.error?.error ?? this.t.errUnknown;
  }

  closeModal() {
    // Never close mid-submit: the result (and the idempotency key) would be lost.
    if (this.submitting()) return;
    this.close.emit();
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  initials(n: string | null)  { return this.svc.initials(n ?? '??'); }
}
