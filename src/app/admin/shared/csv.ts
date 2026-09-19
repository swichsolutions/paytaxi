/**
 * Client-side CSV export. Prepends a UTF-8 BOM so Excel opens Georgian text
 * correctly, quotes cells that need it, and triggers a browser download.
 */
export function downloadCsv(filename: string, header: string[], rows: (string | number | null | undefined)[][]): void {
  if (typeof document === 'undefined') return;
  const escape = (v: string | number | null | undefined): string => {
    const s = v === null || v === undefined ? '' : String(v);
    return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const body = [header, ...rows].map(r => r.map(escape).join(',')).join('\r\n');
  const blob = new Blob(['﻿' + body], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename.endsWith('.csv') ? filename : `${filename}.csv`;
  a.click();
  // Give the browser a tick to start the download before revoking.
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** `2026-09-19` in local time, for filenames. */
export function isoDateLocal(d = new Date()): string {
  const pad = (n: number) => (n < 10 ? '0' + n : String(n));
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
