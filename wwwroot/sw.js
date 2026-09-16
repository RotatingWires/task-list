const CACHE = 'retrotodo-shell-v4';
const SHELL = ['/', '/style.css', '/app.js', '/manifest.webmanifest', '/icons/icon-192.png', '/icons/icon-512.png', '/icons/icon-180.png'];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)));
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
  );
  self.clients.claim();
});

self.addEventListener('fetch', event => {
  const request = event.request;
  const url = new URL(request.url);

  // Task data always comes from the NAS. Don't cache API reads/writes yet.
  if (url.pathname.startsWith('/api/')) return;

  event.respondWith(
    caches.match(request).then(cached => cached || fetch(request))
  );
});
