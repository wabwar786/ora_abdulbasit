(function () {
    'use strict';

    const app = document.getElementById('calendarApp');
    if (!app) return;
    document.body.classList.add('calendar-page');

    const config = window.frontDeskCalendarConfig || {};
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const contextMenu = document.getElementById('calendarContextMenu');
    const drawer = document.getElementById('calendarDrawer');
    const modalOverlay = document.getElementById('calendarModalOverlay');
    const dateOverlay = document.getElementById('calendarDateOverlay');
    const modalTitle = document.getElementById('calendarModalTitle');
    const modalBody = document.getElementById('calendarModalBody');
    const modalActions = document.getElementById('calendarModalActions');
    const loader = document.getElementById('calendarLoader');
    const toastEl = document.getElementById('calendarToast');
    let activeCell = null;
    let draggedCell = null;
    let resizeState = null;

    function q(sel, root) { return (root || document).querySelector(sel); }
    function qa(sel, root) { return Array.from((root || document).querySelectorAll(sel)); }
    function attr(el, name) { return (el?.getAttribute(name) || '').trim(); }
    function cellPayload(el) {
        return {
            regId: attr(el, 'data-reg'), visitId: attr(el, 'data-visit'), serviceId: attr(el, 'data-service'),
            bookId: attr(el, 'data-book'), roomNo: attr(el, 'data-room'), roomCategory: attr(el, 'data-category'),
            categoryLocalId: attr(el, 'data-category-id'), check: attr(el, 'data-check'), date: attr(el, 'data-date'),
            arrival: attr(el, 'data-arrival'), departure: attr(el, 'data-departure'), blockId: Number(attr(el, 'data-block-id') || 0),
            blockReason: attr(el, 'data-block-reason'), paymentStatus: attr(el, 'data-payment'),
            guestName: (q('.guest-name', el)?.textContent || '').trim()
        };
    }
    function isReservation(c) { return ['O', 'CO', 'R', 'P'].includes(c?.check); }
    function showLoader(show) { loader?.classList.toggle('show', !!show); }
    function toast(message, type) {
        if (!toastEl) return;
        toastEl.textContent = message || '';
        toastEl.className = 'calendar-toast show ' + (type || 'info');
        clearTimeout(toastEl._timer);
        toastEl._timer = setTimeout(() => toastEl.classList.remove('show'), 3200);
    }
    function showModal(title, bodyHtml, buttons) {
        modalTitle.textContent = title;
        modalBody.innerHTML = bodyHtml || '';
        modalActions.innerHTML = '';
        (buttons || []).forEach(b => {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'fd-btn ' + (b.className || '');
            btn.textContent = b.text;
            btn.addEventListener('click', b.onClick);
            modalActions.appendChild(btn);
        });
        modalOverlay.classList.add('open');
    }
    function closeModal() { modalOverlay?.classList.remove('open'); }
    function closeDrawer() {
        drawer?.classList.remove('open');
        drawer?.setAttribute('aria-hidden', 'true');
        q('#drawerActionMenu')?.classList.remove('show');
        q('#drawerActionToggle')?.setAttribute('aria-expanded', 'false');
        const arrow = q('#drawerActionArrow');
        if (arrow) arrow.textContent = '▼';
    }
    function openDrawer() {
        drawer?.classList.add('open');
        drawer?.setAttribute('aria-hidden', 'false');
    }
    function closeContext() {
        contextMenu?.classList.remove('open');
        contextMenu?.setAttribute('aria-hidden', 'true');
    }

    async function getJson(url) {
        const r = await fetch(url, { credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
        const data = await r.json().catch(() => null);
        if (!r.ok) throw new Error(data?.message || 'The request failed.');
        return data;
    }
    async function postJson(url, data) {
        const r = await fetch(url, {
            method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token, 'X-Requested-With': 'XMLHttpRequest' },
            body: JSON.stringify(data || {})
        });
        const body = await r.json().catch(() => null);
        if (!r.ok) throw new Error(body?.message || 'The request failed.');
        return body;
    }
    async function runAction(url, data, reload) {
        try {
            showLoader(true);
            const result = await postJson(url, data);
            if (result?.success === false) throw new Error(result.message || 'Unable to complete action.');
            toast(result?.message || 'Completed.', 'success');
            if (reload !== false) setTimeout(() => location.reload(), 450);
            return result;
        } catch (e) {
            toast(e.message || 'Unable to complete action.', 'error');
            return null;
        } finally { showLoader(false); }
    }

    function legacyUrl(action, c) {
        const p = new URLSearchParams({ action: action || '', regId: c?.regId || '', visitId: c?.visitId || '', roomNo: c?.roomNo || '', serviceId: c?.serviceId || '' });
        return '/Calendar/LegacyAction?' + p.toString();
    }

    async function loadDetails(c) {
        if (!c?.regId || !config.permissions?.view) return;
        try {
            showLoader(true);
            const p = new URLSearchParams({ regId: c.regId, roomNo: c.roomNo || '', serviceId: c.serviceId || '' });
            const d = await getJson('/Calendar/Reservation?' + p.toString());

            setText('drawerGuestName', d.guestName || c.guestName || '');
            setText('drawerRoomNoHeader', d.roomNo || c.roomNo || '');
            setText('drawerStatusHeader', drawerStatusText(d.status || c.check));

            setText('drawerRegId', d.regId || c.regId || '');
            setText('drawerBookId', d.bookId || c.bookId || '');
            setText('drawerPhone', d.phone || '');
            setText('drawerEmail', d.email || '');
            setText('drawerCreatedAt', fmtCreated(d.createdAt));
            const createdRow = q('#drawerCreatedRow');
            if (createdRow) createdRow.style.display = d.createdAt ? '' : 'none';

            setText('drawerArrival', fmtDateDash(d.arrival));
            setText('drawerDeparture', fmtDateDash(d.departure));
            setText('drawerNights', stayNights(d.arrival, d.departure));
            setText('drawerTotalRooms', d.totalRooms || 1);
            setText('drawerSource', d.source || '');

            setText('drawerTotal', money(d.grandTotal));
            setText('drawerPayable', money(d.grandTotal));
            setText('drawerPaid', money(d.paidAmount));
            setText('drawerBalance', money(d.remainingAmount));

            setText('drawerOtaNotes', d.notes || '');
            const otaCard = q('#drawerOtaNotesCard');
            if (otaCard) otaCard.style.display = String(d.notes || '').trim() ? '' : 'none';

            setText('drawerNotebook', d.notebook || '');
            const notebookCard = q('#drawerNotebookCard');
            if (notebookCard) notebookCard.style.display = String(d.notebook || '').trim() ? '' : 'none';

            drawer._cell = activeCell;
            drawer._payload = { ...c, check: normalizeStatusCode(d.status, c.check), guestName: d.guestName || c.guestName || '' };
            applyDrawerActionVisibility(drawer._payload);
            openDrawer();
        } catch (e) {
            toast(e.message || 'Unable to load reservation details.', 'error');
        } finally {
            showLoader(false);
        }
    }

    function setText(id, value) {
        const el = document.getElementById(id);
        if (el) el.textContent = value ?? '';
    }

    function normalizeStatusCode(status, fallback) {
        const s = String(status || '').trim().toLowerCase();
        if (s === 'check in' || s === 'checkin' || s === 'checked in') return 'O';
        if (s === 'check out' || s === 'checkout' || s === 'checked out') return 'CO';
        if (s === 'reservation' || s === 'reserved') return 'R';
        if (s === 'provisional') return 'P';
        return String(fallback || '').toUpperCase();
    }

    function drawerStatusText(status) {
        const code = normalizeStatusCode(status, status);
        return code === 'O' ? 'CHECK IN' :
               code === 'CO' ? 'CHECK OUT' :
               code === 'R' ? 'RESERVATION' :
               code === 'P' ? 'PROVISIONAL' :
               String(status || '').toUpperCase();
    }

    function fmtDateDash(v) {
        if (!v) return '';
        const d = new Date(v);
        if (Number.isNaN(d.getTime())) return String(v);
        const dd = String(d.getDate()).padStart(2, '0');
        const mm = String(d.getMonth() + 1).padStart(2, '0');
        return `${dd}-${mm}-${d.getFullYear()}`;
    }

    function fmtCreated(v) {
        if (!v) return '';
        const d = new Date(v);
        if (Number.isNaN(d.getTime())) return String(v);
        const date = d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
        const time = d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hour12: true });
        return `${date} ${time}`;
    }

    function stayNights(arrival, departure) {
        if (!arrival || !departure) return '0';
        const a = new Date(arrival), d = new Date(departure);
        if (Number.isNaN(a.getTime()) || Number.isNaN(d.getTime())) return '0';
        return String(Math.max(1, Math.round((d.setHours(12,0,0,0) - a.setHours(12,0,0,0)) / 86400000)));
    }

    function money(v) {
        const n = Number(v || 0);
        return (config.currency || '') + n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }
    function fmtDate(v) {
        if (!v) return '';
        const d = new Date(v);
        return Number.isNaN(d.getTime()) ? String(v) : d.toLocaleDateString('en-GB');
    }
    function esc(s) { return String(s ?? '').replace(/[&<>'"]/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[m])); }
    function iso(d) { return d.toISOString().slice(0, 10); }
    function addDays(isoValue, days) { const d = new Date(isoValue + 'T12:00:00'); d.setDate(d.getDate() + days); return iso(d); }
    function navigateRange(start, end) { showLoader(true); location.href = `/Calendar?start=${encodeURIComponent(start)}&end=${encodeURIComponent(end)}`; }

    q('#calPrev')?.addEventListener('click', () => navigateRange(addDays(config.start, -15), addDays(config.end, -15)));
    q('#calNext')?.addEventListener('click', () => navigateRange(addDays(config.start, 15), addDays(config.end, 15)));
    q('#calOpenDates')?.addEventListener('click', () => dateOverlay.classList.add('open'));
    qa('[data-close-date]').forEach(b => b.addEventListener('click', () => dateOverlay.classList.remove('open')));
    q('#rangeApplyButton')?.addEventListener('click', () => {
        const s = q('#rangeStartDate').value, e = q('#rangeEndDate').value;
        if (!s || !e) return toast('Please select both dates.', 'error');
        if (new Date(e) < new Date(s)) return toast('End date cannot be before start date.', 'error');
        const days = Math.round((new Date(e) - new Date(s)) / 86400000) + 1;
        if (days > 62) return toast('The MVC calendar supports a maximum 62-day view.', 'error');
        navigateRange(s, e);
    });
    q('#calApplyRange')?.addEventListener('click', () => dateOverlay.classList.add('open'));
    q('#calRefreshPayments')?.addEventListener('click', () => runAction('/Calendar/RefreshPayments', {}, true));
    q('#calGetRate')?.addEventListener('click', openRateModal);

    async function openRateModal() {
        showModal('Get Reservation Rate', `
            <div class="fd-form-grid">
              <div class="fd-field"><label>Arrival</label><input id="rateStart" type="date" value="${esc(config.today)}"></div>
              <div class="fd-field"><label>Departure</label><input id="rateEnd" type="date" value="${esc(addDays(config.today, 1))}"></div>
              <div class="fd-field full"><div id="rateResults" style="min-height:30px;font-size:11px;color:#64748b">Select dates and click Load Rates.</div></div>
            </div>`, [
            { text: 'Close', onClick: closeModal },
            { text: 'Load Rates', className: 'primary', onClick: async () => {
                const s = q('#rateStart').value, e = q('#rateEnd').value;
                if (!s || !e || e <= s) return toast('Departure must be after arrival.', 'error');
                try {
                    const data = await getJson(`/Calendar/Rates?start=${encodeURIComponent(s)}&end=${encodeURIComponent(e)}`);
                    const rates = data.rates || [];
                    q('#rateResults').innerHTML = rates.length ? `<table class="tablepop"><thead><tr><th>Category</th><th>Rate Plan</th><th>Total</th></tr></thead><tbody>${rates.map(r => `<tr><td>${esc(r.category)}</td><td>${esc(r.planName)}</td><td>${esc(money(r.totalRate))}</td></tr>`).join('')}</tbody></table>` : 'No rate plans found for these dates.';
                } catch (e2) { toast(e2.message, 'error'); }
            }}
        ]);
    }

    qa('[data-close-modal]').forEach(b => b.addEventListener('click', closeModal));
    modalOverlay?.addEventListener('click', e => { if (e.target === modalOverlay) closeModal(); });
    dateOverlay?.addEventListener('click', e => { if (e.target === dateOverlay) dateOverlay.classList.remove('open'); });
    qa('[data-close-drawer]').forEach(b => b.addEventListener('click', closeDrawer));

    qa('.room-status').forEach(cell => {
        cell.addEventListener('dblclick', e => {
            const c = cellPayload(cell);
            const visibleBar = e.target.closest('.cell-inner');
            activeCell = cell;
            if (isReservation(c)) {
                if (!visibleBar || !config.permissions?.view) return;
                e.preventDefault();
                loadDetails(c);
            }
            else if (c.check === 'B') {
                if (!visibleBar) return;
                e.preventDefault();
                openBlockModal(c, true);
            }
            else if (c.check === 'A' || c.check === 'D') {
                e.preventDefault();
                openEmptyCellMenu(c);
            }
        });
        cell.addEventListener('contextmenu', e => {
            const c = cellPayload(cell);
            if (!isReservation(c) && c.check !== 'B' && c.check !== 'D' && c.check !== 'A') return;
            const visibleBar = e.target.closest('.cell-inner');
            if ((isReservation(c) || c.check === 'B') && !visibleBar) return;
            e.preventDefault();
            activeCell = cell;
            if (c.check === 'B') return openBlockModal(c, true);
            if (c.check === 'D') return openDirtyModal(c);
            if (c.check === 'A') return openEmptyCellMenu(c);
            showContext(e.clientX, e.clientY, c);
        });
        if (cell.draggable) {
            cell.addEventListener('dragstart', e => { draggedCell = cell; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', JSON.stringify(cellPayload(cell))); });
        }
        cell.addEventListener('dragover', e => { if (!draggedCell || !config.permissions?.drag) return; e.preventDefault(); cell.classList.add('room-drop-target'); });
        cell.addEventListener('dragleave', () => cell.classList.remove('room-drop-target'));
        cell.addEventListener('drop', async e => {
            cell.classList.remove('room-drop-target');
            if (!draggedCell || !config.permissions?.drag) return;
            e.preventDefault();
            const src = cellPayload(draggedCell), dst = cellPayload(cell);
            draggedCell = null;
            if (!src.regId || !dst.roomNo || src.roomNo === dst.roomNo) return;
            if (!confirm(`Move ${src.regId} from room ${src.roomNo} to ${dst.roomNo}?`)) return;
            await runAction('/Calendar/Move', {
                regId: src.regId, visitId: src.visitId, serviceId: src.serviceId,
                oldRoomNo: src.roomNo, newRoomNo: dst.roomNo,
                newRoomCategory: dst.roomCategory, newCategoryLocalId: dst.categoryLocalId,
                arrival: src.arrival, departure: src.departure
            }, true);
        });
    });

    document.addEventListener('dragend', () => { draggedCell = null; qa('.room-drop-target').forEach(x => x.classList.remove('room-drop-target')); });

    qa('.resize-handle').forEach(handle => {
        handle.addEventListener('mousedown', e => {
            e.preventDefault(); e.stopPropagation();
            const cell = handle.closest('.room-status');
            if (!cell || !config.permissions?.resize) return;
            resizeState = { cell, startX: e.clientX, payload: cellPayload(cell) };
            document.body.style.cursor = 'ew-resize';
        });
    });
    document.addEventListener('mouseup', e => {
        if (!resizeState) return;
        const s = resizeState; resizeState = null; document.body.style.cursor = '';
        const unit = 94;
        const delta = Math.round((e.clientX - s.startX) / unit);
        if (!delta) return;
        const newDeparture = addDays(s.payload.departure, delta);
        openResizeModal(s.payload, newDeparture);
    });

    function isCouncilUser() {
        return String(config.role || '').trim().toLowerCase() === 'council';
    }

    function isTodayDeparture(c) {
        return !!c?.departure && String(c.departure).slice(0, 10) === String(config.today || '').slice(0, 10);
    }

    function canDelete(c) {
        const p = config.permissions || {};
        if (c.check === 'O') return !!p.deleteAfterCheckIn;
        if (c.check === 'CO') return !!p.deleteAfterCheckOut;
        if (c.check === 'R' || c.check === 'P') return !!p.deleteRoom;
        return false;
    }

    function setActionVisible(root, cmd, visible) {
        const el = q(`[data-cmd="${cmd}"]`, root);
        if (el) el.style.display = visible ? '' : 'none';
    }

    function applyWebFormsActionVisibility(root, c) {
        const commands = ['checkin', 'edit', 'notebook', 'undocheckin', 'payment', 'guesthistory', 'invoice', 'restaurant', 'delete'];
        commands.forEach(cmd => setActionVisible(root, cmd, false));

        const council = isCouncilUser();
        const restaurant = !!config.isRestaurant;

        if (c.check === 'CO') {
            setActionVisible(root, 'payment', true);
            setActionVisible(root, 'guesthistory', true);
            setActionVisible(root, 'delete', canDelete(c));
        } else if (c.check === 'R') {
            if (council) {
                setActionVisible(root, 'payment', true);
            } else {
                setActionVisible(root, 'checkin', true);
                setActionVisible(root, 'edit', true);
                setActionVisible(root, 'notebook', true);
                setActionVisible(root, 'guesthistory', true);
                setActionVisible(root, 'invoice', true);
                setActionVisible(root, 'delete', canDelete(c));
            }
        } else if (c.check === 'P') {
            // Web Forms shows Confirm/Cancel/Delete for provisional reservations.
            // Confirm/Cancel are not exposed here until their MVC endpoints are migrated;
            // never show unrelated reservation/check-in actions instead.
            if (council) setActionVisible(root, 'payment', true);
            else setActionVisible(root, 'delete', canDelete(c));
        } else if (c.check === 'O') {
            if (council) {
                setActionVisible(root, 'payment', true);
            } else {
                setActionVisible(root, 'edit', true);
                setActionVisible(root, 'notebook', true);
                setActionVisible(root, 'undocheckin', true);
                setActionVisible(root, 'invoice', true);
                setActionVisible(root, 'guesthistory', true);
                setActionVisible(root, 'delete', canDelete(c));
                if (restaurant) setActionVisible(root, 'restaurant', true);
            }
        }

        const edit = q('[data-cmd="edit"]', root);
        if (edit) {
            const label = q('span', edit);
            if (label) label.textContent = c.check === 'O' ? 'Edit Checkin' : 'Edit Reservation';
        }

        const divider = q('[data-context-delete-divider]', root);
        if (divider) divider.style.display = canDelete(c) ? '' : 'none';
    }

    function showContext(x, y, c) {
        contextMenu._payload = c;
        applyWebFormsActionVisibility(contextMenu, c);

        const title = q('#calendarContextTitle');
        if (title) title.textContent = `${c.guestName || c.regId || 'Reservation'} - ${c.roomNo || ''}`;

        contextMenu.classList.add('open');
        contextMenu.setAttribute('aria-hidden', 'false');

        // Measure after displaying, then keep the dark Web Forms menu inside viewport.
        const rect = contextMenu.getBoundingClientRect();
        const pad = 8;
        const left = Math.max(pad, Math.min(x + 2, window.innerWidth - rect.width - pad));
        const top = Math.max(pad, Math.min(y + 2, window.innerHeight - rect.height - pad));
        contextMenu.style.left = left + 'px';
        contextMenu.style.top = top + 'px';
    }

    contextMenu?.addEventListener('click', e => {
        const btn = e.target.closest('button[data-cmd]'); if (!btn) return;
        const c = contextMenu._payload; const cmd = btn.dataset.cmd; closeContext();
        doCommand(cmd, c);
    });
    document.addEventListener('click', e => { if (!contextMenu?.contains(e.target)) closeContext(); });
    window.addEventListener('scroll', closeContext, true);
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') {
            closeContext();
            closeDrawer();
        }
    });

    async function doCommand(cmd, c) {
        activeCell = activeCell || q(`.room-status[data-reg="${CSS.escape(c.regId || '')}"]`);
        if (cmd === 'details') return loadDetails(c);
        if (['edit', 'payment', 'invoice', 'guesthistory', 'restaurant'].includes(cmd)) {
            return window.open(legacyUrl(cmd, c), '_blank', 'noopener');
        }
        if (cmd === 'checkin') return runAction('/Calendar/CheckIn', roomAction(c), true);
        if (cmd === 'undocheckin') {
            if (confirm('Undo check-in for this reservation?'))
                return runAction('/Calendar/UndoCheckIn', roomAction(c), true);
        }
        if (cmd === 'housekeeping') return runAction('/Calendar/Housekeeping', roomAction(c), true);
        if (cmd === 'delete') {
            if (confirm(`Are you sure you want to delete Room ${c.roomNo || ''}${c.guestName ? ' for ' + c.guestName : ''}?\n\nThis action cannot be undone.`))
                return runAction('/Calendar/DeleteRoom', roomAction(c), true);
        }
        if (cmd === 'notebook') return openNotebookModal(c);
        if (cmd === 'wakeup') return openWakeupModal(c);
        if (cmd === 'resize') return openResizeModal(c, c.departure);
    }
    function roomAction(c) {
        return {
            regId: c.regId, visitId: c.visitId, roomNo: c.roomNo,
            roomCategory: c.roomCategory, serviceId: c.serviceId, bookId: c.bookId
        };
    }

    function applyDrawerActionVisibility(c) {
        const root = q('#drawerActionMenu');
        if (!root) return;

        const all = qa('[data-drawer-action]', root);
        all.forEach(x => x.style.display = 'none');

        const show = (cmd, yes) => {
            const el = q(`[data-drawer-action="${cmd}"]`, root);
            if (el) el.style.display = yes ? '' : 'none';
        };

        const council = isCouncilUser();
        if (c.check === 'CO') {
            show('payment', true);
        } else if (c.check === 'R') {
            if (council) show('payment', true);
            else {
                show('checkin', true);
                show('edit', true);
                show('notebook', true);
                show('invoice', true);
            }
        } else if (c.check === 'P') {
            if (council) show('payment', true);
            // Keep unsupported Confirm/Cancel controls hidden rather than showing wrong actions.
        } else if (c.check === 'O') {
            if (council) show('payment', true);
            else {
                show('edit', true);
                show('notebook', true);
                show('undocheckin', true);
                show('invoice', true);
                if (config.isRestaurant) show('restaurant', true);
            }
        }

        const edit = q('[data-drawer-action="edit"]', root);
        if (edit) {
            const spans = qa('span', edit);
            const label = spans[spans.length - 1];
            if (label) label.textContent = c.check === 'O' ? 'Edit Checkin' : 'Edit Reservation';
        }
    }

    q('#drawerActionToggle')?.addEventListener('click', () => {
        const menu = q('#drawerActionMenu');
        const toggle = q('#drawerActionToggle');
        const arrow = q('#drawerActionArrow');
        if (!menu || !toggle) return;
        const show = !menu.classList.contains('show');
        menu.classList.toggle('show', show);
        toggle.setAttribute('aria-expanded', show ? 'true' : 'false');
        if (arrow) arrow.textContent = show ? '▲' : '▼';
    });

    qa('[data-drawer-action]').forEach(btn => btn.addEventListener('click', () => {
        const c = drawer._payload || cellPayload(drawer._cell);
        q('#drawerActionMenu')?.classList.remove('show');
        doCommand(btn.dataset.drawerAction, c);
    }));

    function openNotebookModal(c) {
        showModal('Reservation Notebook', `<div class="fd-field full"><label>Notes</label><textarea id="notebookText" placeholder="Enter FDO / reservation note"></textarea></div>`, [
            { text: 'Cancel', onClick: closeModal },
            { text: 'Save', className: 'primary', onClick: async () => { const result = await runAction('/Calendar/Notebook', { regId: c.regId, description: q('#notebookText').value }, false); if (result) { closeModal(); setTimeout(() => location.reload(), 350); } } }
        ]);
    }
    function openWakeupModal(c) {
        showModal('Wake-up Call', `<div class="fd-form-grid">
            <div class="fd-field"><label>Date</label><input id="wakeDate" type="date" value="${esc(config.today)}"></div>
            <div class="fd-field"><label>Time</label><input id="wakeTime" type="time"></div>
            <div class="fd-field full"><label>Comment</label><textarea id="wakeComment"></textarea></div>
        </div>`, [
            { text: 'Cancel', onClick: closeModal },
            { text: 'Save', className: 'primary', onClick: async () => {
                const result = await runAction('/Calendar/Wakeup', { regId: c.regId, visitId: c.visitId, roomNo: c.roomNo, roomCategory: c.roomCategory, date: q('#wakeDate').value, time: q('#wakeTime').value, comment: q('#wakeComment').value }, false);
                if (result) { closeModal(); setTimeout(() => location.reload(), 350); }
            } }
        ]);
    }
    function openBlockModal(c, edit) {
        const start = edit && c.arrival ? c.arrival : c.date;
        const end = edit && c.departure ? addDays(c.departure, -1) : c.date;
        showModal(edit ? 'Edit Blocked Room' : 'Block Room', `<div class="fd-form-grid">
            <div class="fd-field"><label>Room</label><input id="blockRoom" value="${esc(c.roomNo)}" readonly></div>
            <div class="fd-field"><label>Reason</label><input id="blockReason" value="${esc(c.blockReason || '')}" placeholder="Maintenance / Out of order"></div>
            <div class="fd-field"><label>From</label><input id="blockStart" type="date" value="${esc(start)}"></div>
            <div class="fd-field"><label>To</label><input id="blockEnd" type="date" value="${esc(end)}"></div>
        </div>`, [
            { text: 'Cancel', onClick: closeModal },
            ...(edit ? [{ text: 'Activate Room', className: 'danger', onClick: async () => { const r = await runAction('/Calendar/ActivateRoom', { blockId: c.blockId, roomNo: c.roomNo, start, end, reason: c.blockReason }, false); if (r) { closeModal(); location.reload(); } } }] : []),
            { text: edit ? 'Update Block' : 'Block Room', className: 'primary', onClick: async () => {
                const body = { blockId: c.blockId || 0, roomNo: q('#blockRoom').value, start: q('#blockStart').value, end: q('#blockEnd').value, reason: q('#blockReason').value };
                const r = await runAction(edit ? '/Calendar/UpdateBlock' : '/Calendar/Block', body, false); if (r) { closeModal(); location.reload(); }
            } }
        ]);
    }
    function openDirtyModal(c) {
        showModal('Dirty Room', `<p style="font-size:12px;color:#475569">Room <strong>${esc(c.roomNo)}</strong> is marked dirty. You can activate it after confirming the room is ready.</p>`, [
            { text: 'Cancel', onClick: closeModal },
            { text: 'Housekeeping', onClick: () => window.open(legacyUrl('housekeeping', c), '_blank', 'noopener') },
            { text: 'Activate Room', className: 'primary', onClick: async () => { const r = await runAction('/Calendar/Housekeeping', roomAction(c), false); if (r) { closeModal(); location.reload(); } } }
        ]);
    }
    function openEmptyCellMenu(c) {
        showModal('Available Room', `<p style="font-size:12px;color:#475569">Room <strong>${esc(c.roomNo)}</strong> is available on <strong>${esc(c.date)}</strong>.</p>`, [
            { text: 'Close', onClick: closeModal },
            { text: 'Block Room', onClick: () => { closeModal(); openBlockModal(c, false); } },
            { text: 'New Reservation', className: 'primary', onClick: () => { const p = new URLSearchParams(); if (c.date) p.set('FD', btoa(c.date)); if (c.roomCategory) p.set('BT', btoa(unescape(encodeURIComponent(c.roomCategoryId || c.roomCategory)))); if (c.roomNo) p.set('RN', btoa(unescape(encodeURIComponent(c.roomNo)))); p.set('NT','MQ=='); p.set('RS','MQ=='); window.open('/CreateReservation?' + p.toString(), '_blank', 'noopener'); } }
        ]);
    }

    async function openResizeModal(c, suggestedDeparture) {
        if (!c?.regId) return;
        let plans = [];
        try {
            showLoader(true);
            const p = new URLSearchParams({ regId: c.regId, roomNo: c.roomNo, serviceId: c.serviceId || '' });
            const result = await getJson('/Calendar/ExtendPlans?' + p.toString());
            plans = result.plans || [];
        } catch (e) { toast(e.message, 'error'); }
        finally { showLoader(false); }

        const dep = suggestedDeparture || c.departure;
        const planHtml = plans.length ? `<div class="fd-field full"><label>Rates for added nights</label><div style="max-height:220px;overflow:auto"><table class="tablepop"><thead><tr><th>Plan</th><th>Old Rate</th><th>New Rate</th><th>Include Tax</th></tr></thead><tbody>${plans.map((p, i) => `<tr><td>${esc(p.planName || p.roomType)}</td><td>${esc(money(p.oldRate))}</td><td><input class="resize-rate" data-i="${i}" type="number" step="0.01" value="${Number(p.newRate || p.oldRate || 0).toFixed(2)}" style="width:90px"></td><td><input class="resize-tax" data-i="${i}" type="checkbox" ${p.includeTax ? 'checked' : ''}></td></tr>`).join('')}</tbody></table></div></div>` : '';
        showModal('Extend / Shrink Reservation', `<div class="fd-form-grid">
            <div class="fd-field"><label>Arrival</label><input id="resizeArrival" type="date" value="${esc(c.arrival)}" readonly></div>
            <div class="fd-field"><label>Current Departure</label><input id="resizeOldDeparture" type="date" value="${esc(c.departure)}" readonly></div>
            <div class="fd-field full"><label>New Departure</label><input id="resizeNewDeparture" type="date" value="${esc(dep)}"></div>
            ${planHtml}
        </div>`, [
            { text: 'Cancel', onClick: closeModal },
            { text: 'Check & Apply', className: 'primary', onClick: async () => {
                const newDep = q('#resizeNewDeparture').value;
                if (!newDep || newDep <= c.arrival) return toast('Departure must be after arrival.', 'error');
                const planRates = plans.map((p, i) => ({ planId: p.planId, roomType: p.roomType, categoryLocalId: p.categoryLocalId, newRate: Number(q(`.resize-rate[data-i="${i}"]`)?.value || p.newRate || p.oldRate || 0), includeTax: !!q(`.resize-tax[data-i="${i}"]`)?.checked }));
                const payload = { regId: c.regId, roomNo: c.roomNo, serviceId: c.serviceId, arrival: c.arrival, oldDeparture: c.departure, newDeparture: newDep, planRates };
                try {
                    showLoader(true);
                    const check = await postJson('/Calendar/CheckResize', payload);
                    if (check?.success === false) throw new Error(check.message || 'Room is not available for this stay.');
                    if (!confirm(`Apply new departure date ${newDep}?`)) return;
                    const result = await postJson('/Calendar/Resize', payload);
                    if (result?.success === false) throw new Error(result.message);
                    toast(result?.message || 'Reservation updated.', 'success'); closeModal(); setTimeout(() => location.reload(), 450);
                } catch (e) { toast(e.message, 'error'); }
                finally { showLoader(false); }
            } }
        ]);
    }

    // The original Web Forms page intentionally collapses Site.Master's sidebar for Calendar only.
    // Preserve that page-specific behaviour without writing to localStorage.
    window.addEventListener('load', () => {
        if (window.innerWidth > 768) document.body.classList.add('sidebar-collapsed');
        else document.body.classList.remove('mobile-sidebar-open');
        const bodyScroll = q('#calendarBodyScroll');
        const today = q('.today-head');
        if (bodyScroll && today) {
            const roomWidth = 95;
            bodyScroll.scrollLeft = Math.max(0, today.offsetLeft - roomWidth - bodyScroll.clientWidth / 3);
        }
    });
})();
