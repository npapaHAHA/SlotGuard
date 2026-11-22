"use strict";
const version = "slotguard-offline-v2";
const offlineFiles = ["/offline.html", "/time-tools.html", "/time-tools.js", "/time-zones.js",
  "/styles.css", "/workspace.css", "/p3.css", "/theme.css", "/theme.js", "/design/slotguard-mark.svg"];
const offline = new Set(offlineFiles);
self.addEventListener("install", event => event.waitUntil(caches.open(version).then(cache => cache.addAll(offlineFiles))));
self.addEventListener("activate", event => event.waitUntil((async () => {
  const names = await caches.keys();
  await Promise.all(names.filter(name => name.startsWith("slotguard-offline-") && name !== version).map(name => caches.delete(name)));
  await self.clients.claim();
})()));
self.addEventListener("fetch", event => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== "GET" || url.origin !== self.location.origin ||
      url.pathname.startsWith("/api/") || url.pathname.startsWith("/hubs/") || url.pathname === "/mcp") return;
  if (offline.has(url.pathname) && !url.search) {
    event.respondWith(fetch(request).catch(async () => (await caches.open(version)).match(url.pathname)));
  } else if (request.mode === "navigate") {
    event.respondWith(fetch(request).catch(async () => (await caches.open(version)).match("/offline.html")));
  }
});
