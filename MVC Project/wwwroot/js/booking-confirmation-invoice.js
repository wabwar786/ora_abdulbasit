/* Booking Confirmation Invoice. Everything is scoped to the page root; nothing
   global is written and no library is loaded. */
(function () {
    'use strict';

    var page = document.querySelector('.bcinvoice-page');
    if (!page) { return; }

    function $(id) { return document.getElementById(id); }

    var sheet = $('bcinvoiceSheet');
    if (!sheet) { return; }              /* only the error card rendered */

    /* ================= the three views ================= */
    /* The buttons only change what the sheet shows and what the title reads -
       exactly what the old page did, with the same totals recomputed. */
    /* The sheet is printed by the server with a dot as the decimal mark and no grouping.
       toLocaleString would follow the reader's browser instead, so a German browser showed
       1.234,56 in the popup next to 1234.56 on the sheet. This keeps the two the same. */
    function money(value) {
        var symbol = sheet.getAttribute('data-symbol') || '';
        var n = Number(value);
        if (!isFinite(n)) { n = 0; }

        /* the server prints these amounts with "0.##" - up to two decimals, trailing
           zeros dropped - so the totals this script works out read the same way */
        var text = Math.abs(n).toFixed(2).replace(/\.?0+$/, '');
        if (text === '') { text = '0'; }
        return (n < 0 ? '- ' : '') + (symbol ? symbol + ' ' : '') + text;
    }

    function amount(text) {
        var n = parseFloat(String(text || '0').replace(/[^0-9.-]/g, ''));
        return isNaN(n) ? 0 : n;
    }

    function totals(selector) {
        var out = { net: 0, vat: 0, bed: 0, gross: 0 };
        var rows = sheet.querySelectorAll(selector);
        for (var i = 0; i < rows.length; i++) {
            out.net += amount(rows[i].getAttribute('data-net'));
            out.vat += amount(rows[i].getAttribute('data-vat'));
            out.bed += amount(rows[i].getAttribute('data-bed'));
            out.gross += amount(rows[i].getAttribute('data-gross'));
        }
        return out;
    }

    function setText(id, value) {
        var el = $(id);
        if (el) { el.textContent = value; }
    }

    function recalc() {
        var room = totals('.bcinvoice-room-row');
        var extras = totals('.bcinvoice-extras-row');

        setText('bcinvoiceRoomNet', money(room.net));
        setText('bcinvoiceRoomVat', money(room.vat));
        setText('bcinvoiceRoomBed', money(room.bed));
        setText('bcinvoiceRoomGross', money(room.gross));

        setText('bcinvoiceExtrasNet', money(extras.net));
        setText('bcinvoiceExtrasTax', money(extras.vat + extras.bed));
        setText('bcinvoiceExtrasGross', money(extras.gross));
    }

    function setView(view) {
        sheet.setAttribute('data-view', view);

        var buttons = page.querySelectorAll('.bcinvoice-views button');
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].classList.toggle('is-on', buttons[i].getAttribute('data-view') === view);
            buttons[i].setAttribute('aria-pressed', buttons[i].getAttribute('data-view') === view ? 'true' : 'false');
        }

        var title = $('bcinvoiceDocTitle');
        if (title) {
            if (!title.getAttribute('data-original')) { title.setAttribute('data-original', title.textContent); }
            if (view === 'room') { title.textContent = 'ROOM RENT REPORT'; }
            else if (view === 'extras') { title.textContent = 'EXTRAS REPORT'; }
            else { title.textContent = title.getAttribute('data-original') || 'INVOICE'; }
        }

        recalc();
    }

    var viewButtons = page.querySelectorAll('.bcinvoice-views button');
    for (var v = 0; v < viewButtons.length; v++) {
        (function (button) {
            button.onclick = function () { setView(button.getAttribute('data-view')); };
        })(viewButtons[v]);
    }

    /* the "View / Print" link on an Extras group header. There is one per extras group,
       not one per invoice, so this is a class and not an id. */
    var extrasLinks = page.querySelectorAll('.bcinvoice-extras-link');
    for (var x = 0; x < extrasLinks.length; x++) {
        extrasLinks[x].onclick = function (e) {
            e.preventDefault();
            setView('extras');
            window.scrollTo(0, 0);
        };
    }

    /* ================= the logo ================= */
    /* The hotel's stored logo path can be empty, or point at a file that is not there any
       more. Rather than leave a broken image on an invoice, fall back once to the one that
       ships with the page, and if that is missing too, take the image out altogether. */
    var logo = page.querySelector('.bcinvoice-logo');
    if (logo) {
        var logoSwap = function () {
            var fallback = logo.getAttribute('data-fallback') || '';
            if (fallback && logo.getAttribute('src') !== fallback) {
                logo.setAttribute('src', fallback);
                return;
            }
            logo.style.display = 'none';
        };
        logo.addEventListener('error', logoSwap);
        /* The image starts loading while the page is still being parsed, so when the
           stored path is a dead one its error event has already fired by the time this
           script runs and the listener above would never see it - the invoice was left
           showing a broken icon. Check the image's own state as well. */
        if (logo.complete && logo.naturalWidth === 0) { logoSwap(); }
    }

    var printButton = $('bcinvoicePrint');
    if (printButton) { printButton.onclick = function () { window.print(); }; }

    /* ================= the receipt, shown on this page ================= */
    /* The icon in a log row opens that payment's receipt here, built from the row
       itself so it always matches the invoice. The original receipt the payment
       was saved with is one click away inside it. Everything is written with
       textContent - no HTML ever comes from the data. */
    var lastReceiptButton = null;

    function openReceipt(button) {
        var row = button.closest ? button.closest('tr') : null;
        var modal = $('bcinvoiceReceiptModal');
        var overlay = $('bcinvoiceReceiptOverlay');
        if (!row || !modal || !overlay) { return; }

        var hotel = sheet.getAttribute('data-hotel') || '';
        var booking = sheet.getAttribute('data-reg') || '';
        var guest = sheet.getAttribute('data-guest') || '';
        var room = sheet.getAttribute('data-room') || '';
        var stay = sheet.getAttribute('data-stay') || '';
        var taxName = sheet.getAttribute('data-tax-name') || 'VAT';

        var kind = row.getAttribute('data-kind') || 'payment';
        var paid = amount(row.getAttribute('data-amount'));
        var net = amount(row.getAttribute('data-net'));
        var tax = amount(row.getAttribute('data-tax'));
        var date = row.getAttribute('data-date') || '';
        var method = row.getAttribute('data-method') || '';
        var by = row.getAttribute('data-by') || '';

        setText('bcinvoiceReceiptTitle', hotel ? 'Receipt from ' + hotel : 'Payment receipt');
        setText('bcinvoiceReceiptNo', booking ? 'Booking #' + booking : '');
        setText('bcinvoiceReceiptAmount', money(paid));
        setText('bcinvoiceReceiptDate', date || '—');
        setText('bcinvoiceReceiptMethod', method || '—');

        setText('bcinvoiceReceiptLine',
            kind === 'security'
                ? 'Room security — Reservation #' + booking + (guest ? ' — ' + guest : '')
                : 'Reservation #' + booking + (guest ? ' — ' + guest : ''));
        setText('bcinvoiceReceiptStay', (room ? 'Room ' + room + ' · ' : '') + stay);
        setText('bcinvoiceReceiptLineAmount', money(paid));
        setText('bcinvoiceReceiptNet', money(net));
        setText('bcinvoiceReceiptTaxName', taxName);
        setText('bcinvoiceReceiptTax', money(tax));
        setText('bcinvoiceReceiptTotal', money(paid));

        var byLine = $('bcinvoiceReceiptBy');
        if (byLine) {
            byLine.hidden = !by || by === '—';
            byLine.textContent = byLine.hidden ? '' : 'Received by ' + by;
        }

        /* the saved receipt (checked on the server: http/https or a local path only) */
        var original = $('bcinvoiceReceiptOriginal');
        var url = row.getAttribute('data-receipt') || '';
        if (original) {
            original.hidden = !url;
            if (url) { original.setAttribute('href', url); }
            else { original.removeAttribute('href'); }
        }

        if (overlay.parentNode !== document.body) { document.body.appendChild(overlay); }
        if (modal.parentNode !== document.body) { document.body.appendChild(modal); }

        lastReceiptButton = button;
        overlay.hidden = false;
        modal.hidden = false;
        document.body.classList.add('bcinvoice-receipt-open');

        var close = $('bcinvoiceReceiptClose');
        if (close) { setTimeout(function () { close.focus(); }, 30); }
    }

    function closeReceipt() {
        var modal = $('bcinvoiceReceiptModal');
        if (!modal || modal.hidden) { return; }
        modal.hidden = true;
        var overlay = $('bcinvoiceReceiptOverlay');
        if (overlay) { overlay.hidden = true; }
        document.body.classList.remove('bcinvoice-receipt-open');
        if (lastReceiptButton && document.contains(lastReceiptButton)) {
            try { lastReceiptButton.focus({ preventScroll: true }); } catch (e) { lastReceiptButton.focus(); }
        }
    }

    document.addEventListener('click', function (e) {
        var button = e.target.closest ? e.target.closest('.bcinvoice-receipt-btn') : null;
        if (button) { e.preventDefault(); openReceipt(button); return; }
        if (e.target === $('bcinvoiceReceiptOverlay')) { closeReceipt(); }
    });

    var closeButton = $('bcinvoiceReceiptClose');
    if (closeButton) { closeButton.onclick = closeReceipt; }

    var printReceipt = $('bcinvoiceReceiptPrint');
    if (printReceipt) { printReceipt.onclick = function () { window.print(); }; }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') { closeReceipt(); }
    });

    setView('full');
})();
