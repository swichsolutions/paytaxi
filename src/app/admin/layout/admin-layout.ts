import { Component, DestroyRef, OnInit, PLATFORM_ID, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter, fromEvent } from 'rxjs';
import { AdminSidebarComponent } from '../shared/sidebar/sidebar';
import { AdminTopbarComponent } from '../shared/topbar/topbar';
import { AdminParkContextService } from '../services/admin-park-context.service';
import { AdminI18nService } from '../services/admin-i18n.service';

@Component({
  selector: 'app-admin-layout',
  imports: [RouterOutlet, AdminSidebarComponent, AdminTopbarComponent],
  templateUrl: './admin-layout.html',
  styleUrl: './admin-layout.scss',
})
export class AdminLayoutComponent implements OnInit {
  private platformId = inject(PLATFORM_ID);
  private router = inject(Router);
  private destroyRef = inject(DestroyRef);
  readonly parkCtx = inject(AdminParkContextService);
  private i18n = inject(AdminI18nService);

  get t() { return this.i18n.t; }

  // Open by default on desktop, closed on mobile.
  sidebarOpen = signal(true);

  ngOnInit() {
    if (!isPlatformBrowser(this.platformId)) return;

    const isPhone = () => window.innerWidth <= 768;
    this.sidebarOpen.set(!isPhone());

    // Auto-close the drawer after navigating on phone so the new page is visible.
    this.router.events
      .pipe(filter(e => e instanceof NavigationEnd), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => { if (isPhone()) this.sidebarOpen.set(false); });

    // Re-open the sidebar when the viewport grows back to desktop, so a user
    // who collapsed it on mobile and then resized doesn't end up with no nav.
    fromEvent(window, 'resize')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => { if (!isPhone() && !this.sidebarOpen()) this.sidebarOpen.set(true); });
  }

  /** True when the park list failed to load — every page would otherwise be blank. */
  parkLoadFailed(): boolean {
    return !this.parkCtx.loading() && this.parkCtx.error() !== null && this.parkCtx.parks().length === 0;
  }

  parkErrorText(): string {
    return this.parkCtx.error() === 'no_parks' ? this.t.noParksAccessible : this.t.errLoadParks;
  }

  retryParks() {
    void this.parkCtx.retry();
  }

  toggleSidebar() {
    this.sidebarOpen.update(v => !v);
  }

  closeSidebar() {
    this.sidebarOpen.set(false);
  }
}
