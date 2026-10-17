import { Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AdminApiService, ApiLicensee, CreateParkResult } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AdminAuthService } from '../../services/admin-auth.service';
import { environment } from '../../../../environments/environment';

interface NewParkForm {
  name: string;
  slug: string;
  bankProvider: string;
  cashoutFee: string;
  minCashoutAmount: string;
  maxCashoutAmount: string;
  dailyCashoutLimitPerDriver: string;
  yandexParkId: string;
  yandexClientId: string;
  yandexApiKey: string;
  legalEntityName: string;
  taxId: string;
  phone: string;
  bankAccountIban: string;
  managerEmail: string;
  managerName: string;
  managerPassword: string;
}

const EMPTY: NewParkForm = {
  name: '', slug: '', bankProvider: 'tbc',
  cashoutFee: '0.50', minCashoutAmount: '5', maxCashoutAmount: '', dailyCashoutLimitPerDriver: '',
  yandexParkId: '', yandexClientId: '', yandexApiKey: '',
  legalEntityName: '', taxId: '', phone: '', bankAccountIban: '',
  managerEmail: '', managerName: '', managerPassword: '',
};

@Component({
  selector: 'app-admin-add-park',
  templateUrl: './add-park.html',
  styleUrl: './add-park.scss',
})
export class AddParkComponent {
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private i18n = inject(AdminI18nService);
  private auth = inject(AdminAuthService);
  private router = inject(Router);

  get t() { return this.i18n.t; }

  readonly isSuperAdmin = computed(() => this.auth.admin()?.role === 'super_admin');
  /** The mock bank is for development: a production build does not offer it (the live server refuses it anyway). */
  readonly offerMock = !environment.production;

  form = signal<NewParkForm>({ ...EMPTY });
  saving = signal(false);
  error = signal<string | null>(null);
  created = signal<CreateParkResult | null>(null);

  // ── Which agreement settles the park ──────────────────────────────
  // Chosen here, never left to chance: a park silently left Swich-direct is settled under its own split from
  // the next run day — its cashouts stamped for good, outside the licensee's statement and barrier (§2.2–2.3).
  // Preselected: the one licensee in force; with none, Swich-direct; with several, Swich must pick.
  readonly licensees = signal<ApiLicensee[]>([]);
  readonly licenseesFailed = signal(false);
  /** A licensee id, 'direct', or '' while nothing is chosen. */
  readonly settlement = signal('');
  private readonly defaultSettlement = computed(() => {
    const active = this.licensees();
    return active.length === 1 ? active[0].id : active.length === 0 ? 'direct' : '';
  });

  readonly canSubmit = computed(() => {
    const f = this.form();
    const required = [
      f.name, f.yandexParkId, f.yandexClientId, f.yandexApiKey,
      f.legalEntityName, f.taxId, f.phone, f.bankAccountIban,
      f.managerEmail, f.managerPassword,
    ];
    // Without the list the API applies the same rule itself (the one licensee in force) and the result says which.
    const settlementChosen = this.settlement() !== '' || this.licenseesFailed();
    return required.every(v => v.trim().length > 0) && settlementChosen && !this.saving();
  });

  constructor() {
    if (this.isSuperAdmin()) void this.loadLicensees();
  }

  private async loadLicensees() {
    try {
      const all = (await this.api.listLicensees()).licensees;
      this.licensees.set(all.filter(l => l.isActive));
      this.settlement.set(this.defaultSettlement());
    } catch {
      this.licenseesFailed.set(true);
    }
  }

  /** The success screen's line: the agreement the API actually put the park under. */
  settledUnder(c: CreateParkResult): string {
    return c.licenseeName ? this.t.setViaLicensee.replace('{name}', c.licenseeName) : this.t.setSwichDirect;
  }

  setField<K extends keyof NewParkForm>(key: K, value: string) {
    this.form.update(f => ({ ...f, [key]: value }));
  }

  async submit() {
    if (!this.canSubmit()) return;
    this.saving.set(true);
    this.error.set(null);
    const f = this.form();
    try {
      const result = await this.api.createPark({
        name: f.name.trim(),
        slug: f.slug.trim() || undefined,
        operatingModel: 'ModelA',
        bankProvider: f.bankProvider.trim() || undefined,
        cashoutFee: Number(f.cashoutFee) || 0,
        minCashoutAmount: Number(f.minCashoutAmount) || 5,
        maxCashoutAmount: f.maxCashoutAmount.trim() ? Number(f.maxCashoutAmount) : null,
        dailyCashoutLimitPerDriver: f.dailyCashoutLimitPerDriver.trim() ? Number(f.dailyCashoutLimitPerDriver) : null,
        yandexParkId: f.yandexParkId.trim(),
        yandexClientId: f.yandexClientId.trim() || undefined,
        yandexApiKey: f.yandexApiKey.trim() || undefined,
        legalEntityName: f.legalEntityName.trim() || undefined,
        taxId: f.taxId.trim() || undefined,
        phone: f.phone.trim() || undefined,
        bankAccountIban: f.bankAccountIban.trim(),
        managerEmail: f.managerEmail.trim() || undefined,
        managerName: f.managerName.trim() || undefined,
        managerPassword: f.managerPassword.trim() || undefined,
        ...(this.settlement() === 'direct' ? { direct: true }
          : this.settlement() ? { licenseeId: this.settlement() } : {}),
      });
      await this.parkCtx.refresh();
      this.created.set(result);
    } catch (err: any) {
      const code = err?.error?.error;
      this.error.set(
        code === 'name_required' ? this.t['errNameRequired']
        : code === 'slug_taken' ? this.t['errSlugTaken']
        : code === 'email_taken' ? this.t['errEmailTaken']
        : code === 'weak_password' ? this.t['errWeakPassword']
        : code === 'yandex_park_id_required' ? this.t['errYandexParkIdRequired']
        : code === 'invalid_tax_id' ? this.t['parkErrInvalidTaxId']
        : code === 'invalid_phone' ? this.t['parkErrInvalidPhone']
        : code === 'invalid_iban' ? this.t['parkErrInvalidIban']
        : code === 'iban_required' ? this.t['errIbanRequired']
        : code === 'invalid_fee' ? this.t['errFee']
        : code === 'invalid_min_cashout' ? this.t['errMinCashout']
        : code === 'invalid_max_cashout' ? this.t['errMaxCashout']
        : code === 'invalid_daily_limit' ? this.t['errDailyLimit']
        : code === 'licensee_required' ? this.t['errLicenseeRequired']
        : code === 'licensee_not_found' ? this.t['errLicenseeNotFound']
        : code === 'invalid_provider' ? this.t['errInvalidProvider']
        : code === 'unsupported_bank' ? this.t['errUnsupportedBank']
        : code === 'test_environment_not_allowed' ? this.t['errTestEnvironmentNotAllowed']
        : code === 'legal_details_required' ? this.legalRequiredText(err?.error?.missing)
        : err?.error?.message ?? err?.message ?? this.t['errCreatePark']
      );
    } finally {
      this.saving.set(false);
    }
  }

  /** The API's legal_details_required, with the missing fields named as on this form. */
  private legalRequiredText(missing: unknown): string {
    const label: Record<string, string> = {
      legalEntityName: this.t['legalEntity'], taxId: this.t['identificationCode'], phone: this.t['phone'],
    };
    const keys = Array.isArray(missing) ? missing as string[] : Object.keys(label);
    return this.t['parkErrLegalDetailsRequired'].replace('{fields}', keys.map(k => label[k] ?? k).join(', '));
  }

  addAnother() {
    this.form.set({ ...EMPTY });
    this.settlement.set(this.defaultSettlement());
    this.created.set(null);
    this.error.set(null);
  }

  goToConsole() {
    this.router.navigate(['/admin/overview']);
  }
}
