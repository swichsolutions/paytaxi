import { ElementRef } from '@angular/core';

/**
 * What the user typed into a field of a prerendered page before the app script ran.
 *
 * The login pages are prerendered, so on a slow connection their form is on screen, and usable, before Angular
 * takes the page over (hydration). The first render then writes the component's empty state into the fields (an
 * empty ngModel, an empty [value]) and the typing is lost: the field goes blank and the button stays disabled.
 * Called from the component's constructor, this reads the server-rendered field before that first render, so the
 * component can start from it.
 *
 * Empty outside hydration: a client-side navigation creates the host without children, and on the server nothing
 * has been typed. (No `instanceof HTMLInputElement`: that global does not exist during server rendering.)
 */
export function typedBeforeHydration(host: ElementRef<HTMLElement>, selector: string): string {
  const field = host.nativeElement.querySelector?.(selector) as { value?: unknown } | null | undefined;
  return typeof field?.value === 'string' ? field.value : '';
}
