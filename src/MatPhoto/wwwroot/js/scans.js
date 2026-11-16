/* MatPhoto – the page Scans: while a scan runs the page asks /live/scans every three seconds and loads itself again when a scan has finished. */
(() => {
  const table = document.querySelector('[data-scans]');
  if (!table) return;
  let running = new Set();
  const tick = async () => {
    try {
      const response = await fetch('/live/scans', { headers: { Accept: 'application/json' }, credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) return;
      const data = await response.json();
      const now = new Set(data.scans.filter(s => s.running || s.phase === 'queued').map(s => s.id));
      // a scan that ran before and does not now: show its result
      for (const id of running) if (!now.has(id)) { location.reload(); return; }
      running = now;
      for (const s of data.scans) {
        const row = table.querySelector(`[data-scan="${s.id}"]`);
        if (!row) continue;
        const pill = row.querySelector('[data-scan-phase] .mat-pill');
        if (pill) pill.textContent = s.phaseText;
        row.querySelector('[data-scan-files]').textContent = s.files.toLocaleString();
        const fill = row.querySelector('[data-scan-progress] .mat-meter__fill');
        if (fill && s.toIndex) fill.style.setProperty('--mat-meter', Math.round(100 * (s.indexed + s.failed) / s.toIndex) + '%');
      }
    } finally {
      setTimeout(tick, 3000);
    }
  };
  setTimeout(tick, 1000);
})();
