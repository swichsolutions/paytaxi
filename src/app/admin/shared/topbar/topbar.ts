import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { filter, map, startWith } from 'rxjs';
import { AdminAuthService } from '../../services/admin-auth.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';

@Component({
  selector: 'app-admin-topbar',
  imports: [FormsModule],
  templateUrl: './topbar.html',
  styleUrl: './topbar.scss',
})
export class AdminTopbarComponent implements OnInit {
  toggle = output<void>();

  private router = inject(Router);
  private auth = inject(AdminAuthService);
  readonly parkCtx = inject(AdminParkContextService);

  readonly admin = computed(() => this.auth.admin());

  menuOpen = signal(false);

  ngOnInit() {
    this.parkCtx.ensureLoaded();
  }

  onParkChange(parkId: string) {
    this.parkCtx.setCurrentPark(parkId);
  }

  toggleMenu() {
    this.menuOpen.update(v => !v);
  }

  closeMenu() {
    this.menuOpen.set(false);
  }

  readonly adminName = computed(() => this.admin()?.name ?? this.admin()?.email ?? 'Admin');
  readonly adminRole = computed(() => {
    const a = this.admin();
    if (!a) return '';
    if (a.role === 'super_admin') return 'Super Admin';
    return a.parkName ? `Manager · ${a.parkName}` : 'Park Manager';
  });
  readonly adminInitials = computed(() => {
    const name = this.adminName();
    return name.split(/[\s@]/).slice(0, 2).map(s => s[0] ?? '').join('').toUpperCase();
  });

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
    this.menuOpen.set(false);
    this.auth.logout();
    this.router.navigate(['/admin/login']);
  }
}
