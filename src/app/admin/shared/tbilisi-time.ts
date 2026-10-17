/**
 * Wall-clock times typed into the console are Tbilisi time, like every time the console shows and like
 * the contract's clocks (Annex 1 §2: the 00:00–09:00 P1 window, business hours).
 *
 * An `<input type="datetime-local">` yields "2026-10-14T14:00" with no zone, and `new Date(value)` reads
 * that in the BROWSER's zone. An engineer whose laptop runs on UTC who types 14:00, meaning Tbilisi like
 * everything around it, would store 18:00 Tbilisi. That loses four hours of P1 duration, or the start is
 * refused as "in the future" if it was recent. These helpers read and write the value in Tbilisi time
 * whatever the browser's zone.
 *
 * The offset comes from Intl, not a fixed +04:00, so a future DST rule in Georgia would still convert right.
 */
export const TBILISI_TZ = 'Asia/Tbilisi';

const WALL_CLOCK = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/;

/** Minutes the zone is ahead of UTC at that instant (Tbilisi: 240). */
function offsetMinutes(instantMs: number, zone: string): number {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: zone, hourCycle: 'h23',
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
  }).formatToParts(new Date(instantMs));
  const part = (type: Intl.DateTimeFormatPartTypes) => Number(parts.find(p => p.type === type)?.value ?? 0);
  const wallAsUtc = Date.UTC(part('year'), part('month') - 1, part('day'), part('hour'), part('minute'), part('second'));
  return Math.round((wallAsUtc - Math.floor(instantMs / 1000) * 1000) / 60_000);
}

/** "2026-10-14T14:00" as Tbilisi wall-clock time → "2026-10-14T10:00:00.000Z". Null when it is not such a value. */
export function tbilisiInputToIso(value: string, zone: string = TBILISI_TZ): string | null {
  const m = WALL_CLOCK.exec(value.trim());
  if (!m) return null;
  const wall = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +(m[6] ?? 0));
  if (Number.isNaN(wall)) return null;
  // The offset at the first guess, then at the corrected instant: exact even across a DST change.
  let utc = wall - offsetMinutes(wall, zone) * 60_000;
  utc = wall - offsetMinutes(utc, zone) * 60_000;
  return new Date(utc).toISOString();
}

/** An instant as the datetime-local value of its Tbilisi wall-clock time: "2026-10-14T14:00". */
export function isoToTbilisiInput(instant: string | number | Date, zone: string = TBILISI_TZ): string {
  const ms = new Date(instant).getTime();
  return new Date(ms + offsetMinutes(ms, zone) * 60_000).toISOString().slice(0, 16);
}

/** Today's calendar day in Tbilisi as "YYYY-MM-DD" — the day the contract's dates (a revocation, a launch) count in. */
export function tbilisiToday(zone: string = TBILISI_TZ, now: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-US', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(now);
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find(p => p.type === type)?.value ?? '';
  return `${part('year')}-${part('month')}-${part('day')}`;
}

/** "YYYY-MM-DD" plus <c>n</c> calendar days (no time zone involved: it is a date, not an instant). */
export function addDays(day: string, n: number): string {
  const [y, m, d] = day.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d + n)).toISOString().slice(0, 10);
}
