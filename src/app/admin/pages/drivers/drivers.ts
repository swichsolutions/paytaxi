import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService, ApiDriver, ApiCashout, UpdateDriverBody } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';

type StatusFilter = 'all' | 'active' | 'inactive' | 'suspended';
type SortKey = 'name' | 'balance' | 'lastSeen' | 'cashedOut';

/** Display shape adapted from ApiDriver to satisfy the existing template. */
interface DisplayDriver {
  id: string;
  name: string;
  phone: string;
  yandexProfileId: string;
  status: StatusFilter;
  balance: number;
  totalCashedOutMonth: number;
  cashoutCountMonth: number;
  lastSeenAt: Date;
  joinedAt: Date;
  parkId: string;
  carPlate: string;
}

@Component({
  selector: 'app-admin-drivers',
  imports: [FormsModule],
  templateUrl: './drivers.html',
  styleUrl: './drivers.scss',
})
export class DriversComponent {
  svc = inject(AdminMockService); // formatters only
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private i18n = inject(AdminI18nService);

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

  constructor() {
    this.parkCtx.ensureLoaded();
    effect(() => {
      const parkId = this.parkCtx.currentParkId();
      if (parkId) this.fetchFor(parkId);
    });
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
      this.loadError.set(`Could not load drivers: ${err?.message ?? err}`);
    } finally {
      this.loading.set(false);
    }
  }

  drivers = computed<DisplayDriver[]>(() => {
    const cashouts = this.driverCashouts();
    const monthAgo = Date.now() - 30 * 24 * 3600 * 1000;
    return this.raw().map(d => {
      const mine = cashouts.filter(c => c.driverId === d.id);
      const recent = mine.filter(c => new Date(c.createdAt).getTime() >= monthAgo);
      return {
        id: d.id,
        name: d.name ?? '(unnamed)',
        phone: d.phone ?? '—',
        yandexProfileId: d.yandexProfileId ?? '—',
        status: (d.status?.toLowerCase() as StatusFilter) ?? 'active',
        balance: d.yandex?.balance ?? 0,
        totalCashedOutMonth: recent.reduce((s, c) => s + c.amount, 0),
        cashoutCountMonth: recent.length,
        lastSeenAt: mine[0] ? new Date(mine[0].createdAt) : new Date(0),
        joinedAt: new Date(0),
        parkId: this.parkCtx.currentParkId() ?? '',
        carPlate: d.yandex?.carPlate ?? '—',
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
        case 'cashedOut':  return (a.totalCashedOutMonth - b.totalCashedOutMonth) * dir;
        case 'lastSeen':   return (a.lastSeenAt.getTime() - b.lastSeenAt.getTime()) * dir;
      }
    });
    return list;
  });

  counts = computed(() => {
    const all = this.drivers();
    return {
      all:       all.length,
      active:    all.filter(d => d.status === 'active').length,
      inactive:  all.filter(d => d.status === 'inactive').length,
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
        status: c.status.toLowerCase(),
        bankType: c.bankType,
        maskedPan: c.maskedPan,
        createdAt: new Date(c.createdAt),
      }));
  });

  sumThisMonth = computed(() =>
    this.filtered().reduce((s, d) => s + d.totalCashedOutMonth, 0)
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
  }
  closeDetail()      { this.selectedId.set(null); this.editing.set(false); }

  // ── Edit + suspend/activate ──────────────────────────────────────
  editing = signal(false);
  saving = signal(false);
  editError = signal<string | null>(null);
  statusUpdating = signal(false);

  editForm = signal({ name: '', phone: '', yandexProfileId: '', status: 'active' });

  startEdit() {
    const d = this.selected();
    if (!d) return;
    this.editForm.set({
      name: d.name === '(unnamed)' ? '' : d.name,
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
        code === 'phone_already_registered'      ? this.t['drvErrPhoneDup']
      : code === 'yandex_profile_already_linked' ? this.t['drvErrYandexDup']
      : code === 'invalid_phone'                  ? this.t['drvErrInvalidPhone']
      : err?.error?.message ?? err?.message ?? this.t['drvErrSave'];
      this.editError.set(msg);
    } finally {
      this.saving.set(false);
    }
  }

  async setDriverStatus(target: 'active' | 'inactive' | 'suspended') {
    const parkId = this.parkCtx.currentParkId();
    const d = this.selected();
    if (!parkId || !d || this.statusUpdating()) return;
    this.statusUpdating.set(true);
    try {
      await this.api.updateDriver(parkId, d.id, { status: this.statusPascal(target) });
      await this.fetchFor(parkId);
    } catch (err: any) {
      this.editError.set(err?.error?.message ?? err?.message ?? this.t['drvErrStatus']);
    } finally {
      this.statusUpdating.set(false);
    }
  }

  /** Backend enum is PascalCase; UI uses lowercase. */
  private statusPascal(s: string): string {
    return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase();
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  formatRel(d: Date)          { return this.svc.formatRelTime(d); }
  initials(n: string)         { return this.svc.initials(n); }
}
