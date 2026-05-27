import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MockDataService } from '../../core/services/mock-data.service';
import { AuthService } from '../../core/services/auth.service';
import { Lang } from '../../core/mock/data';

@Component({
  selector: 'app-profile',
  templateUrl: './profile.html',
  styleUrl: './profile.scss',
})
export class ProfileComponent {
  private router = inject(Router);
  readonly svc = inject(MockDataService);
  private auth = inject(AuthService);
  notificationsOn = signal(true);

  get t() { return this.svc.t; }
  get driver() { return this.svc.driver(); }
  get cards() { return this.svc.cards(); }

  setLang(l: Lang) { this.svc.lang.set(l); }

  removeCard(id: string) {
    this.svc.cards.update(cards => cards.filter(c => c.id !== id));
  }

  logout() {
    this.auth.logout();
    this.router.navigate(['/login']);
  }

  maskedPhone(phone: string): string {
    return phone.slice(0, 8) + '** ***';
  }
}
