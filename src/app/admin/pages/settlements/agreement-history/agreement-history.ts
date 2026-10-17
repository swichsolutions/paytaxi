import { Component, effect, inject, input, signal } from '@angular/core';
import { AdminApiService, ApiAgreementEvent, ApiAgreementEventKind } from '../../../services/admin-api.service';
import { AdminI18nService } from '../../../services/admin-i18n.service';
import { AdminMockService } from '../../../services/admin-mock.service';

/** The kinds the filter offers, in the order shown. */
const EVENT_KINDS: ApiAgreementEventKind[] = [
  'launch_date', 'terms', 'cost_added', 'cost_edited', 'cost_deleted', 'cost_held', 'cost_released', 'correction_added',
];

/**
 * The licence agreement's change history (§2.6: identical data for both sides) — every change to what the monthly
 * statement is computed from: the launch date, the terms, and each shared-cost invoice added, edited, deleted, held,
 * released or corrected. Newest first, filterable by kind; read-only (the API offers no way to change an entry).
 * The values are recorded as snapshots; this renders them in the reader's language.
 *
 * <c>refreshKey</c>: the page bumps it after a change of its own, so the new entry shows without a reload.
 */
@Component({
  selector: 'app-agreement-history',
  templateUrl: './agreement-history.html',
  styleUrl: './agreement-history.scss',
})
export class AgreementHistoryComponent {
  private api = inject(AdminApiService);
  private i18n = inject(AdminI18nService);
  private svc = inject(AdminMockService); // formatters

  readonly licenseeId = input.required<string>();
  readonly refreshKey = input(0);

  get t() { return this.i18n.t; }
  private get tt() { return this.i18n.t as Record<string, string>; }

  readonly kinds = EVENT_KINDS;
  readonly kind = signal<ApiAgreementEventKind | ''>('');
  readonly events = signal<ApiAgreementEvent[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.licenseeId();
      const kind = this.kind();
      this.refreshKey();
      void this.load(id, kind);
    });
  }

  private async load(licenseeId: string, kind: ApiAgreementEventKind | '') {
    this.loading.set(true);
    this.error.set(null);
    try {
      const r = await this.api.listAgreementEvents(licenseeId, kind || undefined);
      if (this.licenseeId() !== licenseeId || this.kind() !== kind) return;   // moved on meanwhile
      this.events.set(r.events);
    } catch (err: any) {
      this.error.set(err?.error?.message ?? this.t.stlHistErrLoad);
    } finally {
      this.loading.set(false);
    }
  }

  setKind(kind: string) { this.kind.set((kind as ApiAgreementEventKind) || ''); }
  kindLabel(kind: string): string { return this.tt['stlHistKind_' + kind] ?? kind; }

  /** "admin:levan@operator.local" → "levan@operator.local"; the set-up data as such. */
  actorLabel(actor: unknown): string {
    if (typeof actor !== 'string' || !actor) return '—';
    if (actor === 'seed') return this.t.stlHistActorSeed;
    return actor.startsWith('admin:') ? actor.slice(6) : actor;
  }

  /** "SMSOffice № SO-24871 · Paid by the licensee" for a cost entry; nothing for the licensee's own terms. */
  costRef(e: ApiAgreementEvent): string | null {
    if (!e.cost) return null;
    const ref = `${e.cost.vendor} № ${e.cost.invoiceNumber}`;
    const side = e.cost.paidBy === 'Swich' ? this.t.stlPaidBySwich : this.t.stlPaidByLicensee;
    return `${e.cost.isCorrection ? this.t.stlHistCorrectionOf.replace('{ref}', ref) : ref} · ${side}`;
  }

  /** What changed, from the recorded snapshots. */
  summary(e: ApiAgreementEvent): string {
    const o = e.old ?? {};
    const n = e.new ?? {};
    const date = (v: unknown) => (typeof v === 'string' && v ? this.formatDate(v) : this.t.stlLicNotSet);
    switch (e.kind) {
      case 'launch_date':
        return `${date(o['launchDate'])} → ${date(n['launchDate'])}`;
      case 'terms': {
        const terms = (s: Record<string, any>) => ({
          barrier: `${this.t.stlLicBarrier}: ${this.gel(Number(s['barrierGel']), 0)}`,
          share: `${this.t.stlHistShare}: ${Number(s['swichSharePercent'])}%`,
          active: s['isActive'] ? this.t.stlHistActive : this.t.stlHistInactive,
        });
        const after = terms(n);
        if (!e.old) return [after.barrier, after.share, after.active].join(' · ');   // registered with
        const before = terms(o);
        const parts: string[] = [];
        if (o['barrierGel'] !== n['barrierGel']) parts.push(`${before.barrier} → ${this.gel(Number(n['barrierGel']), 0)}`);
        if (o['swichSharePercent'] !== n['swichSharePercent']) parts.push(`${before.share} → ${Number(n['swichSharePercent'])}%`);
        if (o['isActive'] !== n['isActive']) parts.push(`${before.active} → ${after.active}`);
        return parts.join(' · ');
      }
      case 'cost_edited': {
        // Only what changed, field by field.
        const fields: Array<[string, string, (v: any) => string]> = [
          ['amount', this.t.stlCostAmount, v => this.gel(Number(v))],
          ['category', this.t.stlCostCategory, v => this.categoryLabel(v)],
          ['vendor', this.t.stlCostVendor, v => String(v ?? '—')],
          ['invoiceNumber', this.t.stlCostInvoiceNo, v => String(v ?? '—')],
          ['invoiceDate', this.t.stlCostDate, v => date(v)],
          ['note', this.t.stlHistNote, v => String(v ?? '—')],
          ['reason', this.t.stlCorrectReason, v => String(v ?? '—')],
        ];
        return fields
          .filter(([k]) => JSON.stringify(o[k] ?? null) !== JSON.stringify(n[k] ?? null))
          .map(([k, label, fmt]) => `${label}: ${fmt(o[k])} → ${fmt(n[k])}`)
          .join(' · ');
      }
      case 'cost_added':
      case 'correction_added': {
        const amount = e.kind === 'correction_added' ? this.signedGel(Number(n['amount'])) : this.gel(Number(n['amount']));
        return [amount, this.categoryLabel(n['category']), date(n['invoiceDate'])].join(' · ');
      }
      case 'cost_deleted':
      case 'cost_held':
        return this.gel(Number(o['amount']));
      case 'cost_released':
        return this.t.stlHistHeldBy.replace('{who}', this.actorLabel(o['heldBy'])).replace('{reason}', String(o['holdReason'] ?? '—'));
      default:
        return '';
    }
  }

  private categoryLabel(c: unknown): string { return this.tt['stlCat_' + String(c ?? '')] ?? String(c ?? ''); }
  private gel(n: number, d = 2): string { return n < 0 ? `−${this.svc.formatGel(-n, d)}` : this.svc.formatGel(n, d); }
  private signedGel(n: number): string { return n > 0 ? `+${this.gel(n)}` : this.gel(n); }

  private formatDate(iso: string): string {
    const d = new Date(iso.length === 10 ? iso + 'T00:00:00' : iso);
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  }
  /** A record's time with the year — the history spans the whole agreement. */
  stamp(iso: string): string {
    const d = new Date(iso);
    return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) + ' ' +
      d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }
}
