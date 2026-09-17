import { Component, computed, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { AdminAuthService } from '../../services/admin-auth.service';

interface NavItem {
  path: string;
  key: string;
  icon: string;
  tag?: 'soon';
}

@Component({
  selector: 'app-admin-sidebar',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './sidebar.html',
  styleUrl: './sidebar.scss',
})
export class AdminSidebarComponent {
  collapsed = input<boolean>(false);

  private i18n = inject(AdminI18nService);
  private auth = inject(AdminAuthService);
  get t() { return this.i18n.t; }

  readonly isSuperAdmin = computed(() => this.auth.admin()?.role === 'super_admin');

  primary: NavItem[] = [
    { path: '/admin/overview',   key: 'navOverview',   icon: 'overview' },
    { path: '/admin/cashouts',   key: 'navCashouts',   icon: 'cashout' },
    { path: '/admin/drivers',    key: 'navDrivers',    icon: 'drivers' },
    { path: '/admin/onboarding', key: 'navOnboarding', icon: 'plus' },
  ];

  secondary: NavItem[] = [
    { path: '/admin/settlements',    key: 'navSettlements',    icon: 'settlements' },
    { path: '/admin/reconciliation', key: 'navReconciliation', icon: 'reconcile' },
    { path: '/admin/reports',        key: 'navReports',        icon: 'reports' },
    { path: '/admin/settings',       key: 'navSettings',       icon: 'settings' },
  ];
}
