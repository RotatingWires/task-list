// TaskList keeps no offline copy, like NewsReview: without a connection the browser shows its own offline page, which
// makes a connection problem obvious. A browser still running the worker from an earlier release picks up this one,
// which deletes that release's cache and then removes itself.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    for (const key of await caches.keys()) await caches.delete(key);
    await self.registration.unregister();
  })());
});
