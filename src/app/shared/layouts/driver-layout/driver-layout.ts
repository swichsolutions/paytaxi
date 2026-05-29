import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { BottomNavComponent } from '../../components/bottom-nav/bottom-nav';
import { DriverNotificationService } from '../../../core/services/notification.service';

@Component({
  selector: 'app-driver-layout',
  imports: [RouterOutlet, BottomNavComponent],
  templateUrl: './driver-layout.html',
  styleUrl: './driver-layout.scss',
})
export class DriverLayoutComponent implements OnInit, OnDestroy {
  private notifications = inject(DriverNotificationService);

  ngOnInit() {
    // Authenticated routes only (route guard already ensures token presence).
    this.notifications.startPolling();
  }

  ngOnDestroy() {
    this.notifications.stopPolling();
  }
}
