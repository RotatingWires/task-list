const CACHE = 'task-list-shell-v1-2-7';
const SHELL = ['/login.html', '/login.js', '/style.css', '/app.js', '/manifest.webmanifest', '/favicon.ico', '/icons/favicon.svg', '/icons/icon-192.png', '/icons/icon-512.png', '/icons/icon-180.png'];

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
  if (url.pathname.startsWith('/api/')) return;
  event.respondWith(caches.match(request).then(cached => cached || fetch(request)));
});
