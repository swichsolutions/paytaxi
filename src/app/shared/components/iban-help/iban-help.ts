import { Component, inject, input, signal } from '@angular/core';
import { MockDataService } from '../../../core/services/mock-data.service';

/**
 * "Where do I find my IBAN?" — a collapsible helper under the IBAN field. Shows, per bank the
 * park pays out to, an optional real screenshot of that bank's app (public/iban-help/{tbc,bog}.png)
 * with a short instruction, plus the generic hint, in the driver's language.
 *
 * Screenshots are optional: a missing/failed image simply hides itself and the text stands
 * alone, so the helper works before the images exist and survives a bank app redesign.
 */
@Component({
  selector: 'app-iban-help',
  templateUrl: './iban-help.html',
  styleUrl: './iban-help.scss',
})
export class IbanHelpComponent {
  private readonly svc = inject(MockDataService);
  get t() { return this.svc.t as Record<string, string>; }

  /** Bank labels the park supports, e.g. ['TBC', 'BOG']. Only these get a per-bank block. */
  banks = input<string[]>([]);

  open = signal(false);
  toggle() { this.open.update(v => !v); }

  has(label: string) { return this.banks().includes(label); }

  /** Per-bank screenshot state: true once the image loaded, false when missing/failed. */
  private readonly loaded = signal<Record<string, boolean>>({});
  imageOk(bank: string) { return this.loaded()[bank] === true; }
  onImageLoad(bank: string) { this.loaded.update(m => ({ ...m, [bank]: true })); }
  onImageError(bank: string) { this.loaded.update(m => ({ ...m, [bank]: false })); }

  /** Served from public/iban-help/. Lower-case bank label = file name. */
  imageSrc(bank: string) { return `/iban-help/${bank.toLowerCase()}.png`; }
}
