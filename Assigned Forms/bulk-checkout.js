/* =========================================================
   Bulk Check-out (MVC) - page behaviour.
   Server actions: GET /BulkCheckout (date + search), POST /BulkCheckout/CheckOut,
   POST /BulkCheckout/SettleSecurity (JSON). Select-all, filters, sort, KPIs,
   export and dialogs run in the browser.
   ========================================================= */
(function () {
    'use strict';

    function q(sel, ctx) { return (ctx || document).querySelector(sel); }
    function qa(sel, ctx) { return Array.prototype.slice.call((ctx || document).querySelectorAll(sel)); }
    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    var root = q('.bco');
    if (!root) return;
    var CUR = root.getAttribute('data-currency') || '';
    var TODAY = root.getAttribute('data-today') || '';
    function num(n) { return Number(n || 0).toLocaleString('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }
    function money(n) { return CUR + num(n); }
    function setText(sel, v) { var el = q(sel); if (el) el.textContent = v; }
    function token() { var t = q('#bcoForm input[name="__RequestVerificationToken"]'); return t ? t.value : ''; }
    function isoFromDmy(s) { var m = /^(\d{2})-(\d{2})-(\d{4})$/.exec(s || ''); return m ? m[3] + '-' + m[2] + '-' + m[1] : ''; }

    /* ---------------- one overlay + modal system ---------------- */
    var openEl = null, lastFocus = null;
    function openModal(m, focusEl) {
        if (!m) return;
        closeModal(false);
        lastFocus = document.activeElement; openEl = m;
        q('#bcoOverlay').classList.add('show'); m.classList.add('show');
        requestAnimationFrame(function () { m.classList.add('in'); });
        var f = focusEl || q('input, button', m); if (f) { try { f.focus(); } catch (e) { } }
    }
    function closeModal(restore) {
        if (!openEl) return;
        openEl.classList.remove('in'); openEl.classList.remove('show'); q('#bcoOverlay').classList.remove('show');
        openEl = null;
        if (restore !== false && lastFocus && document.body.contains(lastFocus)) { try { lastFocus.focus(); } catch (e) { } }
    }
    document.addEventListener('keydown', function (e) {
        if (!openEl) return;
        if (e.key === 'Escape') { e.preventDefault(); closeModal(); return; }
        if (e.key === 'Tab') {
            var f = qa('button:not([disabled]), input:not([type=hidden]):not([disabled]), a[href]', openEl).filter(function (x) { return x.offsetParent !== null; });
            if (!f.length) return;
            if (e.shiftKey && document.activeElement === f[0]) { e.preventDefault(); f[f.length - 1].focus(); }
            else if (!e.shiftKey && document.activeElement === f[f.length - 1]) { e.preventDefault(); f[0].focus(); }
        }
    });
    document.addEventListener('click', function (e) {
        if (e.target.closest && e.target.closest('[data-bco-close]')) { e.preventDefault(); closeModal(); return; }
        if (openEl && e.target === q('#bcoOverlay')) closeModal();
    });

    function loading(text) {
        var l = q('#bcoLoading'); if (!l) return;
        if (text === false) { l.classList.remove('show'); return; }
        setText('#bcoLoadingText', text || 'Loading...'); l.classList.add('show');
    }
    window.addEventListener('pageshow', function () { loading(false); });

    function showNotice(title, text) {
        setText('#bcoRTitle', title); setText('#bcoRSub', '');
        q('#bcoRBody').innerHTML = '<div class="bco-msg"></div>'; q('#bcoRBody .bco-msg').textContent = text;
        openModal(q('#bcoResult'));
    }

    /* ---------------- rows ---------------- */
    var view = { f: 'all', sort: '', dir: 1, q: '' };
    function allRows() { return qa('#bcoBody tr.bco-row'); }
    function cb(r) { if (r._cb === undefined) r._cb = q('input.bco-row-check', r) || null; return r._cb; }
    function d(r, k) { return r.getAttribute('data-' + k) || ''; }
    function n(r, k) { return parseFloat(d(r, k)) || 0; }
    function hay(r) {
        if (!r._hay) { var t = [d(r, 'room'), d(r, 'reg'), d(r, 'guest'), d(r, 'cat'), d(r, 'find')].join(' ').toLowerCase(); r._hay = t; r._dg = t.replace(/\D+/g, ''); }
        return r._hay;
    }
    var lastWords = [], lastTerm = null;
    function words(term) {                       // split once per keystroke, not once per row
        if (term !== lastTerm) { lastTerm = term; lastWords = term ? term.split(/\s+/).map(function (w) { return { w: w, d: w.replace(/\D+/g, '') }; }) : []; }
        return lastWords;
    }
    function matches(r, term) {
        if (!term) return true;
        var h = hay(r), list = words(term);
        for (var i = 0; i < list.length; i++) {
            var it = list[i];
            if (h.indexOf(it.w) > -1) continue;
            if (it.d.length >= 3 && r._dg.indexOf(it.d) > -1) continue;
            return false;
        }
        return true;
    }
    /* Total / Paid / Balance / Security belong to the reservation and repeat on each of its rooms: count once */
    function sumOnce(rows) {
        var seen = {}, t = { total: 0, paid: 0, rem: 0, sec: 0 };
        rows.forEach(function (r) { var k = d(r, 'reg'); if (seen[k]) return; seen[k] = 1; t.total += n(r, 'total'); t.paid += n(r, 'paid'); t.rem += n(r, 'rem'); t.sec += n(r, 'sec'); });
        return t;
    }

    function update() {
        var all = allRows(), vis = [], sel = [];
        all.forEach(function (r) { var c = cb(r), on = !!(c && c.checked); if (r.classList.contains('is-selected') !== on) r.classList.toggle('is-selected', on); if (!r.hidden) vis.push(r); if (on) sel.push(r); });
        var s = sumOnce(sel), f = sumOnce(vis);
        setText('#bcoKDep', all.length); setText('#bcoKSel', sel.length); setText('#bcoKDue', money(f.rem)); setText('#bcoKSec', money(f.sec));
        setText('#bcoFTot', money(f.total)); setText('#bcoFPaid', money(f.paid)); setText('#bcoFDue', money(f.rem)); setText('#bcoFSec', money(f.sec));
        var foot = q('#bcoFoot'); if (foot) foot.hidden = vis.length === 0;
        var ca = q('#bcoAll');
        if (ca) { var vs = vis.filter(function (r) { var c = cb(r); return c && c.checked; }).length; ca.checked = vis.length > 0 && vs === vis.length; ca.indeterminate = vs > 0 && vs < vis.length; ca.disabled = !vis.length; }
        var refChk = q('#bcoRefund'), tg = q('#bcoRefTg');
        if (refChk && tg) { var none = s.sec <= 0; if (none && refChk.checked) refChk.checked = false; refChk.disabled = none; tg.classList.toggle('off', none); tg.classList.toggle('on', refChk.checked); }
        var hiddenSel = sel.filter(function (r) { return r.hidden; }).length, k = sel.length;
        var bar = q('#bcoBar'); if (bar) bar.classList.toggle('show', k > 0);
        var info = q('#bcoBarInfo');
        if (info) info.innerHTML = '<b>' + k + ' room' + (k === 1 ? '' : 's') + '</b>' + (hiddenSel ? ' <span class="muted">(' + hiddenSel + ' not in this search)</span>' : '') +
            ' &middot; Balance due <span class="due">' + esc(money(s.rem)) + '</span> &middot; Security refund ' + (refChk && refChk.checked ? 'up to ' : '') +
            '<span class="ref">' + esc(money(refChk && refChk.checked ? s.sec : 0)) + '</span>';
        var bulk = q('#bcoBulk');
        if (bulk && !bulk.classList.contains('is-busy')) bulk.textContent = 'Check out ' + k + ' room' + (k === 1 ? '' : 's');
        var empty = q('#bcoEmpty');
        if (empty && all.length) {
            empty.classList.toggle('show', vis.length === 0);
            if (view.q) { setText('#bcoEmptyTitle', 'No departures match \u201c' + (q('[data-bco="search"]').value || '').trim() + '\u201d'); setText('#bcoEmptyHint', 'Press Enter to search the server as well (finds ID / CNIC too).'); }
            else { setText('#bcoEmptyTitle', 'No rooms match this filter'); setText('#bcoEmptyHint', 'Choose another filter above.'); }
        }
    }

    function applyView() {
        var body = q('#bcoBody'); if (!body) return;
        var rows = allRows();
        rows.forEach(function (r) {
            var keep = true, rem = n(r, 'rem'), sec = n(r, 'sec');
            if (view.f === 'due' && !(rem > 0)) keep = false;
            if (view.f === 'paid' && rem > 0) keep = false;
            if (view.f === 'sec' && !(sec > 0)) keep = false;
            if (!keep) { var c = cb(r); if (c) c.checked = false; }            // filtered-out rooms are unticked
            var h = !keep || !matches(r, view.q);                              // search only hides (ticks stay, listed in confirm)
            if (r.hidden !== h) r.hidden = h;
        });
        if (view.sort && view._s !== view.sort + view.dir) {
            view._s = view.sort + view.dir;
            rows.slice().sort(function (a, b) {
                var k = view.sort, x, y;
                if (k === 'room') { x = parseInt(d(a, 'room'), 10); y = parseInt(d(b, 'room'), 10); if (isNaN(x)) x = 1e9; if (isNaN(y)) y = 1e9; }
                else if (k === 'guest') { x = d(a, 'guest').toLowerCase(); y = d(b, 'guest').toLowerCase(); }
                else { x = n(a, k); y = n(b, k); }
                return x < y ? -view.dir : x > y ? view.dir : (+d(a, 'i')) - (+d(b, 'i'));
            }).forEach(function (r) { body.appendChild(r); });
        }
        qa('#bcoChips .bco-chip').forEach(function (c) { c.setAttribute('aria-pressed', c.getAttribute('data-f') === view.f ? 'true' : 'false'); });
        qa('.bco-grid th.sort').forEach(function (th) {
            var on = th.getAttribute('data-sort') === view.sort, a = q('.arr', th);
            if (a) a.textContent = on ? (view.dir > 0 ? '\u2191' : '\u2193') : '';
            th.setAttribute('aria-sort', on ? (view.dir > 0 ? 'ascending' : 'descending') : 'none');
        });
        update();
    }

    /* ---------------- confirm + submit ---------------- */
    var submitting = false;
    function selRows() { return allRows().filter(function (r) { var c = cb(r); return c && c.checked; }); }
    function openConfirm() {
        var sel = selRows(); if (!sel.length) return;
        var refChk = q('#bcoRefund'), refOn = !!(refChk && refChk.checked);
        var s = sumOnce(sel), early = [], dueRegs = {}, secRegs = {}, methods = [];
        var html = '<div class="bco-list">' + sel.map(function (r) {
            var dep = isoFromDmy(d(r, 'dep')), rem = n(r, 'rem'), sec = n(r, 'sec');
            if (TODAY && dep > TODAY) early.push(r);
            if (rem > 0) dueRegs[d(r, 'reg')] = 1;
            if (sec > 0) { secRegs[d(r, 'reg')] = 1; var m = d(r, 'method'); if (m && methods.indexOf(m) < 0) methods.push(m); }
            return '<div class="bco-li"><span class="rm">Room ' + esc(d(r, 'room')) + '</span><span><b>' + esc(d(r, 'guest')) + '</b><small>' + esc(d(r, 'reg')) +
                (TODAY && dep > TODAY ? ' &middot; early departure' : TODAY && dep && dep < TODAY ? ' &middot; overdue' : '') + '</small></span>' +
                (rem > 0 ? '<span class="bco-pill due">' + esc(money(rem)) + '</span>' : '<span class="bco-pill paid">Settled</span>') +
                '<span class="sec">' + (sec > 0 ? 'Security ' + esc(money(sec)) : '') + '</span></div>';
        }).join('') + '</div>';
        html += '<div class="bco-sum"><span>Security ' + (refOn ? 'to refund (up to)' : 'held (not refunded)') + (methods.length ? ' &middot; ' + esc(methods.join(' / ')) : '') + '</span><b>' + esc(money(refOn ? s.sec : 0)) + '</b></div>';
        var nd = Object.keys(dueRegs).length, ns = Object.keys(secRegs).length;
        if (nd) html += '<div class="bco-warn"><b>' + nd + ' reservation' + (nd === 1 ? ' has' : 's have') + ' a balance due (' + esc(money(s.rem)) + ').</b> The last room of a reservation is skipped while its balance is not zero.</div>';
        if (ns && !refOn) html += '<div class="bco-warn"><b>Security is held</b> for ' + ns + ' reservation' + (ns === 1 ? '' : 's') + ' and <b>Refund security</b> is off: the last room of those reservations will be skipped.</div>';
        if (ns && refOn) html += '<div class="bco-info">Security is refunded in full (amount read again from the database) only when the <b>last room</b> of a reservation checks out, and only if its balance is zero.</div>';
        if (early.length) html += '<div class="bco-info"><b>Early departure:</b> ' + early.map(function (r) { return 'Room ' + esc(d(r, 'room')); }).join(', ') + ' (departure date after today).</div>';
        setText('#bcoCTitle', 'Check out ' + sel.length + ' room' + (sel.length === 1 ? '' : 's') + '?');
        q('#bcoCBody').innerHTML = html;
        openModal(q('#bcoConfirm'), q('#bcoConfirmBtn'));
    }

    /* ---------------- result (Post / Redirect / Get) ---------------- */
    function showResult(res) {
        if (!res) return;
        if (!res.Selected) { showNotice('Nothing selected', 'Please select at least one room to check out.'); return; }
        var ok = res.CheckedOut, skipped = res.Failed;
        setText('#bcoRTitle', ok ? 'Check-out complete' : 'Nothing was checked out');
        setText('#bcoRSub', 'Every room was checked again on the server before it was updated.');

        // Plain "it worked" line first, then the numbers.
        var html = '';
        if (ok) {
            html += '<div class="bco-banner ok" role="status"><svg class="bco-ico" viewBox="0 0 24 24" aria-hidden="true">' +
                '<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/></svg><span><b>' +
                ok + ' room' + (ok === 1 ? '' : 's') + ' checked out successfully.</b>' +
                (skipped ? ' ' + skipped + ' room' + (skipped === 1 ? ' was' : 's were') + ' skipped &ndash; see the reason below.' : '') +
                '</span></div>';
        } else if (skipped) {
            html += '<div class="bco-banner warn" role="alert"><svg class="bco-ico" viewBox="0 0 24 24" aria-hidden="true">' +
                '<path d="M12 9v4"/><path d="M12 17h.01"/><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/></svg>' +
                '<span><b>No room was checked out.</b> The reason for each room is below.</span></div>';
        }
        html += '<div class="bco-rgrid"><div class="bco-rbox"><small>Selected</small><b>' + res.Selected + '</b></div>' +
            '<div class="bco-rbox"><small>Checked out</small><b class="bco-ok">' + res.CheckedOut + '</b></div>' +
            '<div class="bco-rbox"><small>Skipped</small><b class="bco-skip">' + res.Failed + '</b></div></div>';
        if (res.Rooms && res.Rooms.length) {
            html += '<div class="bco-list">' + res.Rooms.map(function (x) {
                var msg = x.Success ? (x.SecurityMessage || x.Message) : x.Message;
                return '<div class="bco-li res"><span class="rm">Room ' + esc(x.RoomNo) + '</span><span><b>' + esc(x.GuestName || x.RegId) + '</b><small>' + esc(msg) + '</small></span>' +
                    (x.Success ? '<span class="bco-ok">Done</span><span>' + (x.InvoiceUrl ? '<a class="bco-inv" target="_blank" rel="noopener noreferrer" href="' + esc(x.InvoiceUrl) + '">Invoice</a>' : '') + '</span>'
                               : '<span class="bco-skip">Skipped</span><span></span>') + '</div>';
            }).join('') + '</div>';
        } else if (res.Failed) {
            html += '<div class="bco-msg">Something went wrong. Please try again later.</div>';
        }
        q('#bcoRBody').innerHTML = html;
        openModal(q('#bcoResult'));
    }

    /* ---------------- security settlement ---------------- */
    var secRow = null;
    function secSummary() {
        if (!secRow) return;
        var hold = n(secRow, 'sec'), rel = parseFloat(q('#bcoSecRelease').value); if (isNaN(rel)) rel = 0;
        var code = (d(secRow, 'currency-code') || '').toUpperCase();
        setText('#bcoSecHold', hold.toFixed(2) + ' ' + code); setText('#bcoSecRel', Math.max(0, rel).toFixed(2) + ' ' + code); setText('#bcoSecCap', Math.max(0, hold - rel).toFixed(2) + ' ' + code);
    }
    function secError(msg) { var e = q('#bcoSecError'); if (!e) return; e.textContent = msg || ''; e.style.display = msg ? 'block' : 'none'; }
    function openSettle(row) {
        var modal = q('#bcoSecurity');
        if (modal.getAttribute('data-available') !== '1') {
            showNotice('Security settlement', 'Security settlement is not connected in this page yet. Please use the security page for now.');
            return;
        }
        secRow = row;
        setText('#bcoSecGuest', d(row, 'guest') || '-'); setText('#bcoSecReg', d(row, 'reg') || '-'); setText('#bcoSecMethod', d(row, 'method') || '-');
        q('#bcoSecRelease').value = n(row, 'sec').toFixed(2); q('#bcoSecRelease').max = n(row, 'sec').toFixed(2);
        secError(''); secSummary();
        openModal(modal, q('#bcoSecRelease'));
    }
    function settle() {
        if (!secRow) return;
        var hold = n(secRow, 'sec'), rel = parseFloat(q('#bcoSecRelease').value);
        if (isNaN(rel) || rel < 0) { secError('Release amount cannot be negative.'); return; }
        if (rel > hold + 0.00001) { secError('Release amount cannot be greater than room security.'); return; }
        var btn = q('#bcoSecOk'); btn.disabled = true; btn.textContent = 'Processing...'; secError('');
        fetch('/BulkCheckout/SettleSecurity', {
            method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json', 'X-Requested-With': 'XMLHttpRequest', 'RequestVerificationToken': token() },
            body: JSON.stringify({ regId: d(secRow, 'reg'), releaseAmount: Math.round(rel * 100) / 100 })
        })
            .then(function (r) { if (r.status === 401) { location.reload(); throw new Error('session'); } return r.json(); })
            .then(function (data) {
                btn.disabled = false; btn.textContent = 'Settle security';
                if (data && data.ok) { closeModal(false); loading('Updating list...'); location.reload(); return; }
                secError((data && data.message) || 'Security settlement failed.');
            })
            .catch(function (e) { btn.disabled = false; btn.textContent = 'Settle security'; if (e && e.message !== 'session') secError('Security settlement failed.'); });
    }

    /* ---------------- CSV export ---------------- */
    function exportCsv() {
        var head = ['Room', 'Reservation #', 'Guest', 'Category', 'Arrival', 'Departure', 'Nights Stay', 'Total', 'Paid', 'Balance', 'Security'];
        var lines = [head].concat(allRows().filter(function (r) { return !r.hidden; }).map(function (r) {
            var nts = +d(r, 'nights') || 0;
            return [d(r, 'room'), d(r, 'reg'), d(r, 'guest'), d(r, 'cat'), d(r, 'arr'), d(r, 'dep'), nts + (nts === 1 ? ' Night' : ' Nights'), d(r, 'total'), d(r, 'paid'), d(r, 'rem'), d(r, 'sec')];
        })).map(function (a) { return a.map(function (v) { v = String(v); if (/^[=+\-@]/.test(v)) v = "'" + v; return /[",\n]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v; }).join(','); });
        var blob = new Blob(['\ufeff' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' }), a = document.createElement('a');
        a.href = URL.createObjectURL(blob); a.download = 'departures.csv'; document.body.appendChild(a); a.click();
        setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 500);
    }

    /* ---------------- wire up ---------------- */
    var rows = allRows();
    rows.forEach(function (r, i) { r.setAttribute('data-i', i); });
    setText('#bcoCAll', rows.length);
    setText('#bcoCDue', rows.filter(function (r) { return n(r, 'rem') > 0; }).length);
    setText('#bcoCPaid', rows.filter(function (r) { return !(n(r, 'rem') > 0); }).length);
    setText('#bcoCSec', rows.filter(function (r) { return n(r, 'sec') > 0; }).length);

    var body = q('#bcoBody');
    if (body) {
        body.addEventListener('click', function (e) {
            var t = e.target;
            var sb = t.closest('[data-settle]'); if (sb) { e.preventDefault(); openSettle(sb.closest('tr.bco-row')); return; }
            if (t.closest('a, button, input, label, select')) return;
            var r = t.closest('tr.bco-row'); if (!r) return; var c = cb(r); if (!c) return; c.checked = !c.checked; update();
        });
        body.addEventListener('change', function (e) { if (e.target.matches('input[type=checkbox]')) update(); });
    }
    var ca = q('#bcoAll');
    if (ca) ca.addEventListener('change', function () { var on = ca.checked; allRows().forEach(function (r) { if (!r.hidden) { var c = cb(r); if (c) c.checked = on; } }); update(); });
    var chips = q('#bcoChips'); if (chips) chips.addEventListener('click', function (e) { var c = e.target.closest('.bco-chip'); if (!c) return; view.f = c.getAttribute('data-f'); applyView(); });
    qa('.bco-grid th.sort').forEach(function (th) {
        th.tabIndex = 0;
        var go = function () { var k = th.getAttribute('data-sort'); view.dir = view.sort === k ? -view.dir : 1; view.sort = k; applyView(); };
        th.addEventListener('click', go); th.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); } });
    });
    var ex = q('#bcoExport'); if (ex) ex.addEventListener('click', exportCsv);
    var clr = q('#bcoClear'); if (clr) clr.addEventListener('click', function () { allRows().forEach(function (r) { var c = cb(r); if (c) c.checked = false; }); update(); });
    var refChk = q('#bcoRefund'); if (refChk) refChk.addEventListener('change', update);

    /* date + search: GET form. Typing filters instantly; Enter / Show / date change = server. */
    var form = q('#bcoSearchForm'), s = q('[data-bco="search"]'), sc = q('#bcoSClear'), dateIn = q('#bcoDate'), st = null;
    var serverTerm = s ? (s.value || '').trim() : '';
    function runSearch() {
        var term = (s.value || '').trim(); if (sc) sc.hidden = !s.value;
        if (!term && serverTerm && form) { serverTerm = ''; loading('Loading...'); form.submit(); return; }
        view.q = term.toLowerCase() === serverTerm.toLowerCase() ? '' : term.toLowerCase(); applyView();
    }
    if (s) {
        if (sc) sc.hidden = !s.value;
        // Instant: filter on the next animation frame instead of after a timer.
        s.addEventListener('input', function () {
            if (st) cancelAnimationFrame(st);
            st = requestAnimationFrame(runSearch);
        });
        s.addEventListener('keydown', function (e) { if (e.key === 'Escape' && s.value) { e.preventDefault(); s.value = ''; runSearch(); } });
    }
    if (sc) sc.addEventListener('click', function () { s.value = ''; runSearch(); s.focus(); });
    if (dateIn && form) dateIn.addEventListener('change', function () { if (dateIn.value) { loading('Loading departures...'); form.submit(); } });
    if (form) form.addEventListener('submit', function () { loading('Loading...'); });

    var bulk = q('#bcoBulk'), cbtn = q('#bcoConfirmBtn'), postForm = q('#bcoForm');
    if (bulk) bulk.addEventListener('click', function () { if (!submitting) openConfirm(); });
    if (cbtn) cbtn.addEventListener('click', function () {
        if (submitting || !postForm) return;
        var k = selRows().length; if (!k) { closeModal(); return; }
        submitting = true;
        cbtn.disabled = true; cbtn.innerHTML = '<span class="bco-spin"></span>Checking out...';
        if (bulk) { bulk.classList.add('is-busy'); bulk.disabled = true; bulk.textContent = 'Checking out...'; }
        closeModal(false);
        loading('Checking out ' + k + ' room' + (k === 1 ? '' : 's') + '... please keep this page open');
        postForm.submit();
    });

    var rel = q('#bcoSecRelease');
    if (rel) { rel.addEventListener('input', secSummary); rel.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); settle(); } }); }
    var secOk = q('#bcoSecOk'); if (secOk) secOk.addEventListener('click', settle);

    applyView();

    var rd = q('#bcoResultData');
    if (rd) { try { showResult(JSON.parse(rd.textContent)); } catch (e) { } }
})();


//commnett
