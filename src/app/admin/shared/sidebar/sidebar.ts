import { Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

interface NavItem {
  path: string;
  label: string;
  icon: string;
}

@Component({
  selector: 'app-admin-sidebar',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './sidebar.html',
  styleUrl: './sidebar.scss',
})
export class AdminSidebarComponent {
  collapsed = input<boolean>(false);

  primary: NavItem[] = [
    { path: '/admin/overview',   label: 'Overview',   icon: 'overview' },
    { path: '/admin/cashouts',   label: 'Cashouts',   icon: 'cashout' },
    { path: '/admin/drivers',    label: 'Drivers',    icon: 'drivers' },
    { path: '/admin/onboarding', label: 'Onboarding', icon: 'plus' },
  ];

  secondary: NavItem[] = [
    { path: '/admin/reports',  label: 'Reports',  icon: 'reports' },
    { path: '/admin/settings', label: 'Settings', icon: 'settings' },
  ];
}
