import { Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AdminAuthService } from '../../services/admin-auth.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService } from '../../services/admin-api.service';

interface ParkForm {
  legalEntityName: string;
  taxId: string;
  phone: string;
  bankAccountIban: string;
  yandexClientId: string;
  yandexApiKey: string;
  yandexParkId: string;
}

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

  // ── Edit state ────────────────────────────────────────────────────
  editing = signal(false);
  saving = signal(false);
  editError = signal<string | null>(null);
  form = signal<ParkForm>({
    legalEntityName: '', taxId: '', phone: '', bankAccountIban: '',
    yandexClientId: '', yandexApiKey: '', yandexParkId: '',
  });

  constructor() {
    this.parkCtx.ensureLoaded();
  }

  readonly initials = computed(() => {
    const name = this.admin()?.name ?? this.admin()?.email ?? 'Admin';
    return name.split(/[\s@]/).slice(0, 2).map(s => s[0] ?? '').join('').toUpperCase();
  });

  roleLabel(role: string | undefined): string {
    if (role === 'super_admin') return this.t['roleSuperAdmin'];
    return this.t['roleParkManager'];
  }

  modelLabel(model: string | undefined): string {
    switch (model) {
      case 'ModelA5': return 'Model A.5';
      case 'ModelA':  return 'Model A';
      case 'ModelB':  return 'Model B';
      default:        return model ?? '—';
    }
  }

  statusLabel(status: string | undefined): string {
    if (!status) return '—';
    return (this.t as Record<string, string>)[status.toLowerCase()] ?? status;
  }

  formatGel(n: number, d = 0) { return this.svc.formatGel(n, d); }

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
      });
      await this.parkCtx.refresh();
      this.editing.set(false);
    } catch (err: any) {
      const code = err?.error?.error;
      this.editError.set(
        code === 'invalid_tax_id' ? this.t['parkErrInvalidTaxId']
        : code === 'invalid_phone' ? this.t['parkErrInvalidPhone']
        : code === 'invalid_iban' ? this.t['parkErrInvalidIban']
        : err?.error?.message ?? err?.message ?? this.t['parkErrSave']
      );
    } finally {
      this.saving.set(false);
    }
  }

  logout() {
    this.auth.logout();
    this.router.navigate(['/admin/login']);
  }
}
