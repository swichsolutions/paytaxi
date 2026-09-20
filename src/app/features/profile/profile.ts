import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MockDataService } from '../../core/services/mock-data.service';
import { AuthService } from '../../core/services/auth.service';
import { DriverNotificationService } from '../../core/services/notification.service';
import { DriverSessionService, SessionCard } from '../../core/services/driver-session.service';
import { Lang } from '../../core/mock/data';
import { IbanHelpComponent } from '../../shared/components/iban-help/iban-help';

@Component({
  selector: 'app-profile',
  imports: [IbanHelpComponent],
  templateUrl: './profile.html',
  styleUrl: './profile.scss',
})
export class ProfileComponent implements OnInit {
  private router = inject(Router);
  readonly svc = inject(MockDataService); // i18n + formatters
  readonly session = inject(DriverSessionService);
  private auth = inject(AuthService);
  private notifications = inject(DriverNotificationService);

  // ── Add bank account ─────────────────────────────────────────────
  adding = signal(false);
  newIban = signal('');
  newHolder = signal('');
  busy = signal(false);
  cardError = signal<string | null>(null);

  // ── Remove bank account (inline confirm) ─────────────────────────
  /** Card whose row currently shows the "Remove this account?" confirm. */
  confirmingRemoveId = signal<string | null>(null);
  removingId = signal<string | null>(null);

  async ngOnInit() {
    await this.session.ensureLoaded();
    this.newHolder.set(this.session.driver()?.name ?? '');
  }

  get t() { return this.svc.t; }
  get cards(): SessionCard[] { return this.session.cards(); }

  readonly driver = computed(() => {
    const d = this.session.driver();
    return d
      ? { name: d.name || this.t.unnamedDriver, phone: d.phone }
      : { name: '…', phone: '' };
  });

  readonly supportedBanksLabel = computed(() =>
    this.session.supportedBanks().map(b => b.bankLabel).join(', '));
  readonly supportedBankLabels = computed(() => this.session.supportedBanks().map(b => b.bankLabel));

  setLang(l: Lang) { this.svc.lang.set(l); }

  openAdd() {
    this.cardError.set(null);
    // Pre-fill with the profile name (as the park registered it) — editable, e.g. for a driver
    // whose bank account is under a Latin passport name.
    if (!this.newHolder().trim()) this.newHolder.set(this.session.driver()?.name ?? '');
    this.adding.set(true);
  }
  cancelAdd() { this.adding.set(false); this.newIban.set(''); this.cardError.set(null); }

  onIbanInput(v: string) {
    const raw = v.replace(/\s+/g, '').toUpperCase().slice(0, 22);
    this.newIban.set(raw.replace(/(.{4})/g, '$1 ').trim());
  }

  get ibanComplete(): boolean {
    return this.newIban().replace(/\s+/g, '').length === 22;
  }

  async saveCard() {
    if (this.busy()) return;
    this.busy.set(true);
    this.cardError.set(null);
    try {
      await this.session.addCard(this.newIban(), this.newHolder().trim(), this.cards.length === 0);
      this.adding.set(false);
      this.newIban.set('');
    } catch (err: any) {
      this.cardError.set(this.mapError(err));
    } finally {
      this.busy.set(false);
    }
  }

  /** Step 1 of removal: show the inline confirm on that row. */
  askRemove(id: string) {
    if (this.busy()) return;
    this.cardError.set(null);
    this.confirmingRemoveId.set(id);
  }

  cancelRemove() {
    this.confirmingRemoveId.set(null);
  }

  /** Step 2: the driver confirmed. */
  async removeCard(id: string) {
    if (this.busy()) return;
    this.busy.set(true);
    this.removingId.set(id);
    this.cardError.set(null);
    try {
      await this.session.removeCard(id);
      this.confirmingRemoveId.set(null);
    } catch (err: any) {
      this.cardError.set(this.mapError(err));
      this.confirmingRemoveId.set(null);
    } finally {
      this.removingId.set(null);
      this.busy.set(false);
    }
  }

  async makeDefault(id: string) {
    if (this.busy()) return;
    this.busy.set(true);
    try {
      await this.session.setDefaultCard(id);
    } catch (err: any) {
      this.cardError.set(this.mapError(err));
    } finally {
      this.busy.set(false);
    }
  }

  private mapError(err: any): string {
    const code = err?.error?.error;
    const t = this.t;
    if (code === 'invalid_iban_format' || code === 'invalid_iban_checksum' || code === 'iban_required')
      return t.errInvalidIban;
    if (code === 'bank_not_supported') {
      const supported = (err?.error?.supported ?? []).map((b: any) => b.bankLabel).join(', ');
      return `${t.errBankNotSupported} ${supported || this.supportedBanksLabel()}`;
    }
    if (code === 'iban_already_added') return t.errIbanExists;
    if (code === 'holder_name_mismatch') return t.errHolderNameMismatch;
    if (code === 'third_party_account_locked') return t.errThirdPartyLocked;
    if (code === 'card_has_pending_cashout') return t.errCardHasPending;
    return t.errGeneric;
  }

  logout() {
    this.auth.logout();
    this.notifications.clear();
    this.router.navigate(['/login']);
  }

  maskedPhone(phone: string): string {
    if (!phone) return '';
    return phone.slice(0, 8) + '** ***';
  }
}
