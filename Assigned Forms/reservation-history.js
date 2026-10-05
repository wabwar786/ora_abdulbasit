/* =========================================================
   Guest Log - search box behaviour.
   One screen: type two letters, pick a guest, and the page loads
   that guest. No other request is made, and nothing typed is ever
   turned into HTML (every value goes in through textContent).
   ========================================================= */
(function () {
    'use strict';

    var SUGGEST_URL = '/Reservation_History/Suggest';
    var DEBOUNCE_MS = 200;

    var box, wrap, suggestBox, form;
    var timer = null, seq = 0, items = [], index = -1, lastTerm = null, cache = {};

    function $(id) { return document.getElementById(id); }

    function hideSuggest() {
        if (suggestBox) { suggestBox.classList.remove('reservation-history-show'); suggestBox.innerHTML = ''; }
        items = []; index = -1;
    }

    function syncBox() {
        if (wrap && box) wrap.classList.toggle('reservation-history-has-value', (box.value || '').length > 0);
    }

    function draw(rows) {
        if (!suggestBox) return;
        items = rows || [];
        index = -1;
        suggestBox.innerHTML = '';

        if (!items.length) {
            var none = document.createElement('div');
            none.className = 'rh-suggest-empty';
            none.textContent = 'No guest matches that yet.';
            suggestBox.appendChild(none);
            suggestBox.classList.add('reservation-history-show');
            return;
        }

        for (var i = 0; i < items.length; i++) {
            var r = items[i];

            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'rh-suggest-item';
            btn.setAttribute('role', 'option');
            btn.setAttribute('data-reg', r.regId || r.RegId || '');

            var name = document.createElement('span');
            name.className = 'rh-suggest-name';
            name.textContent = r.name || r.Name || 'Unknown guest';

            var meta = document.createElement('span');
            meta.className = 'rh-suggest-meta';
            var reg = document.createElement('b');
            reg.textContent = r.regId || r.RegId || '';
            meta.appendChild(reg);

            var extra = [];
            var arrival = r.arrival || r.Arrival;
            var status = r.status || r.Status;
            if (arrival) extra.push('Arrival ' + arrival);
            if (status) extra.push(status);
            if (extra.length) meta.appendChild(document.createTextNode(' \u00b7 ' + extra.join(' \u00b7 ')));

            btn.appendChild(name);
            btn.appendChild(meta);
            suggestBox.appendChild(btn);
        }
        suggestBox.classList.add('reservation-history-show');
    }

    function ask(term) {
        if (cache[term]) { draw(cache[term]); return; }          /* same keystroke, no second request */

        var mine = ++seq;
        fetch(SUGGEST_URL + '?term=' + encodeURIComponent(term), {
            headers: { 'Accept': 'application/json' },
            credentials: 'same-origin'
        })
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (rows) {
                if (mine !== seq) return;                         /* a newer keystroke already won */
                cache[term] = rows || [];
                draw(cache[term]);
            })
            .catch(function () { if (mine === seq) hideSuggest(); });
    }

    function onType() {
        syncBox();
        var term = (box.value || '').trim();
        if (term === lastTerm) return;
        lastTerm = term;

        clearTimeout(timer);
        if (term.length < 2) { hideSuggest(); return; }
        timer = setTimeout(function () { ask(term); }, DEBOUNCE_MS);
    }

    function move(step) {
        var nodes = suggestBox.querySelectorAll('.rh-suggest-item');
        if (!nodes.length) return;
        index += step;
        if (index < 0) index = nodes.length - 1;
        if (index >= nodes.length) index = 0;
        for (var i = 0; i < nodes.length; i++) nodes[i].classList.toggle('reservation-history-is-active', i === index);
        nodes[index].scrollIntoView({ block: 'nearest' });
    }

    function pick(regId) {
        if (!regId) return;
        box.value = regId;
        hideSuggest();
        form.submit();                                            /* same GET the button does */
    }

    /* The (x) empties the box AND the screen straight away - no reload, no request.
       The address bar is put back to the plain page so a refresh stays empty. */
    function clearAll() {
        box.value = '';
        lastTerm = null;
        cache = {};
        hideSuggest();
        syncBox();
        box.focus();

        var blocks = document.querySelectorAll('.rh-panel, .rh-matches, .rh-empty, .rh-card-shell');
        for (var i = 0; i < blocks.length; i++) blocks[i].remove();

        var invoice = document.querySelector('.rh-invoice-btn');
        if (invoice && invoice.tagName === 'A') {
            var off = document.createElement('span');
            off.className = 'rh-invoice-btn is-off';
            off.setAttribute('aria-disabled', 'true');
            off.title = 'Search a guest first';
            off.innerHTML = invoice.innerHTML;
            invoice.replaceWith(off);
        }

        try {
            var plain = form.getAttribute('action') || window.location.pathname;
            window.history.replaceState(null, '', plain);
        } catch (e) { /* older browsers: the address bar just keeps the old term */ }
    }

    function init() {
        box = $('rhTerm');
        wrap = $('rhSearchWrap');
        suggestBox = $('rhSuggest');
        form = $('rhForm');
        if (!box || !form) return;

        syncBox();

        box.addEventListener('input', onType);
        box.addEventListener('focus', function () { if ((box.value || '').trim().length >= 2) onType(); });

        box.addEventListener('keydown', function (e) {
            var key = e.key || '';
            if (key === 'ArrowDown') { e.preventDefault(); move(1); return; }
            if (key === 'ArrowUp') { e.preventDefault(); move(-1); return; }
            if (key === 'Escape') { hideSuggest(); return; }
            if (key === 'Enter') {
                if (index >= 0 && items[index]) {
                    e.preventDefault();
                    pick(items[index].regId || items[index].RegId);
                }
                /* otherwise the form submits normally */
            }
        });

        suggestBox.addEventListener('click', function (e) {
            var item = e.target.closest ? e.target.closest('.rh-suggest-item') : null;
            if (item) pick(item.getAttribute('data-reg'));
        });

        var clear = $('rhClear');
        if (clear) clear.addEventListener('click', clearAll);

        document.addEventListener('click', function (e) {
            if (wrap && !wrap.contains(e.target)) hideSuggest();
        });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
/* ============ */