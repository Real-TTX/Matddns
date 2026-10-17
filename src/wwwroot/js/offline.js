/* The page the service worker shows when the server cannot be reached (/offline.html): it asks again until the server answers, then loads the app. */
(function () {
  'use strict';

  var retry = document.getElementById('retry');
  var statusEl = document.getElementById('status');
  var sending = !!document.querySelector('meta[name="offline-post"]'); // a form was being sent: reloading would send it again

  if (sending) {
    document.getElementById('title').textContent = 'Not saved';
    document.getElementById('text').textContent = 'The connection was lost while sending. Go back to your form, check your entries and send it again once you’re online.';
    retry.hidden = true;
    var back = document.getElementById('back');
    back.hidden = false;
    back.addEventListener('click', function () { history.back(); });
    return;
  }

  var checking = false;
  function check() {
    if (checking) { return; }
    checking = true;
    retry.disabled = true;
    statusEl.textContent = 'Checking the connection…';
    var controller = window.AbortController ? new AbortController() : null;
    var timeout = setTimeout(function () { if (controller) { controller.abort(); } }, 5000);
    fetch('/healthz', { cache: 'no-store', signal: controller ? controller.signal : undefined })
      .then(function (r) { if (!r.ok) { throw new Error('down'); } statusEl.textContent = 'Connected. Loading…'; location.reload(); })
      .catch(function () { statusEl.textContent = 'Still not reachable. Trying again…'; })
      .then(function () { clearTimeout(timeout); checking = false; retry.disabled = false; });
  }

  retry.addEventListener('click', check);
  window.addEventListener('online', check);
  document.addEventListener('visibilitychange', function () { if (!document.hidden) { check(); } });
  setInterval(function () { if (!document.hidden) { check(); } }, 5000);
})();
