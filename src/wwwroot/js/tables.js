// Shared data-table behaviour: live search, dropdown filters, clickable row groups.
// Markup contract:
//   <div data-dt> … toolbar with [data-dt-search] and/or <select data-dt-filter="kind"> …
//     <table class="dtable">
//       <tbody class="dt-group" data-search="…" data-kind="unifi" data-href="/Sources/Edit?id=…">
//         <tr class="dt-parent">…</tr> <tr class="dt-child">…</tr>
//       </tbody>
//       <tbody class="dt-empty" hidden><tr><td colspan="N">No matches</td></tr></tbody>
//     </table>
//   </div>
(function () {
    function initTable(wrap) {
        var table = wrap.querySelector('table.dtable');
        if (!table) return;
        var groups = Array.prototype.slice.call(table.querySelectorAll('tbody.dt-group'));
        var search = wrap.querySelector('[data-dt-search]');
        var filters = Array.prototype.slice.call(wrap.querySelectorAll('[data-dt-filter]'));
        var empty = table.querySelector('tbody.dt-empty');
        var count = wrap.querySelector('[data-dt-count]');

        function apply() {
            var q = (search && search.value || '').trim().toLowerCase();
            var visible = 0;
            groups.forEach(function (g) {
                var ok = true;
                if (q) ok = (g.getAttribute('data-search') || '').toLowerCase().indexOf(q) >= 0;
                if (ok) {
                    for (var i = 0; i < filters.length; i++) {
                        var key = filters[i].getAttribute('data-dt-filter');
                        var val = filters[i].value;
                        if (val && (g.getAttribute('data-' + key) || '') !== val) { ok = false; break; }
                    }
                }
                g.hidden = !ok;
                if (ok) visible++;
            });
            if (empty) empty.hidden = visible !== 0;
            if (count) count.textContent = visible + ' / ' + groups.length;
        }

        if (search) search.addEventListener('input', apply);
        filters.forEach(function (f) { f.addEventListener('change', apply); });

        groups.forEach(function (g) {
            var href = g.getAttribute('data-href');
            if (!href) return;
            g.classList.add('clickable');
            g.addEventListener('click', function (e) {
                if (e.target.closest('a, button, input, select, label')) return;
                window.location.href = href;
            });
        });

        apply();
    }

    function initAll() {
        Array.prototype.slice.call(document.querySelectorAll('[data-dt]')).forEach(initTable);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initAll);
    else initAll();
})();
