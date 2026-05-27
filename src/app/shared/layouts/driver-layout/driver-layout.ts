import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { BottomNavComponent } from '../../components/bottom-nav/bottom-nav';

@Component({
  selector: 'app-driver-layout',
  imports: [RouterOutlet, BottomNavComponent],
  templateUrl: './driver-layout.html',
  styleUrl: './driver-layout.scss',
})
export class DriverLayoutComponent {}
