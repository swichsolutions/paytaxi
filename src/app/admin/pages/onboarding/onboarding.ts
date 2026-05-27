import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AdminMockService } from '../../services/admin-mock.service';

interface YandexLookupResult {
  found: boolean;
  name?: string;
  carPlate?: string;
  yandexBalance?: number;
  rating?: number;
  ridesLast7Days?: number;
}

@Component({
  selector: 'app-admin-onboarding',
  imports: [FormsModule],
  templateUrl: './onboarding.html',
  styleUrl: './onboarding.scss',
})
export class OnboardingComponent {
  svc = inject(AdminMockService);
  router = inject(Router);

  step = signal<1 | 2 | 3>(1);
  phone = signal('');
  yandexId = signal('');
  lookupRunning = signal(false);
  lookupResult = signal<YandexLookupResult | null>(null);

  // Editable fields populated from Yandex lookup
  driverName = signal('');
  carPlate   = signal('');
  notify     = signal(true);
  creating   = signal(false);

  step1Valid = computed(() =>
    this.phone().replace(/\D/g, '').length >= 9 &&
    this.yandexId().trim().length >= 6
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

  // Mock Yandex Fleet lookup
  lookup() {
    if (!this.step1Valid()) return;
    this.lookupRunning.set(true);
    this.lookupResult.set(null);

    setTimeout(() => {
      this.lookupRunning.set(false);
      // 90% of the time return a "found" result; 10% return not found for demo
      const found = this.yandexId().toLowerCase() !== 'not_found';
      if (found) {
        const result: YandexLookupResult = {
          found: true,
          name: 'Vakhtang Bregadze',
          carPlate: 'WW-' + Math.floor(100 + Math.random() * 900) + '-XY',
          yandexBalance: 234.50,
          rating: 4.86,
          ridesLast7Days: 47,
        };
        this.lookupResult.set(result);
        this.driverName.set(result.name!);
        this.carPlate.set(result.carPlate!);
        this.step.set(2);
      } else {
        this.lookupResult.set({ found: false });
      }
    }, 900);
  }

  create() {
    if (!this.step2Valid()) return;
    this.creating.set(true);
    setTimeout(() => {
      this.creating.set(false);
      this.step.set(3);
    }, 700);
  }

  reset() {
    this.step.set(1);
    this.phone.set('');
    this.yandexId.set('');
    this.lookupResult.set(null);
    this.driverName.set('');
    this.carPlate.set('');
  }

  goToDrivers() {
    this.router.navigate(['/admin/drivers']);
  }

  back() {
    if (this.step() === 2) this.step.set(1);
  }
}
