import { Component, computed, effect, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AdminAuthService } from '../../services/admin-auth.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService, ApiBankAccount } from '../../services/admin-api.service';

interface ParkForm {
  legalEntityName: string;
  taxId: string;
  phone: string;
  bankAccountIban: string;
  yandexClientId: string;
  yandexApiKey: string;
  yandexParkId: string;
  cashoutFee: string;
  minCashoutAmount: string;
  maxCashoutAmount: string;
  dailyCashoutLimitPerDriver: string;
  swichSharePercent: string;
  phase1SharePercent: string;
  phase1CapGel: string;
}

interface AccountForm {
  iban: string;
  provider: string;
  holderName: string;
  label: string;
  credentialsJson: string;
}

const EMPTY_ACCOUNT: AccountForm = { iban: '', provider: '', holderName: '', label: '', credentialsJson: '' };

@Component({
  selector: 'app-admin-settings',
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
})
export class SettingsComponent {
  private auth = inject(AdminAuthService);
  readonly parkCtx = inject(AdminParkContextService);
  private i18n = inject(AdminI18nService);
  private router = inject(Router);
  private svc = inject(AdminMockService); // formatGel
  private api = inject(AdminApiService);

  get t() { return this.i18n.t; }

  readonly admin = computed(() => this.auth.admin());
  readonly park = computed(() => this.parkCtx.currentPark());

  // ── Park edit state ──────────────────────────────────────────────
  editing = signal(false);
  saving = signal(false);
  editError = signal<string | null>(null);
  form = signal<ParkForm>({
    legalEntityName: '', taxId: '', phone: '', bankAccountIban: '',
    yandexClientId: '', yandexApiKey: '', yandexParkId: '',
    cashoutFee: '0.50', minCashoutAmount: '5', maxCashoutAmount: '', dailyCashoutLimitPerDriver: '',
    swichSharePercent: '50', phase1SharePercent: '', phase1CapGel: '',
  });

  readonly isSuperAdmin = computed(() => this.auth.admin()?.role === 'super_admin');

  // ── Payout accounts ──────────────────────────────────────────────
  accounts = signal<ApiBankAccount[]>([]);
  accountsLoading = signal(false);
  accountsError = signal<string | null>(null);
  addingAccount = signal(false);
  accountBusy = signal<string | null>(null); // account id or 'new'
  accountForm = signal<AccountForm>({ ...EMPTY_ACCOUNT });

  constructor() {
    this.parkCtx.ensureLoaded();
    effect(() => {
      const parkId = this.parkCtx.currentParkId();
      if (parkId) this.loadAccounts(parkId);
    });
  }

  readonly initials = computed(() => {
    const name = this.admin()?.name ?? this.admin()?.email ?? 'Admin';
    return name.split(/[\s@]/).slice(0, 2).map(s => s[0] ?? '').join('').toUpperCase();
  });

  roleLabel(role: string | undefined): string {
    if (role === 'super_admin') return this.t['roleSuperAdmin'];
    if (role === 'operator') return this.t['roleOperator'];
    return this.t['roleParkManager'];
  }

  modelLabel(model: string | undefined): string {
    switch (model) {
      case 'ModelA':  return this.t['modelAOpt'];
      case 'ModelA5': return 'Model A.5 (legacy)';
      case 'ModelB':  return 'Model B';
      default:        return model ?? '—';
    }
  }

  statusLabel(status: string | undefined): string {
    if (!status) return '—';
    return (this.t as Record<string, string>)[status.toLowerCase()] ?? status;
  }

  formatGel(n: number, d = 0) { return this.svc.formatGel(n, d); }

  // ── Park details ─────────────────────────────────────────────────

  setField<K extends keyof ParkForm>(key: K, value: string) {
    this.form.update(f => ({ ...f, [key]: value }));
  }

  startEdit() {
    const p = this.park();
    if (!p) return;
    this.form.set({
      legalEntityName: p.legalEntityName ?? '',
      taxId: p.taxId ?? '',
      phone: p.phone ?? '',
      bankAccountIban: p.bankAccountIban ?? '',
      yandexClientId: p.yandexClientId ?? '',
      yandexApiKey: '', // write-only — never prefilled
      yandexParkId: p.yandexParkId ?? '',
      cashoutFee: String(p.cashoutFee ?? 0.5),
      minCashoutAmount: String(p.minCashoutAmount ?? 5),
      maxCashoutAmount: p.maxCashoutAmount === null || p.maxCashoutAmount === undefined ? '' : String(p.maxCashoutAmount),
      dailyCashoutLimitPerDriver: p.dailyCashoutLimitPerDriver === null || p.dailyCashoutLimitPerDriver === undefined ? '' : String(p.dailyCashoutLimitPerDriver),
      swichSharePercent: String(p.swichSharePercent ?? 50),
      phase1SharePercent: p.phase1SharePercent === null || p.phase1SharePercent === undefined ? '' : String(p.phase1SharePercent),
      phase1CapGel: p.phase1CapGel === null || p.phase1CapGel === undefined ? '' : String(p.phase1CapGel),
    });
    this.editError.set(null);
    this.editing.set(true);
  }

  cancelEdit() {
    this.editing.set(false);
    this.editError.set(null);
  }

  async save() {
    const p = this.park();
    if (!p || this.saving()) return;
    this.saving.set(true);
    this.editError.set(null);
    try {
      const f = this.form();
      await this.api.updatePark(p.id, {
        legalEntityName: f.legalEntityName.trim(),
        taxId: f.taxId.trim(),
        phone: f.phone.trim(),
        bankAccountIban: f.bankAccountIban.trim(),
        yandexClientId: f.yandexClientId.trim(),
        yandexParkId: f.yandexParkId.trim(),
        yandexApiKey: f.yandexApiKey.trim(), // blank = keep existing
        cashoutFee: Number(f.cashoutFee) || 0,
        minCashoutAmount: Number(f.minCashoutAmount) || 0,
        maxCashoutAmount: f.maxCashoutAmount.trim() ? Number(f.maxCashoutAmount) : 0,               // 0 = no limit
        dailyCashoutLimitPerDriver: f.dailyCashoutLimitPerDriver.trim() ? Number(f.dailyCashoutLimitPerDriver) : 0,
        ...(this.isSuperAdmin() ? {
          swichSharePercent: Number(f.swichSharePercent) || 0,
          phase1SharePercent: f.phase1SharePercent.trim() ? Number(f.phase1SharePercent) : -1,
          phase1CapGel: f.phase1CapGel.trim() ? Number(f.phase1CapGel) : 0,
        } : {}),
      });
      await this.parkCtx.refresh();
      this.editing.set(false);
    } catch (err: any) {
      this.editError.set(this.mapParkError(err));
    } finally {
      this.saving.set(false);
    }
  }

  private mapParkError(err: any): string {
    const code = err?.error?.error;
    const t = this.t as Record<string, string>;
    switch (code) {
      case 'invalid_tax_id': return t['parkErrInvalidTaxId'];
      case 'invalid_phone': return t['parkErrInvalidPhone'];
      case 'invalid_iban': return t['parkErrInvalidIban'];
      case 'invalid_fee': return t['errFee'];
      case 'invalid_min_cashout': return t['errMinCashout'];
      case 'invalid_max_cashout': return t['errMaxCashout'];
      case 'invalid_daily_limit': return t['errDailyLimit'];
      case 'invalid_share': return t['errShare'];
      default: return err?.error?.message ?? err?.message ?? t['parkErrSave'];
    }
  }

  // ── Payout accounts ──────────────────────────────────────────────

  private async loadAccounts(parkId: string) {
    this.accountsLoading.set(true);
    this.accountsError.set(null);
    try {
      const r = await this.api.listBankAccounts(parkId);
      this.accounts.set(r.accounts);
    } catch (err: any) {
      this.accountsError.set(err?.error?.message ?? err?.message ?? 'Could not load payout accounts.');
    } finally {
      this.accountsLoading.set(false);
    }
  }

  setAccountField<K extends keyof AccountForm>(key: K, value: string) {
    this.accountForm.update(f => ({ ...f, [key]: value }));
    if (key === 'iban') {
      // Provider follows the bank code inside the IBAN unless the operator overrides it.
      const code = value.replace(/\s+/g, '').toUpperCase().substring(4, 6);
      const inferred = code === 'TB' ? 'tbc' : code === 'BG' ? 'bog' : '';
      if (inferred && !this.accountForm().provider) this.accountForm.update(f => ({ ...f, provider: inferred }));
    }
  }

  openAddAccount() {
    const p = this.park();
    this.accountForm.set({ ...EMPTY_ACCOUNT, holderName: p?.legalEntityName ?? p?.name ?? '' });
    this.accountsError.set(null);
    this.addingAccount.set(true);
  }

  cancelAddAccount() {
    this.addingAccount.set(false);
    this.accountForm.set({ ...EMPTY_ACCOUNT });
  }

  readonly canSaveAccount = computed(() =>
    this.accountForm().iban.replace(/\s+/g, '').length === 22 && !this.accountBusy());

  async saveAccount() {
    const p = this.park();
    if (!p || !this.canSaveAccount()) return;
    this.accountBusy.set('new');
    this.accountsError.set(null);
    const f = this.accountForm();
    try {
      await this.api.createBankAccount(p.id, {
        iban: f.iban.replace(/\s+/g, ''),
        provider: f.provider || undefined,
        holderName: f.holderName.trim() || undefined,
        label: f.label.trim() || undefined,
        credentialsJson: f.credentialsJson.trim() || undefined,
      });
      this.addingAccount.set(false);
      this.accountForm.set({ ...EMPTY_ACCOUNT });
      await Promise.all([this.loadAccounts(p.id), this.parkCtx.refresh()]);
    } catch (err: any) {
      this.accountsError.set(this.mapAccountError(err));
    } finally {
      this.accountBusy.set(null);
    }
  }

  async toggleActive(a: ApiBankAccount) {
    await this.mutateAccount(a, { isActive: !a.isActive });
  }

  async makePrimary(a: ApiBankAccount) {
    await this.mutateAccount(a, { isPrimary: true });
  }

  private async mutateAccount(a: ApiBankAccount, body: { isActive?: boolean; isPrimary?: boolean }) {
    const p = this.park();
    if (!p || this.accountBusy()) return;
    this.accountBusy.set(a.id);
    this.accountsError.set(null);
    try {
      await this.api.updateBankAccount(p.id, a.id, body);
      await Promise.all([this.loadAccounts(p.id), this.parkCtx.refresh()]);
    } catch (err: any) {
      this.accountsError.set(this.mapAccountError(err));
    } finally {
      this.accountBusy.set(null);
    }
  }

  private mapAccountError(err: any): string {
    const code = err?.error?.error;
    const t = this.t as Record<string, string>;
    switch (code) {
      case 'iban_required': return t['errIbanRequired'];
      case 'invalid_iban_format': return t['parkErrInvalidIban'];
      case 'invalid_iban_checksum': return t['errInvalidIbanChecksum'];
      case 'iban_already_added': return t['errIbanAdded'];
      default: return err?.error?.message ?? err?.message ?? t['parkErrSave'];
    }
  }

  logout() {
    this.auth.logout();
    this.router.navigate(['/admin/login']);
  }
}
