const CACHE = 'task-list-shell-v1.5.10';
const SHELL = [
  '/login.html',
  '/login.js',
  '/version.js',
  '/theme.js',
  '/style.css?v=1.5.9',
  '/task-controls.css?v=1.5.9',
  '/theme.css?v=1.5.9',
  '/milestones.css',
  '/app.js?v=1.5.9',
  '/tasks.js?v=1.5.9',
  '/lists.js?v=1.5.9',
  '/search.js',
  '/milestones.js?v=1.5.10',
  '/ui.js?v=1.5.10',
  '/manifest.webmanifest',
  '/favicon.ico',
  '/icons/favicon.svg',
  '/icons/icon-192.png',
  '/icons/icon-512.png',
  '/icons/icon-180.png'
];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)));
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys().then(keys => Promise.all(keys.filter(key => key !== CACHE).map(key => caches.delete(key))))
  );
  self.clients.claim();
});

self.addEventListener('fetch', event => {
  const request = event.request;
  const url = new URL(request.url);

  if (url.pathname.startsWith('/api/')) return;
  if (request.method !== 'GET' || url.origin !== self.location.origin) return;

  event.respondWith(
    fetch(request, { cache: 'no-store' }).then(response => {
      const copy = response.clone();
      caches.open(CACHE).then(cache => cache.put(request, copy));
      return response;
    }).catch(() => caches.match(request).then(hit => hit || caches.match(url.pathname)))
  );
});
