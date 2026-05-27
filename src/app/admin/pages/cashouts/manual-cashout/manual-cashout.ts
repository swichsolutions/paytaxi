import { Component, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminMockService } from '../../../services/admin-mock.service';
import { AdminDriver } from '../../../mock/admin-data';
import { BankType } from '../../../../core/mock/data';

interface MockCard {
  id: string;
  bankType: BankType;
  maskedPan: string;
  isDefault: boolean;
}

@Component({
  selector: 'app-manual-cashout',
  imports: [FormsModule],
  templateUrl: './manual-cashout.html',
  styleUrl: './manual-cashout.scss',
})
export class ManualCashoutComponent {
  svc = inject(AdminMockService);
  close = output<void>();
  submitted = output<{ driverId: string; amount: number; cardId: string }>();

  step = signal<1 | 2 | 3 | 4>(1);
  driverSearch = signal('');
  selectedDriver = signal<AdminDriver | null>(null);
  amountStr = signal('');
  selectedCardId = signal('card_001');
  submitting = signal(false);

  // Mock cards available for any driver (in a real impl this would be per-driver)
  mockCards: MockCard[] = [
    { id: 'card_001', bankType: 'BOG', maskedPan: '**** 4521', isDefault: true },
    { id: 'card_002', bankType: 'TBC', maskedPan: '**** 8834', isDefault: false },
  ];

  filteredDrivers = computed(() => {
    const term = this.driverSearch().trim().toLowerCase();
    let list = this.svc.drivers().filter(d => d.status === 'active');
    if (term) {
      list = list.filter(d =>
        d.name.toLowerCase().includes(term) ||
        d.phone.includes(term) ||
        d.carPlate.toLowerCase().includes(term),
      );
    }
    return list.slice(0, 8);
  });

  amount = computed(() => parseFloat(this.amountStr()) || 0);
  fee    = computed(() => Math.max(2, parseFloat((this.amount() * 0.01).toFixed(2))));
  net    = computed(() => Math.max(0, this.amount() - this.fee()));

  canProceedStep2 = computed(() => {
    const d = this.selectedDriver();
    const a = this.amount();
    return d !== null && a >= 5 && a <= d.balance;
  });

  selectedCard = computed(() => this.mockCards.find(c => c.id === this.selectedCardId()) ?? this.mockCards[0]);

  pickDriver(d: AdminDriver) {
    this.selectedDriver.set(d);
    this.step.set(2);
  }

  keypad = [['1','2','3'],['4','5','6'],['7','8','9'],['.','0','⌫']];

  pressKey(k: string) {
    if (k === '⌫') {
      this.amountStr.update(s => s.slice(0, -1));
      return;
    }
    if (k === '.' && this.amountStr().includes('.')) return;
    if (this.amountStr().length >= 7) return;
    this.amountStr.update(s => s + k);
  }

  setMax() {
    const d = this.selectedDriver();
    if (d) this.amountStr.set(String(Math.floor(d.balance)));
  }

  next() {
    if (this.step() === 2 && this.canProceedStep2()) this.step.set(3);
  }

  back() {
    if (this.step() === 2) this.step.set(1);
    else if (this.step() === 3) this.step.set(2);
  }

  confirm() {
    const d = this.selectedDriver();
    if (!d) return;
    this.submitting.set(true);
    setTimeout(() => {
      this.submitting.set(false);
      this.step.set(4);
      this.submitted.emit({ driverId: d.id, amount: this.amount(), cardId: this.selectedCardId() });
    }, 700);
  }

  closeModal() {
    this.close.emit();
  }

  formatGel(n: number, d = 2) { return this.svc.formatGel(n, d); }
  initials(n: string)         { return this.svc.initials(n); }
}
