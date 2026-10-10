/* Matddns UI behaviour. Plain JavaScript, no build step.
   Every behaviour is switched on by a data-* attribute, so several of the same control can live on one page. */
(function () {
  'use strict';

  var script = document.currentScript; // needed for data-sw; only available while the script is first executed
  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  // ------------------------------------------------------------------ theme (system / light / dark)

  var THEME_KEY = 'matddns-theme';

  function applyTheme(pref) {
    var dark = pref === 'dark' || (pref === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
    document.documentElement.setAttribute('data-theme-pref', pref);
    // The browser / installed-app status bar takes this colour, so it blends into the app bar.
    var meta = document.getElementById('meta-theme-color');
    if (meta) { meta.setAttribute('content', dark ? '#121830' : '#ffffff'); }
    $$('[data-theme-set]').forEach(function (b) { b.classList.toggle('is-active', b.getAttribute('data-theme-set') === pref); });
  }

  function currentPref() { try { return localStorage.getItem(THEME_KEY) || 'system'; } catch (e) { return 'system'; } }

  applyTheme(currentPref());
  if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
      if (currentPref() === 'system') { applyTheme('system'); }
    });
  }

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-theme-set]');
    if (!btn) { return; }
    var pref = btn.getAttribute('data-theme-set');
    try { localStorage.setItem(THEME_KEY, pref); } catch (err) { /* private mode */ }
    applyTheme(pref);
  });

  // ------------------------------------------------------------------ scrollbars only while scrolling

  var scrollTimers = new WeakMap();
  document.addEventListener('scroll', function (e) {
    var el = e.target === document ? document.documentElement : e.target;
    if (!el || !el.classList) { return; }
    el.classList.add('is-scrolling');
    clearTimeout(scrollTimers.get(el));
    scrollTimers.set(el, setTimeout(function () { el.classList.remove('is-scrolling'); }, 700));
  }, true);

  // ------------------------------------------------------------------ small helpers: menu, select-all, dialogs

  document.addEventListener('click', function (e) {
    var link = e.target.closest('.sidebar a.nav-link');
    var toggle = document.getElementById('nav-toggle');
    if (link && toggle) { toggle.checked = false; } // mobile menu closes after a tap on a link

    var field = e.target.closest('input[data-select]');
    if (field) { field.select(); }

    var open = e.target.closest('[data-dialog-open]');
    if (open) { var dlg = $(open.getAttribute('data-dialog-open')); if (dlg && dlg.showModal) { dlg.showModal(); } }
    var close = e.target.closest('[data-dialog-close]');
    if (close) { var host = close.closest('dialog'); if (host) { host.close(); } }
  });

  // The menu button is a label for a hidden checkbox: make it work with the keyboard too.
  document.addEventListener('keydown', function (e) {
    var toggle = document.getElementById('nav-toggle');
    if (!toggle) { return; }
    if ((e.key === 'Enter' || e.key === ' ') && e.target.closest && e.target.closest('.topbar__menu')) { e.preventDefault(); toggle.checked = !toggle.checked; }
    else if (e.key === 'Escape' && toggle.checked) { toggle.checked = false; }
  });

  // ------------------------------------------------------------------ tabs: <div data-tabs="key"> buttons[data-tab] + [data-tab-panel]

  $$('[data-tabs]').forEach(function (bar) {
    var key = 'tab-' + bar.getAttribute('data-tabs');
    var tabs = $$('[data-tab]', bar);
    var panels = $$('[data-tab-panel]');
    function show(name) {
      tabs.forEach(function (t) { t.classList.toggle('is-active', t.getAttribute('data-tab') === name); });
      panels.forEach(function (p) { p.hidden = p.getAttribute('data-tab-panel') !== name; });
      try { sessionStorage.setItem(key, name); } catch (e) { }
    }
    tabs.forEach(function (t) { t.addEventListener('click', function () { show(t.getAttribute('data-tab')); }); });
    var saved = null;
    try { saved = sessionStorage.getItem(key); } catch (e) { }
    show(saved && tabs.some(function (t) { return t.getAttribute('data-tab') === saved; }) ? saved : tabs[0].getAttribute('data-tab'));
  });

  // ------------------------------------------------------------------ dependent fields:
  // data-show-if="#selector" with data-show-value="x" (show only for x) or data-hide-value="x" (hide for x)

  function refreshDependents() {
    $$('[data-show-if]').forEach(function (el) {
      var ctl = $(el.getAttribute('data-show-if'));
      if (!ctl) { return; } // no controlling field: keep the state the server rendered
      var value = ctl.type === 'checkbox' ? String(ctl.checked) : ctl.value;
      var hideValue = el.getAttribute('data-hide-value');
      var wanted = el.getAttribute('data-show-value');
      el.hidden = hideValue !== null ? value === hideValue : (wanted === null ? !value : value !== wanted);
    });
  }
  document.addEventListener('change', refreshDependents);
  refreshDependents();

  // ------------------------------------------------------------------ list toolbars: submit on change

  $$('form.js-autosubmit').forEach(function (form) {
    form.addEventListener('change', function (e) { if (e.target.matches('select, input[type=checkbox]')) { form.requestSubmit(); } });
  });

  // ------------------------------------------------------------------ loading state: progress bar + busy buttons

  var progress = document.createElement('div');
  progress.className = 'nav-progress';
  progress.setAttribute('aria-hidden', 'true');
  document.body.appendChild(progress);

  function startProgress() { progress.classList.add('is-active'); }
  function resetLoading() {
    progress.classList.remove('is-active');
    $$('.is-busy').forEach(function (b) { b.classList.remove('is-busy'); b.removeAttribute('aria-busy'); });
    $$('form').forEach(function (f) { f.__busy = false; });
  }
  // Coming back through the back/forward cache restores the page as it was: undo any "busy" state.
  window.addEventListener('pageshow', function (e) { if (e.persisted) { resetLoading(); checkConnection(); } });
  document.addEventListener('mat:navigate', startProgress);

  document.addEventListener('click', function (e) {
    var a = e.target.closest('a[href]');
    if (!a || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) { return; }
    if (a.target && a.target !== '_self') { return; }
    if (a.hasAttribute('download') || a.origin !== location.origin) { return; }
    if (a.pathname === location.pathname && a.search === location.search) { return; } // same page / in-page anchor
    startProgress();
  });

  // ------------------------------------------------------------------ confirm dialogs and form submit

  function confirmDialog(message, okText) {
    return new Promise(function (resolve) {
      var backdrop = document.createElement('div');
      backdrop.className = 'dialog-backdrop';
      backdrop.innerHTML =
        '<div class="dialog" role="alertdialog" aria-modal="true">' +
        '<h3>Are you sure?</h3><p></p>' +
        '<div class="mat-buttons"><button type="button" class="mat-btn mat-btn--ghost" data-no>Cancel</button>' +
        '<button type="button" class="mat-btn mat-btn--solid-danger" data-yes></button></div></div>';
      backdrop.querySelector('p').textContent = message;
      backdrop.querySelector('[data-yes]').textContent = okText || 'Confirm';
      function close(result) { backdrop.remove(); document.removeEventListener('keydown', onKey); resolve(result); }
      function onKey(ev) { if (ev.key === 'Escape') { close(false); } }
      backdrop.addEventListener('click', function (ev) {
        if (ev.target === backdrop || ev.target.hasAttribute('data-no')) { close(false); }
        else if (ev.target.hasAttribute('data-yes')) { close(true); }
      });
      document.addEventListener('keydown', onKey);
      document.body.appendChild(backdrop);
      backdrop.querySelector('[data-yes]').focus();
    });
  }

  document.addEventListener('submit', function (e) {
    var form = e.target;
    var submitter = e.submitter;
    // data-confirm on the form, or on the button that was pressed (one form can hold several actions)
    var message = (submitter && submitter.getAttribute('data-confirm')) || form.getAttribute('data-confirm');
    if (message && !form.__confirmed) {
      e.preventDefault();
      confirmDialog(message, (submitter && submitter.getAttribute('data-confirm-ok')) || 'Delete').then(function (ok) {
        if (!ok) { return; }
        form.__confirmed = true;
        form.requestSubmit(submitter || undefined);
      });
      return;
    }
    form.__confirmed = false;

    if (form.__busy) { e.preventDefault(); return; } // a second click while the first request is still running
    if (connection.offline) {
      // Nothing can be saved without a connection; keep the page (and what was typed) instead of showing a browser error.
      e.preventDefault();
      showBanner('offline');
      return;
    }
    form.__busy = true;
    if (submitter) { submitter.classList.add('is-busy'); submitter.setAttribute('aria-busy', 'true'); }
    startProgress();
  });

  // ------------------------------------------------------------------ connection: offline banner, reconnect, stale data

  var connection = { offline: false, timer: null };
  var main = $('main');
  var reconnectMode = (main && main.getAttribute('data-reconnect')) || 'keep'; // "reload": show fresh data once the server is back
  var offlineCopy = $('meta[name="offline-copy"]'); // the service worker served the last saved dashboard

  var banner = null;
  var bannerTimer = null;

  function hideBanner() { if (banner) { banner.remove(); banner = null; } clearTimeout(bannerTimer); }

  function showBanner(kind) {
    hideBanner();
    banner = document.createElement('div');
    banner.className = 'conn-banner conn-banner--' + kind;
    banner.setAttribute('role', kind === 'offline' ? 'alert' : 'status');
    var text;
    if (kind === 'online') {
      text = 'Back online.';
    } else if (offlineCopy) {
      var saved = new Date(offlineCopy.getAttribute('content'));
      var when = isNaN(saved) ? '' : ' (saved ' + saved.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) + ')';
      text = (navigator.onLine ? 'Matddns can’t be reached.' : 'You’re offline.') + ' Showing the last known state' + when + '.';
    } else {
      text = (navigator.onLine ? 'Matddns can’t be reached.' : 'You’re offline.') + ' Changes can’t be saved until the connection is back.';
    }
    banner.innerHTML = '<svg class="mat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="1em" height="1em" aria-hidden="true">' +
      (kind === 'offline'
        ? '<line x1="2" y1="2" x2="22" y2="22"/><path d="M8.5 16.5a5 5 0 0 1 7 0"/><path d="M2 8.82a15 15 0 0 1 4.17-2.65"/><path d="M10.66 5c4.01-.36 8.14.9 11.34 3.76"/><path d="M16.85 11.25a10 10 0 0 1 2.22 1.68"/><path d="M5 13a10 10 0 0 1 5.24-2.76"/><line x1="12" y1="20" x2="12.01" y2="20"/>'
        : '<polyline points="20 6 9 17 4 12"/>') + '</svg>' +
      '<span class="conn-banner__text"></span>' +
      (kind === 'offline' ? '<button type="button" class="mat-btn mat-btn--sm" data-retry>Retry</button>' : '');
    banner.querySelector('.conn-banner__text').textContent = text;
    document.body.appendChild(banner);
    if (kind === 'online') { bannerTimer = setTimeout(hideBanner, 3000); }
  }

  document.addEventListener('click', function (e) {
    var retry = e.target.closest('[data-retry]');
    if (!retry) { return; }
    retry.classList.add('is-busy');
    checkConnection().then(function () { retry.classList.remove('is-busy'); });
  });

  // Asks the server itself (not just the browser's online flag): a reachable network is no proof the server is up.
  function ping() {
    var controller = window.AbortController ? new AbortController() : null;
    var timeout = setTimeout(function () { if (controller) { controller.abort(); } }, 5000);
    return fetch('/healthz', { cache: 'no-store', credentials: 'same-origin', signal: controller ? controller.signal : undefined })
      .then(function (r) { return r.ok; })
      .catch(function () { return false; })
      .then(function (ok) { clearTimeout(timeout); return ok; });
  }

  function setOffline(offline) {
    if (connection.offline === offline) { if (offline && !banner) { showBanner('offline'); } return; } // no state change: don't rebuild the banner on every check
    connection.offline = offline;
    clearInterval(connection.timer);
    // check faster while the server is away, so recovery shows up quickly
    connection.timer = setInterval(checkConnection, offline ? 5000 : 30000);
    if (offline) { showBanner('offline'); return; }
    // Recovered. Read-only pages are reloaded so no stale data stays on screen; forms keep what the user typed.
    if (reconnectMode === 'reload' || offlineCopy) { showBanner('online'); location.reload(); }
    else { showBanner('online'); }
  }

  function checkConnection() {
    return ping().then(function (ok) { setOffline(!ok); });
  }

  window.addEventListener('offline', function () { setOffline(true); });
  window.addEventListener('online', checkConnection);
  document.addEventListener('visibilitychange', function () { if (!document.hidden) { checkConnection(); } });
  connection.timer = setInterval(function () { if (!document.hidden) { checkConnection(); } }, 30000);
  if (offlineCopy || !navigator.onLine) { setOffline(true); }

  // ------------------------------------------------------------------ pages that keep themselves current

  var auto = $('[data-autorefresh]');
  var every = auto ? parseInt(auto.getAttribute('data-autorefresh'), 10) : 0;
  if (every > 0) {
    var lastLoad = Date.now();
    var refresh = function () {
      if (document.hidden || connection.offline || $('dialog[open], .dialog-backdrop')) { return; }
      location.reload();
    };
    setInterval(function () { if (Date.now() - lastLoad >= every) { refresh(); } }, 5000);
    // returning to a tab / app that was in the background: don't show what was current minutes ago
    document.addEventListener('visibilitychange', function () { if (!document.hidden && Date.now() - lastLoad >= every) { refresh(); } });
  }

  // ------------------------------------------------------------------ install as an app / service worker

  var installPrompt = null;
  window.addEventListener('beforeinstallprompt', function (e) {
    e.preventDefault();
    installPrompt = e;
    $$('[data-install]').forEach(function (el) { el.hidden = false; });
  });
  window.addEventListener('appinstalled', function () {
    installPrompt = null;
    $$('[data-install]').forEach(function (el) { el.hidden = true; });
  });
  document.addEventListener('click', function (e) {
    if (!e.target.closest('[data-install-btn]') || !installPrompt) { return; }
    installPrompt.prompt();
    installPrompt.userChoice.then(function () { installPrompt = null; $$('[data-install]').forEach(function (el) { el.hidden = true; }); });
  });

  var swUrl = script && script.getAttribute('data-sw');
  if (swUrl && 'serviceWorker' in navigator && window.isSecureContext) { // service workers need HTTPS (or localhost)
    window.addEventListener('load', function () { navigator.serviceWorker.register(swUrl).catch(function () { /* the app works without it */ }); });
  }
})();
