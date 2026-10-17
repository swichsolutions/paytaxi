import { Component, computed, effect, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AdminAuthService } from '../../services/admin-auth.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService, ApiBankAccount, ApiLicensee } from '../../services/admin-api.service';
import { AdminAlertsService } from '../../services/admin-alerts.service';
import { addDays, tbilisiToday } from '../../shared/tbilisi-time';

interface ParkForm {
  legalEntityName: string;
  taxId: string;
  phone: string;
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
  private alerts = inject(AdminAlertsService);

  get t() { return this.i18n.t; }

  readonly admin = computed(() => this.auth.admin());
  readonly park = computed(() => this.parkCtx.currentPark());

  // ── Park edit state ──────────────────────────────────────────────
  editing = signal(false);
  saving = signal(false);
  editError = signal<string | null>(null);
  form = signal<ParkForm>({
    legalEntityName: '', taxId: '', phone: '',
    yandexClientId: '', yandexApiKey: '', yandexParkId: '',
    cashoutFee: '0.50', minCashoutAmount: '5', maxCashoutAmount: '', dailyCashoutLimitPerDriver: '',
    swichSharePercent: '50', phase1SharePercent: '', phase1CapGel: '',
  });

  readonly isSuperAdmin = computed(() => this.auth.admin()?.role === 'super_admin');

  // ── Legal details (who the drivers' privacy notice names) ────────
  // The driver app shows the park as responsible for the driver's data under Profile → "Your park": its legal name,
  // identification code and phone. The API refuses an Active park without all three (legal_details_required) — on
  // any edit and on reactivation — so a park created before that rule is flagged here until they are filled in.
  readonly legalMissing = computed(() => {
    const p = this.park();
    if (!p) return [] as string[];
    return [
      !p.legalEntityName?.trim() ? 'legalEntityName' : null,
      !p.taxId?.trim() ? 'taxId' : null,
      !p.phone?.trim() ? 'phone' : null,
    ].filter((k): k is string => k !== null);
  });

  /** "Legal entity, Phone" — the field labels of the given API names, in the console's language. */
  legalFieldsText(keys: readonly string[]): string {
    const t = this.t as Record<string, string>;
    const label: Record<string, string> = { legalEntityName: t['legalEntity'], taxId: t['identificationCode'], phone: t['phone'] };
    return keys.map(k => label[k] ?? k).join(', ');
  }

  // ── Settlement assignment (licence agreement vs Swich-direct) ────
  /** A licensee park is settled with the licensee's other parks; its own split fields do not apply. */
  readonly viaLicensee = computed(() => !!this.park()?.licenseeId);
  licensees = signal<ApiLicensee[]>([]);
  assignEditing = signal(false);
  assignChoice = signal('');                 // licensee id, '' = Swich-direct
  assignBusy = signal(false);
  assignError = signal<string | null>(null);
  assignNote = signal<string | null>(null);

  /** Swich and the operator suspend / reactivate parks; a park manager only sees the status. */
  readonly canChangeStatus = computed(() => {
    const role = this.auth.admin()?.role;
    return role === 'super_admin' || role === 'operator';
  });

  // ── Park status (suspend / reactivate) ───────────────────────────
  statusConfirm = signal<'Suspended' | 'Active' | null>(null);
  statusReason = signal('');
  statusBusy = signal(false);
  statusError = signal<string | null>(null);
  /** After a suspension: how many payouts already debited from Yandex will still be paid. */
  statusInFlight = signal<number | null>(null);

  // ── Annex 3 §7: the park revokes the platform's authorisations ──
  // The park writes to the licensee and Swich; one of them records it here (the park's manager sees it). From its first day
  // nothing is paid or collected from the park's account. It can be withdrawn only before that day.
  /** Days of written notice before a revocation can take effect — the server's ParkAuthorisation.NoticeDays ([30]). */
  readonly noticeDays = 30;
  readonly today = signal(tbilisiToday());
  revokeOpen = signal(false);
  revokeNotice = signal('');               // "YYYY-MM-DD", Tbilisi calendar day
  revokeFrom = signal('');
  revokeReason = signal('');
  revokeBusy = signal(false);
  revokeError = signal<string | null>(null);
  withdrawConfirm = signal(false);
  withdrawReason = signal('');

  /** Display-only: the IBAN under Park details mirrors the primary payout account. */
  readonly primaryIban = computed(() =>
    this.accounts().find(a => a.isPrimary && a.isActive)?.iban
    ?? this.accounts().find(a => a.isPrimary)?.iban
    ?? this.park()?.bankAccountIban
    ?? null);

  /** Account awaiting a deactivate confirmation (D17). */
  confirmDeactivateId = signal<string | null>(null);

  // ── Payout accounts ──────────────────────────────────────────────
  accounts = signal<ApiBankAccount[]>([]);
  /**
   * The providers this server takes (from the payout-account list): no "mock" on the live server, where test rails are
   * refused. Until the list has loaded, the two real banks.
   */
  allowedProviders = signal<string[]>(['tbc', 'bog']);
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
      this.assignEditing.set(false);
      this.assignNote.set(null);
      this.revokeOpen.set(false);
      this.withdrawConfirm.set(false);
      this.revokeError.set(null);
    });
  }

  /** Swich only: pick the licensee (or Swich-direct) this park is settled under. */
  async startAssign() {
    const p = this.park();
    if (!p || !this.isSuperAdmin()) return;
    this.assignError.set(null);
    this.assignNote.set(null);
    this.assignChoice.set(p.licenseeId ?? '');
    this.assignEditing.set(true);
    if (this.licensees().length === 0) {
      try { this.licensees.set((await this.api.listLicensees()).licensees); }
      catch { this.assignError.set(this.t.setAssignErr); }
    }
  }

  cancelAssign() { this.assignEditing.set(false); this.assignError.set(null); }

  async saveAssign() {
    const p = this.park();
    if (!p || this.assignBusy()) return;
    this.assignBusy.set(true);
    this.assignError.set(null);
    try {
      const r = await this.api.assignParkLicensee(p.id, this.assignChoice() || null);
      await this.parkCtx.refresh();
      this.assignEditing.set(false);
      // §2.2: every fee stays under the arrangement in force when it was EARNED — the move applies to new cashouts only.
      // Cashouts completed before regimes were recorded carry none: they follow the assignment when they are settled.
      if (!r.changed) {
        this.assignNote.set(null);
      } else {
        const stays = r.unsettledEarnedBefore > 0
          ? this.t.setAssignUnsettled.replace('{amount}', this.formatGel(r.unsettledEarnedBefore, 2))
          : this.t.setAssignEarnedStay;
        const follows = r.unsettledUnstamped > 0
          ? ' ' + this.t.setAssignUnstamped.replace('{amount}', this.formatGel(r.unsettledUnstamped, 2))
          : '';
        this.assignNote.set(stays + follows);
      }
    } catch {
      this.assignError.set(this.t.setAssignErr);
    } finally {
      this.assignBusy.set(false);
    }
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

  /** Launch is Model A only; any legacy value is echoed as-is. */
  modelLabel(model: string | undefined): string {
    return model === 'ModelA' ? this.t['modelAOpt'] : (model ?? '—');
  }

  statusLabel(status: string | undefined): string {
    if (!status) return '—';
    return (this.t as Record<string, string>)[status.toLowerCase()] ?? status;
  }

  formatGel(n: number, d = 0) { return this.svc.formatGel(n, d); }

  formatDate(iso: string | null | undefined): string {
    if (!iso) return '—';
    // en-GB like every other console date (browsers often lack ka-GE data and fall back to US format).
    return new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
  }

  /** "admin:ops@swich.dev" → "ops@swich.dev". */
  actorLabel(by: string | null | undefined): string {
    return by ? by.replace(/^admin:/, '') : '';
  }

  // ── Park status ──────────────────────────────────────────────────

  openStatusConfirm(target: 'Suspended' | 'Active') {
    this.statusReason.set('');
    this.statusError.set(null);
    this.statusInFlight.set(null);
    this.statusConfirm.set(target);
  }

  cancelStatus() {
    this.statusConfirm.set(null);
    this.statusError.set(null);
  }

  readonly canSubmitStatus = computed(() =>
    !this.statusBusy() && (this.statusConfirm() !== 'Suspended' || this.statusReason().trim().length > 0));

  async submitStatus() {
    const p = this.park();
    const target = this.statusConfirm();
    if (!p || !target || !this.canSubmitStatus()) return;
    this.statusBusy.set(true);
    this.statusError.set(null);
    try {
      const r = await this.api.setParkStatus(p.id, target, this.statusReason().trim() || undefined);
      this.statusConfirm.set(null);
      this.statusInFlight.set(target === 'Suspended' ? r.inFlightCount : null);
      await Promise.all([this.parkCtx.refresh(), this.alerts.refresh()]);
    } catch (err: any) {
      const code = err?.error?.error;
      const t = this.t as Record<string, string>;
      this.statusError.set(
        code === 'reason_required' ? t['parkStatusErrReason'] :
        code === 'reason_too_long' ? t['parkStatusErrReasonLong'] :
        code === 'park_terminated' ? t['parkStatusErrTerminated'] :
        code === 'legal_details_required' ? this.legalRequiredText(err) :
        err?.status === 403 ? t['parkStatusErrForbidden'] :
        err?.error?.message ?? err?.message ?? t['parkErrSave']);
    } finally {
      this.statusBusy.set(false);
    }
  }

  // ── Annex 3 §7: revocation of the authorisations ────────────────

  /** The earliest first day without the authorisations: the notice period after the notice, and never before today. */
  readonly revokeEarliest = computed(() => {
    const notice = this.revokeNotice();
    const earliest = notice ? addDays(notice, this.noticeDays) : '';
    return earliest > this.today() ? earliest : this.today();
  });

  readonly canSubmitRevoke = computed(() => {
    const notice = this.revokeNotice();
    const from = this.revokeFrom();
    return !this.revokeBusy() && !!notice && notice <= this.today() && !!from && from >= this.revokeEarliest()
      && this.revokeReason().trim().length > 0;
  });

  openRevoke() {
    const today = tbilisiToday();
    this.today.set(today);
    this.revokeNotice.set(today);
    this.revokeFrom.set(addDays(today, this.noticeDays));
    this.revokeReason.set('');
    this.revokeError.set(null);
    this.withdrawConfirm.set(false);
    this.revokeOpen.set(true);
  }

  cancelRevoke() {
    this.revokeOpen.set(false);
    this.revokeError.set(null);
  }

  /** A later notice moves the earliest day: the chosen day follows it rather than silently falling short. */
  setRevokeNotice(value: string) {
    this.revokeNotice.set(value);
    if (value && this.revokeFrom() < this.revokeEarliest()) this.revokeFrom.set(this.revokeEarliest());
  }

  async submitRevoke() {
    const p = this.park();
    if (!p || !this.canSubmitRevoke()) return;
    this.revokeBusy.set(true);
    this.revokeError.set(null);
    try {
      await this.api.recordAuthorisationRevocation(p.id, {
        noticeOn: this.revokeNotice(), revokedFrom: this.revokeFrom(), reason: this.revokeReason().trim(),
      });
      this.revokeOpen.set(false);
      await Promise.all([this.parkCtx.refresh(), this.alerts.refresh()]);
    } catch (err: any) {
      this.revokeError.set(this.revocationError(err));
    } finally {
      this.revokeBusy.set(false);
    }
  }

  async withdrawRevocation() {
    const p = this.park();
    if (!p || this.revokeBusy()) return;
    this.revokeBusy.set(true);
    this.revokeError.set(null);
    try {
      await this.api.withdrawAuthorisationRevocation(p.id, this.withdrawReason().trim() || undefined);
      this.withdrawConfirm.set(false);
      this.withdrawReason.set('');
      await Promise.all([this.parkCtx.refresh(), this.alerts.refresh()]);
    } catch (err: any) {
      this.revokeError.set(this.revocationError(err));
      // In effect meanwhile (midnight passed), or withdrawn by someone else: show what stands now.
      if (err?.status === 409) { this.withdrawConfirm.set(false); await this.parkCtx.refresh(); }
    } finally {
      this.revokeBusy.set(false);
    }
  }

  private revocationError(err: any): string {
    const code = err?.error?.error;
    const t = this.t;
    switch (code) {
      case 'revocation_too_early':        return t.authErrTooEarly.replace('{date}', this.formatDay(err?.error?.earliest));
      case 'invalid_notice_date':         return t.authErrNoticeDate;
      case 'invalid_revoked_from':        return t.authErrFromDate;
      case 'revocation_in_past':          return t.authErrInPast;
      case 'reason_required':             return t.authErrReason;
      case 'reason_too_long':             return t.parkStatusErrReasonLong;
      case 'revocation_already_recorded': return t.authErrAlreadyRecorded;
      case 'revocation_in_effect':        return t.authErrInEffect;
      default: return err?.status === 403 ? t.authErrForbidden : (err?.error?.message ?? t.authErrGeneric);
    }
  }

  /** Whole days from today (Tbilisi) to a "YYYY-MM-DD" day. */
  daysUntil(day: string | null | undefined): number {
    if (!day) return 0;
    const at = (d: string) => Date.UTC(+d.slice(0, 4), +d.slice(5, 7) - 1, +d.slice(8, 10));
    return Math.max(0, Math.round((at(day) - at(tbilisiToday())) / 86_400_000));
  }

  /** "2026-11-13" → "13 Nov 2026": a calendar day, shown as such (no time zone to convert). */
  formatDay(day: string | null | undefined): string {
    if (!day) return '—';
    return new Date(`${day.slice(0, 10)}T00:00:00Z`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
  }

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
    const f = this.form();
    // Same rule as the API, said before the round trip: an active park keeps all three legal details.
    if (p.status === 'Active') {
      const blank = [
        !f.legalEntityName.trim() ? 'legalEntityName' : null,
        !f.taxId.trim() ? 'taxId' : null,
        !f.phone.trim() ? 'phone' : null,
      ].filter((k): k is string => k !== null);
      if (blank.length) {
        this.editError.set(this.t.parkErrLegalDetailsRequired.replace('{fields}', this.legalFieldsText(blank)));
        return;
      }
    }
    this.saving.set(true);
    this.editError.set(null);
    try {
      await this.api.updatePark(p.id, {
        legalEntityName: f.legalEntityName.trim(),
        taxId: f.taxId.trim(),
        phone: f.phone.trim(),
        yandexClientId: f.yandexClientId.trim(),
        yandexParkId: f.yandexParkId.trim(),
        yandexApiKey: f.yandexApiKey.trim(), // blank = keep existing
        // Fee is Swich-only (backend returns 403 otherwise) — only sent by super_admin.
        ...(this.isSuperAdmin() ? { cashoutFee: Number(f.cashoutFee) || 0 } : {}),
        minCashoutAmount: Number(f.minCashoutAmount) || 0,
        maxCashoutAmount: f.maxCashoutAmount.trim() ? Number(f.maxCashoutAmount) : 0,               // 0 = no limit
        dailyCashoutLimitPerDriver: f.dailyCashoutLimitPerDriver.trim() ? Number(f.dailyCashoutLimitPerDriver) : 0,
        // The per-park split only applies to Swich-direct parks; a licensee park follows the agreement.
        ...(this.isSuperAdmin() && !this.viaLicensee() ? {
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

  private legalRequiredText(err: any): string {
    const missing: string[] = Array.isArray(err?.error?.missing) ? err.error.missing : ['legalEntityName', 'taxId', 'phone'];
    return this.t.parkErrLegalDetailsRequired.replace('{fields}', this.legalFieldsText(missing));
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
      case 'legal_details_required': return this.legalRequiredText(err);
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
      if (r.allowedProviders?.length) this.allowedProviders.set(r.allowedProviders);
    } catch (err: any) {
      this.accountsError.set(err?.error?.message ?? this.t.errLoadAccounts);
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

  /** Activating is immediate; deactivating asks first (and warns when payouts are queued on the account). */
  async toggleActive(a: ApiBankAccount) {
    if (!a.isActive) { await this.mutateAccount(a, { isActive: true }); return; }
    if (this.confirmDeactivateId() !== a.id) { this.confirmDeactivateId.set(a.id); return; }
    this.confirmDeactivateId.set(null);
    await this.mutateAccount(a, { isActive: false });
  }

  cancelDeactivate() { this.confirmDeactivateId.set(null); }

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

  providerName(p: string): string {
    return p === 'mock' ? this.t.providerMockDev : p.toUpperCase();
  }

  private mapAccountError(err: any): string {
    const code = err?.error?.error;
    const t = this.t as Record<string, string>;
    switch (code) {
      // Test rails are for testing only: the live server takes TBC/BOG accounts and production credentials.
      case 'invalid_provider': return t['errInvalidProvider'];
      case 'unsupported_bank': return t['errUnsupportedBank'];
      case 'test_environment_not_allowed': return t['errTestEnvironmentNotAllowed'];
      case 'iban_required': return t['errIbanRequired'];
      case 'invalid_iban_format': return t['parkErrInvalidIban'];
      case 'invalid_iban_checksum': return t['errInvalidIbanChecksum'];
      case 'iban_already_added': return t['errIbanAdded'];
      // Payout credentials are written by park staff: the API opens no file path they name, and takes no oversized blob.
      case 'certificate_path_not_allowed': return t['errCertificatePathNotAllowed'];
      case 'certificate_too_large': return t['errCertificateTooLarge'];
      default: return err?.error?.message ?? err?.message ?? t['parkErrSave'];
    }
  }

  logout() {
    this.auth.logout();
    this.router.navigate(['/admin/login']);
  }
}
