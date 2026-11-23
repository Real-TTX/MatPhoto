/* MatPhoto – the photo grid and the viewer.
   The grid: the months of a scope (/live/timeline) stand one under the other with a height that is estimated until the month is loaded (/live/items); only the months
   near the screen hold their tiles (a library of 100,000 photos is a few hundred elements in the page). Inside a month the days have their head, the photos lie in
   justified rows (the height of a row follows its photos, so nothing is cropped more than needed). The selection: a tick on a tile, Shift for a range, a long press
   on a touch screen. The viewer: the preview of the cache, the original of a video (with ranges), the next and the previous photo loaded ahead, swipe, keys, zoom,
   the info panel, favourites, lists, the trash. */
import { t, el, icon, request, toast } from './util.js';

const GAP = 4;
const SIZES = { s: 120, m: 190, l: 280 };
const PHONE = 600;
const sizeKey = 'md-grid-size';

const reduceMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const thumbUrl = item => `/media/${item.i}/thumb?v=${item.k}`;
const previewUrl = item => `/media/${item.i}/preview?v=${item.k}`;
const originalUrl = id => `/media/${id}/original`;
const lang = () => document.documentElement.lang || 'en';

function duration(ms) {
  const s = Math.round(ms / 1000);
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(r).padStart(2, '0');
}

function dayLabel(iso) {
  const d = new Date(iso.slice(0, 10) + 'T12:00:00');
  const now = new Date();
  const opts = { weekday: 'short', day: 'numeric', month: 'short' };
  if (d.getFullYear() !== now.getFullYear()) opts.year = 'numeric';
  return d.toLocaleDateString(lang(), opts);
}

/** The rows of justified photos for a width: each row as high as needed to fill it, at most the target height (the last row keeps the target). */
function justify(items, width, target) {
  const rows = [];
  let row = [], sum = 0;
  for (const item of items) {
    const ratio = item.w > 0 && item.h > 0 ? Math.min(Math.max(item.w / item.h, 0.4), 5) : 1;
    row.push({ item, ratio });
    sum += ratio;
    const height = (width - GAP * (row.length - 1)) / sum;
    if (height <= target) {
      rows.push({ cells: row, height });
      row = [];
      sum = 0;
    }
  }
  if (row.length) rows.push({ cells: row, height: Math.min(target, (width - GAP * (row.length - 1)) / sum), last: true });
  return rows;
}

class PhotoGrid {
  constructor(root) {
    this.root = root;
    this.scope = root.dataset.scope || 'all';
    this.listId = root.dataset.list || '';
    this.days = root.dataset.days !== '0';
    this.canvas = root.querySelector('[data-grid-canvas]');
    this.status = root.querySelector('[data-grid-status]');
    this.scroller = root.closest('.mat-main') || document.scrollingElement;
    this.sections = [];
    this.selected = new Set();
    this.anchor = null;
    this.width = 0;
    this.size = localStorage.getItem(sizeKey) || 'm';
    this.viewer = null;
    this.frame = 0;
    this.selectionBar = new SelectionBar(this, root.querySelector('[data-selbar]'));
    this.wire();
    this.start();
  }

  get target() {
    const base = SIZES[this.size] || SIZES.m;
    return window.innerWidth < PHONE ? Math.round(base * 0.62) : base;
  }

  async start() {
    try {
      const data = await request('GET', `/live/timeline?scope=${encodeURIComponent(this.scope)}`);
      this.status.hidden = true;
      if (!data.buckets.length) {
        this.root.querySelector('[data-grid-empty]').hidden = false;
        return;
      }
      this.sections = data.buckets.map(b => ({ key: b.k, label: b.label, year: b.y, count: b.n, items: null, rows: null, top: 0, height: 0, node: null, loading: null }));
      this.measure();
      this.scrubber();
      this.render();
      this.openFromHash();
    } catch (e) {
      this.status.replaceChildren(icon('alert'), el('span', { text: e.status === 0 ? t('Grid.Offline') : t('Grid.Failed') }));
    }
  }

  wire() {
    const schedule = () => { if (!this.frame) this.frame = requestAnimationFrame(() => { this.frame = 0; this.render(); }); };
    this.scroller.addEventListener('scroll', schedule, { passive: true });
    new ResizeObserver(() => { const w = this.canvas.clientWidth; if (w && Math.abs(w - this.width) > 1) { this.measure(); this.render(); } }).observe(this.canvas);
    document.addEventListener('md:grid-size', e => { this.size = e.detail; this.measure(); this.render(); });
    window.addEventListener('popstate', () => { if (!location.hash.startsWith('#m') && this.viewer) this.viewer.close(true); else this.openFromHash(); });
    window.addEventListener('online', () => { if (!this.sections.length) this.start(); });

    this.canvas.addEventListener('click', e => this.onClick(e));
    this.canvas.addEventListener('keydown', e => {
      if ((e.key === ' ' || e.key === 'x') && e.target.closest('.md-tile')) { e.preventDefault(); this.toggle(Number(e.target.closest('.md-tile').dataset.id), e.shiftKey); }
    });
    // a long press selects on a touch screen
    let timer = 0, pressed = null;
    this.canvas.addEventListener('pointerdown', e => {
      if (e.pointerType !== 'touch') return;
      const tile = e.target.closest('.md-tile');
      if (!tile) return;
      pressed = tile;
      timer = setTimeout(() => { this.toggle(Number(tile.dataset.id), false); tile.dataset.pressed = '1'; navigator.vibrate?.(15); }, 450);
    });
    const cancel = () => { clearTimeout(timer); };
    this.canvas.addEventListener('pointerup', cancel);
    this.canvas.addEventListener('pointermove', e => { if (pressed && (Math.abs(e.movementX) + Math.abs(e.movementY)) > 6) cancel(); });
    this.canvas.addEventListener('pointercancel', cancel);
    this.canvas.addEventListener('contextmenu', e => { if (e.target.closest('.md-tile') && matchMedia('(pointer: coarse)').matches) e.preventDefault(); });
  }

  onClick(e) {
    const check = e.target.closest('[data-tile-check]');
    const tile = e.target.closest('.md-tile');
    const dayAll = e.target.closest('[data-day-select]');
    if (dayAll) {
      e.preventDefault();
      const ids = JSON.parse(dayAll.dataset.daySelect);
      const all = ids.every(id => this.selected.has(id));
      ids.forEach(id => all ? this.selected.delete(id) : this.selected.add(id));
      this.refreshSelection();
      return;
    }
    if (!tile) return;
    e.preventDefault();
    const id = Number(tile.dataset.id);
    if (tile.dataset.pressed) { delete tile.dataset.pressed; return; }
    if (check || e.shiftKey || e.ctrlKey || e.metaKey || this.selected.size > 0) {
      this.toggle(id, e.shiftKey);
      return;
    }
    this.open(id);
  }

  toggle(id, range) {
    if (range && this.anchor !== null) {
      const flat = this.flat();
      const a = flat.findIndex(x => x.i === this.anchor), b = flat.findIndex(x => x.i === id);
      if (a >= 0 && b >= 0) {
        for (let i = Math.min(a, b); i <= Math.max(a, b); i++) this.selected.add(flat[i].i);
      }
    } else if (this.selected.has(id)) {
      this.selected.delete(id);
    } else {
      this.selected.add(id);
    }
    this.anchor = id;
    this.refreshSelection();
  }

  refreshSelection() {
    this.root.classList.toggle('is-selecting', this.selected.size > 0);
    for (const tile of this.canvas.querySelectorAll('.md-tile')) {
      const on = this.selected.has(Number(tile.dataset.id));
      tile.classList.toggle('is-selected', on);
      tile.querySelector('[data-tile-check]')?.setAttribute('aria-pressed', on ? 'true' : 'false');
    }
    this.selectionBar.update();
  }

  clearSelection() {
    this.selected.clear();
    this.anchor = null;
    this.refreshSelection();
  }

  /** Every loaded photo in the order of the grid. */
  flat() {
    return this.sections.flatMap(s => s.items || []);
  }

  // ---- layout ----------------------------------------------------------------------------------------------------------

  measure() {
    this.width = this.canvas.clientWidth || this.root.clientWidth;
    let top = 0;
    for (const s of this.sections) {
      s.top = top;
      if (s.items) this.layout(s);
      else s.height = this.estimate(s);
      top += s.height;
      if (s.node) s.node.style.top = s.top + 'px';
    }
    this.canvas.style.height = top + 'px';
  }

  estimate(s) {
    const perRow = Math.max(1, Math.floor(this.width / (this.target * 1.3)));
    const rows = Math.ceil(s.count / perRow);
    const head = s.key === 'all' ? 0 : 56;
    const days = this.days && s.key !== 'all' ? Math.min(s.count, Math.ceil(s.count / 6)) * 36 : 0;
    return head + days + rows * (this.target + GAP);
  }

  /** The rows and heads of a loaded month, as boxes relative to the month. */
  layout(s) {
    const blocks = [];
    let y = 0;
    if (s.key !== 'all') {
      blocks.push({ kind: 'month', y, h: 48 });
      y += 56;
    }
    const groups = this.days && s.key !== 'all' ? groupByDay(s.items) : [{ day: null, items: s.items }];
    for (const g of groups) {
      if (g.day) {
        blocks.push({ kind: 'day', y, h: 32, day: g.day, ids: g.items.map(x => x.i) });
        y += 36;
      }
      for (const row of justify(g.items, this.width, this.target)) {
        let x = 0;
        const h = Math.floor(row.height);
        for (const cell of row.cells) {
          const w = row.last ? Math.floor(cell.ratio * h) : Math.floor(cell.ratio * row.height);
          blocks.push({ kind: 'tile', x, y, w, h, item: cell.item });
          x += w + GAP;
        }
        // the last cell of a full row takes the pixels the rounding left
        const lastTile = blocks[blocks.length - 1];
        if (!row.last && lastTile && lastTile.kind === 'tile') lastTile.w = Math.max(1, this.width - lastTile.x);
        y += h + GAP;
      }
      y += 8;
    }
    s.blocks = blocks;
    s.height = y + 16;
  }

  // ---- render ----------------------------------------------------------------------------------------------------------

  render() {
    if (!this.sections.length) return;
    const view = this.viewport();
    const margin = window.innerHeight * 1.5;
    for (const s of this.sections) {
      const near = s.top + s.height > view.top - margin && s.top < view.bottom + margin;
      if (near) {
        if (!s.items && !s.loading) this.load(s);
        if (s.items && !s.node) this.draw(s);
      } else if (s.node) {
        s.node.remove();
        s.node = null;
      }
    }
    this.markScrubber(view);
  }

  viewport() {
    const rect = this.canvas.getBoundingClientRect();
    const top = -rect.top;
    return { top, bottom: top + this.scroller.clientHeight };
  }

  async load(s) {
    s.loading = (async () => {
      const items = [];
      try {
        while (items.length < s.count) {
          const data = await request('GET', `/live/items?scope=${encodeURIComponent(this.scope)}&bucket=${s.key}&offset=${items.length}&limit=1000`);
          items.push(...data.items);
          if (data.items.length < 1000) break;
        }
      } catch (e) {
        s.loading = null;
        if (e.status === 0) toast(t('Grid.Offline'), 'warning');
        return;
      }
      s.items = items;
      s.count = items.length;
      const before = s.height;
      const view = this.viewport();
      this.measure();
      // a month above the screen that changed its height moves what is shown: keep it in place
      if (s.top + before < view.top) this.scroller.scrollTop += s.height - before;
      this.render();
      this.viewer?.grew();
    })();
    return s.loading;
  }

  draw(s) {
    const node = el('div', { class: 'md-month', role: 'presentation', dataset: { key: s.key } });
    node.style.top = s.top + 'px';
    node.style.height = s.height + 'px';
    for (const b of s.blocks) {
      if (b.kind === 'month') {
        const head = el('h2', { class: 'md-month__title', text: s.label });
        head.style.top = b.y + 'px';
        node.append(head);
      } else if (b.kind === 'day') {
        const head = el('div', { class: 'md-day' },
          el('button', { type: 'button', class: 'md-day__check', 'aria-label': t('Grid.SelectDay', dayLabel(b.day)), dataset: { daySelect: JSON.stringify(b.ids) } }, icon('check')),
          el('span', { class: 'md-day__title', text: dayLabel(b.day) }));
        head.style.top = b.y + 'px';
        node.append(head);
      } else {
        node.append(this.tile(b));
      }
    }
    s.node = node;
    this.canvas.append(node);
  }

  tile(b) {
    const item = b.item;
    const on = this.selected.has(item.i);
    const label = (item.v ? t('Grid.Video') : t('Grid.Photo')) + ', ' + new Date(item.d).toLocaleString(lang(), { dateStyle: 'medium', timeStyle: 'short' });
    const node = el('a', { class: 'md-tile' + (on ? ' is-selected' : '') + (item.t ? '' : ' is-missing'), href: `#m${item.i}`, role: 'listitem', draggable: 'false', 'aria-label': label, dataset: { id: item.i } },
      item.t ? el('img', { src: thumbUrl(item), alt: '', loading: 'lazy', decoding: 'async', draggable: 'false' }) : icon(item.v ? 'video' : 'image', 'md-tile__placeholder'),
      el('span', { class: 'md-tile__check', role: 'button', tabindex: '-1', 'aria-pressed': on ? 'true' : 'false', 'aria-label': t('Grid.Select'), dataset: { tileCheck: '1' } }, icon('check')),
      item.v ? el('span', { class: 'md-tile__badge' }, icon('play'), el('span', { text: duration(item.v) })) : null,
      item.l ? el('span', { class: 'md-tile__badge md-tile__badge--live', title: 'Live' }, icon('live')) : null,
      item.f ? el('span', { class: 'md-tile__fav', 'aria-label': t('Grid.IsFavorite') }, icon('heart')) : null);
    node.style.left = b.x + 'px';
    node.style.top = b.y + 'px';
    node.style.width = b.w + 'px';
    node.style.height = b.h + 'px';
    const img = node.querySelector('img');
    if (img) img.addEventListener('error', () => { img.remove(); node.classList.add('is-missing'); node.prepend(icon(item.v ? 'video' : 'image', 'md-tile__placeholder')); }, { once: true });
    return node;
  }

  /** Redraws the months that are shown (after favourites or the trash changed what is in them). */
  redraw() {
    for (const s of this.sections) {
      if (s.node) { s.node.remove(); s.node = null; }
    }
    this.measure();
    this.render();
  }

  /** Takes photos out of the grid (the trash, off a list). */
  remove(ids) {
    const gone = new Set(ids);
    for (const s of this.sections) {
      if (!s.items) continue;
      const before = s.items.length;
      s.items = s.items.filter(x => !gone.has(x.i));
      s.count -= before - s.items.length;
    }
    this.sections = this.sections.filter(s => s.count > 0 || !s.items);
    ids.forEach(id => this.selected.delete(id));
    this.redraw();
    this.selectionBar.update();
    if (!this.sections.length) this.root.querySelector('[data-grid-empty]').hidden = false;
  }

  setFavorite(ids, on) {
    const set = new Set(ids);
    for (const item of this.flat()) if (set.has(item.i)) item.f = on ? 1 : 0;
    this.redraw();
  }

  // ---- the years at the right edge ---------------------------------------------------------------------------------------

  scrubber() {
    const nav = this.root.querySelector('[data-grid-scrubber]');
    if (!nav || this.scope.startsWith('list:')) return;
    const years = [];
    for (const s of this.sections) if (!years.length || years[years.length - 1].year !== s.year) years.push({ year: s.year, section: s });
    if (years.length < 2) return;
    nav.replaceChildren(...years.map(y => el('button', { type: 'button', class: 'md-scrubber__year', text: String(y.year), dataset: { key: y.section.key },
      onclick: () => { this.scroller.scrollTo({ top: this.canvas.offsetTop + y.section.top - 8, behavior: reduceMotion() ? 'auto' : 'smooth' }); } })));
    nav.hidden = false;
  }

  markScrubber(view) {
    const nav = this.root.querySelector('[data-grid-scrubber]');
    if (!nav || nav.hidden) return;
    const current = this.sections.find(s => s.top + s.height > view.top) || this.sections[0];
    for (const b of nav.children) b.classList.toggle('is-active', Number(b.textContent) === current.year);
  }

  // ---- the viewer ------------------------------------------------------------------------------------------------------

  openFromHash() {
    const match = /^#m(\d+)$/.exec(location.hash);
    if (match) this.open(Number(match[1]), true);
  }

  open(id, fromHash) {
    if (!this.viewer) this.viewer = new Viewer(this);
    if (!fromHash) history.pushState({ viewer: id }, '', `#m${id}`);
    this.viewer.show(id);
  }

  /** The photo next to <id> in the grid (direction 1 or -1), loading the next month when needed. */
  async neighbour(id, direction) {
    let flat = this.flat();
    let at = flat.findIndex(x => x.i === id);
    if (at < 0) return null;
    let next = flat[at + direction];
    if (next) return next;
    const index = this.sections.findIndex(s => s.items && s.items.some(x => x.i === id));
    const other = this.sections[index + direction];
    if (!other) return null;
    if (!other.items) await (other.loading || this.load(other));
    flat = this.flat();
    at = flat.findIndex(x => x.i === id);
    return flat[at + direction] || null;
  }

  item(id) {
    return this.flat().find(x => x.i === id) || null;
  }

  /** Brings the tile of a photo into the screen (when the viewer closes). */
  reveal(id) {
    for (const s of this.sections) {
      const b = s.blocks?.find(x => x.kind === 'tile' && x.item.i === id);
      if (b) {
        const y = this.canvas.offsetTop + s.top + b.y;
        const view = this.viewport();
        if (s.top + b.y < view.top || s.top + b.y + b.h > view.bottom) this.scroller.scrollTop = y - this.scroller.clientHeight / 3;
        requestAnimationFrame(() => this.canvas.querySelector(`.md-tile[data-id="${id}"]`)?.focus({ preventScroll: true }));
        return;
      }
    }
  }
}

function groupByDay(items) {
  const groups = [];
  for (const item of items) {
    const day = item.d.slice(0, 10);
    if (!groups.length || groups[groups.length - 1].day !== day) groups.push({ day, items: [] });
    groups[groups.length - 1].items.push(item);
  }
  return groups;
}

// ---- the selection --------------------------------------------------------------------------------------------------------

class SelectionBar {
  constructor(grid, bar) {
    this.grid = grid;
    this.bar = bar;
    if (!bar) return;
    bar.querySelector('[data-sel-clear]').addEventListener('click', () => grid.clearSelection());
    bar.addEventListener('click', e => {
      const button = e.target.closest('[data-sel-action]');
      if (button) this.run(button.dataset.selAction);
    });
    document.addEventListener('keydown', e => {
      if (e.key === 'Escape' && grid.selected.size && !document.querySelector('dialog[open]') && !grid.viewer?.isOpen) grid.clearSelection();
    });
  }

  update() {
    if (!this.bar) return;
    const n = this.grid.selected.size;
    this.bar.hidden = n === 0;
    this.bar.querySelector('[data-sel-count]').textContent = n === 1 ? t('Grid.SelectedOne') : t('Grid.Selected', n);
  }

  async run(action) {
    const ids = [...this.grid.selected];
    if (!ids.length) return;
    try {
      if (action === 'favorite') {
        const allFavorites = ids.every(id => this.grid.item(id)?.f);
        await request('POST', '/live/media/favorite', { ids, on: !allFavorites });
        this.grid.setFavorite(ids, !allFavorites);
        toast(allFavorites ? t('Grid.Unfavorited', ids.length) : t('Grid.Favorited', ids.length), 'success');
      } else if (action === 'list') {
        if (await addToList(ids)) this.grid.clearSelection();
      } else if (action === 'unlist') {
        const data = await request('POST', `/live/lists/${this.grid.listId}/remove`, { ids });
        toast(data.message, 'success');
        this.grid.remove(ids);
      } else if (action === 'trash') {
        const ok = await window.MatUi.confirm(ids.length === 1 ? t('Grid.TrashOne') : t('Grid.TrashMany', ids.length), { danger: true, ok: t('Grid.Trash'), title: t('Grid.Trash') });
        if (!ok) return;
        const data = await request('POST', '/live/media/trash', { ids });
        toast(data.message, data.refused ? 'warning' : 'success');
        if (data.errors?.length) window.MatUi.alert(data.errors.join('\n'), { title: t('Grid.TrashErrors') });
        const refused = new Set();
        if (data.refused || data.errors?.length) {
          // what could not be moved stays; reload to know exactly
          location.reload();
          return;
        }
        this.grid.remove(ids.filter(id => !refused.has(id)));
      }
    } catch (e) {
      toast(e.body?.error || (e.status === 0 ? t('Grid.Offline') : t('Grid.Failed')), 'danger');
    }
  }
}

/** The dialog "Add to list": the lists of the person (and the shared ones), and a new list by its name. Answers true when the photos were added. */
export async function addToList(ids) {
  let lists = [];
  try {
    lists = (await request('GET', '/live/lists')).lists;
  } catch {
    toast(t('Grid.Failed'), 'danger');
    return false;
  }
  return new Promise(resolve => {
    const name = el('input', { class: 'mat-input', id: 'md-newlist', name: 'name', autocomplete: 'off', maxlength: '120', placeholder: t('Lists.NewName') });
    const items = lists.map(l => el('li', { class: 'mat-item' },
      el('button', { type: 'button', class: 'mat-item__main', dataset: { list: l.id } },
        el('span', { class: 'mat-item__lead' }, (() => { const i = icon('list'); if (l.colour) i.dataset.colour = l.colour; return i; })()),
        el('span', { class: 'mat-item__body' }, el('span', { class: 'mat-item__title', text: l.name })))));
    const dialog = el('dialog', { class: 'mat-dialog mat-dialog--sm', 'aria-labelledby': 'md-addlist-t' },
      el('div', { class: 'mat-dialog__head' }, icon('list-plus'), el('h2', { class: 'mat-dialog__title', id: 'md-addlist-t', text: t('Grid.AddToListTitle', ids.length) }),
        el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close', 'data-dialog-close': '', 'aria-label': t('Common.Close') }, icon('x'))),
      el('div', { class: 'mat-dialog__body' },
        lists.length ? el('ul', { class: 'mat-itemlist md-listpick' }, items) : el('p', { class: 'md-muted', text: t('Lists.None') }),
        el('form', { class: 'md-newlist', dataset: { newList: '1' } },
          el('div', { class: 'mat-field' }, el('label', { class: 'mat-label', for: 'md-newlist', text: t('Lists.New') }), el('div', { class: 'mat-row' }, name,
            el('button', { type: 'submit', class: 'mat-btn mat-btn--secondary' }, icon('plus'), el('span', { text: t('Lists.Create') })))))),
      el('div', { class: 'mat-dialog__foot' }, el('div', { class: 'mat-btn-row mat-btn-row--plain' },
        el('button', { type: 'button', class: 'mat-btn mat-btn--secondary', 'data-dialog-close': '', text: t('Common.Cancel') }))));
    let done = false;
    const add = async (listId, listName) => {
      try {
        const data = await request('POST', `/live/lists/${listId}/add`, { ids });
        toast(data.message + (listName ? ' · ' + listName : ''), 'success');
        done = true;
        window.MatUi.dialog.close(dialog);
      } catch (e) {
        toast(e.body?.error || t('Grid.Failed'), 'danger');
      }
    };
    dialog.addEventListener('click', e => {
      const b = e.target.closest('[data-list]');
      if (b) add(b.dataset.list, b.textContent);
    });
    dialog.querySelector('[data-new-list]').addEventListener('submit', async e => {
      e.preventDefault();
      if (!name.value.trim()) { name.focus(); return; }
      try {
        const created = await request('POST', '/live/lists', { name: name.value });
        await add(created.id, created.name);
      } catch (err) {
        toast(err.body?.error || t('Grid.Failed'), 'danger');
      }
    });
    dialog.addEventListener('close', () => { resolve(done); setTimeout(() => dialog.remove(), 0); });
    document.body.append(dialog);
    window.MatUi.refresh?.(dialog);
    window.MatUi.dialog.open(dialog);
  });
}

// ---- the viewer -----------------------------------------------------------------------------------------------------------

class Viewer {
  constructor(grid) {
    this.grid = grid;
    this.id = null;
    this.detail = null;
    this.zoom = 1;
    this.pan = { x: 0, y: 0 };
    this.cache = new Map();
    this.slideshow = 0;
    this.build();
  }

  get isOpen() { return !this.node.hidden; }

  build() {
    const button = (name, label, ico, extra = {}) => el('button', { type: 'button', class: 'md-viewer__btn', 'aria-label': label, title: label, dataset: { v: name }, ...extra }, icon(ico));
    this.title = el('div', { class: 'md-viewer__title' }, el('strong', { 'data-v-title': '' }), el('small', { 'data-v-sub': '' }));
    this.favButton = button('favorite', t('Viewer.Favorite'), 'heart', { 'aria-pressed': 'false' });
    this.infoButton = button('info', t('Viewer.Info'), 'info', { 'aria-pressed': 'false' });
    this.trashButton = button('trash', t('Grid.Trash'), 'trash');
    this.liveButton = el('button', { type: 'button', class: 'md-viewer__live', dataset: { v: 'live' }, hidden: true }, icon('live'), el('span', { text: 'LIVE' }));
    this.stage = el('div', { class: 'md-viewer__stage' });
    this.info = el('aside', { class: 'md-viewer__info', 'aria-label': t('Viewer.Info'), hidden: true });
    this.prev = el('button', { type: 'button', class: 'md-viewer__nav md-viewer__nav--prev', 'aria-label': t('Viewer.Previous'), dataset: { v: 'prev' } }, icon('chevron-left'));
    this.next = el('button', { type: 'button', class: 'md-viewer__nav md-viewer__nav--next', 'aria-label': t('Viewer.Next'), dataset: { v: 'next' } }, icon('chevron-right'));
    this.node = el('div', { class: 'md-viewer', role: 'dialog', 'aria-modal': 'true', 'aria-label': t('Viewer.Label'), hidden: true, tabindex: '-1' },
      el('header', { class: 'md-viewer__bar' },
        button('close', t('Viewer.Close'), 'back'),
        this.title,
        el('div', { class: 'md-viewer__actions' },
          this.liveButton,
          button('slideshow', t('Viewer.Slideshow'), 'slideshow', { 'aria-pressed': 'false' }),
          button('zoom', t('Viewer.Zoom'), 'zoom-in'),
          this.favButton,
          button('list', t('Grid.AddToList'), 'list-plus'),
          button('download', t('Viewer.Download'), 'download'),
          this.trashButton,
          this.infoButton)),
      el('div', { class: 'md-viewer__body' }, this.stage, this.prev, this.next, this.info));
    // the info panel opens as it was left (on a wide screen)
    if (localStorage.getItem('md-viewer-info') === '1' && window.innerWidth > 900) {
      this.info.hidden = false;
      this.infoButton.setAttribute('aria-pressed', 'true');
    }

    document.body.append(this.node);
    this.node.addEventListener('click', e => {
      const b = e.target.closest('[data-v]');
      if (b) this.action(b.dataset.v);
    });
    this.node.addEventListener('keydown', e => this.key(e));
    this.swipe();
  }

  async show(id) {
    this.id = id;
    if (this.node.hidden) {
      this.returnFocus = document.activeElement;
      this.node.hidden = false;
      document.documentElement.classList.add('md-viewer-open');
      this.node.focus({ preventScroll: true });
    }
    this.resetZoom();
    const item = this.grid.item(id);
    this.paint(item);
    this.preload();
    try {
      const detail = await request('GET', `/live/media/${id}`);
      if (this.id !== id) return;
      this.detail = detail;
      this.describe(detail);
      if (!item) this.paint(null, detail);
    } catch (e) {
      if (e.status === 404) { toast(t('Viewer.Gone'), 'warning'); this.close(); }
    }
  }

  /** The picture of a photo, the player of a video. */
  paint(item, detail) {
    const id = this.id;
    const k = item?.k || detail?.hash;
    const isVideo = item ? !!item.v : detail?.type === 'video';
    this.stage.replaceChildren();
    this.liveButton.hidden = !(item?.l || detail?.live);
    if (isVideo) {
      const video = el('video', { class: 'md-viewer__video', controls: true, autoplay: true, playsinline: true, preload: 'metadata', src: originalUrl(id) });
      if (k) video.poster = `/media/${id}/preview?v=${k}`;
      video.addEventListener('error', () => {
        this.stage.append(el('p', { class: 'md-viewer__note', text: t('Viewer.VideoUnsupported') }));
      }, { once: true });
      this.stage.append(video);
      return;
    }
    const img = el('img', { class: 'md-viewer__img', alt: '', draggable: 'false', decoding: 'async' });
    if (item) {
      // the tile first (it is in the cache of the browser), the preview over it as soon as it is there
      img.src = thumbUrl(item);
      const full = this.cache.get(id) || this.load(item);
      full.then(src => { if (this.id === id && src) img.src = src; });
    } else if (k) {
      img.src = `/media/${id}/preview?v=${k}`;
    }
    img.addEventListener('error', () => { if (this.id === id) this.stage.replaceChildren(el('div', { class: 'md-viewer__missing' }, icon('image'), el('p', { text: t('Viewer.NoPreview') }))); }, { once: true });
    this.stage.append(img);
  }

  load(item) {
    const promise = new Promise(resolve => {
      const image = new Image();
      image.decoding = 'async';
      image.onload = () => resolve(image.src);
      image.onerror = () => resolve(null);
      image.src = previewUrl(item);
    });
    this.cache.set(item.i, promise);
    if (this.cache.size > 24) this.cache.delete(this.cache.keys().next().value);
    return promise;
  }

  /** The next two and the previous photo are loaded ahead, so turning the page shows them at once. */
  async preload() {
    const id = this.id;
    const next = await this.grid.neighbour(id, 1);
    const prev = await this.grid.neighbour(id, -1);
    if (this.id !== id) return;
    this.prev.hidden = !prev;
    this.next.hidden = !next;
    for (const item of [next, prev]) if (item && !item.v && !this.cache.has(item.i)) this.load(item);
    if (next) {
      const after = await this.grid.neighbour(next.i, 1);
      if (after && !after.v && !this.cache.has(after.i)) this.load(after);
    }
  }

  grew() {
    if (this.isOpen) this.preload();
  }

  describe(d) {
    this.node.querySelector('[data-v-title]').textContent = d.title;
    this.node.querySelector('[data-v-sub]').textContent = d.name;
    this.favButton.setAttribute('aria-pressed', d.favorite ? 'true' : 'false');
    this.favButton.classList.toggle('is-on', d.favorite);
    this.trashButton.hidden = !d.canEdit;
    this.info.replaceChildren(...[
      el('h2', { class: 'md-viewer__info-title', text: t('Viewer.Info') }),
      this.peopleSection(d),
      el('ul', { class: 'md-facts' }, d.rows.map(r => el('li', { class: 'md-fact' },
        icon(r.icon),
        el('div', { class: 'md-fact__body' },
          r.href ? el('a', { href: r.href, class: 'md-fact__value', text: r.value }) : el('span', { class: 'md-fact__value', text: r.value }),
          el('small', { class: 'md-fact__label', text: r.secondary || r.label }))))),
      d.lists.length ? el('div', { class: 'md-viewer__lists' }, el('h3', { text: t('Lists.Title') }),
        el('div', { class: 'mat-chips' }, d.lists.map(l => el('a', { class: 'mat-chip', href: `/Lists/View/${l.id}` }, el('span', { class: 'mat-chip__text', text: l.name }))))) : null].filter(Boolean));
  }

  /**
   * The faces on the photo (when the recognition is on): a person links to all their photos; the pencil says who it is – a name another person has puts the face
   * there, a new name makes a person, "nobody" hides the face (someone in the background).
   */
  peopleSection(d) {
    if (!d.people?.length) return null;
    const form = el('form', { class: 'md-viewer__name', hidden: true });
    const list = el('div', { class: 'md-viewer__people-list' }, d.people.map(p => {
      const face = el('img', { src: `/faces/${p.face}/thumb`, alt: '', loading: 'lazy' });
      const label = el('span', { text: p.name });
      const main = p.id
        ? el('a', { class: 'md-viewer__person-main', href: `/People/View/${p.id}` }, face, label)
        : el('span', { class: 'md-viewer__person-main' }, face, label);
      const edit = el('button', { type: 'button', class: 'md-viewer__person-edit', 'aria-label': t('Viewer.WhoIsThis'), title: t('Viewer.WhoIsThis') }, icon('edit'));
      edit.addEventListener('click', () => this.nameFace(form, p));
      return el('div', { class: 'md-viewer__person' + (p.named ? '' : ' is-unnamed') + (p.id ? '' : ' is-unknown') }, main, edit);
    }));
    return el('div', { class: 'md-viewer__people' }, el('h3', { text: t('Viewer.People') }), list, form);
  }

  /** The little form under the faces: the name (the names there are suggested), save, "nobody", cancel. */
  async nameFace(form, p) {
    const id = this.id;
    if (!this.names) {
      try { this.names = (await request('GET', '/live/people/names')).names; } catch { this.names = []; }
    }
    const input = el('input', { class: 'mat-input', id: 'md-face-name', name: 'name', autocomplete: 'off', maxlength: '200', list: 'md-face-names', value: p.current || '', placeholder: t('Viewer.NamePlaceholder') });
    const cancel = el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--sm', text: t('Common.Cancel') });
    const nobody = el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--sm', title: t('Viewer.NobodyHint') }, icon('eye-off'), el('span', { text: t('Viewer.Nobody') }));
    form.replaceChildren(
      el('img', { class: 'md-viewer__name-face', src: `/faces/${p.face}/thumb`, alt: '' }),
      el('div', { class: 'mat-field' },
        el('label', { class: 'mat-label', for: 'md-face-name', text: t('Viewer.WhoIsThis') }),
        input,
        el('datalist', { id: 'md-face-names' }, this.names.map(n => el('option', { value: n }))),
        el('span', { class: 'mat-hint', text: t('Viewer.NameHint') })),
      el('div', { class: 'mat-btn-row mat-btn-row--plain' },
        el('button', { type: 'submit', class: 'mat-btn mat-btn--primary mat-btn--sm' }, icon('check'), el('span', { text: t('Viewer.SaveName') })), nobody, cancel));
    form.hidden = false;
    input.focus();
    input.select();
    const refresh = async message => {
      toast(message, 'success');
      this.names = null;
      if (this.id !== id) return;
      try { this.detail = await request('GET', `/live/media/${id}`); this.describe(this.detail); } catch { /* the next photo shows it */ }
    };
    cancel.onclick = () => { form.hidden = true; form.replaceChildren(); };
    nobody.onclick = async () => {
      try { await refresh((await request('POST', `/live/faces/${p.face}/ignore`, {})).message); } catch (e) { toast(e.body?.error || t('Grid.Failed'), 'danger'); }
    };
    form.onsubmit = async e => {
      e.preventDefault();
      if (!input.value.trim()) { input.focus(); return; }
      try { await refresh((await request('POST', `/live/faces/${p.face}/name`, { name: input.value })).message); } catch (err) { toast(err.body?.error || t('Grid.Failed'), 'danger'); }
    };
  }

  async action(name) {
    const id = this.id;
    switch (name) {
      case 'close': this.close(); break;
      case 'prev': this.go(-1); break;
      case 'next': this.go(1); break;
      case 'info': {
        const open = this.info.hidden;
        this.info.hidden = !open;
        this.infoButton.setAttribute('aria-pressed', open ? 'true' : 'false');
        localStorage.setItem('md-viewer-info', open ? '1' : '0');
        break;
      }
      case 'favorite': {
        const on = !(this.detail?.favorite);
        try {
          await request('POST', '/live/media/favorite', { ids: [id], on });
          if (this.detail) this.detail.favorite = on;
          this.favButton.setAttribute('aria-pressed', on ? 'true' : 'false');
          this.favButton.classList.toggle('is-on', on);
          this.grid.setFavorite([id], on);
        } catch { toast(t('Grid.Failed'), 'danger'); }
        break;
      }
      case 'list': await addToList([id]); this.show(id); break;
      case 'download': {
        const a = el('a', { href: originalUrl(id) + '?download=true', download: this.detail?.name || '' });
        document.body.append(a);
        a.click();
        a.remove();
        break;
      }
      case 'trash': {
        const ok = await window.MatUi.confirm(t('Grid.TrashOne'), { danger: true, ok: t('Grid.Trash'), title: t('Grid.Trash') });
        if (!ok) return;
        try {
          const data = await request('POST', '/live/media/trash', { ids: [id] });
          if (!data.moved) { toast(data.errors?.[0] || data.message, 'danger'); return; }
          toast(data.message, 'success');
          const next = (await this.grid.neighbour(id, 1)) || (await this.grid.neighbour(id, -1));
          this.grid.remove([id]);
          if (next) { history.replaceState({ viewer: next.i }, '', `#m${next.i}`); this.show(next.i); } else this.close();
        } catch (e) { toast(e.body?.error || t('Grid.Failed'), 'danger'); }
        break;
      }
      case 'zoom': this.toggleZoom(); break;
      case 'live': this.playLive(); break;
      case 'slideshow': this.toggleSlideshow(); break;
    }
  }

  async go(direction) {
    const item = await this.grid.neighbour(this.id, direction);
    if (!item) return;
    history.replaceState({ viewer: item.i }, '', `#m${item.i}`);
    this.show(item.i);
  }

  close(fromHistory) {
    if (this.node.hidden) return;
    this.stopSlideshow();
    this.node.hidden = true;
    this.stage.replaceChildren();
    document.documentElement.classList.remove('md-viewer-open');
    if (!fromHistory && location.hash.startsWith('#m')) history.back();
    this.grid.reveal(this.id);
  }

  key(e) {
    if (e.target.closest('input, textarea, select')) return;
    if (e.key === 'Escape') { e.preventDefault(); if (this.zoom > 1) this.resetZoom(); else this.close(); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); this.go(1); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); this.go(-1); }
    else if (e.key === 'i') this.action('info');
    else if (e.key === 'f') this.action('favorite');
    else if (e.key === 'z') this.toggleZoom();
    else if (e.key === ' ' && !this.stage.querySelector('video')) { e.preventDefault(); this.toggleSlideshow(); }
  }

  swipe() {
    let start = null;
    this.stage.addEventListener('pointerdown', e => {
      if (e.target.closest('video')) return;
      start = { x: e.clientX, y: e.clientY, pan: { ...this.pan }, at: Date.now() };
      this.stage.setPointerCapture(e.pointerId);
    });
    this.stage.addEventListener('pointermove', e => {
      if (!start) return;
      const dx = e.clientX - start.x, dy = e.clientY - start.y;
      const img = this.stage.querySelector('img');
      if (this.zoom > 1 && img) {
        this.pan = { x: start.pan.x + dx, y: start.pan.y + dy };
        img.style.transform = `translate(${this.pan.x}px, ${this.pan.y}px) scale(${this.zoom})`;
      } else if (img && Math.abs(dx) > Math.abs(dy)) {
        img.style.transform = `translateX(${dx}px)`;
      } else if (img && dy > 0) {
        img.style.transform = `translateY(${dy}px) scale(${Math.max(0.8, 1 - dy / 1200)})`;
        this.node.style.setProperty('--md-viewer-dim', String(Math.max(0.3, 1 - dy / 400)));
      }
    });
    const end = e => {
      if (!start) return;
      const dx = e.clientX - start.x, dy = e.clientY - start.y;
      const quick = Date.now() - start.at < 300;
      start = null;
      const img = this.stage.querySelector('img');
      this.node.style.removeProperty('--md-viewer-dim');
      if (this.zoom > 1) return;
      if (img) img.style.transform = '';
      if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy)) this.go(dx < 0 ? 1 : -1);
      else if (dy > 120) this.close();
      else if (quick && Math.abs(dx) < 6 && Math.abs(dy) < 6 && e.pointerType === 'touch') this.node.classList.toggle('is-bare');
    };
    this.stage.addEventListener('pointerup', end);
    this.stage.addEventListener('pointercancel', end);
    this.stage.addEventListener('dblclick', () => this.toggleZoom());
    this.stage.addEventListener('wheel', e => {
      if (!e.ctrlKey) return;
      e.preventDefault();
      if (e.deltaY < 0 && this.zoom === 1) this.toggleZoom();
      else if (e.deltaY > 0 && this.zoom > 1) this.resetZoom();
    }, { passive: false });
  }

  async toggleZoom() {
    const img = this.stage.querySelector('img');
    if (!img) return;
    if (this.zoom > 1) { this.resetZoom(); return; }
    // the original where the browser can show it (full resolution), else the preview made larger
    if (this.detail?.browserCanShow && this.detail.type === 'photo') img.src = originalUrl(this.id);
    this.zoom = 2.5;
    img.classList.add('is-zoomed');
    img.style.transform = `scale(${this.zoom})`;
  }

  resetZoom() {
    this.zoom = 1;
    this.pan = { x: 0, y: 0 };
    const img = this.stage.querySelector('img');
    if (img) { img.style.transform = ''; img.classList.remove('is-zoomed'); }
  }

  playLive() {
    const live = this.detail?.live;
    if (!live) return;
    const video = el('video', { class: 'md-viewer__video md-viewer__video--live', autoplay: true, muted: true, playsinline: true, src: originalUrl(live) });
    video.muted = true;
    video.addEventListener('ended', () => video.remove());
    video.addEventListener('error', () => video.remove());
    this.stage.append(video);
  }

  toggleSlideshow() {
    if (this.slideshow) { this.stopSlideshow(); return; }
    const button = this.node.querySelector('[data-v="slideshow"]');
    button.setAttribute('aria-pressed', 'true');
    this.slideshow = setInterval(async () => {
      const next = await this.grid.neighbour(this.id, 1);
      if (!next) { this.stopSlideshow(); return; }
      history.replaceState({ viewer: next.i }, '', `#m${next.i}`);
      this.show(next.i);
    }, 4000);
  }

  stopSlideshow() {
    clearInterval(this.slideshow);
    this.slideshow = 0;
    this.node.querySelector('[data-v="slideshow"]')?.setAttribute('aria-pressed', 'false');
  }
}

// the size of the tiles (the buttons of the header bar), kept in the browser
document.addEventListener('click', e => {
  const b = e.target.closest('[data-grid-zoom]');
  if (!b) return;
  const order = ['s', 'm', 'l'];
  const current = localStorage.getItem(sizeKey) || 'm';
  const next = order[Math.min(2, Math.max(0, order.indexOf(current) + (b.dataset.gridZoom === 'in' ? 1 : -1)))];
  localStorage.setItem(sizeKey, next);
  document.dispatchEvent(new CustomEvent('md:grid-size', { detail: next }));
});

for (const root of document.querySelectorAll('[data-photo-grid]')) new PhotoGrid(root);
