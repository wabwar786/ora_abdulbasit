/* =============================================================================
   BlockedRooms - module script
   No library, no global leak. Everything hangs off the page root element, so a
   second module on the same layout cannot collide with it.

   The server calls replace the old postbacks:
       Room category picker                   -> POST <roomsUrl>
       Current room blocks                    -> POST <listUrl>
       Bulk Room (one or many rooms)          -> POST <bulkBlockUrl>
       Edit or unblock                        -> POST <updateUrl>
   All requests carry the anti-forgery token. The hotel id is NOT sent by the browser -
   the server takes it from the session, so a hand-edited request cannot reach
   another hotel's rooms.
   ============================================================================= */
(function () {
    'use strict';

    var page = document.querySelector('.blockedrooms-page');
    if (!page) { return; }

    /* Stamped on the page root so you can tell at a glance which copy of this file the
       browser is running: F12 -> Elements -> <div class="blockedrooms-page" ...>. No
       attribute, or an older number, means it is still serving a cached script and the
       ?v= in Index.cshtml has not been raised. */
    page.setAttribute('data-blockedrooms-build', '20261119bulkroom2');

    var listUrl = page.getAttribute('data-list-url') || '';
    var roomsUrl = page.getAttribute('data-rooms-url') || '';
    var bulkBlockUrl = page.getAttribute('data-bulk-block-url') || '';
    var updateUrl = page.getAttribute('data-update-url') || '';

    var tbody = page.querySelector('.blockedrooms-tbody');

    var categorySelect = page.querySelector('.blockedrooms-cat');
    var reasonInput = page.querySelector('.blockedrooms-reason-input');
    var blockButton = page.querySelector('.blockedrooms-block');
    var bulkField = page.querySelector('.blockedrooms-field.is-bulk-picker');
    var bulkList = page.querySelector('.blockedrooms-bulk-list');
    var bulkSearch = page.querySelector('.blockedrooms-bulk-search');
    var bulkCount = page.querySelector('.blockedrooms-bulk-count');
    var bulkAll = page.querySelector('.blockedrooms-bulk-selectall');
    var bulkClear = page.querySelector('.blockedrooms-bulk-clear');
    var categoryRooms = [];
    var roomLoading = false;
    var roomCount = page.querySelector('.blockedrooms-current-count');
    var selectionSummary = page.querySelector('.blockedrooms-bulk-selection-summary');
    var selectedRooms = new Set();
    var roomLoadSerial = 0;

    var backdrop = document.querySelector('.blockedrooms-backdrop');
    var modal = document.querySelector('.blockedrooms-modal');
    var toastHost = document.querySelector('.blockedrooms-toasts');

    if (!tbody) { return; }

    var COLUMNS = 7;
    var allRows = [];
    var activeBlock = null;

    /* ---------------------------------------------------------------- icons */

    var ICON_EMPTY = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M3 9h18M8 2.5v3M16 2.5v3"/></svg>';
    var ICON_ERROR = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 8v5M12 16h.01"/></svg>';
    var ICON_EDIT = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>';

    var TOAST_ICON = {
        ok: '<path d="M20 6 9 17l-5-5"/>',
        warn: '<path d="M12 8v5M12 16h.01"/>',
        err: '<path d="M18 6 6 18M6 6l12 12"/>'
    };

    /* ---------------------------------------------------------------- toasts */
    /* success and warning clear themselves; an error stays until dismissed,
       because a conflict message usually needs reading twice */
    function setAlert(kind, text) {
        if (!toastHost) { return; }
        /* never swallow a failure: a refusal with no wording still has to say something,
           or the user reads silence as success */
        if (!text) { text = kind === 'err' ? 'Something went wrong. Please try again later.' : ''; }
        if (!text) { return; }

        /* an error stays until it is dismissed, so keep the column short enough to reach */
        var live = toastHost.querySelectorAll('.blockedrooms-toast');
        for (var old = 0; old < live.length - 3; old++) { live[old].remove(); }

        var toast = document.createElement('div');
        toast.className = 'blockedrooms-toast is-' + kind;

        var icon = document.createElement('span');
        icon.className = 'blockedrooms-toast-icon';
        icon.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" ' +
            'stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">' +
            (TOAST_ICON[kind] || TOAST_ICON.ok) + '</svg>';

        var body = document.createElement('span');
        body.className = 'blockedrooms-toast-text';
        body.textContent = text;

        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'blockedrooms-toast-close';
        close.setAttribute('aria-label', 'Dismiss');
        close.textContent = '×';
        close.addEventListener('click', function () { toast.remove(); });

        toast.appendChild(icon);
        toast.appendChild(body);
        toast.appendChild(close);
        toastHost.appendChild(toast);

        if (kind !== 'err') {
            setTimeout(function () { toast.remove(); }, 4500);
        }
    }

    /* ------------------------------------------------------- the date range */
    /* One component, created per field. The old page had ONE global calendar
       state shared by the card and the popup, so opening one moved the other's
       dates; each instance here keeps its own. */
    function DateRange(root) {
        var input = root.querySelector('.blockedrooms-daterange-input');
        var label = root.querySelector('.blockedrooms-daterange-text');
        var panel = root.querySelector('.blockedrooms-daterange-panel');
        var months = root.querySelectorAll('.blockedrooms-cal-month');
        var bodies = root.querySelectorAll('.blockedrooms-cal-body');
        var hint = root.querySelector('.blockedrooms-daterange-hint');
        var startField = root.querySelector('input[data-start]');
        var endField = root.querySelector('input[data-end]');

        var MONTHS = ['January', 'February', 'March', 'April', 'May', 'June',
                      'July', 'August', 'September', 'October', 'November', 'December'];
        var DOW = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su'];

        var view = firstOfMonth(new Date());
        var pickStart = null;
        var pickEnd = null;
        var open = false;

        function firstOfMonth(d) { return new Date(d.getFullYear(), d.getMonth(), 1); }
        function same(a, b) { return a && b && a.getTime() === b.getTime(); }
        function dayOnly(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); }

        function pad(n) { return (n < 10 ? '0' : '') + n; }

        /* dd/MM/yyyy - the format the old hidden fields posted */
        function toText(d) { return pad(d.getDate()) + '/' + pad(d.getMonth() + 1) + '/' + d.getFullYear(); }

        function parse(text) {
            var m = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(text || '');
            if (!m) { return null; }
            var d = new Date(+m[3], +m[2] - 1, +m[1]);
            return isNaN(d.getTime()) ? null : d;
        }

        function readFields() {
            pickStart = parse(startField ? startField.value : '');
            pickEnd = parse(endField ? endField.value : '');
            if (pickStart) { view = firstOfMonth(pickStart); }
            paintLabel();
        }

        function paintLabel() {
            if (pickStart && pickEnd) {
                label.textContent = toText(pickStart) + ' → ' + toText(pickEnd);
                label.classList.remove('is-empty');
            } else {
                label.textContent = 'dd/mm/yyyy → dd/mm/yyyy';
                label.classList.add('is-empty');
            }
        }

        function paint() {
            var right = new Date(view.getFullYear(), view.getMonth() + 1, 1);
            months[0].textContent = MONTHS[view.getMonth()] + ' ' + view.getFullYear();
            months[1].textContent = MONTHS[right.getMonth()] + ' ' + right.getFullYear();
            grid(bodies[0], view);
            grid(bodies[1], right);
            paintHint();
        }

        function paintHint() {
            if (!hint) { return; }
            if (pickStart && !pickEnd) {
                hint.textContent = 'Now pick the end date';
                hint.classList.add('is-waiting');
            } else if (pickStart && pickEnd) {
                var days = Math.round((pickEnd - pickStart) / 86400000) + 1;
                hint.textContent = days + (days === 1 ? ' day' : ' days');
                hint.classList.remove('is-waiting');
            } else {
                hint.textContent = 'Pick the start date';
                hint.classList.remove('is-waiting');
            }
        }

        function grid(host, month) {
            host.replaceChildren();

            for (var d = 0; d < 7; d++) {
                var head = document.createElement('span');
                head.className = 'blockedrooms-dow';
                head.textContent = DOW[d];
                host.appendChild(head);
            }

            var first = new Date(month.getFullYear(), month.getMonth(), 1);
            /* Monday-first, like the other pages */
            var lead = (first.getDay() + 6) % 7;
            var start = new Date(first.getFullYear(), first.getMonth(), 1 - lead);
            var today = dayOnly(new Date());

            for (var i = 0; i < 42; i++) {
                var cell = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i);
                var btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'blockedrooms-day';
                btn.textContent = String(cell.getDate());

                if (cell.getMonth() !== month.getMonth()) { btn.classList.add('is-muted'); }
                if (same(cell, today)) { btn.classList.add('is-today'); }

                if (pickStart && pickEnd && cell > pickStart && cell < pickEnd) { btn.classList.add('is-in-range'); }
                if (same(cell, pickStart)) { btn.classList.add('is-edge', 'is-start'); }
                if (same(cell, pickEnd)) { btn.classList.add('is-edge', 'is-end'); }

                (function (picked) {
                    btn.addEventListener('click', function () { choose(picked); });
                })(cell);

                host.appendChild(btn);
            }
        }

        function choose(d) {
            if (!pickStart || pickEnd) {
                pickStart = d;
                pickEnd = null;
            } else if (d < pickStart) {
                pickEnd = pickStart;
                pickStart = d;
            } else {
                pickEnd = d;
            }
            paint();
        }

        function apply() {
            if (!pickStart) { return; }
            if (!pickEnd) { pickEnd = pickStart; }
            if (startField) { startField.value = toText(pickStart); }
            if (endField) { endField.value = toText(pickEnd); }
            paintLabel();
            root.closest('.blockedrooms-field').classList.remove('is-invalid');
            close();
        }

        /* the modal is a scroll box, and a scroll box clips an absolutely positioned
           child - it has to stop clipping for as long as this panel is open */
        var host = root.closest('.blockedrooms-modal');

        function show() {
            readFields();
            open = true;
            panel.hidden = false;
            root.classList.add('is-open');
            if (host) { host.classList.add('is-picking'); }
            input.setAttribute('aria-expanded', 'true');
            paint();
        }

        function close() {
            open = false;
            panel.hidden = true;
            root.classList.remove('is-open');
            if (host) { host.classList.remove('is-picking'); }
            input.setAttribute('aria-expanded', 'false');
        }

        input.addEventListener('click', function () { open ? close() : show(); });

        root.querySelectorAll('.blockedrooms-nav-btn').forEach(function (b) {
            b.addEventListener('click', function () {
                view = new Date(view.getFullYear(), view.getMonth() + (+b.getAttribute('data-month')), 1);
                paint();
            });
        });

        root.querySelector('[data-range="apply"]').addEventListener('click', apply);
        root.querySelector('[data-range="cancel"]').addEventListener('click', function () {
            readFields();
            close();
        });

        /* CAPTURE, not bubble: paint() replaces the clicked day button, so by the
           time a bubbling listener runs the target is already detached and
           root.contains(target) is false - the panel would shut on every pick. */
        document.addEventListener('click', function (e) {
            if (!open) { return; }
            if (root.contains(e.target)) { return; }
            close();
        }, true);

        document.addEventListener('keydown', function (e) {
            if (open && e.key === 'Escape') { close(); }
        });

        this.reset = function () {
            if (startField) { startField.value = ''; }
            if (endField) { endField.value = ''; }
            pickStart = null;
            pickEnd = null;
            paintLabel();
        };

        this.set = function (s, e) {
            if (startField) { startField.value = s || ''; }
            if (endField) { endField.value = e || ''; }
            readFields();
        };

        this.value = function () {
            return {
                start: startField ? startField.value : '',
                end: endField ? endField.value : ''
            };
        };

        readFields();
    }

    var ranges = {};
    page.querySelectorAll('.blockedrooms-daterange').forEach(function (el) {
        ranges[el.getAttribute('data-range-name')] = new DateRange(el);
    });
    if (modal) {
        modal.querySelectorAll('.blockedrooms-daterange').forEach(function (el) {
            ranges[el.getAttribute('data-range-name')] = new DateRange(el);
        });
    }

    /* --------------------------------------------------------- rendering rows */

    function cell(tr, label, className, node) {
        var td = document.createElement('td');
        if (className) { td.className = className; }
        td.setAttribute('data-label', label);
        if (node instanceof Node) { td.appendChild(node); }
        else { td.textContent = node == null ? '' : String(node); }
        tr.appendChild(td);
        return td;
    }

    function pill(className, text) {
        var span = document.createElement('span');
        span.className = className;
        span.textContent = text;
        return span;
    }

    /* ASP.NET Core writes camelCase by default, but a master that sets
       JsonSerializerOptions.PropertyNamingPolicy = null writes PascalCase instead. Reading
       both here means this module does not depend on a setting in Program.cs, which it is
       not allowed to change. */
    function field(row, name) {
        var upper = name.charAt(0).toUpperCase() + name.slice(1);
        var value = row[name];
        if (value === undefined || value === null) { value = row[upper]; }
        return value === undefined || value === null ? '' : value;
    }

    function tidy(row) {
        return {
            blockId: field(row, 'blockId'),
            roomNo: String(field(row, 'roomNo')),
            category: String(field(row, 'category')),
            categoryId: String(field(row, 'categoryId')),
            startText: String(field(row, 'startText')),
            endText: String(field(row, 'endText')),
            reason: String(field(row, 'reason'))
        };
    }

    function buildRow(row, index) {
        var tr = document.createElement('tr');
        tr.className = 'blockedrooms-row';

        cell(tr, 'Sr #', 'blockedrooms-sr', index);
        cell(tr, 'Room No.', 'blockedrooms-roomno', row.roomNo || '-');
        cell(tr, 'Room Category', '', row.category || '-');
        cell(tr, 'Block From', 'blockedrooms-date', row.startText || '-');
        cell(tr, 'Block To', 'blockedrooms-date', row.endText || '-');

        /* the truncation sits on an inner span: max-width on a <td> is ignored
           while the table lays out automatically, so the ellipsis never appears */
        var reason = document.createElement('span');
        reason.className = 'blockedrooms-reason-text';
        reason.textContent = row.reason || '-';
        reason.title = row.reason || '';
        cell(tr, 'Reason', 'blockedrooms-reason', reason);

        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'blockedrooms-iconbtn';
        button.setAttribute('data-block', row.blockId);
        button.setAttribute('aria-label', 'Edit or unblock room ' + (row.roomNo || ''));
        button.innerHTML = ICON_EDIT;
        button.addEventListener('click', function () { openModal(row, button); });
        cell(tr, 'Action', 'blockedrooms-action', button);

        return tr;
    }

    function stateRow(kind, icon, title, note) {
        var tr = document.createElement('tr');
        tr.className = 'blockedrooms-state' + (kind ? ' ' + kind : '');
        var td = document.createElement('td');
        td.colSpan = COLUMNS;

        var badge = document.createElement('div');
        badge.className = 'blockedrooms-state-icon';
        badge.innerHTML = icon;
        td.appendChild(badge);

        var strong = document.createElement('span');
        strong.className = 'blockedrooms-state-title';
        strong.textContent = title;
        td.appendChild(strong);

        if (note) { td.appendChild(document.createTextNode(note)); }

        tr.appendChild(td);
        return tr;
    }

    function skeleton() {
        var frag = document.createDocumentFragment();
        for (var r = 0; r < 4; r++) {
            var tr = document.createElement('tr');
            for (var c = 0; c < COLUMNS; c++) {
                var td = document.createElement('td');
                var bar = document.createElement('div');
                bar.className = 'blockedrooms-skeleton';
                bar.style.width = [40, 55, 80, 70, 70, 90, 34][c] + '%';
                td.appendChild(bar);
                tr.appendChild(td);
            }
            frag.appendChild(tr);
        }
        tbody.replaceChildren(frag);
    }

    var highlightRoom = '';

    /* ------------------------------------------------ search current room blocks */
    var searchBox = page.querySelector('#blockedroomsSearch');
    var searchWrap = page.querySelector('#blockedroomsSearchWrap');
    var searchClear = page.querySelector('#blockedroomsSearchClear');

    function term() { return searchBox ? searchBox.value.trim().toLowerCase() : ''; }

    function searched() {
        var t = term();
        if (!t) { return allRows.slice(); }
        return allRows.filter(function (r) {
            return [r.roomNo, r.category, r.reason, r.startText, r.endText]
                .join(' ').toLowerCase().indexOf(t) > -1;
        });
    }

    if (searchBox) {
        searchBox.addEventListener('input', function () {
            if (searchWrap) { searchWrap.classList.toggle('has-value', searchBox.value.length > 0); }
            render(searched());
        });
    }
    if (searchClear) {
        searchClear.addEventListener('click', function () {
            searchBox.value = '';
            if (searchWrap) { searchWrap.classList.remove('has-value'); }
            render(searched());
            searchBox.focus();
        });
    }

    function render(rows) {
        if (roomCount) {
            roomCount.textContent = allRows.length + (allRows.length === 1 ? ' active block' : ' active blocks');
        }
        if (!rows.length && term()) {
            tbody.replaceChildren(stateRow('', ICON_EMPTY, 'No blocked room matches your search.',
                                           'Try a different room number, category or reason.'));
        } else if (!rows.length) {
            tbody.replaceChildren(stateRow('', ICON_EMPTY, 'No rooms are blocked right now.',
                                           'Use Bulk Room above to block one room or multiple rooms.'));
        } else {
            var frag = document.createDocumentFragment();
            var target = null;
            for (var i = 0; i < rows.length; i++) {
                var tr = buildRow(rows[i], i + 1);
                if (highlightRoom && rows[i].roomNo === highlightRoom && !target) {
                    tr.classList.add('is-new');
                    target = tr;
                }
                frag.appendChild(tr);
            }
            tbody.replaceChildren(frag);

            /* the grid can be long enough to push a new block out of sight, and a row the
               user cannot see reads as a row that was never added */
            if (target && target.scrollIntoView) {
                target.scrollIntoView({ block: 'nearest' });
            }
            highlightRoom = '';
        }

    }

    /* ------------------------------------------------------------- the modal */

    var modalOpener = null;

    function openModal(row, opener) {
        if (!modal) { return; }
        activeBlock = row;
        modalOpener = opener || null;

        modal.querySelector('.blockedrooms-modal-cat').value = row.category || '';
        modal.querySelector('.blockedrooms-modal-room').value = row.roomNo || '';
        modal.querySelector('.blockedrooms-modal-status').value = 'Active';
        modal.querySelector('.blockedrooms-modal-reason').value = row.reason || '';
        ranges.edit.set(row.startText, row.endText);

        paintModalMode();
        backdrop.hidden = false;
        modal.hidden = false;
        modal.querySelector('.blockedrooms-modal-status').focus();
    }

    function closeModal() {
        if (!modal) { return; }
        modal.hidden = true;
        modal.classList.remove('is-picking');
        backdrop.hidden = true;
        activeBlock = null;

        /* back to the pencil that opened it, not to the top of the document */
        if (modalOpener && document.body.contains(modalOpener)) { modalOpener.focus(); }
        modalOpener = null;
    }

    /* Inactive means "unblock" - the dates and the reason no longer apply, so
       they are disabled rather than left looking editable. The old page hid the
       whole section instead. */
    function paintModalMode() {
        var inactive = modal.querySelector('.blockedrooms-modal-status').value === 'Inactive';
        modal.querySelector('.blockedrooms-modal-daterange')
             .classList.toggle('is-disabled', inactive);
        modal.querySelector('.blockedrooms-modal-daterange .blockedrooms-daterange-input').disabled = inactive;
        modal.querySelector('.blockedrooms-apply').textContent = inactive ? 'Unblock Room' : 'Apply';
    }

    if (modal) {
        modal.querySelector('.blockedrooms-modal-status').addEventListener('change', paintModalMode);
        modal.querySelector('.blockedrooms-modal-close').addEventListener('click', closeModal);
        modal.querySelector('.blockedrooms-cancel').addEventListener('click', closeModal);
        backdrop.addEventListener('click', closeModal);
        /* CAPTURE, not bubble. The date pickers are built first, so their own document
           keydown listener runs before this one would in the bubbling phase - it would
           already have cleared .is-open, and this handler would then close the dialog on
           the very same keypress. In the capture phase this runs first and can see that
           the calendar is still open. */
        document.addEventListener('keydown', function (e) {
            if (modal.hidden || e.key !== 'Escape') { return; }
            if (modal.querySelector('.blockedrooms-daterange.is-open')) { return; }
            closeModal();
        }, true);

        /* aria-modal only promises the dialog is modal - Tab still has to be kept inside
           it, and focus has to go back to the button that opened it */
        modal.addEventListener('keydown', function (e) {
            if (e.key !== 'Tab') { return; }

            var focusable = modal.querySelectorAll(
                'button:not([disabled]), select:not([disabled]), input:not([disabled]):not([type=hidden]), textarea:not([disabled])');
            var usable = [];
            for (var i = 0; i < focusable.length; i++) {
                if (focusable[i].offsetParent !== null) { usable.push(focusable[i]); }
            }
            if (!usable.length) { return; }

            var first = usable[0];
            var last = usable[usable.length - 1];

            if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
            else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        });

        modal.querySelector('.blockedrooms-apply').addEventListener('click', function () {
            var status = modal.querySelector('.blockedrooms-modal-status').value;
            var dates = ranges.edit.value();

            if (status === 'Active' && (!dates.start || !dates.end)) {
                setAlert('warn', 'Invalid edited date range.');
                return;
            }

            var button = this;
            busy(button, true);

            server.update({
                blockId: activeBlock.blockId,
                status: status,
                category: activeBlock.category,
                categoryId: activeBlock.categoryId,
                roomNo: activeBlock.roomNo,
                start: dates.start,
                end: dates.end
            }).then(function (res) {
                /* ok:false is the room-block service's own refusal, conflict line and
                   all - the same text the old alert() showed */
                if (!(res.ok === undefined ? res.Ok : res.ok)) { setAlert('err', res.message || res.Message); return; }

                closeModal();
                /* the grid comes back from the server, so the order and the
                   "expired blocks are hidden" rule stay the server's, not ours */
                if (Array.isArray(res.rows || res.Rows)) {
                    allRows = (res.rows || res.Rows).map(tidy);
                    render(searched());
                } else { reload(); }
                setAlert(res.availabilityQueued === false ? 'warn' : 'ok', res.message || res.Message);
            }).catch(function (err) {
                setAlert('err', err.message);
            }).then(function () { busy(button, false); });
        });
    }

    /* ---------------------------------------------------------- block a room */

    function busy(button, on) {
        if (!button) { return; }
        if (on) {
            button.disabled = true;
            button.setAttribute('data-busy', 'true');
            button.setAttribute('data-label', button.textContent);
            button.replaceChildren();
            var s = document.createElement('span');
            s.className = 'blockedrooms-spinner';
            button.appendChild(s);
            button.appendChild(document.createTextNode('Please wait…'));
        } else {
            button.disabled = false;
            button.removeAttribute('data-busy');
            button.textContent = button.getAttribute('data-label') || 'Apply';
            if (button === blockButton) { showBulkCount(); }
        }
    }

    function markInvalid(el, bad) {
        var field = el.closest('.blockedrooms-field');
        if (field) { field.classList.toggle('is-invalid', bad); }
    }

    function selectedRoomNumbers() { return Array.from(selectedRooms); }

    function showBulkCount() {
        if (bulkCount) {
            bulkCount.textContent = selectedRooms.size + ' of ' + categoryRooms.length + ' selected';
        }
        if (bulkAll) { bulkAll.disabled = roomLoading || !categoryRooms.length; }
        if (bulkClear) { bulkClear.disabled = !selectedRooms.size; }
        if (selectionSummary) {
            if (roomLoading) {
                selectionSummary.textContent = 'Loading rooms...';
            } else if (!categorySelect.value) {
                selectionSummary.textContent = 'Select a category to get started.';
            } else if (!categoryRooms.length) {
                selectionSummary.textContent = 'No rooms found in this category.';
            } else if (selectedRooms.size === 0) {
                selectionSummary.textContent = 'Select at least one room to continue.';
            } else {
                selectionSummary.textContent = selectedRooms.size === 1
                    ? '1 room selected — ready to block.'
                    : selectedRooms.size + ' rooms selected — all will share the same dates and reason.';
            }
        }
        if (blockButton && blockButton.getAttribute('data-busy') !== 'true') {
            blockButton.textContent = selectedRooms.size === 1 ? 'Block 1 Room' :
                selectedRooms.size > 1 ? 'Block ' + selectedRooms.size + ' Rooms' : 'Block Selected Rooms';
        }
    }

    function paintBulkRooms() {
        if (!bulkList) { return; }
        bulkList.replaceChildren();
        var search = bulkSearch ? bulkSearch.value.trim().toLowerCase() : '';
        var visible = categoryRooms.filter(function (name) {
            return name.toLowerCase().indexOf(search) !== -1;
        });
        if (roomLoading || !visible.length) {
            var empty = document.createElement('span');
            empty.className = 'blockedrooms-bulk-placeholder';
            empty.textContent = roomLoading ? 'Loading rooms...' :
                categoryRooms.length ? 'No matching rooms. Try another number.' :
                categorySelect.value ? 'No rooms found in this category.' :
                'Select a category above to view rooms.';
            bulkList.appendChild(empty);
        }
        if (!roomLoading) {
            visible.forEach(function (room) {
                var label = document.createElement('label');
                label.className = 'blockedrooms-bulk-room';
                var checkbox = document.createElement('input');
                checkbox.type = 'checkbox';
                checkbox.value = room;
                checkbox.checked = selectedRooms.has(room);
                checkbox.addEventListener('change', function () {
                    if (this.checked) {
                        if (selectedRooms.size >= 200) {
                            this.checked = false;
                            setAlert('warn', 'You can block up to 200 rooms at once.');
                            return;
                        }
                        selectedRooms.add(room);
                    } else { selectedRooms.delete(room); }
                    label.classList.toggle('is-checked', this.checked);
                    bulkField.classList.remove('is-invalid');
                    showBulkCount();
                });
                var name = document.createElement('span');
                name.textContent = room;
                label.appendChild(checkbox);
                label.appendChild(name);
                label.classList.toggle('is-checked', checkbox.checked);
                bulkList.appendChild(label);
            });
        }
        showBulkCount();
    }

    function resetCategoryRooms() {
        categoryRooms = [];
        selectedRooms.clear();
        roomLoading = false;
        if (bulkSearch) { bulkSearch.value = ''; }
        if (bulkField) { bulkField.classList.remove('is-invalid'); }
        paintBulkRooms();
    }

    if (bulkSearch) { bulkSearch.addEventListener('input', paintBulkRooms); }
    if (bulkAll) {
        bulkAll.addEventListener('click', function () {
            if (!categoryRooms.length) { return; }
            // Select all in this category; the search box only filters what is displayed.
            if (categoryRooms.length > 200) {
                setAlert('warn', 'This category has more than 200 rooms. Select a smaller batch.');
                return;
            }
            categoryRooms.forEach(function (room) { selectedRooms.add(room); });
            paintBulkRooms();
        });
    }
    if (bulkClear) {
        bulkClear.addEventListener('click', function () {
            selectedRooms.clear();
            paintBulkRooms();
        });
    }

    if (categorySelect) {
        categorySelect.addEventListener('change', function () {
            markInvalid(categorySelect, false);
            var serial = ++roomLoadSerial;
            resetCategoryRooms();
            if (!categorySelect.value) { return; }
            roomLoading = true;
            paintBulkRooms();
            server.rooms(categorySelect.value).then(function (rooms) {
                if (serial !== roomLoadSerial) { return; } // ignore late response to prior category
                categoryRooms = Array.from(new Set(rooms.map(String)));
                roomLoading = false;
                paintBulkRooms();
            }).catch(function (err) {
                if (serial !== roomLoadSerial) { return; }
                resetCategoryRooms();
                setAlert('err', err.message);
            });
        });
    }

    if (blockButton) {
        blockButton.addEventListener('click', function () {
            var dates = ranges.block.value();
            var roomNos = selectedRoomNumbers();
            var bad = false;
            [[categorySelect, !categorySelect.value],
             [reasonInput, !reasonInput.value.trim()]].forEach(function (pair) {
                markInvalid(pair[0], pair[1]);
                if (pair[1]) { bad = true; }
            });
            var missingRooms = !roomNos.length;
            bulkField.classList.toggle('is-invalid', missingRooms);
            if (missingRooms) { bad = true; }
            var rangeField = page.querySelector('.blockedrooms-field.is-range');
            var noDates = !dates.start || !dates.end;
            rangeField.classList.toggle('is-invalid', noDates);
            if (noDates) { bad = true; }
            if (bad) {
                setAlert('warn', 'Please select a category, one or more rooms, date range and reason.');
                return;
            }
            if (roomNos.length > 1 && !window.confirm('Block ' + roomNos.length + ' rooms from ' +
                    dates.start + ' to ' + dates.end + '? If any room conflicts, none will be blocked.')) {
                return;
            }

            var button = this;
            busy(button, true);
            var request = {
                categoryId: categorySelect.value,
                roomNos: roomNos,
                start: dates.start,
                end: dates.end,
                reason: reasonInput.value.trim()
            };
            server.bulkBlock(request).then(function (res) {
                if (!(res.ok === undefined ? res.Ok : res.ok)) {
                    setAlert('err', res.message || res.Message);
                    return;
                }
                highlightRoom = roomNos.length === 1 ? roomNos[0] : '';
                categorySelect.selectedIndex = 0;
                ++roomLoadSerial;
                resetCategoryRooms();
                reasonInput.value = '';
                markInvalid(reasonInput, false);
                ranges.block.reset();
                rangeField.classList.remove('is-invalid');
                if (Array.isArray(res.rows || res.Rows)) {
                    allRows = (res.rows || res.Rows).map(tidy);
                    render(searched());
                } else { reload(); }
                setAlert(res.availabilityQueued === false ? 'warn' : 'ok', res.message || res.Message);
            }).catch(function (err) {
                setAlert('err', err.message);
            }).then(function () { busy(button, false); });
        });
    }

    /* ------------------------------------------------- show/hide Bulk Room */
    var panel = page.querySelector('#blockedroomsPanel');
    var openBulk = page.querySelector('#blockedroomsOpenBulk');
    var closeBulk = page.querySelector('#blockedroomsPanelClose');
    function toggleBulkPanel(open) {
        if (!panel || !openBulk) { return; }
        panel.hidden = !open;
        openBulk.setAttribute('aria-expanded', open ? 'true' : 'false');
        openBulk.classList.toggle('is-open', open);
        if (open && categorySelect) { categorySelect.focus(); }
        else if (!open) { openBulk.focus(); }
    }
    if (openBulk && panel) {
        openBulk.addEventListener('click', function () { toggleBulkPanel(panel.hidden); });
    }
    if (closeBulk) {
        closeBulk.addEventListener('click', function () { toggleBulkPanel(false); });
    }
    showBulkCount();

    /* =============================== the server =============================== */

    function token() {
        var field = page.querySelector('input[name="__RequestVerificationToken"]');
        return field ? field.value : '';
    }

    /* One shape for all four calls. A non-2xx answer still carries the server's own
       wording, so it is read out of the body rather than replaced with a generic line.

       The call is also given a deadline. Without one a request that never answers leaves
       the button spinning for ever and the user reads that as "nothing happened". */
    var DEADLINE = 45000;

    function post(url, body) {
        var stop = typeof AbortController === 'function' ? new AbortController() : null;
        var timer = stop ? setTimeout(function () { stop.abort(); }, DEADLINE) : null;

        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            signal: stop ? stop.signal : undefined,
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token(),
                'X-Requested-With': 'XMLHttpRequest'
            },
            body: JSON.stringify(body || {})
        }).catch(function (err) {
            if (err && err.name === 'AbortError') {
                throw new Error('The server did not answer in time. Refresh the page and check '
                    + 'whether the change went through before trying again.');
            }
            throw err;
        }).then(function (res) {
            return res.json().catch(function () { return {}; }).then(function (data) {
                if (!res.ok) {
                    var error = new Error((data && data.message) || 'Something went wrong. Please try again later.');
                    error.status = res.status;
                    throw error;
                }
                return data;
            });
        }).then(function (value) {
            if (timer) { clearTimeout(timer); }
            return value;
        }, function (err) {
            if (timer) { clearTimeout(timer); }
            throw err;
        });
    }

    var server = {
        rooms: function (categoryId) {
            return post(roomsUrl, { categoryId: categoryId })
                .then(function (d) { return (d && (d.rooms || d.Rooms)) || []; });
        },
        list: function (categoryId) {
            return post(listUrl, { categoryId: categoryId || null })
                .then(function (d) { return (((d && (d.rows || d.Rows)) || [])).map(tidy); });
        },
        bulkBlock: function (req) { return post(bulkBlockUrl, req); },
        update: function (req) { return post(updateUrl, req); }
    };

    /* ------------------------------------------------------------ first load */
    function reload() {
        skeleton();
        return server.list('').then(function (rows) {
            allRows = rows;
            render(searched());
        }).catch(function (err) {
            allRows = [];
            if (roomCount) { roomCount.textContent = 'Unable to load blocks'; }
            tbody.replaceChildren(stateRow('is-err', ICON_ERROR, 'Could not load the blocked rooms.',
                (err && err.message) ? err.message : 'Please try again.'));
        });
    }

    reload();
})();
