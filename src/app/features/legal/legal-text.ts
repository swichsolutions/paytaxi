import { Component, computed, input } from '@angular/core';
import { fillParts } from './legal-config';
import { LegalLang } from './legal-doc';

/**
 * Renders a legal-document string with its {placeholders} filled from LEGAL_DETAILS. A blank value is
 * shown as a highlighted bracket with what is missing, so a draft can never pass for final.
 * Plain text only — no innerHTML anywhere on the legal pages.
 */
@Component({
  selector: 'legal-text',
  template: `@for (p of parts(); track $index) {@if (p.missing) {<mark class="legal-blank" [attr.title]="blankTitle()">[{{ labels()[p.missing] ?? p.missing }}]</mark>} @else {<ng-container>{{ p.text }}</ng-container>}}`,
  host: { style: 'display: contents' },
  styles: [`
    .legal-blank {
      padding: 0 4px;
      border-radius: 4px;
      background: rgba(255, 184, 0, 0.28);
      color: inherit;
      font-weight: 700;
      box-decoration-break: clone;
      -webkit-box-decoration-break: clone;
    }
    @media print { .legal-blank { background: none; border: 1px dashed #000; } }
  `],
})
export class LegalTextComponent {
  readonly text = input.required<string>();
  readonly lang = input.required<LegalLang>();
  /** Per-language names of the blanks ("privacy contact email", …). */
  readonly labels = input<Partial<Record<string, string>>>({});
  readonly blankTitle = input<string>('');

  readonly parts = computed(() => fillParts(this.text(), this.lang()));
}
