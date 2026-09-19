import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService, ApiYandexLookup, YandexRosterDriver } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';

interface OnboardingLookup {
  found: boolean;
  alreadyLinked?: boolean;
  name?: string;
  carPlate?: string;
  yandexBalance?: number;
}

@Component({
  selector: 'app-admin-onboarding',
  imports: [FormsModule],
  templateUrl: './onboarding.html',
  styleUrl: './onboarding.scss',
})
export class OnboardingComponent {
  svc = inject(AdminMockService);
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  router = inject(Router);
  private i18n = inject(AdminI18nService);

  get t() { return this.i18n.t; }

  step = signal<1 | 2 | 3>(1);
  phone = signal('');
  yandexId = signal('');
  lookupRunning = signal(false);
  lookupError = signal<string | null>(null);
  lookupResult = signal<OnboardingLookup | null>(null);

  // Editable fields populated from Yandex lookup
  driverName = signal('');
  carPlate   = signal('');
  iban       = signal('');
  holderName = signal('');
  creating   = signal(false);
  createError = signal<string | null>(null);

  constructor() {
    this.parkCtx.ensureLoaded();
  }

  // Computed park name for display
  readonly parkName = computed(() => this.parkCtx.currentPark()?.name ?? '');

  // ── Bulk sync from Yandex ─────────────────────────────────────────
  rosterLoading = signal(false);
  rosterLoaded = signal(false);
  rosterError = signal<string | null>(null);
  roster = signal<YandexRosterDriver[]>([]);
  selected = signal<Set<string>>(new Set());
  bulkBusy = signal(false);
  bulkResult = signal<{ created: number; skipped: number } | null>(null);

  readonly newDrivers = computed(() => this.roster().filter(d => !d.alreadyOnboarded));
  readonly selectedCount = computed(() => this.selected().size);

  async loadRoster() {
    const parkId = this.parkCtx.currentParkId();
    if (!parkId || this.rosterLoading()) return;
    this.rosterLoading.set(true);
    this.rosterError.set(null);
    this.bulkResult.set(null);
    try {
      const resp = await this.api.getYandexRoster(parkId);
      this.roster.set(resp.drivers);
      this.selected.set(new Set());
      this.rosterLoaded.set(true);
    } catch (err: any) {
      this.rosterError.set(err?.error?.message ?? this.t.errLoadRoster);
    } finally {
      this.rosterLoading.set(false);
    }
  }

  toggleSelect(id: string) {
    this.selected.update(s => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  selectAllNew() {
    const newIds = this.newDrivers().filter(d => !!d.phone).map(d => d.yandexProfileId);
    const all = this.selected().size === newIds.length && newIds.every(id => this.selected().has(id));
    this.selected.set(all ? new Set() : new Set(newIds));
  }

  async onboardSelected() {
    const parkId = this.parkCtx.currentParkId();
    const ids = [...this.selected()];
    if (!parkId || ids.length === 0 || this.bulkBusy()) return;
    this.bulkBusy.set(true);
    this.rosterError.set(null);
    try {
      const res = await this.api.bulkOnboard(parkId, ids);
      this.bulkResult.set({ created: res.createdCount, skipped: res.skippedCount });
      await this.loadRoster(); // refresh flags
    } catch (err: any) {
      this.rosterError.set(err?.error?.message ?? this.t.errBulkOnboard);
    } finally {
      this.bulkBusy.set(false);
    }
  }

  step1Valid = computed(() =>
    this.phone().replace(/\D/g, '').length >= 9 &&
    this.yandexId().trim().length >= 6 &&
    !!this.parkCtx.currentParkId()
  );

  // Car plate is display-only (it comes from Yandex and is never sent), so only the name is required.
  step2Valid = computed(() => this.driverName().trim().length > 0);

  formatPhone(raw: string): string {
    const digits = raw.replace(/\D/g, '').slice(0, 9);
    const parts = [digits.slice(0, 3), digits.slice(3, 6), digits.slice(6, 9)].filter(Boolean);
    return parts.join(' ');
  }

  onPhoneChange(v: string) {
    this.phone.set(this.formatPhone(v));
  }

  async lookup() {
    if (!this.step1Valid()) return;
    const parkId = this.parkCtx.currentParkId();
    if (!parkId) return;
    this.lookupRunning.set(true);
    this.lookupResult.set(null);
    this.lookupError.set(null);

    try {
      const r = await this.api.yandexLookup(parkId, this.yandexId().trim());
      this.lookupResult.set({
        found: true,
        alreadyLinked: r.alreadyLinked,
        name: r.name ?? this.t.noNameInYandex,
        carPlate: r.carPlate ?? '',
        yandexBalance: r.balance,
      });
      this.driverName.set(r.name ?? '');
      this.carPlate.set(r.carPlate ?? '');
      // Skip step 2 if already linked — the operator just wanted to verify.
      this.step.set(r.alreadyLinked ? 1 : 2);
    } catch (err: any) {
      if (err?.status === 404) {
        this.lookupResult.set({ found: false });
      } else {
        const msg = err?.error?.message ?? err?.error?.error ?? this.t.lookupFailed;
        this.lookupError.set(msg);
      }
    } finally {
      this.lookupRunning.set(false);
    }
  }

  async create() {
    if (!this.step2Valid()) return;
    const parkId = this.parkCtx.currentParkId();
    if (!parkId) return;
    this.creating.set(true);
    this.createError.set(null);
    try {
      await this.api.createDriver(parkId, {
        phone: '+995' + this.phone().replace(/\D/g, ''),
        yandexProfileId: this.yandexId().trim(),
        name: this.driverName().trim(),
        consentGiven: true, // operator confirms on driver's behalf at this step
        iban: this.iban().replace(/\s+/g, '') || undefined,
        holderName: this.holderName().trim() || undefined,
      });
      this.step.set(3);
    } catch (err: any) {
      const code = err?.error?.error;
      const msg =
        code === 'phone_already_registered'      ? this.t['obErrPhoneLinked']
      : code === 'yandex_profile_already_linked' ? this.t['obErrYandexLinked']
      : code === 'bank_not_supported' ? this.t['obErrBankNotSupported']
      : (code === 'invalid_iban_format' || code === 'invalid_iban_checksum') ? this.t['obErrInvalidIban']
      : err?.error?.message ?? this.t['obErrCreate'];
      this.createError.set(msg);
    } finally {
      this.creating.set(false);
    }
  }

  reset() {
    this.step.set(1);
    this.phone.set('');
    this.yandexId.set('');
    this.lookupResult.set(null);
    this.lookupError.set(null);
    this.driverName.set('');
    this.carPlate.set('');
    this.iban.set('');
    this.holderName.set('');
    this.createError.set(null);
  }

  goToDrivers() {
    this.router.navigate(['/admin/drivers']);
  }

  back() {
    if (this.step() === 2) this.step.set(1);
  }

  formatGel(n: number) { return this.svc.formatGel(n, 2); }
}
