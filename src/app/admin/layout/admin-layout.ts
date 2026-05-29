import { Component, OnInit, PLATFORM_ID, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AdminSidebarComponent } from '../shared/sidebar/sidebar';
import { AdminTopbarComponent } from '../shared/topbar/topbar';

@Component({
  selector: 'app-admin-layout',
  imports: [RouterOutlet, AdminSidebarComponent, AdminTopbarComponent],
  templateUrl: './admin-layout.html',
  styleUrl: './admin-layout.scss',
})
export class AdminLayoutComponent implements OnInit {
  private platformId = inject(PLATFORM_ID);
  private router = inject(Router);

  // Open by default on desktop, closed on mobile.
  sidebarOpen = signal(true);

  ngOnInit() {
    if (!isPlatformBrowser(this.platformId)) return;

    const isPhone = () => window.innerWidth <= 768;
    this.sidebarOpen.set(!isPhone());

    // Auto-close the drawer after navigating on phone so the new page is visible.
    this.router.events.pipe(filter(e => e instanceof NavigationEnd)).subscribe(() => {
      if (isPhone()) this.sidebarOpen.set(false);
    });

    // Re-open the sidebar when the viewport grows back to desktop, so a user
    // who collapsed it on mobile and then resized doesn't end up with no nav.
    window.addEventListener('resize', () => {
      if (!isPhone() && !this.sidebarOpen()) this.sidebarOpen.set(true);
    });
  }

  toggleSidebar() {
    this.sidebarOpen.update(v => !v);
  }

  closeSidebar() {
    this.sidebarOpen.set(false);
  }
}
