import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService, ApiDriver, ApiCashout, ApiCard, UpdateDriverBody } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { downloadCsv, isoDateLocal } from '../../shared/csv';

/** Backend enum: Active | Suspended | Pending. */
type DriverStatus = 'active' | 'pending' | 'suspended';
type StatusFilter = 'all' | DriverStatus;
type SortKey = 'name' | 'balance' | 'lastSeen' | 'cashedOut';

/** Display shape adapted from ApiDriver. */
interface DisplayDriver {
  id: string;
  name: string;
  hasName: boolean;
  phone: string;
  yandexProfileId: string;
  status: DriverStatus;
  balance: number;
  /** Cashed out in the last 30 days — derived from the park's most recent 200 cashouts. */
  totalCashedOut30d: number;
  cashoutCount30d: number;
  /** Most recent cashout, or null when the driver has never cashed out. */
  lastSeenAt: Date | null;
  carPlate: string;
  /** Active payout destinations (IBANs), default first. */
  cards: ApiCard[];
}

const WINDOW_MS = 30 * 24 * 3600 * 1000;

// Mirrors backend PersonName.LooksLikeSamePerson for the live hint in the add-account form.
// Georgian/Cyrillic → lowercase Latin, words compared as sets (order/middle name irrelevant).
const GEO: Record<string, string> = {
  'ა':'a','ბ':'b','გ':'g','დ':'d','ე':'e','ვ':'v','ზ':'z','თ':'t','ი':'i','კ':'k','ლ':'l','მ':'m','ნ':'n','ო':'o',
  'პ':'p','ჟ':'zh','რ':'r','ს':'s','ტ':'t','უ':'u','ფ':'p','ქ':'k','ღ':'gh','ყ':'q','შ':'sh','ჩ':'ch','ც':'ts',
  'ძ':'dz','წ':'ts','ჭ':'ch','ხ':'kh','ჯ':'j','ჰ':'h',
};
const CYR: Record<string, string> = {
  'а':'a','б':'b','в':'v','г':'g','д':'d','е':'e','ё':'e','ж':'zh','з':'z','и':'i','й':'i','к':'k','л':'l','м':'m',
  'н':'n','о':'o','п':'p','р':'r','с':'s','т':'t','у':'u','ф':'f','х':'kh','ц':'ts','ч':'ch','ш':'sh','щ':'sh',
  'ъ':'','ы':'y','ь':'','э':'e','ю':'yu','я':'ya',
};
const FOLDS: Array<[string, string]> = [
  ['ph', 'p'], ['th', 't'], ['gh', 'g'], ['kh', 'h'], ['zh', 'j'], ['dz', 'z'],
  ['ts', 'c'], ['tz', 'c'], ['ch', 'c'], ['sh', 's'],
  ['q', 'k'], ['y', 'i'], ['w', 'v'], ['f', 'p'], ['x', 'ks'],
];
function translit(word: string): string {
  let out = '';
  for (const ch of word.normalize('NFD')) {
    if (GEO[ch] !== undefined) { out += GEO[ch]; continue; }
    const lo = ch.toLowerCase();
    if (CYR[lo] !== undefined) { out += CYR[lo]; continue; }
    if (lo >= 'a' && lo <= 'z') out += lo;
  }
  // Same folding as backend PersonName.Fold: common Latin spelling variants → one letter, no doubles.
  for (const [from, to] of FOLDS) out = out.split(from).join(to);
  return out.replace(/(.)\1+/g, '$1');
}
function nameWords(name: string): Set<string> {
  return new Set(name.split(/[\s\-,.'’]+/).map(translit).filter(w => w.length >= 2));
}
export function namesLookAlike(a: string, b: string): boolean {
  const wa = nameWords(a), wb = nameWords(b);
  if (wa.size === 0 || wb.size === 0) return false;
  const [shorter, longer] = wa.size <= wb.size ? [wa, wb] : [wb, wa];
  for (const w of shorter) if (!longer.has(w)) return false;
  return true;
}


@Component({
  selector: 'app-admin-drivers',
  imports: [FormsModule, RouterLink],
  templateUrl: './drivers.html',
  styleUrl: './drivers.scss',
})
export class DriversComponent {
  svc = inject(AdminMockService); // formatters only
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private i18n = inject(AdminI18nService);
  private router = inject(Router);

  get t() { return this.i18n.t; }

  search       = signal('');
  status       = signal<StatusFilter>('all');
  sortKey      = signal<SortKey>('balance');
  sortDir      = signal<'asc' | 'desc'>('desc');
  selectedId   = signal<string | null>(null);

  loading = signal(true);
  loadError = signal<string | null>(null);
  raw = signal<ApiDriver[]>([]);
  private driverCashouts = signal<ApiCashout[]>([]);

  readonly firstLoad = computed(() => this.loading() && this.raw().length === 0 && this.loadError() === null);

  constructor() {
    this.parkCtx.ensureLoaded();
    effect(() => {
      const parkId = this.parkCtx.currentParkId();
      if (parkId) this.fetchFor(parkId);
    });
  }

  refresh() {
    const parkId = this.parkCtx.currentParkId();
    if (parkId && !this.loading()) void this.fetchFor(parkId);
  }

  private async fetchFor(parkId: string) {
    this.loading.set(true);
    this.loadError.set(null);
    this.selectedId.set(null);
    try {
      const [driversResp, cashoutsResp] = await Promise.all([
        this.api.listDrivers(parkId),
        this.api.listCashouts(parkId, 200),
      ]);
      this.raw.set(driversResp.drivers);
      this.driverCashouts.set(cashoutsResp.cashouts);
    } catch (err: any) {
      this.loadError.set(err?.error?.message ?? this.t.errLoadDrivers);
    } finally {
      this.loading.set(false);
    }
  }

  private toStatus(s: string | undefined): DriverStatus {
    const lower = (s ?? '').toLowerCase();
    return lower === 'suspended' ? 'suspended' : lower === 'pending' ? 'pending' : 'active';
  }

  drivers = computed<DisplayDriver[]>(() => {
    const cashouts = this.driverCashouts();
    const since = Date.now() - WINDOW_MS;
    return this.raw().map(d => {
      const mine = cashouts
        .filter(c => c.driverId === d.id)
        .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
      const recent = mine.filter(c => new Date(c.createdAt).getTime() >= since);
      return {
        id: d.id,
        name: d.name ?? this.t.unnamed,
        hasName: !!d.name,
        phone: d.phone ?? '—',
        yandexProfileId: d.yandexProfileId ?? '—',
        status: this.toStatus(d.status),
        balance: d.yandex?.balance ?? 0,
        totalCashedOut30d: recent.reduce((s, c) => s + c.amount, 0),
        cashoutCount30d: recent.length,
        lastSeenAt: mine[0] ? new Date(mine[0].createdAt) : null,
        carPlate: d.yandex?.carPlate ?? '—',
        cards: d.cards ?? [],
      };
    });
  });

  filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    const status = this.status();
    let list = this.drivers().slice();
    if (status !== 'all') list = list.filter(d => d.status === status);
    if (term) {
      list = list.filter(d =>
        d.name.toLowerCase().includes(term) ||
        d.phone.toLowerCase().includes(term) ||
        d.carPlate.toLowerCase().includes(term)
      );
    }
    const key = this.sortKey();
    const dir = this.sortDir() === 'asc' ? 1 : -1;
    list.sort((a, b) => {
      switch (key) {
        case 'name':       return a.name.localeCompare(b.name) * dir;
        case 'balance':    return (a.balance - b.balance) * dir;
        case 'cashedOut':  return (a.totalCashedOut30d - b.totalCashedOut30d) * dir;
        case 'lastSeen':   return ((a.lastSeenAt?.getTime() ?? 0) - (b.lastSeenAt?.getTime() ?? 0)) * dir;
      }
    });
    return list;
  });

  counts = computed(() => {
    const all = this.drivers();
    return {
      all:       all.length,
      active:    all.filter(d => d.status === 'active').length,
      pending:   all.filter(d => d.status === 'pending').length,
      suspended: all.filter(d => d.status === 'suspended').length,
    };
  });

  selected = computed<DisplayDriver | null>(() => {
    const id = this.selectedId();
    return id ? this.drivers().find(d => d.id === id) ?? null : null;
  });

  selectedCashouts = computed(() => {
    const id = this.selectedId();
    if (!id) return [];
    return this.driverCashouts()
      .filter(c => c.driverId === id)
      .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())
      .slice(0, 6)
      .map(c => ({
        id: c.id,
        amount: c.amount,
        fee: c.fee,
        net: c.amount - c.fee,
        status: c.status.toLowerCase(),       // queued | processing | completed | failed | reviewrequired
        bankType: c.bankType,
        maskedPan: c.maskedPan,
        createdAt: new Date(c.createdAt),
      }));
  });

  cashoutStatusLabel(s: string): string {
    switch (s) {
      case 'queued':         return this.t.queued;
      case 'processing':     return this.t.processing;
      case 'completed':      return this.t.completed;
      case 'failed':         return this.t.failed;
      case 'reviewrequired': return this.t.reviewrequired;
      default:               return s;
    }
  }

  statusLabel(s: DriverStatus): string {
    switch (s) {
      case 'active':    return this.t.active;
      case 'pending':   return this.t.pending;
      case 'suspended': return this.t.suspended;
    }
  }

  sumThisMonth = computed(() =>
    this.filtered().reduce((s, d) => s + d.totalCashedOut30d, 0)
  );

  setSort(key: SortKey) {
    if (this.sortKey() === key) {
      this.sortDir.update(d => d === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortKey.set(key);
      this.sortDir.set(key === 'name' ? 'asc' : 'desc');
    }
  }

  setStatus(s: StatusFilter) { this.status.set(s); }

  open(id: string)   {
    this.selectedId.set(id);
    this.editing.set(false);
    this.editError.set(null);
    this.confirmSuspend.set(false);
    this.cancelAddCard();
    this.confirmRemoveCardId.set(null);
  }
  closeDetail()      {
    this.selectedId.set(null);
    this.editing.set(false);
    this.confirmSuspend.set(false);
    this.cancelAddCard();
    this.confirmRemoveCardId.set(null);
  }

  // ── Payout accounts (IBANs) ──────────────────────────────────────
  // The park may register an account in someone else's name (the driver app refuses that);
  // the backend then demands a reason and flags the account as third-party everywhere.
  addingCard = signal(false);
  cardBusy = signal(false);
  cardError = signal<string | null>(null);
  /** Set when the backend says the typed holder is not the driver: reveals the reason field. */
  cardNeedsReason = signal(false);
  cardForm = signal<{ iban: string; holderName: string; reason: string }>({ iban: '', holderName: '', reason: '' });
  confirmRemoveCardId = signal<string | null>(null);
  removingCardId = signal<string | null>(null);

  startAddCard() {
    const d = this.selected();
    this.cardForm.set({ iban: '', holderName: d?.hasName ? d.name : '', reason: '' });
    this.cardError.set(null);
    this.cardNeedsReason.set(false);
    this.addingCard.set(true);
  }

  cancelAddCard() {
    this.addingCard.set(false);
    this.cardError.set(null);
    this.cardNeedsReason.set(false);
  }

  setCardField<K extends keyof ReturnType<typeof this.cardForm>>(key: K, value: ReturnType<typeof this.cardForm>[K]) {
    this.cardForm.update(f => ({ ...f, [key]: value }));
  }

  /** Pure-client hint: the typed holder differs from the registered name, so a reason will be needed. */
  cardLooksThirdParty = computed(() => {
    const d = this.selected();
    const typed = this.cardForm().holderName.trim();
    if (!d?.hasName || !typed) return false;
    return !namesLookAlike(d.name, typed);
  });

  canSubmitCard = computed(() => {
    const f = this.cardForm();
    const ibanDigits = f.iban.replace(/\s+/g, '');
    if (ibanDigits.length < 22 || this.cardBusy()) return false;
    if ((this.cardLooksThirdParty() || this.cardNeedsReason()) && f.reason.trim().length < 3) return false;
    return true;
  });

  async submitAddCard() {
    const parkId = this.parkCtx.currentParkId();
    const d = this.selected();
    if (!parkId || !d || !this.canSubmitCard()) return;
    this.cardBusy.set(true);
    this.cardError.set(null);
    const f = this.cardForm();
    try {
      await this.api.addDriverCard(parkId, d.id, {
        iban: f.iban.replace(/\s+/g, '').toUpperCase(),
        holderName: f.holderName.trim() || undefined,
        makeDefault: d.cards.length === 0,
        reason: f.reason.trim() || undefined,
      });
      this.addingCard.set(false);
      this.cardNeedsReason.set(false);
      const keep = d.id;
      await this.fetchFor(parkId);
      this.selectedId.set(keep);
    } catch (err: any) {
      const code = err?.error?.error;
      if (code === 'third_party_reason_required') {
        this.cardNeedsReason.set(true);
        this.cardError.set(this.t.errThirdPartyReasonRequired);
      } else if (code === 'invalid_iban_format' || code === 'invalid_iban_checksum' || code === 'iban_required') {
        this.cardError.set(this.t.errInvalidIban);
      } else if (code === 'bank_not_supported') {
        this.cardError.set(this.t.errBankNotSupportedAdmin);
      } else if (code === 'iban_already_added') {
        this.cardError.set(this.t.errIbanExistsAdmin);
      } else if (code === 'holder_name_too_long') {
        this.cardError.set(this.t.errHolderTooLong);
      } else if (code === 'reason_too_long') {
        this.cardError.set(this.t.errReasonTooLong);
      } else {
        this.cardError.set(err?.error?.message ?? this.t.errAddAccount);
      }
    } finally {
      this.cardBusy.set(false);
    }
  }

  askRemoveCard(cardId: string) { this.confirmRemoveCardId.set(cardId); }
  cancelRemoveCard() { this.confirmRemoveCardId.set(null); }

  async removeCard(cardId: string) {
    const parkId = this.parkCtx.currentParkId();
    const d = this.selected();
    if (!parkId || !d || this.removingCardId()) return;
    this.removingCardId.set(cardId);
    this.confirmRemoveCardId.set(null);
    this.cardError.set(null);
    try {
      await this.api.removeDriverCard(parkId, d.id, cardId);
      const keep = d.id;
      await this.fetchFor(parkId);
      this.selectedId.set(keep);
    } catch (err: any) {
      this.cardError.set(err?.error?.message ?? this.t.errAddAccount);
    } finally {
      this.removingCardId.set(null);
    }
  }

  /** "admin:ops@swich.dev" → "ops@swich.dev", "driver:…" → the driver. */
  addedByLabel(v: string | null | undefined): string {
    if (!v) return '—';
    if (v.startsWith('driver:')) return this.t.driver;
    return v.replace(/^admin:/, '');
  }

  thirdPartyWarningText(): string {
    const d = this.selected();
    return this.t.thirdPartyWarning.replace('{name}', d?.hasName ? d.name : '—');
  }

  // ── Export ────────────────────────────────────────────────────────
  exportCsv() {
    const rows = this.filtered();
    if (rows.length === 0) return;
    const park = this.parkCtx.currentPark();
    downloadCsv(
      `paytaxi-drivers-${park?.slug ?? 'park'}-${isoDateLocal()}`,
      [this.t.fullName, this.t.phone, this.t.carPlate, this.t.yandexProfileId, this.t.status, this.t.colBalance,
       this.t.cashedOutThisMonth, this.t.cashoutCount, this.t.lastSeen],
      rows.map(d => [
        d.hasName ? d.name : '', d.phone, d.carPlate, d.yandexProfileId, this.statusLabel(d.status),
        d.balance.toFixed(2), d.totalCashedOut30d.toFixed(2), d.cashoutCount30d,
        d.lastSeenAt ? d.lastSeenAt.toISOString() : '',
      ]),
    );
  }

  /** "New cashout for {driver}" → Cashouts page opens the manual-cashout modal preselected. */
  newCashoutFor(driverId: string) {
    void this.router.navigate(['/admin/cashouts'], { queryParams: { driverId } });
  }

  // ── Edit + suspend/activate ──────────────────────────────────────
  editing = signal(false);
  saving = signal(false);
  editError = signal<string | null>(null);
  statusUpdating = signal(false);
  confirmSuspend = signal(false);

  editForm = signal<{ name: string; phone: string; yandexProfileId: string; status: DriverStatus }>({
    name: '', phone: '', yandexProfileId: '', status: 'active',
  });

  startEdit() {
    const d = this.selected();
    if (!d) return;
    this.editForm.set({
      name: d.hasName ? d.name : '',
      phone: d.phone === '—' ? '' : d.phone,
      yandexProfileId: d.yandexProfileId === '—' ? '' : d.yandexProfileId,
      status: d.status,
    });
    this.editError.set(null);
    this.editing.set(true);
  }

  cancelEdit() {
    this.editing.set(false);
    this.editError.set(null);
  }

  setEditField<K extends keyof ReturnType<typeof this.editForm>>(
    key: K, value: ReturnType<typeof this.editForm>[K]
  ) {
    this.editForm.update(f => ({ ...f, [key]: value }));
  }

  async saveEdit() {
    const parkId = this.parkCtx.currentParkId();
    const d = this.selected();
    if (!parkId || !d || this.saving()) return;
    this.saving.set(true);
    this.editError.set(null);
    try {
      const f = this.editForm();
      const body: UpdateDriverBody = {
        name: f.name.trim(),
        phone: f.phone.trim(),
        yandexProfileId: f.yandexProfileId.trim(),
        status: this.statusPascal(f.status),
      };
      await this.api.updateDriver(parkId, d.id, body);
      this.editing.set(false);
      await this.fetchFor(parkId);
    } catch (err: any) {
      const code = err?.error?.error;
      const msg =
        code === 'phone_already_registered'      ? this.t.drvErrPhoneDup
      : code === 'yandex_profile_already_linked' ? this.t.drvErrYandexDup
      : code === 'invalid_phone'                  ? this.t.drvErrInvalidPhone
      : err?.error?.message ?? this.t.drvErrSave;
      this.editError.set(msg);
    } finally {
      this.saving.set(false);
    }
  }

  askSuspend() {
    if (this.statusUpdating()) return;
    this.confirmSuspend.set(true);
  }

  cancelSuspend() {
    this.confirmSuspend.set(false);
  }

  async setDriverStatus(target: DriverStatus) {
    const parkId = this.parkCtx.currentParkId();
    const d = this.selected();
    if (!parkId || !d || this.statusUpdating()) return;
    this.confirmSuspend.set(false);
    this.statusUpdating.set(true);
    this.editError.set(null);
    try {
      await this.api.updateDriver(parkId, d.id, { status: this.statusPascal(target) });
      const keep = d.id;
      await this.fetchFor(parkId);
      this.selectedId.set(keep); // keep the drawer open on the same driver
    } catch (err: any) {
      this.editError.set(err?.error?.message ?? this.t.drvErrStatus);
    } finally {
      this.statusUpdating.set(false);
    }
  }

  /** Backend enum is PascalCase; UI uses lowercase. */
  private statusPascal(s: string): string {
    return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase();
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatRel(d: Date | null)   { return d ? this.svc.formatRelTime(d) : '—'; }
  initials(n: string)         { return this.svc.initials(n); }
}
