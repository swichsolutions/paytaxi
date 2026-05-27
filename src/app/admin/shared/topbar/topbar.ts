import { Component, computed, inject, output } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs';

@Component({
  selector: 'app-admin-topbar',
  templateUrl: './topbar.html',
  styleUrl: './topbar.scss',
})
export class AdminTopbarComponent {
  toggle = output<void>();

  private router = inject(Router);

  private url = toSignal(
    this.router.events.pipe(
      filter(e => e instanceof NavigationEnd),
      map(e => (e as NavigationEnd).urlAfterRedirects),
      startWith(this.router.url),
    ),
  );

  pageTitle = computed(() => {
    const u = this.url() ?? '/admin/overview';
    if (u.includes('/admin/overview'))   return 'Overview';
    if (u.includes('/admin/cashouts'))   return 'Cashouts';
    if (u.includes('/admin/drivers'))    return 'Drivers';
    if (u.includes('/admin/onboarding')) return 'Onboarding';
    if (u.includes('/admin/reports'))    return 'Reports';
    if (u.includes('/admin/settings'))   return 'Settings';
    return 'Admin';
  });

  emitToggle() {
    this.toggle.emit();
  }

  logout() {
    this.router.navigate(['/admin/login']);
  }
}
