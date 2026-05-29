import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';
import { adminAuthGuard } from './admin/guards/admin-auth.guard';

export const routes: Routes = [
  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },

  // ── Driver app ────────────────────────────────────────────────────
  // Login lives outside the driver shell (no bottom nav)
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login/login').then(m => m.LoginComponent),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./shared/layouts/driver-layout/driver-layout').then(m => m.DriverLayoutComponent),
    children: [
      {
        path: 'dashboard',
        loadComponent: () => import('./features/dashboard/dashboard').then(m => m.DashboardComponent),
      },
      {
        path: 'cashout',
        loadComponent: () => import('./features/cashout/cashout').then(m => m.CashoutComponent),
      },
      {
        path: 'history',
        loadComponent: () => import('./features/history/history').then(m => m.HistoryComponent),
      },
      {
        path: 'profile',
        loadComponent: () => import('./features/profile/profile').then(m => m.ProfileComponent),
      },
      {
        path: 'notifications',
        loadComponent: () => import('./features/notifications/notifications').then(m => m.NotificationsComponent),
      },
    ],
  },

  // ── Admin console ─────────────────────────────────────────────────
  // Admin login lives outside the admin shell
  {
    path: 'admin/login',
    loadComponent: () => import('./admin/auth/admin-login').then(m => m.AdminLoginComponent),
  },
  {
    path: 'admin',
    canActivate: [adminAuthGuard],
    loadComponent: () =>
      import('./admin/layout/admin-layout').then(m => m.AdminLayoutComponent),
    children: [
      { path: '', redirectTo: 'overview', pathMatch: 'full' },
      {
        path: 'overview',
        loadComponent: () => import('./admin/pages/overview/overview').then(m => m.OverviewComponent),
      },
      {
        path: 'cashouts',
        loadComponent: () => import('./admin/pages/cashouts/cashouts').then(m => m.CashoutsComponent),
      },
      {
        path: 'drivers',
        loadComponent: () => import('./admin/pages/drivers/drivers').then(m => m.DriversComponent),
      },
      {
        path: 'onboarding',
        loadComponent: () => import('./admin/pages/onboarding/onboarding').then(m => m.OnboardingComponent),
      },
      {
        path: 'reconciliation',
        loadComponent: () => import('./admin/pages/reconciliation/reconciliation').then(m => m.ReconciliationComponent),
      },
      {
        path: 'reports',
        loadComponent: () => import('./admin/pages/reports/reports').then(m => m.ReportsComponent),
      },
      {
        path: 'settings',
        loadComponent: () => import('./admin/pages/settings/settings').then(m => m.SettingsComponent),
      },
      {
        path: 'add-park',
        loadComponent: () => import('./admin/pages/add-park/add-park').then(m => m.AddParkComponent),
      },
    ],
  },

  { path: '**', redirectTo: 'dashboard' },
];
