import { Component, computed, inject, OnInit, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminMockService } from '../../../services/admin-mock.service';
import { AdminApiService, ApiCard, ApiDriver, ApiPark, CashoutSagaResult } from '../../../services/admin-api.service';
import { AdminParkContextService } from '../../../services/admin-park-context.service';
import { AdminI18nService } from '../../../services/admin-i18n.service';

interface SubmittedEvent {
  parkId: string;
  driverId: string;
  amount: number;
  cardId: string;
  result: CashoutSagaResult;
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

  get t() { return this.i18n.t; }

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

  // Idempotency key — generated once per modal lifetime so retries dedupe correctly.
  private readonly idempotencyKey = crypto.randomUUID();

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
        this.loadError.set('No parks configured on the backend.');
      }
    } catch (err: any) {
      this.loadError.set(`Failed to load parks: ${err?.message ?? err}`);
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
      this.loadError.set(`Failed to load drivers: ${err?.message ?? err}`);
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

  canProceedStep2 = computed(() => {
    const d = this.selectedDriver();
    const a = this.amount();
    const max = this.parkMax();
    return d !== null && a >= this.parkMin() && (max === null || a <= max) && a <= this.driverBalance();
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
    if (balance > 0) this.amountStr.set(String(Math.floor(balance)));
  }

  next() {
    if (this.step() === 2 && this.canProceedStep2()) this.step.set(3);
  }

  back() {
    if (this.step() === 2) this.step.set(1);
    else if (this.step() === 3) this.step.set(2);
  }

  async confirm() {
    const d = this.selectedDriver();
    const card = this.selectedCard();
    const parkId = this.selectedParkId();
    if (!d || !card || !parkId) return;

    this.submitting.set(true);
    this.submitError.set(null);
    try {
      const result = await this.api.createCashout(parkId, {
        driverId: d.id,
        cardId: card.id,
        amount: this.amount(),
        idempotencyKey: this.idempotencyKey,
        initiatedBy: this.svc.user().name,
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
      // HttpErrorResponse: surface server message if present
      const serverMsg = err?.error?.message ?? err?.error?.error ?? err?.message ?? 'Unknown error';
      this.submitError.set(serverMsg);
    } finally {
      this.submitting.set(false);
    }
  }

  closeModal() {
    this.close.emit();
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  initials(n: string | null)  { return this.svc.initials(n ?? '??'); }
}
