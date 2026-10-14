/* Matddns service worker.
   - Static assets (CSS, JS, icons) are cached so the app shell opens without a network.
   - Pages are always fetched fresh. The one exception is the read-only dashboard: its last good copy is kept and shown
     (with an "offline" notice added by the page script) when the server cannot be reached.
   - Any other page that cannot be loaded shows /offline.html, which retries by itself.
   - Nothing that changes data is ever cached or replayed. */

const VERSION = new URL(self.location.href).searchParams.get('v') || 'dev'; // the page passes the build tag: a new build = a new worker
const STATIC_CACHE = 'matddns-static-' + VERSION;
const PAGE_CACHE = 'matddns-pages';
const DASHBOARD_KEY = '/__last-dashboard';
const OFFLINE_URL = '/offline.html';

// The asset URLs carry the same build tag (?v=), so they can be cached by their exact address.
// The offline page asks for the same files without the tag; handleStatic finds them anyway.
const PRECACHE = [
  OFFLINE_URL,
  '/favicon.svg',
  '/img/icon-192.png',
  '/img/logo.svg?v=' + VERSION,
  '/img/logo-detail.svg?v=' + VERSION,
  '/matui/matui.css?v=' + VERSION,
  '/matui/matui.js?v=' + VERSION,
  '/matui/matui.boot.js?v=' + VERSION,
  '/css/matddns.css?v=' + VERSION,
  '/js/matddns.js?v=' + VERSION,
  '/js/offline.js?v=' + VERSION,
];

self.addEventListener('install', function (event) {
  event.waitUntil(caches.open(STATIC_CACHE).then(function (cache) { return cache.addAll(PRECACHE); }).then(function () { return self.skipWaiting(); }));
});

self.addEventListener('activate', function (event) {
  event.waitUntil(
    caches.keys()
      .then(function (keys) { return Promise.all(keys.filter(function (k) { return k.startsWith('matddns-static-') && k !== STATIC_CACHE; }).map(function (k) { return caches.delete(k); })); })
      .then(function () { return self.clients.claim(); })
  );
});

function isStaticAsset(url) {
  return url.pathname.startsWith('/matui/') || url.pathname.startsWith('/css/') || url.pathname.startsWith('/js/') || url.pathname.startsWith('/img/') || url.pathname === '/favicon.svg';
}

self.addEventListener('fetch', function (event) {
  const request = event.request;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) { return; }

  if (request.mode === 'navigate') {
    event.respondWith(handleNavigation(request, url));
  } else if (request.method === 'GET' && isStaticAsset(url)) {
    event.respondWith(handleStatic(event, request, url));
  }
  // Everything else (API calls, the health check, requests made by scripts) goes straight to the network.
});

// Cache first; assets without a version tag are refreshed in the background so they do not go stale.
function handleStatic(event, request, url) {
  return caches.open(STATIC_CACHE).then(function (cache) {
    return cache.match(request).then(function (cached) {
      if (cached && url.searchParams.has('v')) { return cached; } // versioned: never changes
      const fetched = fetch(request).then(function (response) {
        if (response.ok) { cache.put(request, response.clone()); }
        return response;
      });
      if (cached) {
        event.waitUntil(fetched.catch(function () { /* offline: the cached copy is enough */ }));
        return cached;
      }
      // Not cached under this exact address and no network: any copy of the same file will do. The offline page asks without the tag,
      // and the saved dashboard of an older build asks for the tag of that build.
      return fetched.catch(function () {
        return cache.match(request, { ignoreSearch: true }).then(function (any) { return any || Response.error(); });
      });
    });
  });
}

function handleNavigation(request, url) {
  // Signing out wipes what was kept for offline use, even if the sign-out request itself cannot reach the server.
  if (url.pathname === '/Logout') { caches.delete(PAGE_CACHE); }

  return fetch(request).then(function (response) {
    if (request.method === 'GET') {
      if (url.pathname === '/') { rememberDashboard(response); }
      else if (response.type === 'opaqueredirect' || response.status === 401 || response.status === 403) { caches.delete(PAGE_CACHE); } // session ended
    }
    return response;
  }).catch(function () { return offlineResponse(request, url); });
}

// Keep the last good dashboard; an expired session (a redirect to the login page) removes it.
function rememberDashboard(response) {
  if (response.type === 'opaqueredirect' || response.status !== 200) { caches.delete(PAGE_CACHE); return; }
  const copy = response.clone();
  copy.text().then(function (html) {
    const headers = new Headers({ 'Content-Type': 'text/html; charset=utf-8', 'X-Saved-At': new Date().toISOString() });
    return caches.open(PAGE_CACHE).then(function (cache) { return cache.put(DASHBOARD_KEY, new Response(html, { headers: headers })); });
  });
}

function offlineResponse(request, url) {
  if (request.method === 'GET' && url.pathname === '/') {
    return caches.open(PAGE_CACHE).then(function (cache) { return cache.match(DASHBOARD_KEY); }).then(function (saved) {
      if (!saved) { return fallbackPage(false); }
      const savedAt = saved.headers.get('X-Saved-At') || '';
      return saved.text().then(function (html) {
        // The page script reads this marker to show "last known state" and to reload once the server is back.
        const marked = html.replace('</head>', '<meta name="offline-copy" content="' + savedAt + '"></head>');
        return new Response(marked, { headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' } });
      });
    });
  }
  return fallbackPage(request.method !== 'GET');
}

// The offline page. For a form that was being sent it is marked, so the page offers "Go back" instead of a reload that would send it again.
function fallbackPage(wasSending) {
  return caches.match(OFFLINE_URL).then(function (page) {
    if (!page) { return new Response('You are offline.', { status: 503, headers: { 'Content-Type': 'text/plain; charset=utf-8' } }); }
    if (!wasSending) { return page; }
    return page.text().then(function (html) {
      return new Response(html.replace('</head>', '<meta name="offline-post" content="1"></head>'), { headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' } });
    });
  });
}
