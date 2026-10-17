import { Component, DestroyRef, PLATFORM_ID, computed, effect, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import {
  AdminApiService, ApiIncident, ApiIncidentDetail, ApiIncidentMetrics, ApiIncidentTimelineEntry, ApiSlaClock,
  IncidentAction, IncidentChange, IncidentFault, IncidentLevel, ReportIncidentBody,
} from '../../services/admin-api.service';
import { AdminParkContextService } from '../../services/admin-park-context.service';
import { AdminAuthService } from '../../services/admin-auth.service';
import { AdminI18nService } from '../../services/admin-i18n.service';
import { IncidentContractComponent } from './incident-contract';
import { isoToTbilisiInput, tbilisiInputToIso } from '../../shared/tbilisi-time';

type Tone = 'ok' | 'warn' | 'bad' | 'wait' | 'muted';
interface Chip { text: string; tone: Tone }
type StateFilter = 'all' | 'open' | 'resolved';

const LEVELS: IncidentLevel[] = ['P1', 'P2', 'P3'];
const FAULTS: IncidentFault[] = ['Unknown', 'Platform', 'External', 'Park'];

/**
 * Incident log — Annex 1 (SLA) of the licence agreement, §4: every incident with level, cause, fault and
 * response / resolution times, the same log for Swich and the licensee. It is the evidence for chronic
 * breach (§5), abandonment (§6) and the trial-period release (§7), which the monthly card sums up.
 *
 *   - Swich (super_admin): acknowledges (the contractual response), sets level / fault / cause, marks the
 *     external service restored, resolves, reopens.
 *   - Licensee (operator): reads everything incl. the contract card, reports incidents, adds notes.
 *   - Park manager: reads their park's and platform-wide incidents; no contract card.
 *
 * Not tied to the topbar park: the log is platform-wide; a park filter narrows it. SLA chips recompute
 * every 30 s on the client from the deadlines the server sent, so "due in 2 h" does not go stale.
 */
@Component({
  selector: 'app-admin-incidents',
  imports: [IncidentContractComponent],
  templateUrl: './incidents.html',
  styleUrl: './incidents.scss',
  host: { '(document:keydown.escape)': 'closeDetail()' },
})
export class IncidentsComponent {
  private api = inject(AdminApiService);
  private parkCtx = inject(AdminParkContextService);
  private auth = inject(AdminAuthService);
  private i18n = inject(AdminI18nService);
  private destroyRef = inject(DestroyRef);
  private platformId = inject(PLATFORM_ID);

  get t() { return this.i18n.t; }

  readonly levels = LEVELS;
  readonly faults = FAULTS;

  readonly role = computed(() => this.auth.admin()?.role ?? '');
  readonly isSuperAdmin = computed(() => this.role() === 'super_admin');
  readonly seesAll = computed(() => this.role() === 'super_admin' || this.role() === 'operator');
  readonly parks = computed(() => this.parkCtx.parks());

  // ── List ──────────────────────────────────────────────────────────
  stateFilter = signal<StateFilter>('all');
  levelFilter = signal<IncidentLevel | ''>('');
  parkFilter = signal<string>('');            // '' all · 'platform' · park id
  monthFilter = signal<string>('');           // '' or yyyy-MM
  incidents = signal<ApiIncident[]>([]);
  openCount = signal(0);
  loading = signal(true);
  loadError = signal<string | null>(null);

  /** Client clock for the SLA chips; ticks every 30 s. */
  now = signal(Date.now());

  // ── Contract card ─────────────────────────────────────────────────
  metricsMonth = signal<string | null>(null);  // null = let the server pick the current Tbilisi month
  metrics = signal<ApiIncidentMetrics | null>(null);
  metricsError = signal(false);

  // ── Detail drawer ─────────────────────────────────────────────────
  selectedId = signal<string | null>(null);
  detail = signal<ApiIncidentDetail | null>(null);
  detailLoading = signal(false);
  detailError = signal<string | null>(null);
  confirming = signal<IncidentAction | null>(null);
  actionText = signal('');
  busy = signal(false);
  noteText = signal('');
  editing = signal(false);
  edit = signal<{ title: string; level: IncidentLevel; fault: IncidentFault; parkId: string; cause: string; description: string }>(
    { title: '', level: 'P2', fault: 'Unknown', parkId: '', cause: '', description: '' });

  // ── Report form ───────────────────────────────────────────────────
  reporting = signal(false);
  report = signal<{ title: string; level: IncidentLevel; parkId: string; startedAt: string; description: string; fault: IncidentFault; cause: string; acknowledge: boolean }>(this.blankReport());
  reportBusy = signal(false);
  reportError = signal<string | null>(null);

  readonly hasFilters = computed(() =>
    this.stateFilter() !== 'all' || !!this.levelFilter() || !!this.parkFilter() || !!this.monthFilter());

  constructor() {
    this.parkCtx.ensureLoaded();

    effect(() => {
      const filter = {
        state: this.stateFilter(),
        level: this.levelFilter() || undefined,
        parkId: this.parkFilter() || undefined,
        month: this.monthFilter() || undefined,
      };
      void this.fetchList(filter);
    });

    effect(() => {
      if (!this.seesAll()) return;
      void this.fetchMetrics(this.metricsMonth());
    });

    if (isPlatformBrowser(this.platformId)) {
      const tick = setInterval(() => this.now.set(Date.now()), 30_000);
      this.destroyRef.onDestroy(() => clearInterval(tick));
    }
  }

  private async fetchList(filter: Parameters<AdminApiService['listIncidents']>[0]) {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const res = await this.api.listIncidents(filter);
      this.incidents.set(res.incidents);
      this.openCount.set(res.openCount);
      this.now.set(Date.now());
    } catch {
      this.loadError.set(this.t.incErrLoad);
    } finally {
      this.loading.set(false);
    }
  }

  private async fetchMetrics(month: string | null) {
    this.metricsError.set(false);
    try {
      // metricsMonth stays null until someone steps months, so the server's "current month" is not fetched twice.
      this.metrics.set(await this.api.getIncidentMetrics(month ?? undefined));
    } catch {
      this.metricsError.set(true);
    }
  }

  async refresh() {
    await this.fetchList({
      state: this.stateFilter(), level: this.levelFilter() || undefined,
      parkId: this.parkFilter() || undefined, month: this.monthFilter() || undefined,
    });
    if (this.seesAll()) await this.fetchMetrics(this.metricsMonth());
    const id = this.selectedId();
    if (id) await this.loadDetail(id);
  }

  clearFilters() {
    this.stateFilter.set('all');
    this.levelFilter.set('');
    this.parkFilter.set('');
    this.monthFilter.set('');
  }

  shiftMonth(delta: number) {
    const m = this.metricsMonth() ?? this.metrics()?.month;
    if (!m) return;
    const [y, mo] = m.split('-').map(Number);
    const d = new Date(Date.UTC(y, mo - 1 + delta, 1));
    this.metricsMonth.set(`${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`);
  }

  // ── Detail ────────────────────────────────────────────────────────

  async openDetail(id: string) {
    this.selectedId.set(id);
    this.detail.set(null);
    this.resetActions();
    await this.loadDetail(id);
  }

  closeDetail() {
    this.selectedId.set(null);
    this.detail.set(null);
    this.resetActions();
  }

  private resetActions() {
    this.confirming.set(null);
    this.actionText.set('');
    this.noteText.set('');
    this.editing.set(false);
    this.detailError.set(null);
  }

  private async loadDetail(id: string) {
    this.detailLoading.set(true);
    try {
      const d = await this.api.getIncident(id);
      if (this.selectedId() === id) this.detail.set(d);
    } catch {
      if (this.selectedId() === id) this.detailError.set(this.t.incErrLoadOne);
    } finally {
      this.detailLoading.set(false);
    }
  }

  ask(action: IncidentAction) {
    this.confirming.set(action);
    this.actionText.set('');
    this.detailError.set(null);
  }

  cancelAsk() { this.confirming.set(null); }

  /** Reopen needs a reason; the others take an optional note. */
  canConfirm(): boolean {
    return !this.busy() && (this.confirming() !== 'reopen' || this.actionText().trim().length > 0);
  }

  async confirmAction() {
    const d = this.detail();
    const action = this.confirming();
    if (!d || !action || !this.canConfirm()) return;
    await this.run(() => this.api.incidentAction(d.incident.id, action, this.actionText().trim() || undefined));
  }

  async addNote() {
    const d = this.detail();
    const text = this.noteText().trim();
    if (!d || !text || this.busy()) return;
    await this.run(() => this.api.incidentAction(d.incident.id, 'note', text));
  }

  startEdit() {
    const d = this.detail();
    if (!d) return;
    const i = d.incident;
    this.edit.set({
      title: i.title, level: i.level, fault: i.fault, parkId: i.parkId ?? '',
      cause: i.cause ?? '', description: d.description ?? '',
    });
    this.confirming.set(null);
    this.editing.set(true);
  }

  patchEdit(patch: Partial<ReturnType<IncidentsComponent['edit']>>) { this.edit.update(e => ({ ...e, ...patch })); }

  /**
   * The park the edit moves the incident away from (to all parks or to another park), or null. Such a move shows
   * the incident to other parks' managers, so the server wants a new title and new details that do not name that
   * park (neutral_text_required); a platform-wide incident moved to one park shows nothing new to anybody.
   */
  readonly rescopeFrom = computed<string | null>(() => {
    const d = this.detail();
    if (!d || !this.editing() || !d.incident.parkId) return null;
    if ((this.edit().parkId || null) === d.incident.parkId) return null;
    return d.incident.parkName ?? this.parks().find(p => p.id === d.incident.parkId)?.name ?? '';
  });

  /** What a move still lacks, the server's rule mirrored: each text given, changed, and not naming the park (as a word). */
  readonly rescopeMissing = computed<Array<'title' | 'description'>>(() => {
    const park = this.rescopeFrom();
    const d = this.detail();
    if (park === null || !d) return [];
    const e = this.edit();
    const names = (text: string) => {
      const p = park.trim();
      if (!p) return false;
      const esc = p.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      return new RegExp(`(?<![\\p{L}\\p{N}_])${esc}(?![\\p{L}\\p{N}_])`, 'iu').test(text);
    };
    const missing: Array<'title' | 'description'> = [];
    const title = e.title.trim();
    if (!title || title === d.incident.title || names(title)) missing.push('title');
    const description = e.description.trim();
    if (!description || description === (d.description ?? '') || names(description)) missing.push('description');
    return missing;
  });

  async saveEdit() {
    const d = this.detail();
    const e = this.edit();
    if (!d || this.busy() || !e.title.trim() || this.rescopeMissing().length > 0) return;
    const i = d.incident;
    const body: Parameters<AdminApiService['updateIncident']>[1] = {};
    if (e.title.trim() !== i.title) body.title = e.title.trim();
    if (e.level !== i.level) body.level = e.level;
    if (e.fault !== i.fault) body.fault = e.fault;
    if (e.cause.trim() !== (i.cause ?? '')) body.cause = e.cause.trim();
    if (e.description.trim() !== (d.description ?? '')) body.description = e.description.trim();
    if ((e.parkId || null) !== i.parkId) {
      if (e.parkId) body.parkId = e.parkId; else body.platformWide = true;
    }
    await this.run(async () => (await this.api.updateIncident(i.id, body)).incident);
  }

  /** Every write returns the fresh detail; the list and the contract card follow. */
  private async run(call: () => Promise<ApiIncidentDetail>) {
    this.busy.set(true);
    this.detailError.set(null);
    try {
      const fresh = await call();
      this.detail.set(fresh);
      this.confirming.set(null);
      this.actionText.set('');
      this.noteText.set('');
      this.editing.set(false);
      await this.refreshListOnly();
    } catch (err: any) {
      this.detailError.set(this.errorText(err));
      // Someone else acted first (409): show what the incident looks like now.
      if (err?.status === 409) {
        this.confirming.set(null);
        const id = this.selectedId();
        if (id) await this.loadDetail(id);
      }
    } finally {
      this.busy.set(false);
    }
  }

  private async refreshListOnly() {
    await this.fetchList({
      state: this.stateFilter(), level: this.levelFilter() || undefined,
      parkId: this.parkFilter() || undefined, month: this.monthFilter() || undefined,
    });
    if (this.seesAll()) await this.fetchMetrics(this.metricsMonth());
  }

  // ── Report ────────────────────────────────────────────────────────

  private blankReport() {
    return { title: '', level: 'P2' as IncidentLevel, parkId: '', startedAt: '', description: '', fault: 'Unknown' as IncidentFault, cause: '', acknowledge: false };
  }

  openReport() {
    // Pre-pick the park the console is operating; "All parks" is one click away.
    this.report.set({ ...this.blankReport(), parkId: this.parkCtx.currentParkId() ?? '' });
    this.reportError.set(null);
    this.reporting.set(true);
  }

  cancelReport() { this.reporting.set(false); }

  patchReport(patch: Partial<ReturnType<IncidentsComponent['report']>>) { this.report.update(r => ({ ...r, ...patch })); }

  async submitReport() {
    const r = this.report();
    if (this.reportBusy()) return;
    if (!r.title.trim()) { this.reportError.set(this.t.incErrTitle); return; }
    const body: ReportIncidentBody = {
      title: r.title.trim(),
      level: r.level,
      description: r.description.trim() || undefined,
      parkId: r.parkId || null,
      // Typed in Tbilisi time, like every time on this page — not the browser's zone (see tbilisi-time.ts).
      startedAt: r.startedAt ? tbilisiInputToIso(r.startedAt) ?? undefined : undefined,
    };
    if (this.isSuperAdmin()) {
      if (r.fault !== 'Unknown') body.fault = r.fault;
      if (r.cause.trim()) body.cause = r.cause.trim();
      if (r.acknowledge) body.acknowledge = true;
    }
    this.reportBusy.set(true);
    this.reportError.set(null);
    try {
      const created = await this.api.reportIncident(body);
      this.reporting.set(false);
      await this.refreshListOnly();
      this.selectedId.set(created.incident.id);
      this.resetActions();
      this.detail.set(created);
    } catch (err: any) {
      this.reportError.set(this.errorText(err));
    } finally {
      this.reportBusy.set(false);
    }
  }

  /** The latest impact start the form offers: now, as Tbilisi wall-clock time. */
  maxStart(): string { return isoToTbilisiInput(this.now()); }

  private errorText(err: any): string {
    switch (err?.error?.error) {
      case 'title_required': return this.t.incErrTitle;
      case 'started_in_future':
      case 'started_after_report': return this.t.incErrStartedFuture;
      case 'started_too_early': return this.t.incErrStartedTooEarly.replace('{h}', String(Math.round(err.error.maxHours ?? 0)));
      case 'reason_required': return this.t.incErrReason;
      case 'invalid_time': return this.t.incErrRestoreTime;
      case 'neutral_text_required': return this.t.incErrNeutralText.replace('{park}', err.error.park ?? '');
      case 'already_acknowledged':
      case 'already_resolved':
      case 'already_restored':
      case 'incident_resolved':
      case 'not_resolved': return this.t.incErrStale;
    }
    if (err?.status === 403) return this.t.incErrForbidden;
    return this.t.errUnknown;
  }

  // ── Presentation ─────────────────────────────────────────────────

  /** Auto incidents carry an English title from the monitor; show it in the console language unless someone renamed it. */
  title(i: ApiIncident): string {
    if (i.autoKind === 'payout_queue' && i.title.startsWith('Payouts stuck in the queue')) return this.t.incAutoQueueTitle;
    if (i.autoKind === 'payout_queue_wide' && i.title === 'Payouts waiting at several parks') return this.t.incAutoWideTitle;
    if (i.autoKind === 'yandex_outage' && i.title === 'Yandex Fleet API unreachable') return this.t.incAutoYandexTitle;
    return i.title;
  }

  /**
   * Raised to its level after the report (a P2 the queue outgrew, or Swich re-levelling): its clocks, and the §5/§6
   * figures, count from then — the drawer says so instead of leaving a "4 h response" that seems to start at the report.
   */
  raisedLater(i: ApiIncident): boolean {
    return !!i.levelSince && new Date(i.levelSince).getTime() - new Date(i.reportedAt).getTime() > 60_000;
  }

  scope(i: ApiIncident): string { return i.parkName ?? this.t.incidentAllParks; }

  faultLabel(f: IncidentFault): string {
    return ({ Platform: this.t.incFaultPlatform, External: this.t.incFaultExternal, Park: this.t.incFaultPark, Unknown: this.t.incFaultUnknown } as Record<IncidentFault, string>)[f] ?? f;
  }

  levelHint(l: IncidentLevel): string {
    return ({ P1: this.t.incLevelP1Hint, P2: this.t.incLevelP2Hint, P3: this.t.incLevelP3Hint } as Record<IncidentLevel, string>)[l];
  }

  statusLabel(s: string): string {
    return ({ Reported: this.t.incStatusReported, Acknowledged: this.t.incStatusAcknowledged, Resolved: this.t.incStatusResolved } as Record<string, string>)[s] ?? s;
  }

  timelineLabel(kind: string): string {
    return ({
      reported: this.t.incTlReported, acknowledged: this.t.incTlAcknowledged, updated: this.t.incTlUpdated,
      note: this.t.incTlNote, external_restored: this.t.incTlRestored, resolved: this.t.incTlResolved, reopened: this.t.incTlReopened,
    } as Record<string, string>)[kind] ?? kind;
  }

  /**
   * A timeline entry in the console's language. Entries the system worded carry a code and their figures
   * (`data`); people's words (notes, reasons, a note on an action) have none and are shown exactly as typed.
   * The stored English text is the record and the fallback for any code this console does not know.
   */
  timelineText(e: ApiIncidentTimelineEntry): string {
    const d = e.data;
    if (!d?.code) return e.text;
    const t = this.t;
    const n = (v: number | undefined) => String(v ?? 0);
    switch (d.code) {
      case 'reported': {
        const base = this.tpl(t.incTxtReported, { level: d.level ?? '', scope: d.park ?? t.incidentAllParks, title: d.title ?? '' });
        return d.impactSince ? `${base} ${this.tpl(t.incTxtImpactSince, { at: this.when(d.impactSince) })}` : base;
      }
      case 'acknowledged':           return t.incTxtAcknowledged;
      case 'acknowledged_on_report': return t.incTxtAcknowledgedOnReport;
      case 'external_restored': {
        const base = d.since ? this.tpl(t.incTxtRestoredSince, { at: this.when(d.since) }) : t.incTxtRestored;
        return d.note ? `${base} ${d.note}` : base;
      }
      case 'resolved':          return t.incTxtResolved;
      case 'reopened_detached': return `${d.reason ?? ''} ${t.incTxtDetached}`.trim();
      case 'updated': {
        const parts = (d.changes ?? []).map(c => this.changeText(c)).filter(Boolean);
        return parts.length ? parts.join('; ') : e.text;
      }
      case 'auto_queue_opened':
        return this.tpl(t.incTxtAutoQueueOpened, {
          level: d.level ?? '', payouts: n(d.payouts), drivers: n(d.drivers), minutes: n(d.minutes), paid: n(d.paid),
        }) + ' ' + this.tpl(t.incTxtAutoFault, { fault: d.fault ? this.faultLabel(d.fault) : '—', code: d.failureCode ?? '—' });
      case 'auto_escalated':
        return this.tpl(t.incTxtAutoEscalated, { from: d.from ?? '', to: d.to ?? '', drivers: n(d.drivers), payouts: n(d.payouts), paid: n(d.paid) });
      case 'auto_queue_restored':  return t.incTxtAutoQueueRestored;
      case 'auto_queue_resolved':  return t.incTxtAutoQueueResolved;
      // One cause at several parks (Annex 1 §2.1 counted across parks). No park is named: every manager reads these.
      case 'auto_wide_opened':
        return this.tpl(t.incTxtAutoWideOpened, this.wideValues(d))
          + ' ' + this.tpl(t.incTxtAutoFault, { fault: d.fault ? this.faultLabel(d.fault) : '—', code: d.failureCode ?? '—' });
      case 'auto_wide_upgraded':
        return this.tpl(t.incTxtAutoWideUpgraded, this.wideValues(d))
          + (d.from ? ' ' + this.tpl(t.incTxtAutoLevelRaised, { from: d.from }) : '');
      case 'auto_merged':          return this.tpl(t.incTxtAutoMerged, { incident: d.incident ?? '', park: d.park ?? '' });
      case 'auto_duplicate':       return this.tpl(t.incTxtAutoDuplicate, { of: d.of ?? '' });
      case 'auto_wide_resolved':   return t.incTxtAutoWideResolved;
      case 'auto_yandex_opened':
        return (d.parks
          ? this.tpl(t.incTxtAutoYandexOpenedParks, { failures: n(d.failures), minutes: n(d.minutes), parks: n(d.parks) })
          : this.tpl(t.incTxtAutoYandexOpened, { failures: n(d.failures), minutes: n(d.minutes) }))
          + ' ' + t.incTxtAutoYandexRefused;
      case 'auto_yandex_restored': return t.incTxtAutoYandexRestored;
      case 'auto_yandex_resolved': return t.incTxtAutoYandexResolved;
      default:                     return e.text;
    }
  }

  private changeText(c: IncidentChange): string {
    const t = this.t;
    switch (c.field) {
      case 'title':       return this.tpl(t.incChgTitle, { from: c.from ?? '', to: c.to ?? '' });
      case 'description': return t.incChgDescription;
      case 'cause':       return c.to ? this.tpl(t.incChgCause, { to: c.to }) : t.incChgCauseCleared;
      case 'level':       return this.tpl(t.incChgLevel, { from: c.from ?? '', to: c.to ?? '' });
      case 'fault':       return this.tpl(t.incChgFault, { from: this.faultLabel(c.from as IncidentFault), to: this.faultLabel(c.to as IncidentFault) });
      case 'scope':       return this.tpl(t.incChgScope, { to: c.to ?? t.incidentAllParks }) + (c.detached ? ' ' + t.incTxtDetached : '');
      case 'started':     return this.tpl(t.incChgStarted, { from: this.when(c.from), to: this.when(c.to) });
      default:            return '';
    }
  }

  private wideValues(d: NonNullable<ApiIncidentTimelineEntry['data']>): Record<string, string> {
    return {
      payouts: String(d.payouts ?? 0), drivers: String(d.drivers ?? 0), parks: String(d.parks ?? 0), minutes: String(d.minutes ?? 0),
      code: d.failureCode ?? '—', bank: d.bank ?? this.t.incBankUnknown,
    };
  }

  /** Every {placeholder} replaced by its value. Split/join, not a regex: titles and notes may contain anything. */
  private tpl(template: string, values: Record<string, string>): string {
    return Object.entries(values).reduce((s, [k, v]) => s.split(`{${k}}`).join(v), template);
  }

  actor(by: string | null): string {
    if (!by) return '—';
    if (by === 'system') return this.t.incActorSystem;
    if (by === 'admin') return this.t.incActorStaff;
    return by.startsWith('admin:') ? by.slice(6) : by;
  }

  isExternalCause(i: ApiIncident): boolean { return i.fault === 'External' || i.fault === 'Park'; }

  /** Live minutes to the deadline (negative = past it) for a running clock; the server's figure once it stopped. */
  private margin(c: ApiSlaClock): number {
    if ((c.state === 'Pending' || c.state === 'Overdue') && c.dueAt) return (new Date(c.dueAt).getTime() - this.now()) / 60_000;
    return c.marginMinutes ?? 0;
  }

  responseChip(i: ApiIncident): Chip {
    const c = i.sla.response;
    const m = this.margin(c);
    if (c.state === 'Pending' && m < 0) return { text: this.fill(this.t.incSlaRespOverdue, -m), tone: 'bad' };
    switch (c.state) {
      case 'Pending': return { text: this.fill(this.t.incSlaRespDue, m), tone: m < 60 ? 'bad' : 'warn' };
      case 'Overdue': return { text: this.fill(this.t.incSlaRespOverdue, -m), tone: 'bad' };
      case 'Met':     return { text: this.fill(this.t.incSlaRespMet, this.elapsed(c)), tone: 'ok' };
      case 'Missed':  return { text: this.fill(this.t.incSlaRespMissed, -m), tone: 'bad' };
      default:        return { text: '—', tone: 'muted' };
    }
  }

  resolutionChip(i: ApiIncident): Chip {
    const c = i.sla.resolution;
    const m = this.margin(c);
    if (c.state === 'Pending' && m < 0) return { text: this.fill(this.t.incSlaResOverdue, -m), tone: 'bad' };
    switch (c.state) {
      case 'Pending':  return { text: this.fill(this.t.incSlaResDue, m), tone: m < 120 ? 'bad' : 'warn' };
      case 'Overdue':  return { text: this.fill(this.t.incSlaResOverdue, -m), tone: 'bad' };
      case 'Met':      return { text: this.t.incSlaResMet, tone: 'ok' };
      case 'Missed':   return { text: this.fill(this.t.incSlaResMissed, -m), tone: 'bad' };
      case 'Paused':   return { text: this.waitingText(c.waitingFor), tone: 'wait' };
      case 'NoTarget': return { text: i.status === 'Resolved' ? this.t.incSlaResDone : this.t.incSlaNextRelease, tone: 'muted' };
    }
  }

  private waitingText(w: ApiSlaClock['waitingFor']): string {
    switch (w) {
      case 'bank':   return this.t.incWaitBank;
      case 'yandex': return this.t.incWaitYandex;
      case 'park':   return this.t.incWaitPark;
      default:       return this.t.incWaitExternal;
    }
  }

  /** How far through its window a running clock is, 0–100, for the drawer's bars. */
  progress(c: ApiSlaClock): number {
    if (!c.startsAt || !c.dueAt) return 0;
    const start = new Date(c.startsAt).getTime();
    const due = new Date(c.dueAt).getTime();
    const at = c.doneAt ? new Date(c.doneAt).getTime() : this.now();
    if (due <= start) return 100;
    return Math.max(0, Math.min(100, ((at - start) / (due - start)) * 100));
  }

  private elapsed(c: ApiSlaClock): number {
    if (!c.startsAt || !c.doneAt) return 0;
    return (new Date(c.doneAt).getTime() - new Date(c.startsAt).getTime()) / 60_000;
  }

  private fill(tpl: string, minutes: number): string { return tpl.replace('{d}', this.duration(minutes)); }

  /** "35 min", "2 h 15 min", "26 h", "3 d 4 h" — templates carry the language's spacing and units. */
  duration(minutes: number): string {
    const mins = Math.max(0, Math.round(minutes));
    const t = this.t;
    if (mins < 60) return t.durMin.replace('{m}', String(mins));
    const h = Math.floor(mins / 60);
    const m = mins % 60;
    if (h < 10 && m > 0) return t.durHourMin.replace('{h}', String(h)).replace('{m}', String(m));
    if (h < 48) return t.durHour.replace('{h}', String(m >= 30 ? h + 1 : h));
    const d = Math.floor(h / 24);
    const rh = h % 24;
    return rh > 0 ? t.durDayHour.replace('{d}', String(d)).replace('{h}', String(rh)) : t.durDay.replace('{d}', String(d));
  }

  /** Console times are shown in Tbilisi time, like the contract's clocks. */
  when(iso: string | null | undefined): string {
    if (!iso) return '—';
    const d = new Date(iso);
    const locale = this.i18n.lang() === 'ka' ? 'ka-GE' : 'en-GB';
    return d.toLocaleDateString(locale, { day: 'numeric', month: 'short', timeZone: 'Asia/Tbilisi' }) + ' ' +
      d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: 'Asia/Tbilisi' });
  }

  confirmText(action: IncidentAction): string {
    switch (action) {
      case 'acknowledge':       return this.t.incConfirmAck;
      case 'external-restored': return this.t.incConfirmRestored;
      case 'resolve':           return this.t.incConfirmResolve;
      case 'reopen':            return this.t.incConfirmReopen;
      default:                  return '';
    }
  }

  confirmLabel(action: IncidentAction): string {
    switch (action) {
      case 'acknowledge':       return this.t.incActAck;
      case 'external-restored': return this.t.incActRestored;
      case 'resolve':           return this.t.incActResolve;
      case 'reopen':            return this.t.incActReopen;
      default:                  return this.t.confirm;
    }
  }
}
