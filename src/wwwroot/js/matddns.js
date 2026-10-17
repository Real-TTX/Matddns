/* Matddns: the behaviour only this app has. Plain JavaScript, no build step.
   The shell, the theme, dialogs (data-confirm), forms, validation, tabs and the dependent fields (data-show-if) are the toolkit's: wwwroot/matui/matui.js.
   Every behaviour here is switched on by a data-* attribute, so several of the same control can live on one page. */
(function () {
  'use strict';

  var script = document.currentScript; // needed for data-sw; only available while the script is first executed
  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  function icon(name) {
    return '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-' + name + '"></use></svg>';
  }

  // ------------------------------------------------------------------ DynDNS provider presets: <select data-url-preset="#field"> with data-url on its options

  document.addEventListener('change', function (e) {
    var select = e.target.closest('select[data-url-preset]');
    if (!select) { return; }
    var target = $(select.getAttribute('data-url-preset'));
    var url = select.options[select.selectedIndex].getAttribute('data-url');
    if (target && url) { target.value = url; }
  });

  // ------------------------------------------------------------------ the order of the sources on the form of a new rule

  $$('[data-failover-picker]').forEach(function (picker) {
    var select = $('[data-failover-select]', picker);
    var add = $('[data-failover-add]', picker);
    var list = $('[data-failover-list]', picker);
    var items = [];

    function button(name, label, tone, onClick, disabled) {
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'mat-btn mat-btn--' + tone + ' mat-btn--icon mat-btn--sm';
      b.setAttribute('aria-label', label);
      b.setAttribute('data-tip', label);
      b.innerHTML = icon(name);
      b.disabled = !!disabled;
      b.addEventListener('click', onClick);
      return b;
    }

    function swap(i, j) { var t = items[i]; items[i] = items[j]; items[j] = t; render(); }

    function render() {
      list.innerHTML = '';
      items.forEach(function (item, i) {
        var row = document.createElement('div');
        row.className = 'md-failover__row';
        var index = document.createElement('span'); index.className = 'md-failover__index mat-mono'; index.textContent = '#' + (i + 1);
        var name = document.createElement('div'); name.className = 'md-failover__name'; name.textContent = item.label;
        var actions = document.createElement('div'); actions.className = 'md-failover__actions';
        actions.appendChild(button('arrow-up', 'Move ' + item.label + ' up', 'ghost', function () { swap(i, i - 1); }, i === 0));
        actions.appendChild(button('arrow-down', 'Move ' + item.label + ' down', 'ghost', function () { swap(i, i + 1); }, i === items.length - 1));
        actions.appendChild(button('x', 'Remove ' + item.label, 'danger', function () { items.splice(i, 1); render(); }));
        var hidden = document.createElement('input'); hidden.type = 'hidden'; hidden.name = 'SourceEntryIdsInOrder'; hidden.value = item.id;
        [index, name, actions, hidden].forEach(function (el) { row.appendChild(el); });
        list.appendChild(row);
      });
    }

    add.addEventListener('click', function () {
      var value = select.value;
      if (!value || items.some(function (x) { return x.id === value; })) { return; }
      var option = select.options[select.selectedIndex];
      items.push({ id: value, label: option.getAttribute('data-label') || option.text });
      select.value = '';
      render();
    });
  });

  // ------------------------------------------------------------------ loading state: a submitted button shows a spinner and ignores a second press

  var connection = { offline: false, timer: null };

  function resetLoading() {
    $$('.is-loading').forEach(function (b) { b.classList.remove('is-loading'); b.removeAttribute('aria-busy'); });
    $$('form').forEach(function (f) { f.__busy = false; });
  }
  // Coming back through the back/forward cache restores the page as it was: undo any "loading" state.
  window.addEventListener('pageshow', function (e) { if (e.persisted) { resetLoading(); checkConnection(); } });

  // Runs after the toolkit's own handlers (confirm, validation), so a form they stopped is not marked as loading.
  document.addEventListener('submit', function (e) {
    if (e.defaultPrevented) { return; }
    var form = e.target;
    if (form.__busy) { e.preventDefault(); return; } // a second press while the first request is still running
    if (connection.offline) {
      // Nothing can be saved without a connection; keep the page (and what was typed) instead of showing a browser error.
      e.preventDefault();
      showOffline();
      return;
    }
    form.__busy = true;
    var submitter = e.submitter;
    if (submitter) { submitter.classList.add('is-loading'); submitter.setAttribute('aria-busy', 'true'); }
  });

  // ------------------------------------------------------------------ connection: the offline notice, reconnecting and stale data

  var main = $('#main');
  var page = $('.mat-page');
  var reconnectMode = (main && main.getAttribute('data-reconnect')) || 'keep'; // "reload": show fresh data once the server is back
  var offlineCopy = $('meta[name="offline-copy"]'); // the service worker served the last saved dashboard

  function offlineText() {
    var lead = navigator.onLine ? 'Matddns can’t be reached.' : 'You’re offline.';
    if (offlineCopy) {
      var saved = new Date(offlineCopy.getAttribute('content'));
      var when = isNaN(saved) ? '' : ' (saved ' + saved.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) + ')';
      return lead + ' Showing the last known state' + when + '.';
    }
    return lead + ' Changes can’t be saved until the connection is back.';
  }

  function hideOffline() { $$('[data-offline-alert]').forEach(function (el) { el.remove(); }); }

  function showOffline() {
    if (!page) { // a page without the shell (sign-in): a toast
      if (window.MatUi) { window.MatUi.toast(offlineText(), { tone: 'warning', timeout: 8000 }); }
      return;
    }
    var alert = $('[data-offline-alert]');
    if (!alert) {
      alert = document.createElement('div');
      alert.className = 'mat-alert mat-alert--warning';
      alert.setAttribute('role', 'alert');
      alert.setAttribute('data-offline-alert', '');
      alert.innerHTML = icon('alert') + '<div class="mat-alert__body"><span class="mat-alert__title"></span>' +
        '<div class="mat-alert__actions"><button type="button" class="mat-btn mat-btn--secondary mat-btn--sm" data-retry>' + icon('refresh') + '<span>Retry</span></button></div></div>';
      page.insertBefore(alert, page.firstChild);
    }
    $('.mat-alert__title', alert).textContent = offlineText();
  }

  document.addEventListener('click', function (e) {
    var retry = e.target.closest('[data-retry]');
    if (!retry) { return; }
    retry.classList.add('is-loading');
    checkConnection().then(function () { retry.classList.remove('is-loading'); });
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
    if (connection.offline === offline) { if (offline && !$('[data-offline-alert]')) { showOffline(); } return; } // no change: don't rebuild the notice on every check
    connection.offline = offline;
    clearInterval(connection.timer);
    connection.timer = setInterval(checkConnection, offline ? 5000 : 30000); // check faster while the server is away
    if (offline) { showOffline(); return; }
    hideOffline();
    if (window.MatUi) { window.MatUi.toast('Back online.', { tone: 'success', timeout: 3000 }); }
    // Recovered. Read-only pages are reloaded so no stale data stays on screen; forms keep what the user typed.
    if (reconnectMode === 'reload' || offlineCopy) { location.reload(); }
  }

  function checkConnection() { return ping().then(function (ok) { setOffline(!ok); }); }

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
      if (document.hidden || connection.offline || $('dialog[open]')) { return; }
      location.reload();
    };
    setInterval(function () { if (Date.now() - lastLoad >= every) { refresh(); } }, 5000);
    // returning to a tab or an app that was in the background: don't show what was current minutes ago
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
