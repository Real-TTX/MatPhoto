/* MatPhoto service worker: the app as a PWA – small on purpose.
   - Static files (toolkit, styles, scripts, icons): from the cache first, refreshed in the background.
   - Pages and the months of the grids (/live/timeline, /live/items) come from the network; a copy of the last ones is kept, so without a network what was seen
     last is still shown (read-only in effect: every change needs the network). Other pages without a network: the offline page with a button to try again.
   - Thumbnails and previews (/media/{id}/thumb|preview?v=…): their address names their content, so they come from the cache first (the last 3000), and the
     photos you scrolled through are there offline too.
   - Originals, videos, uploads, the platform API and every change (POST) are never touched. */
const VERSION = 'matphoto-v1';
const PAGES = 'mp-pages';
const THUMBS = 'mp-thumbs';
const SHELL = [
  '/offline.html', '/js/offline.js', '/js/pwa.js', '/matui/matui.css', '/matui/matui.js', '/matui/matui.boot.js', '/css/app.css', '/favicon.svg', '/img/icon-192.png', '/img/icon-512.png',
  '/img/logo/matphoto-logo.svg', '/img/logo/matphoto-logo-dark.svg', '/img/logo/matphoto-mark.svg', '/manifest.webmanifest', '/js/util.js', '/js/photos.js',
];
const MAX_PAGES = 60;
const MAX_THUMBS = 3000;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== VERSION && k !== PAGES && k !== THUMBS).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

// signing out clears what was kept for this person (sent by pwa.js)
self.addEventListener('message', event => {
  if (event.data === 'md-clear') {
    event.waitUntil(Promise.all([caches.delete(PAGES), caches.delete(THUMBS)]));
  }
});

const isStatic = url => /^\/(matui|css|js|img)\//.test(url.pathname) || ['/favicon.svg', '/manifest.webmanifest', '/offline.html'].includes(url.pathname);
const isPicture = url => /^\/media\/\d+\/(thumb|preview)$/.test(url.pathname) && url.searchParams.has('v');
const isGridData = url => url.pathname === '/live/timeline' || url.pathname === '/live/items' || /^\/live\/media\/\d+$/.test(url.pathname);
const isLibraryPage = url => /^\/(Photos|Explore|Explore\/View|Albums|Albums\/View\/\d+|Lists|Lists\/View\/\d+|Favorites|Search)$/i.test(url.pathname);

function trim(cache, max) {
  return cache.keys().then(keys => (keys.length > max ? Promise.all(keys.slice(0, keys.length - max).map(k => cache.delete(k))) : undefined));
}

function networkFirst(request, cacheName, max, fallback) {
  return fetch(request).then(response => {
    if (response.ok) {
      const copy = response.clone();
      caches.open(cacheName).then(cache => cache.put(request, copy).then(() => trim(cache, max)));
    }
    return response;
  }).catch(() => caches.open(cacheName).then(cache => cache.match(request)).then(hit => hit || (fallback ? caches.match(fallback) : Response.error())));
}

function cacheFirst(request, cacheName, max) {
  return caches.open(cacheName).then(cache => cache.match(request).then(hit => hit || fetch(request).then(response => {
    if (response.ok) {
      cache.put(request, response.clone()).then(() => (Math.random() < 0.02 ? trim(cache, max) : undefined));
    }
    return response;
  })));
}

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname === '/sw.js' || url.pathname === '/health') return;

  if (isPicture(url)) { event.respondWith(cacheFirst(request, THUMBS, MAX_THUMBS)); return; }
  if (isGridData(url)) { event.respondWith(networkFirst(request, PAGES, 400)); return; }
  if (/^\/(api|live|media|import)\//.test(url.pathname)) return;

  if (request.mode === 'navigate') {
    event.respondWith(isLibraryPage(url) ? networkFirst(request, PAGES, MAX_PAGES, '/offline.html') : fetch(request).catch(() => caches.match('/offline.html')));
    return;
  }

  // scripts and styles without a version in the address: from the network while there is one, so a new build is used at once
  if (isStatic(url) && !url.search.includes('v=') && /^\/(js|css)\//.test(url.pathname)) { event.respondWith(networkFirst(request, VERSION, 500)); return; }

  if (isStatic(url)) {
    event.respondWith(caches.open(VERSION).then(cache => cache.match(request).then(hit => {
      const fresh = fetch(request).then(response => { if (response.ok) cache.put(request, response.clone()); return response; }).catch(() => hit);
      return hit || fresh;
    })));
  }
});
