import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AdminMockService } from '../../services/admin-mock.service';
import { AdminApiService, ApiYandexLookup } from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';

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

  step = signal<1 | 2 | 3>(1);
  phone = signal('');
  yandexId = signal('');
  lookupRunning = signal(false);
  lookupError = signal<string | null>(null);
  lookupResult = signal<OnboardingLookup | null>(null);

  // Editable fields populated from Yandex lookup
  driverName = signal('');
  carPlate   = signal('');
  notify     = signal(true);
  creating   = signal(false);
  createError = signal<string | null>(null);

  constructor() {
    this.parkCtx.ensureLoaded();
  }

  // Computed park name for display
  readonly parkName = computed(() => this.parkCtx.currentPark()?.name ?? '');

  step1Valid = computed(() =>
    this.phone().replace(/\D/g, '').length >= 9 &&
    this.yandexId().trim().length >= 6 &&
    !!this.parkCtx.currentParkId()
  );

  step2Valid = computed(() =>
    this.driverName().trim().length > 0 && this.carPlate().trim().length > 0
  );

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
        name: r.name ?? '(no name in Yandex)',
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
        const msg = err?.error?.message ?? err?.error?.error ?? err?.message ?? 'Lookup failed';
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
      });
      this.step.set(3);
    } catch (err: any) {
      const code = err?.error?.error;
      const msg =
        code === 'phone_already_registered'      ? 'This phone is already linked to a driver.'
      : code === 'yandex_profile_already_linked' ? 'This Yandex profile is already linked to a driver in this park.'
      : err?.error?.message ?? err?.message ?? 'Could not create the driver.';
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
    this.createError.set(null);
  }

  goToDrivers() {
    this.router.navigate(['/admin/drivers']);
  }

  back() {
    if (this.step() === 2) this.step.set(1);
  }
}
