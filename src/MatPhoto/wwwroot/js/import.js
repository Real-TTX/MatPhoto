/* MatPhoto – the import.
   The start page: files of the device (chosen, a whole folder, or dropped; folders that are dropped are walked) are sent one after the other, three at a time, to
   /live/import/{token}/files with their folder (it helps to name the album); then the analysis starts and the review page follows. A folder of a storage is chosen
   by its path with a small browser of its folders. The review page: while the import works, its progress (asked every two seconds), then the page again. */
import { t, el, icon, request, token, toast, debounce } from './util.js';

const PARALLEL = 3;
const media = /\.(jpe?g|jpe|png|gif|webp|avif|bmp|heic|heif|tiff?|jxl|dng|cr2|cr3|nef|arw|orf|rw2|raf|pef|srw|mp4|m4v|mov|webm|mkv|avi|mts|m2ts|3gp|wmv)$/i;

// ---- upload --------------------------------------------------------------------------------------------------------------------

const upload = document.querySelector('[data-import-upload]');
if (upload) {
  const drop = upload.querySelector('[data-drop]');
  const progress = upload.querySelector('[data-progress]');
  const text = upload.querySelector('[data-progress-text]');
  const bar = upload.querySelector('[data-progress-bar]');
  const errors = upload.querySelector('[data-progress-errors]');
  const max = Number(upload.dataset.max || 0);
  let busy = false;

  const pick = input => input.addEventListener('change', () => { start([...input.files].map(f => ({ file: f, path: f.webkitRelativePath || f.name }))); input.value = ''; });
  pick(upload.querySelector('[data-pick-files]'));
  pick(upload.querySelector('[data-pick-folder]'));

  for (const type of ['dragenter', 'dragover']) drop.addEventListener(type, e => { e.preventDefault(); drop.classList.add('is-over'); });
  drop.addEventListener('dragleave', () => drop.classList.remove('is-over'));
  drop.addEventListener('drop', async e => {
    e.preventDefault();
    drop.classList.remove('is-over');
    const entries = [...(e.dataTransfer.items || [])].map(i => i.webkitGetAsEntry?.()).filter(Boolean);
    const files = entries.length ? await walk(entries) : [...e.dataTransfer.files].map(f => ({ file: f, path: f.name }));
    start(files);
  });

  async function start(all) {
    if (busy) return;
    const files = all.filter(f => media.test(f.file.name) && !f.file.name.startsWith('.'));
    const skipped = all.length - files.length;
    if (!files.length) { toast(t('Import.NoMedia'), 'warning'); return; }
    busy = true;
    drop.hidden = true;
    progress.hidden = false;
    errors.replaceChildren();
    let session;
    try {
      session = (await request('POST', '/live/import')).token;
    } catch (e) {
      fail(e);
      return;
    }

    let done = 0, sent = 0, failed = 0;
    const total = files.reduce((n, f) => n + f.file.size, 0);
    const show = () => {
      text.textContent = t('Import.Uploading', done + failed, files.length, size(sent), size(total));
      bar.dataset.value = String(Math.round(100 * sent / Math.max(1, total)));
      bar.style.setProperty('--mat-meter', bar.dataset.value + '%');
      bar.parentElement.setAttribute('aria-valuenow', bar.dataset.value);
    };
    show();
    const queue = [...files];
    const worker = async () => {
      while (queue.length) {
        const item = queue.shift();
        if (max > 0 && item.file.size > max) { failed++; note(item.file.name, t('Import.TooLarge')); continue; }
        try {
          await send(session, item, loaded => { sent += loaded; show(); });
          done++;
        } catch (e) {
          failed++;
          note(item.file.name, e.message || t('Grid.Failed'));
        }
        show();
      }
    };
    await Promise.all(Array.from({ length: PARALLEL }, worker));
    if (!done) { busy = false; text.textContent = t('Import.NothingSent'); drop.hidden = false; return; }
    text.textContent = t('Import.Starting');
    try {
      await request('POST', `/live/import/${session}/analyze`);
      location.href = `/Import/Review/${session.replaceAll('-', '')}`;
    } catch (e) {
      fail(e);
    }
    if (skipped) toast(t('Import.Skipped', skipped), 'info');
  }

  function send(session, item, onProgress) {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      let last = 0;
      xhr.open('POST', `/live/import/${session}/files`);
      xhr.setRequestHeader('RequestVerificationToken', token());
      xhr.setRequestHeader('X-File-Name', encodeURIComponent(item.file.name));
      xhr.setRequestHeader('X-Relative-Path', encodeURIComponent(item.path));
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      xhr.upload.onprogress = e => { onProgress(e.loaded - last); last = e.loaded; };
      xhr.onload = () => {
        onProgress(item.file.size - last);
        if (xhr.status >= 200 && xhr.status < 300) resolve();
        else {
          let message = '';
          try { message = JSON.parse(xhr.responseText).error; } catch { /* no JSON */ }
          reject(new Error(message || 'HTTP ' + xhr.status));
        }
      };
      xhr.onerror = () => reject(new Error(t('Grid.Offline')));
      xhr.send(item.file);
    });
  }

  function note(name, message) {
    errors.append(el('li', {}, icon('alert'), el('span', { text: `${name}: ${message}` })));
  }

  function fail(e) {
    busy = false;
    drop.hidden = false;
    text.textContent = e.body?.error || (e.status === 0 ? t('Grid.Offline') : t('Grid.Failed'));
  }
}

/** The files of dropped folders (with their path, which names the album). */
async function walk(entries) {
  const files = [];
  const read = entry => new Promise(resolve => {
    if (entry.isFile) {
      entry.file(file => { files.push({ file, path: entry.fullPath.replace(/^\//, '') }); resolve(); }, () => resolve());
      return;
    }
    const reader = entry.createReader();
    const all = [];
    const next = () => reader.readEntries(async batch => {
      if (!batch.length) { for (const child of all) await read(child); resolve(); return; }
      all.push(...batch);
      next();
    }, () => resolve());
    next();
  });
  for (const entry of entries) await read(entry);
  return files;
}

function size(bytes) {
  const units = ['B', 'KB', 'MB', 'GB'];
  let v = bytes, u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return (u === 0 ? v : v.toFixed(v < 10 ? 1 : 0)) + ' ' + units[u];
}

// ---- a folder of a storage ---------------------------------------------------------------------------------------------------------

const storageForm = document.querySelector('[data-import-storage]');
if (storageForm) {
  const box = storageForm.querySelector('[data-folders]');
  const path = storageForm.querySelector('[name="SourcePath"]');
  const movable = (storageForm.querySelector('[data-movable]')?.dataset.movable || '').split(',').filter(Boolean);
  const moveField = storageForm.querySelector('[data-movable]');
  const current = () => storageForm.querySelector('[name="SourceStorageId"]:checked')?.value;

  async function browse() {
    const id = current();
    if (!id || !box) return;
    if (moveField) moveField.hidden = !movable.includes(id);
    box.setAttribute('aria-busy', 'true');
    try {
      const data = await request('GET', `/live/storages/${id}/folders?path=${encodeURIComponent(path.value)}`);
      const items = [];
      if (data.path) {
        const parent = data.path.includes('/') ? data.path.slice(0, data.path.lastIndexOf('/')) : '';
        items.push(el('button', { type: 'button', class: 'mat-chip', dataset: { path: parent } }, icon('arrow-up'), el('span', { class: 'mat-chip__text', text: '..' })));
      }
      for (const f of data.folders.slice(0, 60)) items.push(el('button', { type: 'button', class: 'mat-chip', dataset: { path: f.path } }, icon('folder'), el('span', { class: 'mat-chip__text', text: f.name })));
      box.removeAttribute('aria-busy');
      box.replaceChildren(
        el('p', { class: 'mat-hint', text: t('Import.FolderHas', data.files) }),
        el('div', { class: 'mat-chips' }, items));
    } catch (e) {
      box.removeAttribute('aria-busy');
      box.replaceChildren(el('p', { class: 'mat-error', text: e.body?.error || t('Grid.Failed') }));
    }
  }

  box?.addEventListener('click', e => {
    const b = e.target.closest('[data-path]');
    if (!b) return;
    path.value = b.dataset.path;
    browse();
  });
  storageForm.addEventListener('change', e => { if (e.target.name === 'SourceStorageId') { path.value = ''; browse(); } });
  // while typing (not on change: a change comes with the click on Analyse, and a list that is drawn again moves the button away)
  path?.addEventListener('input', debounce(browse, 400));
  browse();
}

// ---- looking at the files of an import ------------------------------------------------------------------------------------------------
// A click on a file of the review page shows it large, on black like the viewer of the grid: the preview of the cache (a video plays from its original), the next and
// the previous file loaded ahead, the arrow keys and a swipe go through every file of the page, Escape or a swipe down closes. "+N" shows the rest of a strip.

const tiles = [...document.querySelectorAll('[data-import-item]')];
if (tiles.length) {
  const importToken = location.pathname.split('/').pop();
  const url = (kind, id) => `/import/${importToken}/${kind}/${id}`;
  let viewer = null;

  document.addEventListener('click', e => {
    const more = e.target.closest('[data-strip-more]');
    if (more) {
      for (const tile of more.closest('[data-import-strip]').querySelectorAll('[data-import-item][hidden]')) tile.hidden = false;
      more.remove();
      return;
    }
    const tile = e.target.closest('[data-import-item]');
    if (tile) (viewer ||= new ImportViewer(tiles, url)).show(tiles.indexOf(tile));
  });
}

class ImportViewer {
  constructor(tiles, url) {
    this.tiles = tiles;
    this.url = url;
    this.index = -1;
    this.cache = new Map();
    const button = (name, label, ico) => el('button', { type: 'button', class: 'md-viewer__btn', 'aria-label': label, title: label, dataset: { v: name } }, icon(ico));
    this.name = el('strong');
    this.sub = el('small');
    this.stage = el('div', { class: 'md-viewer__stage' });
    this.prev = el('button', { type: 'button', class: 'md-viewer__nav md-viewer__nav--prev', 'aria-label': t('Viewer.Previous'), dataset: { v: 'prev' } }, icon('chevron-left'));
    this.next = el('button', { type: 'button', class: 'md-viewer__nav md-viewer__nav--next', 'aria-label': t('Viewer.Next'), dataset: { v: 'next' } }, icon('chevron-right'));
    this.node = el('div', { class: 'md-viewer', role: 'dialog', 'aria-modal': 'true', 'aria-label': t('Viewer.Label'), hidden: true, tabindex: '-1' },
      el('header', { class: 'md-viewer__bar' },
        button('close', t('Import.Back'), 'back'),
        el('div', { class: 'md-viewer__title' }, this.name, this.sub),
        el('div', { class: 'md-viewer__actions' }, button('download', t('Viewer.Download'), 'download'))),
      el('div', { class: 'md-viewer__body' }, this.stage, this.prev, this.next));
    document.body.append(this.node);
    this.node.addEventListener('click', e => {
      const b = e.target.closest('[data-v]');
      if (!b) return;
      if (b.dataset.v === 'close') this.close();
      else if (b.dataset.v === 'prev') this.go(-1);
      else if (b.dataset.v === 'next') this.go(1);
      else if (b.dataset.v === 'download') {
        const a = el('a', { href: this.url('original', this.tile.dataset.importItem), download: this.tile.dataset.name || '' });
        document.body.append(a);
        a.click();
        a.remove();
      }
    });
    this.node.addEventListener('keydown', e => {
      if (e.key === 'Escape') { e.preventDefault(); this.close(); }
      else if (e.key === 'ArrowRight') { e.preventDefault(); this.go(1); }
      else if (e.key === 'ArrowLeft') { e.preventDefault(); this.go(-1); }
    });
    this.swipe();
  }

  get tile() { return this.tiles[this.index]; }

  show(index) {
    if (index < 0 || index >= this.tiles.length) return;
    this.index = index;
    if (this.node.hidden) {
      this.returnFocus = document.activeElement;
      this.node.hidden = false;
      document.documentElement.classList.add('md-viewer-open');
      this.node.focus({ preventScroll: true });
    }
    const tile = this.tile;
    const id = tile.dataset.importItem;
    this.name.textContent = tile.dataset.name || '';
    this.sub.textContent = [t('Import.Position', index + 1, this.tiles.length), tile.dataset.sub].filter(Boolean).join(' · ');
    this.prev.hidden = index === 0;
    this.next.hidden = index === this.tiles.length - 1;
    this.stage.replaceChildren();
    if (tile.dataset.video === '1') {
      const video = el('video', { class: 'md-viewer__video', controls: true, autoplay: true, playsinline: true, preload: 'metadata', src: this.url('original', id) });
      if (tile.dataset.thumb === '1') video.poster = this.url('preview', id);
      video.addEventListener('error', () => this.stage.append(el('p', { class: 'md-viewer__note', text: t('Viewer.VideoUnsupported') })), { once: true });
      this.stage.append(video);
    } else if (tile.dataset.thumb === '1') {
      // the thumbnail first (the browser has it), the preview over it as soon as it is there
      const img = el('img', { class: 'md-viewer__img', alt: '', draggable: 'false', decoding: 'async', src: this.url('thumb', id) });
      this.load(id).then(src => { if (src && this.tile === tile) img.src = src; });
      this.stage.append(img);
    } else {
      this.stage.append(el('div', { class: 'md-viewer__missing' }, icon('image'), el('p', { text: t('Viewer.NoPreview') })));
    }
    for (const n of [index + 1, index - 1, index + 2]) {
      const other = this.tiles[n];
      if (other && other.dataset.thumb === '1' && other.dataset.video !== '1') this.load(other.dataset.importItem);
    }
  }

  load(id) {
    if (!this.cache.has(id)) {
      this.cache.set(id, new Promise(resolve => {
        const image = new Image();
        image.decoding = 'async';
        image.onload = () => resolve(image.src);
        image.onerror = () => resolve(null);
        image.src = this.url('preview', id);
      }));
      if (this.cache.size > 24) this.cache.delete(this.cache.keys().next().value);
    }
    return this.cache.get(id);
  }

  go(direction) {
    this.show(this.index + direction);
  }

  close() {
    if (this.node.hidden) return;
    this.node.hidden = true;
    this.stage.replaceChildren();
    document.documentElement.classList.remove('md-viewer-open');
    // the file that was shown last, in its strip (shown, if it waited behind "+N")
    const tile = this.tile;
    if (tile?.hidden) tile.closest('[data-import-strip]')?.querySelector('[data-strip-more]')?.click();
    tile?.scrollIntoView({ block: 'nearest' });
    (tile || this.returnFocus)?.focus?.({ preventScroll: true });
  }

  swipe() {
    let start = null;
    this.stage.addEventListener('pointerdown', e => {
      if (e.target.closest('video')) return;
      start = { x: e.clientX, y: e.clientY };
      this.stage.setPointerCapture(e.pointerId);
    });
    this.stage.addEventListener('pointermove', e => {
      const img = start && this.stage.querySelector('img');
      if (!img) return;
      const dx = e.clientX - start.x, dy = e.clientY - start.y;
      img.style.transform = Math.abs(dx) > Math.abs(dy) ? `translateX(${dx}px)` : dy > 0 ? `translateY(${dy}px) scale(${Math.max(0.8, 1 - dy / 1200)})` : '';
    });
    const end = e => {
      if (!start) return;
      const dx = e.clientX - start.x, dy = e.clientY - start.y;
      start = null;
      const img = this.stage.querySelector('img');
      if (img) img.style.transform = '';
      if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy)) this.go(dx < 0 ? 1 : -1);
      else if (dy > 120) this.close();
    };
    this.stage.addEventListener('pointerup', end);
    this.stage.addEventListener('pointercancel', end);
  }
}

// ---- the progress of an import -----------------------------------------------------------------------------------------------------

const status = document.querySelector('[data-import-status]');
if (status) {
  const text = status.querySelector('[data-progress-text]');
  const bar = status.querySelector('[data-progress-bar]');
  const id = status.dataset.importStatus;
  const tick = async () => {
    try {
      const s = await request('GET', `/live/import/${id}/status`);
      if (!['collecting', 'analyzing', 'importing'].includes(s.state)) { location.reload(); return; }
      const percent = s.total ? Math.round(100 * s.done / s.total) : 0;
      text.textContent = t(s.state === 'importing' ? 'Import.ImportingNow' : 'Import.AnalyzingNow', s.done, s.total);
      bar.dataset.value = String(percent);
      bar.style.setProperty('--mat-meter', percent + '%');
    } catch {
      // offline for a moment: the next tick tries again
    }
    setTimeout(tick, 2000);
  };
  bar.style.setProperty('--mat-meter', (bar.dataset.value || '0') + '%');
  setTimeout(tick, 1500);
}
