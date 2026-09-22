(() => {
    'use strict';

    const app = document.getElementById('checkinApp');
    if (!app) return;

    const $ = (selector, root = document) => root.querySelector(selector);
    const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));
    const byId = id => document.getElementById(id);
    const token = $('#antiForgeryForm input[name="__RequestVerificationToken"]')?.value || '';

    const state = (() => {
        try { return JSON.parse(byId('checkinInitialState')?.textContent || '{}'); }
        catch { return {}; }
    })();

    const urls = {
        search: '/CheckIn/Search',
        guestSuggestions: '/CheckIn/GuestSuggestions',
        guestByContact: '/CheckIn/GuestByContact',
        cities: '/CheckIn/Cities',
        chargeTypes: '/CheckIn/ChargeTypes',
        rooms: '/CheckIn/Rooms',
        ratePlans: '/CheckIn/RatePlans',
        rateQuote: '/CheckIn/RateQuote',
        saveGuest: '/CheckIn/SaveGuest',
        updateGuest: '/CheckIn/UpdateGuest',
        updateGuestNameQuick: '/CheckIn/UpdateGuestName',
        addCharge: '/CheckIn/AddCharge',
        deleteCharge: '/CheckIn/DeleteCharge',
        updateRate: '/CheckIn/UpdateChargeRate',
        updateGuestName: '/CheckIn/UpdateChargeGuestName',
        roomChangeOptions: '/CheckIn/RoomChangeOptions',
        changeRoom: '/CheckIn/ChangeRoom',
        recordPayment: '/CheckIn/RecordPayment',
        refundPayment: '/CheckIn/RefundPayment',
        completeCheckIn: '/CheckIn/CompleteCheckIn',
        undoCheckIn: '/CheckIn/UndoCheckIn',
        checkOut: '/CheckIn/CheckOut',
        extend: '/CheckIn/Extend',
        applyDiscount: '/CheckIn/ApplyDiscount',
        laundryCategories: '/CheckIn/LaundryCategories',
        laundryItems: '/CheckIn/LaundryItems',
        addLaundry: '/CheckIn/AddLaundry',
        deleteLaundry: '/CheckIn/DeleteLaundry',
        securityMovement: '/CheckIn/SecurityMovement',
        addCompany: '/CheckIn/AddCompany',
        deleteCompany: '/CheckIn/DeleteCompany',
        addSource: '/CheckIn/AddSource',
        deleteSource: '/CheckIn/DeleteSource',
        paymentAudit: '/CheckIn/PaymentAudit',
        stripeCreate: '/CheckIn/Stripe/Create',
        stripeProcess: '/CheckIn/Stripe/Process',
        stripeStatus: '/CheckIn/Stripe/Status',
        stripeCancel: '/CheckIn/Stripe/Cancel',
        stripeCheckout: '/CheckIn/Stripe/Checkout',
        stripeCheckoutStatus: '/CheckIn/Stripe/CheckoutStatus',
        cloverPay: '/CheckIn/Clover/Pay',
        fbrApplyTaxMode: '/CheckIn/Fbr/ApplyTaxMode',
        fbrPost: '/CheckIn/Fbr/Post'
    };

    let currency = app.dataset.currency || state.currency || '£';
    let currencyCode = (app.dataset.currencyCode || state.currencyCode || 'GBP').toLowerCase();
    let regId = (app.dataset.regId || state.reservationId || '').trim();
    let visitId = (app.dataset.visitId || state.visitId || '').trim();
    let currentPaymentIntentId = '';
    let cardSecurityMode = false;
    let pendingSecurityId = 0;
    let pendingSecurityMethod = '';
    let pendingSecurityPaymentIntentId = '';
    let pendingSecurityChargeId = '';
    let currentCardProvider = app.dataset.stripe === '1' ? 'stripe' : 'clover';
    let currentCardMode = 'terminal';
    let manualCheckoutWindow = null;
    let manualCheckoutPoll = 0;
    let manualCheckoutContext = null;
    let searchTimer = 0;
    let calendarView = null;
    let calendarStage = 0;
    let calendarHoverDate = null;
    let confirmResolver = null;
    let inputResolver = null;
    let quickNameSaving = false;
    let quickNameQueued = false;
    let lastQuickNameSnapshot = '';
    let dateTaxResolver = null;

    const money = value => `${currency}${Number(value || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
    const amountOnly = value => Number(value || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const toNumber = value => {
        const n = Number(value);
        return Number.isFinite(n) ? n : 0;
    };
    const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    function base64Url(value) {
        const bytes = new TextEncoder().encode(String(value ?? ''));
        let binary = '';
        for (const b of bytes) binary += String.fromCharCode(b);
        return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
    }

    function openPdqPaymentLikeWebForms() {
        const hotelId = String(state.hotelId || '').trim();
        const currentRegId = String(regId || '').trim();
        if (!hotelId || !currentRegId) {
            message('Missing hotel or reservation ID.', 'error');
            return false;
        }

        const paidEl = byId('paidAmount');
        const major = toNumber(paidEl?.value || 0);
        if (major <= 0) {
            message('Paid Amount must be greater than 0.', 'error');
            paidEl?.focus();
            return false;
        }

        const minor = Math.round(major * 100);
        const fullName = [byId('firstName')?.value || '', byId('lastName')?.value || '']
            .map(x => x.trim()).filter(Boolean).join(' ');
        const arrival = byId('checkIn')?.value || '';
        const departure = byId('checkOut')?.value || '';
        const ccy = String(currencyCode || state.currencyCode || 'GBP').toUpperCase();
        const userId = String(state.userId || '').trim();
        const returnUrl = window.location.href;
        const majorStr = (minor / 100).toFixed(2);

        // Same WebForms behavior: once the PDQ amount has been handed to the
        // TerminalCardPayment popup, reset the page Paid Amount field to zero.
        if (paidEl) paidEl.value = '0';

        const query = [
            ['hotelId', hotelId],
            ['regId', currentRegId],
            ['visitId', visitId || ''],
            ['fullName', fullName],
            ['amount', String(minor)],
            ['currency', ccy],
            ['arrival', arrival],
            ['departure', departure],
            ['grandTotal', majorStr],
            ['payable', majorStr],
            ['advancePaid', '0'],
            ['status', 'check in'],
            ['src', 'NR'],
            ['userid', userId],
            ['returnUrl', returnUrl]
        ].map(([k, v]) => `${k}=${encodeURIComponent(base64Url(v))}`).join('&');

        const terminalUrl = String(app.dataset.terminalUrl || 'TerminalCardPayment.aspx').trim();
        const popup = window.open(`${terminalUrl}?${query}`, 'pdq', 'width=500,height=720');
        if (!popup) message('Please allow pop-ups to open the PDQ payment window.', 'error');
        return false;
    }
    const iso = value => {
        if (!value) return '';
        const d = value instanceof Date ? value : new Date(value);
        if (Number.isNaN(d.getTime())) return '';
        return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
    };
    const parseIso = value => {
        if (!value) return null;
        const parts = String(value).split('-').map(Number);
        if (parts.length !== 3 || parts.some(Number.isNaN)) return null;
        return new Date(parts[0], parts[1] - 1, parts[2]);
    };
    const formatDate = value => {
        const d = parseIso(value);
        return d ? d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : '';
    };
    const isGroup = () => (byId('reservationType')?.value || '').toLowerCase() === 'group';
    const dateMode = () => isGroup() ? (byId('reservationDateMode')?.value || 'groupSame') : 'single';
    const normalizedStayStatus = String(state.reservationStatus || '').trim().toLowerCase().replace(/[_-]+/g, ' ').replace(/\s+/g, ' ');
    const activeStayAllowsDateEdit = ['reservation', 'reserved', 'provisional', 'check in', 'checkin', 'checked in'].includes(normalizedStayStatus);
    // Active reservations and checked-in stays can always open the main Stay Dates picker.
    // This intentionally overrides older server-side type/shape locks that were preventing
    // multi-room active stays from being extended or shortened from this page.
    const canEditDates = state.canEditDates !== false || activeStayAllowsDateEdit;
    const loadedStayArrival = byId('checkIn')?.value || '';
    const loadedStayDeparture = byId('checkOut')?.value || '';

    async function api(url, options = {}) {
        const opts = { credentials: 'same-origin', ...options };
        opts.headers = { Accept: 'application/json', ...(opts.headers || {}) };
        if ((opts.method || 'GET').toUpperCase() !== 'GET') {
            opts.headers.RequestVerificationToken = token;
            if (opts.body && typeof opts.body !== 'string') {
                opts.headers['Content-Type'] = 'application/json';
                opts.body = JSON.stringify(opts.body);
            } else if (typeof opts.body === 'string' && !opts.headers['Content-Type']) {
                opts.headers['Content-Type'] = 'application/json';
            }
        }
        const response = await fetch(url, opts);
        const contentType = response.headers.get('content-type') || '';
        const data = contentType.includes('application/json')
            ? await response.json().catch(() => ({}))
            : { ok: false, message: await response.text().catch(() => '') };
        if (!response.ok) throw new Error(data.message || `Request failed (${response.status}).`);
        return data;
    }

    function showBusy(text = 'Processing…') {
        const overlay = byId('busyOverlay');
        const label = byId('busyText');
        if (label) label.textContent = text;
        overlay?.classList.remove('hidden');
    }
    function hideBusy() { byId('busyOverlay')?.classList.add('hidden'); }

    function message(text, type = 'info') {
        const box = byId('message');
        if (box) {
            box.textContent = text || '';
            box.className = `message ${text ? `is-${type}` : ''}`;
        }
        if (text) toast(text, type);
    }

    function toast(text, type = 'info') {
        const host = byId('toastHost');
        if (!host || !text) return;
        const div = document.createElement('div');
        div.className = `toast toast-${type}`;
        div.setAttribute('role', type === 'error' ? 'alert' : 'status');
        div.innerHTML = `<i class="fa-solid ${type === 'success' ? 'fa-circle-check' : type === 'error' ? 'fa-circle-exclamation' : 'fa-circle-info'}"></i><div class="toast-text"></div><button type="button" class="toast-close" aria-label="Dismiss">×</button>`;
        $('.toast-text', div).textContent = text;
        $('.toast-close', div)?.addEventListener('click', () => div.remove());
        host.appendChild(div);
        setTimeout(() => div.remove(), type === 'error' ? 6000 : 4000);
    }

    function openModal(id) {
        byId(id)?.classList.remove('hidden');
        document.body.style.overflow = 'hidden';
    }
    function closeModal(id) {
        byId(id)?.classList.add('hidden');
        if (!$$('.modal-backdrop:not(.hidden)').length) document.body.style.overflow = '';
    }

    function askConfirm(title, text, okText = 'Confirm') {
        const modal = byId('confirmDialog');
        if (!modal) {
            message(text || title, 'info');
            return Promise.resolve(false);
        }
        byId('confirmTitle').textContent = title;
        byId('confirmMessage').textContent = text;
        byId('confirmOk').textContent = okText;
        openModal('confirmDialog');
        return new Promise(resolve => { confirmResolver = resolve; });
    }
    function resolveConfirm(value) {
        closeModal('confirmDialog');
        if (confirmResolver) confirmResolver(value);
        confirmResolver = null;
    }

    function hasConfiguredDateChangeTax() {
        const gstAvailable = state.hasGst === true && state.hasGstPermission !== false;
        const bedAvailable = state.hasBedTax === true && state.hasBedTaxPermission !== false &&
            String(state.taxLabel || '').toLowerCase() !== 'vat';
        return gstAvailable || bedAvailable;
    }

    function askDateChangeTaxSelection() {
        const gstAvailable = state.hasGst === true && state.hasGstPermission !== false;
        const bedAvailable = state.hasBedTax === true && state.hasBedTaxPermission !== false &&
            String(state.taxLabel || '').toLowerCase() !== 'vat';

        // Nothing is configured for the property, so there is nothing to ask.
        if (!gstAvailable && !bedAvailable) {
            return Promise.resolve({ confirmed: true, applyGst: false, applyBedTax: false });
        }

        const gst = byId('dateTaxGst');
        const bed = byId('dateTaxBed');
        const existingCharges = Array.isArray(state.charges) ? state.charges : [];

        // Keep the most useful default: if the current stay is already taxed, preselect
        // the same configured tax for the added nights. The user can still untick it.
        if (gst) gst.checked = gstAvailable && existingCharges.some(x => toNumber(x?.gst) > 0);
        if (bed) bed.checked = bedAvailable && existingCharges.some(x => toNumber(x?.bedTax) > 0);

        openModal('dateTaxModal');
        return new Promise(resolve => { dateTaxResolver = resolve; });
    }

    function resolveDateChangeTaxSelection(confirm) {
        const result = confirm ? {
            confirmed: true,
            applyGst: !!byId('dateTaxGst')?.checked,
            applyBedTax: !!byId('dateTaxBed')?.checked
        } : null;
        closeModal('dateTaxModal');
        if (dateTaxResolver) dateTaxResolver(result);
        dateTaxResolver = null;
    }

    function askInput(title, label, value = '', options = {}) {
        const modal = byId('inputDialog');
        const input = byId('inputDialogValue');
        if (!modal || !input) return Promise.resolve(null);
        byId('inputDialogTitle').textContent = title || 'Enter Value';
        byId('inputDialogLabel').textContent = label || 'Value';
        input.type = options.type || 'text';
        input.value = value == null ? '' : String(value);
        input.min = options.min != null ? String(options.min) : '';
        input.max = options.max != null ? String(options.max) : '';
        input.step = options.step != null ? String(options.step) : '';
        input.placeholder = options.placeholder || '';
        const hint = byId('inputDialogHint');
        if (hint) {
            hint.textContent = options.hint || '';
            hint.classList.toggle('hidden', !options.hint);
        }
        byId('inputDialogOk').textContent = options.okText || 'OK';
        openModal('inputDialog');
        setTimeout(() => { input.focus(); input.select(); }, 0);
        return new Promise(resolve => { inputResolver = resolve; });
    }
    function resolveInput(value) {
        const input = byId('inputDialogValue');
        const resolved = value ? (input?.value ?? '') : null;
        closeModal('inputDialog');
        if (inputResolver) inputResolver(resolved);
        inputResolver = null;
    }

    function setSelectValue(id, value) {
        const el = byId(id);
        if (!el || value == null) return;
        const v = String(value);
        if (![...el.options].some(o => o.value === v)) {
            const opt = document.createElement('option');
            opt.value = v; opt.textContent = v; el.appendChild(opt);
        }
        el.value = v;
    }

    function guestPayload() {
        return {
            regId,
            visitId,
            firstName: byId('firstName')?.value.trim() || '',
            lastName: byId('lastName')?.value.trim() || '',
            phone: byId('phone')?.value.trim() || '',
            email: byId('email')?.value.trim() || '',
            idType: 'Passport',
            idNumber: byId('passportNo')?.value.trim() || '',
            passportNo: byId('passportNo')?.value.trim() || '',
            vatNo: byId('vatNo')?.value.trim() || '',
            address: byId('address')?.value.trim() || '',
            country: byId('country')?.value || '',
            city: byId('city')?.value || '',
            arrivalDate: byId('checkIn')?.value || '',
            departureDate: byId('checkOut')?.value || '',
            arrivalTime: byId('arrivalTime')?.value || '',
            departureTime: byId('departureTime')?.value || '',
            adults: Math.max(1, parseInt(byId('adults')?.value || '1', 10) || 1),
            children: Math.max(0, parseInt(byId('children')?.value || '0', 10) || 0),
            company: byId('company')?.value || '',
            source: byId('source')?.value || '',
            notes: byId('notes')?.value.trim() || '',
            reservationType: byId('reservationType')?.value || 'Individual',
            bookingId: byId('bookingId')?.value.trim() || '',
            groupName: byId('groupName')?.value.trim() || '',
            complementary: !!byId('complementary')?.checked
        };
    }

    function validateGuest() {
        const name = byId('firstName');
        name?.classList.remove('invalid');
        if (!name?.value.trim()) {
            name?.classList.add('invalid');
            name?.focus();
            message('Guest first name is required.', 'error');
            return false;
        }
        const a = parseIso(byId('checkIn')?.value);
        const d = parseIso(byId('checkOut')?.value);
        if (!a || !d || d <= a) {
            message('Departure date must be after arrival date.', 'error');
            return false;
        }
        return true;
    }

    function monthCount(a, d) {
        if (!a || !d || d <= a) return 0;
        let count = (d.getFullYear() - a.getFullYear()) * 12 + (d.getMonth() - a.getMonth());
        if (d.getDate() > a.getDate()) count += 1;
        return Math.max(1, count || 1);
    }

    function updateStayCount() {
        const a = parseIso(byId('checkIn')?.value);
        const d = parseIso(byId('checkOut')?.value);
        let count = 0;
        if (a && d && d > a) {
            count = app.dataset.monthWise === '1' ? monthCount(a, d) : Math.round((d - a) / 86400000);
        }
        if (byId('stayCount')) byId('stayCount').value = count;
        syncChargeDatesFromMain();
        updateChargeStayCount();
        updateDateDisplay();
        return count;
    }

    function updateDateDisplay() {
        const a = byId('checkIn')?.value || '';
        const d = byId('checkOut')?.value || '';
        const input = byId('stayDateDisplay');
        if (!input) return;
        input.value = a ? (d ? `${formatDate(a)} → ${formatDate(d)}` : `${formatDate(a)} → Select departure`) : 'Select arrival and departure dates';
    }

    function syncChargeDatesFromMain() {
        if (dateMode() === 'groupDifferent') return;
        if (byId('chargeArrival')) byId('chargeArrival').value = byId('checkIn')?.value || '';
        if (byId('chargeDeparture')) byId('chargeDeparture').value = byId('checkOut')?.value || '';
    }

    function updateChargeStayCount() {
        const a = parseIso(byId('chargeArrival')?.value);
        const d = parseIso(byId('chargeDeparture')?.value);
        let count = 0;
        if (a && d && d > a) count = app.dataset.monthWise === '1' ? monthCount(a, d) : Math.round((d - a) / 86400000);
        if (byId('chargeNights')) byId('chargeNights').value = count;
        return count;
    }

    function applyReservationMode() {
        const group = isGroup();
        $$('.group-only').forEach(x => x.classList.toggle('hidden', !group));
        const different = group && dateMode() === 'groupDifferent' && isRoomChargeMode();
        $$('.room-date-col').forEach(x => x.classList.toggle('hidden', !different));
        if (!different) syncChargeDatesFromMain();
    }

    function syncCounter(input) {
        if (!input) return;
        const min = Number(input.min || 0);
        const max = input.max ? Number(input.max) : Infinity;
        const value = Math.min(max, Math.max(min, Number(input.value || min)));
        input.value = value;
        const box = input.closest('.counter');
        if (!box) return;
        const minus = $('[data-delta="-1"]', box);
        const plus = $('[data-delta="1"]', box);
        if (minus) minus.disabled = value <= min;
        if (plus) plus.disabled = value >= max;
    }

    function renderCalendar() {
        const host = byId('calendarDays');
        if (!host) return;
        const a = parseIso(byId('checkIn')?.value);
        const d = parseIso(byId('checkOut')?.value);
        const hover = calendarStage === 1 ? parseIso(calendarHoverDate) : null;
        if (!calendarView) calendarView = new Date((a || new Date()).getFullYear(), (a || new Date()).getMonth(), 1);
        byId('calendarMonth').textContent = calendarView.toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
        host.innerHTML = '';
        const first = new Date(calendarView.getFullYear(), calendarView.getMonth(), 1);
        const last = new Date(calendarView.getFullYear(), calendarView.getMonth() + 1, 0);
        for (let i = 0; i < first.getDay(); i++) host.insertAdjacentHTML('beforeend', '<span class="calendar-blank"></span>');
        const today = iso(new Date());
        for (let day = 1; day <= last.getDate(); day++) {
            const date = new Date(calendarView.getFullYear(), calendarView.getMonth(), day);
            const value = iso(date);
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'calendar-day';
            btn.dataset.calendarDate = value;
            btn.textContent = String(day);
            if (value === today) btn.classList.add('today');
            if (a && value === iso(a)) btn.classList.add('selected', 'range-start');
            if (d && value === iso(d)) btn.classList.add('selected', 'range-end');
            if (a && d && date > a && date < d) btn.classList.add('in-range');
            if (a && !d && hover && hover > a && date > a && date < hover) btn.classList.add('range-preview');
            if (a && !d && hover && value === iso(hover) && hover > a) btn.classList.add('preview-end');
            host.appendChild(btn);
        }
        const hint = byId('calendarHint');
        if (hint) {
            hint.textContent = calendarStage === 0
                ? 'Choose arrival date'
                : 'Now choose a departure date';
            hint.classList.toggle('is-selecting-end', calendarStage === 1);
        }
    }

    function setCalendarOpen(open) {
        if (!canEditDates && open) {
            message('Dates are locked for this reservation type/status.', 'info');
            return;
        }
        const panel = byId('dateCalendar');
        panel?.classList.toggle('hidden', !open);
        byId('stayDateDisplay')?.setAttribute('aria-expanded', open ? 'true' : 'false');
        if (open) {
            const a = parseIso(byId('checkIn')?.value);
            const d = parseIso(byId('checkOut')?.value);
            const anchor = a || new Date();
            calendarView = new Date(anchor.getFullYear(), anchor.getMonth(), 1);
            // Existing reservations/check-ins keep their current arrival and immediately
            // select a new departure date. New walk-ins continue to use range selection.
            calendarStage = a && (regId || !d) ? 1 : 0;
            calendarHoverDate = null;
            renderCalendar();
        } else {
            calendarHoverDate = null;
        }
    }

    function selectCalendarDate(value) {
        const selected = parseIso(value);
        if (!selected) return;

        if (calendarStage === 0) {
            // A range selection always starts cleanly: first click = arrival,
            // second click = departure. Do not auto-create a departure date.
            byId('checkIn').value = value;
            byId('checkOut').value = '';
            calendarStage = 1;
            calendarHoverDate = null;
            updateStayCount();
            renderCalendar();
            return;
        }

        const arr = parseIso(byId('checkIn')?.value);
        if (!arr) {
            byId('checkIn').value = value;
            byId('checkOut').value = '';
            calendarStage = 1;
            updateStayCount();
            renderCalendar();
            return;
        }

        // Existing reservations/check-ins change only the departure edge here.
        // Keep the original arrival date and require departure > arrival.
        if (selected <= arr) {
            if (regId) {
                message('Departure date must be after the arrival date.', 'info');
                return;
            }
            byId('checkIn').value = value;
            byId('checkOut').value = '';
            calendarStage = 1;
            calendarHoverDate = null;
            updateStayCount();
            renderCalendar();
            return;
        }

        byId('checkOut').value = value;
        calendarStage = 0;
        calendarHoverDate = null;
        updateStayCount();
        setCalendarOpen(false);
        loadRoomsAndPlans().catch(() => { });
    }

    async function loadCities(preferredCity = '') {
        const country = (byId('country')?.value || '').trim();
        const city = byId('city');
        if (!city) return;

        const selected = (preferredCity || city.value || '').trim();
        city.disabled = true;
        city.innerHTML = country
            ? '<option value="">Loading cities...</option>'
            : '<option value="">--Select--</option>';

        if (!country) {
            city.disabled = false;
            return;
        }

        try {
            const response = await api(`${urls.cities}?country=${encodeURIComponent(country)}`);
            const items = Array.isArray(response)
                ? response
                : (Array.isArray(response?.data) ? response.data : []);

            city.innerHTML = '<option value="">--Select--</option>';
            items.forEach(x => {
                const value = String(x?.value ?? x?.Value ?? x?.text ?? x?.Text ?? '').trim();
                const text = String(x?.text ?? x?.Text ?? value).trim();
                if (!value) return;
                const option = document.createElement('option');
                option.value = value;
                option.textContent = text;
                city.appendChild(option);
            });

            if (selected) {
                const match = [...city.options].find(o =>
                    o.value.localeCompare(selected, undefined, { sensitivity: 'accent' }) === 0 ||
                    o.textContent.trim().localeCompare(selected, undefined, { sensitivity: 'accent' }) === 0);
                if (match) city.value = match.value;
            }
        } finally {
            city.disabled = false;
        }
    }

    async function tryAutofillGuest(value) {
        value = (value || '').trim();
        if (value.length < 5 || regId) return;
        const guest = await api(`${urls.guestByContact}?value=${encodeURIComponent(value)}`);
        if (!guest || typeof guest !== 'object' || !guest.firstName) return;
        if (!byId('firstName').value) byId('firstName').value = guest.firstName || '';
        if (!byId('lastName').value) byId('lastName').value = guest.lastName || '';
        if (!byId('phone').value) byId('phone').value = guest.phone || '';
        if (!byId('email').value) byId('email').value = guest.email || '';
        if (!byId('address').value) byId('address').value = guest.address || '';
        if (guest.country) { setSelectValue('country', guest.country); await loadCities(guest.city || ''); }
        else if (guest.city) setSelectValue('city', guest.city);
        if (!byId('passportNo').value) byId('passportNo').value = guest.passportNo || '';
        if (!byId('vatNo').value) byId('vatNo').value = guest.vatNo || '';
        if (guest.company) setSelectValue('company', guest.company);
        if (guest.source) setSelectValue('source', guest.source);
    }

    async function searchGuests() {
        const term = byId('guestSearch')?.value.trim() || '';
        const results = byId('searchResults');
        if (!results) return;
        if (term.length < 2) { results.classList.add('hidden'); results.innerHTML = ''; return; }
        const items = await api(`${urls.search}?term=${encodeURIComponent(term)}`);
        results.innerHTML = (Array.isArray(items) && items.length)
            ? items.map(x => `<button type="button" class="search-result" data-reg="${esc(x.regId)}"><b>${esc(`${x.guestName || ''} ${x.lastName || ''}`.trim())}</b><span>${esc(x.regId)} · ${esc(x.phone || x.email || '')}</span><small>${esc(x.status || '')}</small></button>`).join('')
            : '<div class="search-result-empty">No matching guest or reservation found.</div>';
        results.classList.remove('hidden');
    }

    function currentNameSnapshot() {
        return `${(byId('firstName')?.value || '').trim()}\u0000${(byId('lastName')?.value || '').trim()}`;
    }


    function reloadReservation(targetRegId = regId) {
        const url = new URL(window.location.href);
        url.searchParams.delete('RI');
        if (targetRegId) url.searchParams.set('q', targetRegId);
        else url.searchParams.delete('q');
        window.location.assign(url.toString());
    }

    function replaceInnerFromDocument(doc, id) {
        const current = byId(id);
        const fresh = doc.getElementById(id);
        if (!current || !fresh) return;
        current.innerHTML = fresh.innerHTML;
    }

    function replaceValueFromDocument(doc, id) {
        const current = byId(id);
        const fresh = doc.getElementById(id);
        if (!current || !fresh) return;
        if ('value' in current && 'value' in fresh) current.value = fresh.value;
        else current.textContent = fresh.textContent || '';
    }

    function syncChargeOptionalColumns() {
        ['discount', 'gst', 'bedtax'].forEach(name => {
            const cells = $$(`[data-charge-col="${name}"]`);
            if (!cells.length) return;
            const hasValue = cells.some(cell => cell.tagName === 'TD' && Math.abs(toNumber(cell.dataset.value)) > 0.000001);
            cells.forEach(cell => cell.classList.toggle('is-column-hidden', !hasValue));
        });
    }

    function applyTotalsSnapshot(totals) {
        if (!totals || typeof totals !== 'object') return;
        state.totals = { ...(state.totals || {}), ...totals };
        const setText = (id, value) => { const el = byId(id); if (el) el.textContent = money(value); };
        const setAmountText = (id, value) => { const el = byId(id); if (el) el.textContent = amountOnly(value); };
        const setValue = (id, value) => { const el = byId(id); if (el) el.value = money(value); };
        const setAmountValue = (id, value) => { const el = byId(id); if (el) el.value = amountOnly(value); };
        setText('subTotal', totals.subTotal);
        setText('taxTotal', totals.taxTotal);
        setText('grandTotal', totals.grandTotal);
        setAmountText('balance', totals.remaining);
        setAmountValue('paymentTotal', totals.grandTotal);
        setAmountValue('alreadyPaid', totals.paidAmount);
        setValue('payableAmount', totals.payable);
        if (totals.roomSecurity != null) {
            setValue('roomSecurity', totals.roomSecurity);
            setText('depositDisplay', totals.roomSecurity);
        }
    }

    function applyInlineRateResult(row, data) {
        if (!row || !data) return;
        const rateCell = row.querySelector('.rate-cell');
        if (rateCell) {
            rateCell.dataset.value = String(data.rate ?? 0);
            rateCell.textContent = money(data.rate);
        }
        const gstCell = row.querySelector('[data-charge-col="gst"]');
        if (gstCell) {
            gstCell.dataset.value = String(data.gst ?? 0);
            gstCell.textContent = money(data.gst);
        }
        const bedCell = row.querySelector('[data-charge-col="bedtax"]');
        if (bedCell) {
            bedCell.dataset.value = String(data.bedTax ?? 0);
            bedCell.textContent = money(data.bedTax);
        }
        const discountCell = row.querySelector('[data-charge-col="discount"]');
        if (discountCell && data.discount != null) {
            discountCell.dataset.value = String(data.discount);
            discountCell.textContent = money(data.discount);
        }
        const totalCell = row.querySelector('.charge-total-cell');
        if (totalCell) {
            totalCell.dataset.value = String(data.total ?? 0);
            totalCell.innerHTML = `<b>${money(data.total)}</b>`;
        }
        applyTotalsSnapshot(data.totals);
        syncChargeOptionalColumns();
    }

    function bindClickOnce(id, handler) {
        const el = byId(id);
        if (!el || el.dataset.checkinBound === '1') return;
        el.dataset.checkinBound = '1';
        el.addEventListener('click', handler);
    }

    function bindGuestActionButtons() {
        bindClickOnce('updateGuest', () => updateGuest());
        bindClickOnce('guestProceedCheckIn', () => proceedFromGuestInfo().catch(e => message(e.message, 'error')));
    }

    function bindFixedActionButtons() {
        bindClickOnce('complete', () => completeCheckIn(false));
        bindClickOnce('undoCheckIn', undoCheckIn);
        bindClickOnce('checkOutAction', checkOut);
        bindClickOnce('printInvoice', () => openLegacyInvoice('CHECK-IN'));
    }

    function queueReloadToast(text, type = 'success') {
        if (!text) return;
        try { sessionStorage.setItem('oraCheckInReloadToast', JSON.stringify({ text, type })); } catch { }
    }

    function refreshReservationUi(targetRegId = regId, toastText = '', toastType = 'success') {
        // A successful mutation now performs one normal page navigation instead of
        // downloading the page through fetch, parsing a second DOM and patching many
        // fragments. This keeps the screen fully in sync with the database and removes
        // the extra partial-refresh processing path.
        if (!toastText) {
            const box = byId('message');
            toastText = box?.textContent?.trim() || '';
            if (box?.classList.contains('is-error')) toastType = 'error';
            else if (box?.classList.contains('is-info')) toastType = 'info';
            else if (box?.classList.contains('is-success')) toastType = 'success';
        }
        if (toastText) queueReloadToast(toastText, toastType);
        const url = new URL(window.location.href);
        url.searchParams.delete('RI');
        if (targetRegId) url.searchParams.set('q', targetRegId);
        window.location.assign(url.toString());
        return new Promise(() => { });
    }

    function showQueuedReloadToast() {
        try {
            const raw = sessionStorage.getItem('oraCheckInReloadToast');
            if (!raw) return;
            sessionStorage.removeItem('oraCheckInReloadToast');
            const item = JSON.parse(raw);
            if (item?.text) setTimeout(() => message(item.text, item.type || 'success'), 0);
        } catch { }
    }

    async function quickSaveGuestName() {
        if (!regId || state.canUpdateGuest === false) return true;
        const firstName = (byId('firstName')?.value || '').trim();
        const lastName = (byId('lastName')?.value || '').trim();
        if (!firstName) {
            byId('firstName')?.focus();
            message('Guest first name is required.', 'error');
            return false;
        }

        const snapshot = `${firstName}\u0000${lastName}`;
        if (snapshot === lastQuickNameSnapshot && !quickNameSaving) return true;
        if (quickNameSaving) {
            quickNameQueued = true;
            return true;
        }

        quickNameSaving = true;
        try {
            const result = await api(urls.updateGuestNameQuick, {
                method: 'POST',
                body: { regId, firstName, lastName }
            });
            if (!result.ok) {
                message(result.message || 'Unable to update guest name.', 'error');
                return false;
            }
            lastQuickNameSnapshot = snapshot;
            if (state.guest) {
                state.guest.firstName = firstName;
                state.guest.lastName = lastName;
            }
            return true;
        } catch (e) {
            message(e.message, 'error');
            return false;
        } finally {
            quickNameSaving = false;
            if (quickNameQueued) {
                quickNameQueued = false;
                if (currentNameSnapshot() !== lastQuickNameSnapshot) quickSaveGuestName();
            }
        }
    }

    function bindGuestKeyboardSaves() {
        ['firstName', 'lastName'].forEach((id, index) => {
            const input = byId(id);
            if (!input || input.dataset.quickNameBound === '1') return;
            input.dataset.quickNameBound = '1';
            input.addEventListener('change', () => quickSaveGuestName());
            input.addEventListener('keydown', async e => {
                if (e.key !== 'Enter') return;
                e.preventDefault();
                const ok = await quickSaveGuestName();
                if (ok) byId(index === 0 ? 'lastName' : 'phone')?.focus();
            });
        });

        // WebForms saved the remaining guest fields from Update Guest Info. Keep that
        // rule, but make Enter a fast keyboard shortcut to the same update action.
        ['phone', 'email', 'passportNo', 'vatNo', 'address'].forEach(id => {
            const input = byId(id);
            if (!input || input.dataset.guestEnterBound === '1') return;
            input.dataset.guestEnterBound = '1';
            input.addEventListener('keydown', e => {
                if (e.key !== 'Enter') return;
                e.preventDefault();
                updateGuest();
            });
        });
    }

    async function ensureGuestSaved() {
        if (regId) return true;
        if (!validateGuest()) return false;
        showBusy('Saving guest…');
        try {
            const result = await api(urls.saveGuest, { method: 'POST', body: { guest: guestPayload(), legacyReservationId: '' } });
            if (!result.ok) { message(result.message || 'Unable to save guest.', 'error'); return false; }
            regId = result.regId || regId;
            app.dataset.regId = regId;
            if (byId('guestSearch')) byId('guestSearch').value = regId;
            if (result.id) visitId = String(result.id);
            message(result.message || 'Guest saved.', 'success');
            return true;
        } finally { hideBusy(); }
    }

    async function saveGuest() {
        if (!validateGuest()) return;
        showBusy(regId ? 'Saving guest…' : 'Creating guest…');
        try {
            const endpoint = regId ? urls.updateGuest : urls.saveGuest;
            const body = regId ? { guest: guestPayload() } : { guest: guestPayload(), legacyReservationId: '' };
            const result = await api(endpoint, { method: 'POST', body });
            if (!result.ok) return message(result.message || 'Guest could not be saved.', 'error');
            regId = result.regId || regId;
            app.dataset.regId = regId;
            if (byId('guestSearch')) byId('guestSearch').value = regId;
            message(result.message || 'Guest saved successfully.', 'success');
            await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); }
        finally { hideBusy(); }
    }

    async function updateGuest() {
        if (!regId || !validateGuest()) return;

        const guest = guestPayload();
        const newArrival = byId('checkIn')?.value || '';
        const newDeparture = byId('checkOut')?.value || '';
        const datesChanged = newArrival !== loadedStayArrival || newDeparture !== loadedStayDeparture;
        const oldDep = parseIso(loadedStayDeparture);
        const nextDep = parseIso(newDeparture);
        const isExtension = datesChanged && oldDep && nextDep && nextDep > oldDep;

        let dateTaxSelection = { confirmed: false, applyGst: false, applyBedTax: false };
        if (isExtension && hasConfiguredDateChangeTax()) {
            const selected = await askDateChangeTaxSelection();
            if (!selected) return;
            dateTaxSelection = selected;
        }

        showBusy('Updating guest…');
        try {
            const result = await api(urls.updateGuest, {
                method: 'POST',
                body: {
                    guest,
                    dateTaxSelectionConfirmed: !!dateTaxSelection.confirmed,
                    applyGstToDateChange: !!dateTaxSelection.applyGst,
                    applyBedTaxToDateChange: !!dateTaxSelection.applyBedTax
                }
            });
            if (!result.ok) return message(result.message, 'error');
            lastQuickNameSnapshot = currentNameSnapshot();
            await refreshReservationUi(regId, result.message || 'Guest details updated successfully.', 'success');
        } catch (e) { message(e.message, 'error'); }
        finally { hideBusy(); }
    }

    async function proceedFromGuestInfo() {
        if (!(await ensureGuestSaved())) return;
        const roomCard = byId('roomsHeading')?.closest('.card');
        (roomCard || byId('chargeCategory'))?.scrollIntoView({ behavior: 'smooth', block: 'start' });
        setTimeout(() => byId('chargeCategory')?.focus(), 250);
    }

    function isServiceChargeMode() {
        return (byId('chargeCategory')?.value || '') === 'Services';
    }

    function isRoomChargeMode() {
        const value = byId('chargeCategory')?.value || '';
        return !!value && value !== 'Services';
    }

    function selectedRoomCategoryId() {
        const option = byId('chargeCategory')?.selectedOptions?.[0];
        return option?.dataset.categoryId || option?.value || '';
    }

    function currentChargeDescription() {
        return isRoomChargeMode() ? 'Room Rent' : 'Extras';
    }

    function currentChargeType() {
        return isRoomChargeMode() ? (byId('chargeCategory')?.value || '') : 'Others';
    }

    function clearRoomPlan() {
        if (byId('chargeRoom')) byId('chargeRoom').innerHTML = '<option value="">--Select--</option>';
        if (byId('ratePlan')) byId('ratePlan').innerHTML = '<option value="">--Select--</option>';
        if (byId('chargeTotal')) byId('chargeTotal').value = '';
    }

    function updateChargeMode() {
        const category = byId('chargeCategory')?.value || '';
        const serviceMode = category === 'Services';
        const roomMode = !!category && !serviceMode;
        const entry = byId('chargeEntry');

        entry?.classList.toggle('service-mode', serviceMode);
        byId('chargeDetailWrap')?.classList.toggle('hidden', !serviceMode);
        byId('chargeOptionsRow')?.classList.toggle('service-mode-options', serviceMode);
        byId('chargeRoomWrap')?.classList.toggle('hidden', !roomMode);
        byId('ratePlanWrap')?.classList.toggle('hidden', !roomMode || app.dataset.monthWise === '1');
        byId('promoCodeWrap')?.classList.add('hidden');
        if (byId('promoCode')) byId('promoCode').value = '';
        byId('monthlyRateWrap')?.classList.toggle('hidden', !roomMode || app.dataset.monthWise !== '1');

        if (!serviceMode && byId('chargeDetail')) byId('chargeDetail').value = '';

        if (!roomMode) clearRoomPlan();
        applyReservationMode();
    }

    async function loadChargeTypes() {
        const description = byId('chargeDescription')?.value || '';
        const type = byId('chargeType');
        if (!type) return;
        type.innerHTML = '<option value="">--Select--</option>';
        if (!isServiceChargeMode() || !description) return;

        const items = await api(`${urls.chargeTypes}?description=${encodeURIComponent(description)}`);
        (Array.isArray(items) ? items : []).forEach(x => {
            type.insertAdjacentHTML('beforeend', `<option value="${esc(x.text || x.value)}" data-category-id="${esc(x.meta2 || x.value)}" data-extra="${esc(x.meta || '')}">${esc(x.text || x.value)}</option>`);
        });
    }

    async function loadRoomsAndPlans() {
        if (!isRoomChargeMode()) { clearRoomPlan(); return; }

        const category = byId('chargeCategory')?.value || '';
        if (!category) { clearRoomPlan(); return; }
        const categoryId = selectedRoomCategoryId();
        const lookupKeys = [...new Set([categoryId, category].filter(Boolean))];

        const arrival = byId('chargeArrival')?.value || byId('checkIn')?.value || '';
        const departure = byId('chargeDeparture')?.value || byId('checkOut')?.value || '';
        if (!arrival || !departure) {
            if (byId('chargeRoom')) byId('chargeRoom').innerHTML = '<option value="">Select stay dates first</option>';
            return;
        }

        const room = byId('chargeRoom');
        const plan = byId('ratePlan');
        if (room) room.innerHTML = '<option value="">Loading rooms…</option>';
        if (plan) plan.innerHTML = '<option value="">Loading rate plans…</option>';

        // Load rooms independently from rate plans. A rate-plan lookup problem must
        // never stop the Room No. dropdown from being populated.
        try {
            let list = [];
            for (const key of lookupKeys) {
                const rooms = await api(`${urls.rooms}?category=${encodeURIComponent(key)}&arrival=${encodeURIComponent(arrival)}&departure=${encodeURIComponent(departure)}&regId=${encodeURIComponent(regId)}`);
                list = Array.isArray(rooms) ? rooms : [];
                if (list.length) break;
            }
            if (room) {
                room.innerHTML = '<option value="">--Select--</option>';
                list.forEach(x => room.insertAdjacentHTML('beforeend', `<option value="${esc(x.value)}" data-category-id="${esc(x.meta2 || categoryId)}" data-room-status="${esc(x.meta || '')}">${esc(x.text || x.value)}</option>`));
                if (!list.length) room.insertAdjacentHTML('beforeend', '<option value="" disabled>No available rooms</option>');
            }
        } catch (e) {
            if (room) room.innerHTML = '<option value="">Unable to load rooms</option>';
            message(e.message || 'Unable to load room numbers.', 'error');
        }

        try {
            let list = [];
            for (const key of lookupKeys) {
                const plans = await api(`${urls.ratePlans}?category=${encodeURIComponent(key)}`);
                list = Array.isArray(plans) ? plans : [];
                if (list.length) break;
            }
            if (plan) {
                plan.innerHTML = '<option value="">--Select--</option>';
                list.forEach(x => plan.insertAdjacentHTML('beforeend', `<option value="${esc(x.value)}" data-rate="${Number(x.amount || 0)}" data-category-id="${esc(x.meta || categoryId)}">${esc(x.text || x.value)}</option>`));
                if (list.length === 1) {
                    plan.selectedIndex = 1;
                    await quoteRate();
                }
            }
        } catch (e) {
            if (plan) plan.innerHTML = '<option value="">Unable to load rate plans</option>';
            // Room selection remains usable even when rate-plan configuration is incomplete.
        }

        if (app.dataset.monthWise === '1') await quoteRate();
    }

    async function quoteRate() {
        if (!isRoomChargeMode()) return;
        const plan = byId('ratePlan');
        const categoryId = selectedRoomCategoryId();
        const planId = app.dataset.monthWise === '1' ? 'Monthly' : (plan?.value || '');
        if (!categoryId || (app.dataset.monthWise !== '1' && !planId)) return;
        const arrival = byId('chargeArrival')?.value || byId('checkIn')?.value || '';
        const departure = byId('chargeDeparture')?.value || byId('checkOut')?.value || '';
        if (!arrival || !departure) return;
        const result = await api(urls.rateQuote, {
            method: 'POST',
            body: {
                regId,
                categoryId,
                planId,
                arrivalDate: arrival,
                departureDate: departure,
                monthWise: app.dataset.monthWise === '1',
                monthlyRate: toNumber(byId('monthlyRate')?.value)
            }
        });
        if (byId('chargeTotal')) byId('chargeTotal').value = toNumber(result.total).toFixed(2);
        if (byId('chargeNights')) byId('chargeNights').value = result.stayCount || updateChargeStayCount();
    }

    async function addCharge() {
        if (!(await ensureGuestSaved())) return;

        const isRoom = isRoomChargeMode();
        const isService = isServiceChargeMode();
        if (!isRoom && !isService) return message('Select a category.', 'error');

        const description = currentChargeDescription();
        const category = currentChargeType();
        const detail = byId('chargeDetail')?.value.trim() || '';
        if (isService && !detail) return message('Enter service / charge details.', 'error');
        if (isRoom && !byId('chargeRoom')?.value) return message('Select a room number.', 'error');
        if (isRoom && app.dataset.monthWise !== '1' && !byId('ratePlan')?.value) return message('Select a rate plan.', 'error');

        const amount = toNumber(byId('chargeTotal')?.value);
        if (amount === 0 && !['Complementary'].includes(byId('paymentMethod')?.value || '')) {
            const proceed = await askConfirm('Zero Amount', 'The selected charge total is 0. Continue?', 'Continue');
            if (!proceed) return;
        }

        showBusy(isRoom ? 'Adding room…' : 'Adding service…');
        try {
            const plan = byId('ratePlan');
            const arrival = byId('chargeArrival')?.value || byId('checkIn')?.value;
            const departure = byId('chargeDeparture')?.value || byId('checkOut')?.value;
            const result = await api(urls.addCharge, {
                method: 'POST',
                body: {
                    regId,
                    visitId,
                    description,
                    category,
                    typeValue: category,
                    deductionInfo: detail,
                    roomNo: isRoom ? (byId('chargeRoom')?.value || '') : '',
                    ratePlanId: isRoom && app.dataset.monthWise !== '1' ? (plan?.value || '') : (isRoom ? 'Monthly' : ''),
                    ratePlanName: isRoom && app.dataset.monthWise !== '1' ? (plan?.selectedOptions?.[0]?.text || '') : (isRoom ? 'Monthly' : ''),
                    promoCode: '',
                    arrivalDate: arrival,
                    departureDate: departure,
                    rate: amount,
                    numberOfRooms: 1,
                    discount: toNumber(byId('discount')?.value),
                    applyGst: !!byId('gst')?.checked,
                    applyBedTax: !!byId('bedTax')?.checked,
                    reservationDateMode: dateMode(),
                    monthlyRate: toNumber(byId('monthlyRate')?.value)
                }
            });
            if (!result.ok) return message(result.message, 'error');
            message(result.message, 'success');
            await refreshReservationUi(regId);
            if (result.id) {
                const added = document.querySelector(`tr[data-charge-id="${Number(result.id)}"]`);
                if (added) {
                    added.classList.add('row-attention');
                    added.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                    const pending = added.querySelector('.room-check');
                    if (pending) {
                        pending.checked = true;
                        setTimeout(() => pending.focus({ preventScroll: true }), 250);
                    }
                    setTimeout(() => added.classList.remove('row-attention'), 1800);
                }
            }
        } catch (e) { message(e.message, 'error'); }
        finally { hideBusy(); }
    }

    function startInlineChargeEdit(row, mode) {
        if (!row) return;
        const paymentId = Number(row.dataset.chargeId || 0);
        if (!paymentId) return;
        const cell = mode === 'rate' ? row.querySelector('.rate-cell') : row.querySelector('.guest-cell');
        if (!cell || cell.querySelector('.inline-cell-editor')) return;

        const originalHtml = cell.innerHTML;
        const originalValue = cell.dataset.value || (mode === 'rate' ? '0' : (cell.textContent.trim() === '—' ? '' : cell.textContent.trim()));
        const editor = document.createElement('div');
        editor.className = 'inline-cell-editor';
        editor.innerHTML = `<input class="inline-cell-input" ${mode === 'rate' ? 'type="number" min="0" step="0.01"' : 'type="text" maxlength="100"'}><button type="button" class="inline-cell-save" title="Save"><i class="fa-solid fa-check"></i></button><button type="button" class="inline-cell-cancel" title="Cancel"><i class="fa-solid fa-xmark"></i></button>`;
        cell.innerHTML = '';
        cell.appendChild(editor);
        const input = editor.querySelector('input');
        input.value = originalValue;
        input.focus();
        input.select();

        const cancel = () => { cell.innerHTML = originalHtml; };
        const save = async () => {
            const value = input.value.trim();
            if (mode === 'rate' && toNumber(value) < 0) return message('Enter a valid rate.', 'error');
            if (mode === 'guest-name' && !value) return message('Guest name cannot be empty.', 'error');
            showBusy(mode === 'rate' ? 'Updating rate…' : 'Updating room guest…');
            try {
                const result = mode === 'rate'
                    ? await api(urls.updateRate, { method: 'POST', body: { regId, paymentId, rate: toNumber(value) } })
                    : await api(urls.updateGuestName, { method: 'POST', body: { regId, paymentId, guestName: value } });
                if (!result.ok) return message(result.message, 'error');
                if (mode === 'rate') {
                    applyInlineRateResult(row, result.data);
                } else {
                    cell.dataset.value = value;
                    cell.textContent = value || '—';
                }
                await refreshReservationUi(regId, result.message || 'Updated successfully.', 'success');
            } catch (e) { message(e.message, 'error'); }
            finally { hideBusy(); }
        };
        editor.querySelector('.inline-cell-save')?.addEventListener('click', save);
        editor.querySelector('.inline-cell-cancel')?.addEventListener('click', cancel);
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter') { e.preventDefault(); save(); }
            if (e.key === 'Escape') { e.preventDefault(); cancel(); }
        });
    }

    function selectRoomChangeChoice(button) {
        const value = button?.dataset.room || '';
        if (!value) return;
        $$('.reservation-room-option', byId('roomChangeChoices')).forEach(x => {
            const selected = x === button;
            x.classList.toggle('is-selected', selected);
            x.setAttribute('aria-pressed', selected ? 'true' : 'false');
        });
        if (byId('roomChangeSelected')) byId('roomChangeSelected').value = value;
        const save = byId('roomChangeSave');
        if (save) {
            save.disabled = false;
            save.classList.remove('is-disabled');
        }
    }

    async function openRoomChangeForRow(row, paymentId) {
        if (!row || !paymentId) return;
        showBusy('Loading available rooms…');
        try {
            const items = await api(`${urls.roomChangeOptions}?paymentId=${paymentId}`);
            const choices = byId('roomChangeChoices');
            const empty = byId('roomChangeEmpty');
            const save = byId('roomChangeSave');
            if (choices) choices.innerHTML = '';
            if (byId('roomChangePaymentId')) byId('roomChangePaymentId').value = String(paymentId);
            if (byId('roomChangeSelected')) byId('roomChangeSelected').value = '';
            if (byId('roomChangeCurrent')) byId('roomChangeCurrent').textContent = `Room ${row.dataset.room || '—'}`;
            if (save) { save.disabled = true; save.classList.add('is-disabled'); }

            const rooms = Array.isArray(items) ? items : [];
            if (empty) empty.classList.toggle('hidden', rooms.length > 0);

            rooms.forEach(x => {
                const room = String(x.value || x.text || '').trim();
                if (!room || !choices) return;
                choices.insertAdjacentHTML('beforeend', `
          <button type="button" class="reservation-room-option" data-room="${esc(room)}" aria-pressed="false">
            <span class="reservation-room-option-icon"><i class="fa-solid fa-bed"></i></span>
            <span class="reservation-room-option-main">
              <span class="reservation-room-option-number">Room ${esc(room)}</span>
              <span class="reservation-room-option-status">Available</span>
            </span>
          </button>`);
            });
            choices?.querySelectorAll('.reservation-room-option').forEach(btn =>
                btn.addEventListener('click', () => selectRoomChangeChoice(btn)));

            openModal('roomChangeModal');
        } catch (e) {
            message(e.message || 'Unable to load available rooms.', 'error');
        } finally {
            hideBusy();
        }
    }

    async function chargeRowAction(button, row) {
        const id = Number(row.dataset.chargeId || 0);
        const action = button.dataset.action;
        if (!id || !action) return;
        if (action === 'delete') {
            showBusy('Deleting charge…');
            try {
                const r = await api(urls.deleteCharge, { method: 'POST', body: { regId, paymentId: id } });
                if (!r.ok) return message(r.message, 'error');
                row.remove();
                message(r.message || 'Charge deleted.', 'success');
                await refreshReservationUi(regId);
            } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
            return;
        }
        if (action === 'rate') {
            startInlineChargeEdit(row, 'rate');
            return;
        }
        if (action === 'guest-name') {
            startInlineChargeEdit(row, 'guest-name');
            return;
        }
        if (action === 'change-room') {
            await openRoomChangeForRow(row, id);
        }
    }

    async function saveRoomChange() {
        const paymentId = Number(byId('roomChangePaymentId')?.value || 0);
        const newRoomNo = byId('roomChangeSelected')?.value || '';
        if (!paymentId || !newRoomNo) return message('Please select an available room.', 'error');

        const save = byId('roomChangeSave');
        if (save) save.disabled = true;
        showBusy('Changing room…');
        try {
            const r = await api(urls.changeRoom, { method: 'POST', body: { regId, paymentId, newRoomNo } });
            if (!r.ok) {
                if (save) save.disabled = false;
                return message(r.message || 'The room could not be changed.', 'error');
            }

            const changedRow = document.querySelector(`tr[data-charge-id="${paymentId}"]`);
            if (changedRow) {
                changedRow.dataset.room = r.data?.newRoom || newRoomNo;
                const roomText = changedRow.querySelector('.room-number-text');
                if (roomText) roomText.textContent = r.data?.newRoom || newRoomNo;
            }

            closeModal('roomChangeModal');
            message(r.message || 'Room changed successfully.', 'success');
            // Room assignment does not change rates/totals, so avoid a full page reload.
            // This mirrors the WebForms UpdatePanel feel and makes the action immediate.
        } catch (e) {
            if (save) save.disabled = false;
            message(e.message || 'The room could not be changed.', 'error');
        } finally {
            hideBusy();
        }
    }

    function selectedCheckInRows() {
        const checks = $$('.room-check:checked');
        return {
            ids: checks.map(x => Number(x.value)).filter(Boolean),
            rooms: checks.map(x => x.dataset.room || '').filter(Boolean)
        };
    }

    async function completeCheckIn(saveAndPrint = false) {
        if (!(await ensureGuestSaved()) || !validateGuest()) return;
        const selected = selectedCheckInRows();
        if (!selected.ids.length && !selected.rooms.length) return message('Select at least one Room Rent row to check in.', 'error');
        let method = byId('paymentMethod')?.value || '';
        if (byId('complementary')?.checked) method = 'Complementary';
        const paid = toNumber(byId('paidAmount')?.value);
        if (paid > 0 && !method) return message('Select a payment method.', 'error');
        showBusy(saveAndPrint ? 'Checking in and preparing invoice…' : 'Completing check-in…');
        try {
            const r = await api(urls.completeCheckIn, {
                method: 'POST',
                body: {
                    guest: guestPayload(),
                    selectedChargeIds: selected.ids,
                    selectedRoomNos: selected.rooms,
                    paymentMethod: method,
                    paidAmount: paid,
                    roomSecurity: 0,
                    securityNote: '',
                    completeCheckIn: true,
                    saveAndPrint,
                    postAndPrint: false,
                    postPrintPaymentMethod: ''
                }
            });
            if (!r.ok) return message(r.message, 'error');
            regId = r.regId || regId;
            message(r.message || 'Check-in completed.', 'success');
            if (saveAndPrint && r.redirectUrl) window.open(r.redirectUrl, '_blank', 'noopener');
            await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function recordPayment() {
        if (!regId) return message('Save or load a guest first.', 'error');

        const amountInput = byId('paidAmount');
        const button = byId('recordPayment');
        const amount = toNumber(amountInput?.value);
        const method = (byId('paymentMethod')?.value || '').trim();
        const isComplementary = method.toLowerCase() === 'complementary';

        if (!method) return message('Select a payment method.', 'error');
        if (!isComplementary && amount <= 0) {
            amountInput?.focus();
            return message('Enter a paid amount greater than 0.', 'error');
        }

        if (button) button.disabled = true;
        showBusy('Recording payment…');
        try {
            const r = await api(urls.recordPayment, {
                method: 'POST',
                body: {
                    regId,
                    visitId,
                    amount: isComplementary ? 0 : amount,
                    method,
                    roomSecurity: 0,
                    note: ''
                }
            });
            if (!r?.ok) return message(r?.message || 'Unable to record payment.', 'error');

            if (amountInput) amountInput.value = '0';
            message(r.message || 'Payment recorded successfully.', 'success');
            await refreshReservationUi(regId);
        } catch (e) {
            message(e.message || 'Unable to record payment.', 'error');
        } finally {
            hideBusy();
            if (button) button.disabled = false;
        }
    }

    function openRefundPaymentModal(logId, refundableAmount) {
        const amount = Math.max(0, toNumber(refundableAmount));
        if (!logId || amount <= 0) return message('This payment has no refundable balance.', 'error');
        byId('refundPaymentLogId').value = String(logId);
        byId('refundPaymentAmount').value = amount.toFixed(2);
        byId('refundPaymentAmount').max = amount.toFixed(2);
        byId('refundPaymentAmount').dataset.maxRefund = amount.toFixed(2);
        byId('refundPaymentReason').value = '';
        byId('refundPaymentMax').textContent = `Maximum refundable amount: ${money(amount)}`;
        openModal('refundPaymentModal');
        setTimeout(() => byId('refundPaymentAmount')?.focus(), 80);
    }

    async function submitRefundPayment() {
        const logId = Number(byId('refundPaymentLogId')?.value || 0);
        const amountEl = byId('refundPaymentAmount');
        const reasonEl = byId('refundPaymentReason');
        const amount = toNumber(amountEl?.value);
        const max = toNumber(amountEl?.dataset.maxRefund);
        const reason = reasonEl?.value.trim() || '';
        if (!logId) return message('Payment record is missing.', 'error');
        if (amount <= 0 || amount > max + 0.00001) {
            message(`Enter a refund amount between ${money(0.01)} and ${money(max)}.`, 'error');
            amountEl?.focus();
            return;
        }
        if (!reason) {
            message('Refund reason is required.', 'error');
            reasonEl?.focus();
            return;
        }

        const submit = byId('refundPaymentSubmit');
        if (submit) submit.disabled = true;
        showBusy('Processing refund…');
        try {
            const r = await api(urls.refundPayment, { method: 'POST', body: { regId, logId, amount, reason } });
            if (!r.ok) return message(r.message, 'error');
            closeModal('refundPaymentModal');
            message(r.message, 'success');
            await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); }
        finally {
            hideBusy();
            if (submit) submit.disabled = false;
        }
    }

    async function showPaymentAudit(id) {
        showBusy('Loading payment audit…');
        try {
            const data = await api(`${urls.paymentAudit}?id=${id}`);
            byId('auditContent').textContent = JSON.stringify(data, null, 2);
            openModal('auditModal');
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function undoCheckIn() {
        if (!regId) return;
        if (!(await askConfirm('Undo Check-In', 'Return this checked-in guest to Reservation status? Amounts and payment history are not removed.', 'Undo Check-In'))) return;
        showBusy('Undoing check-in…');
        try {
            const r = await api(urls.undoCheckIn, { method: 'POST', body: { regId } });
            if (!r.ok) return message(r.message, 'error');
            message(r.message, 'success'); await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function checkOut() {
        if (!regId) return;
        const method = byId('paymentMethod')?.value || '';
        if (!(await askConfirm('Check-Out', 'Check out the currently checked-in room(s)? Existing WebForms payment/security validation is applied on the server.', 'Check-Out'))) return;
        showBusy('Checking out…');
        try {
            const r = await api(urls.checkOut, { method: 'POST', body: { regId, paymentMethod: method, force: false } });
            if (!r.ok) return message(r.message, 'error');
            message(r.message, 'success');
            if (r.redirectUrl) window.open(r.redirectUrl, '_blank', 'noopener');
            await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function extendReservation() {
        const date = byId('newDeparture')?.value || '';
        if (!date) return message('Select a new departure date.', 'error');
        showBusy('Updating stay dates…');
        try {
            const r = await api(urls.extend, { method: 'POST', body: { regId, newDepartureDate: date, recalculateRates: false } });
            if (!r.ok) return message(r.message, 'error');
            closeModal('extendModal'); message(r.message, 'success'); await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function loadLaundryCategories() {
        const items = await api(urls.laundryCategories);
        const select = byId('laundryCategory');
        select.innerHTML = '<option value="">--Select--</option>';
        (Array.isArray(items) ? items : []).forEach(x => select.insertAdjacentHTML('beforeend', `<option value="${esc(x.value)}">${esc(x.text)}</option>`));
    }

    async function loadLaundryItems() {
        const category = byId('laundryCategory')?.value || '';
        const select = byId('laundryItem');
        select.innerHTML = '<option value="">--Select--</option>';
        byId('laundrySubCategory').value = '';
        byId('laundryRate').value = '';
        byId('laundryAmount').value = '';
        if (!category) return;
        const items = await api(`${urls.laundryItems}?category=${encodeURIComponent(category)}`);
        (Array.isArray(items) ? items : []).forEach(x => select.insertAdjacentHTML('beforeend', `<option value="${esc(x.value)}" data-rate="${Number(x.amount || 0)}" data-meta="${esc(x.meta || '')}">${esc(x.text)}</option>`));
    }

    function updateLaundryAmount() {
        const opt = byId('laundryItem')?.selectedOptions?.[0];
        const rate = Number(opt?.dataset.rate || 0);
        const qty = Math.max(1, parseInt(byId('laundryQty')?.value || '1', 10) || 1);
        byId('laundryRate').value = rate.toFixed(2);
        byId('laundrySubCategory').value = opt?.dataset.meta || '';
        byId('laundryAmount').value = (rate * qty).toFixed(2);
    }

    async function addLaundry() {
        if (!regId) return message('Save or load a guest first.', 'error');
        const category = byId('laundryCategory')?.value || '';
        const item = byId('laundryItem')?.value || '';
        if (!category || !item) return message('Select laundry category and item.', 'error');
        showBusy('Adding laundry…');
        try {
            const r = await api(urls.addLaundry, { method: 'POST', body: { regId, visitId, category, item, quantity: Math.max(1, Number(byId('laundryQty')?.value || 1)), rate: toNumber(byId('laundryRate')?.value) } });
            if (!r.ok) return message(r.message, 'error');
            message(r.message, 'success'); await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function deleteLaundry(id) {
        if (!(await askConfirm('Delete Laundry', 'Delete this laundry item?', 'Delete'))) return;
        showBusy('Deleting laundry…');
        try {
            const r = await api(urls.deleteLaundry, { method: 'POST', body: { regId, id } });
            if (!r.ok) return message(r.message, 'error');
            message(r.message, 'success'); await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function applyDiscount() {
        if (!regId) return message('Save or load a guest first.', 'error');
        const code = byId('discountCode')?.value.trim() || '';
        if (!code) return message('Enter a discount code.', 'error');
        showBusy('Applying discount…');
        try {
            const r = await api(urls.applyDiscount, { method: 'POST', body: { regId, code } });
            if (!r.ok) return message(r.message, 'error');
            message(r.message, 'success'); await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    function setSecurityPaymentMethod(method) {
        method = String(method || '').toLowerCase();
        if (!['card', 'manualcard', 'cash'].includes(method)) method = app.dataset.stripe === '1' ? 'card' : 'cash';
        const locked = String(byId('securityLockedMethod')?.value || '').toLowerCase();
        if ((byId('securityMovement')?.value || '') === 'refund' && locked) method = locked;

        if (byId('securityMethod')) byId('securityMethod').value = method;
        $$('[data-security-method]').forEach(btn => {
            btn.classList.toggle('active', btn.dataset.securityMethod === method);
            btn.disabled = !!locked && btn.dataset.securityMethod !== method;
        });
        byId('securityReaderWrap')?.classList.toggle('hidden', method !== 'card' || (byId('securityMovement')?.value || '') === 'refund');

        const msg = byId('securityActionStatusMsg');
        if (msg && (byId('securityMovement')?.value || '') !== 'refund') {
            msg.textContent = method === 'cash'
                ? 'Enter deposit amount and press Charge Now.'
                : (method === 'manualcard'
                    ? 'Enter deposit amount and continue to Stripe secure card pre-authorization.'
                    : 'Select reader, enter deposit amount and press Charge Now.');
        }
    }

    function setSecurityMode(mode, row = null) {
        const settle = mode === 'settle';
        if (settle && !pendingSecurityId && !row) {
            message('Select Refund / Deduct from a security deposit row first.', 'info');
            mode = 'deposit';
        }
        const isSettle = mode === 'settle';
        if (byId('securityMovement')) byId('securityMovement').value = isSettle ? 'refund' : 'deposit';
        byId('securityDepositFields')?.classList.toggle('hidden', isSettle);
        byId('securitySettleFields')?.classList.toggle('hidden', !isSettle);
        byId('securityPaymentMethodArea')?.classList.toggle('hidden', isSettle);
        byId('securityDepositTab')?.classList.toggle('active', !isSettle);
        byId('securitySettleTab')?.classList.toggle('active', isSettle);
        if (byId('securityModalTitle')) byId('securityModalTitle').textContent = isSettle ? 'Security Refund / Deduct' : 'Room Security';
        if (byId('saveSecurity')) byId('saveSecurity').textContent = isSettle ? 'Settle Now' : 'Charge Now';
        if (byId('securityActionStatusTitle')) byId('securityActionStatusTitle').textContent = 'Ready';
        if (byId('securityActionStatusMsg')) byId('securityActionStatusMsg').textContent =
            isSettle ? 'Enter refund amount. Remaining amount will be deducted.' : 'Select method and press Charge Now.';

        if (!isSettle) {
            if (byId('securityLockedMethod')) byId('securityLockedMethod').value = '';
            setSecurityPaymentMethod(byId('securityMethod')?.value || (app.dataset.stripe === '1' ? 'card' : 'cash'));
        }
        updateSecurityDeductionPreview();
    }

    function updateSecurityDeductionPreview() {
        const preview = byId('securityDeductionPreview');
        if (!preview) return;
        const settle = (byId('securityMovement')?.value || '') === 'refund' && pendingSecurityId > 0;
        preview.classList.toggle('hidden', !settle);
        if (!settle) { preview.textContent = ''; return; }
        const current = Math.max(0, toNumber(byId('securityCurrentAmount')?.value));
        const refund = Math.max(0, Math.min(current, toNumber(byId('securityRefundAmount')?.value)));
        preview.textContent = `Refund: ${money(refund)} · Security Deduction: ${money(Math.max(0, current - refund))}`;
    }

    async function saveSecurity() {
        if (!regId) return message('Save or load a guest first.', 'error');

        const movement = byId('securityMovement')?.value || 'deposit';
        const isSettlement = movement === 'refund' && pendingSecurityId > 0;

        if (isSettlement) {
            const current = Math.max(0, toNumber(byId('securityCurrentAmount')?.value));
            const refundAmount = Math.max(0, toNumber(byId('securityRefundAmount')?.value));
            const note = byId('securitySettleNote')?.value.trim() || '';
            if (!note) return message('Additional info is required.', 'error');
            if (current <= 0) return message('The selected security deposit is not available for settlement.', 'error');
            if (refundAmount > current + 0.00001) return message('Refund cannot exceed current room security.', 'error');

            showBusy('Settling room security…');
            try {
                const r = await api(urls.securityMovement, {
                    method: 'POST',
                    body: {
                        regId, visitId, amount: refundAmount, note,
                        method: pendingSecurityMethod,
                        movement: 'refund',
                        securityId: pendingSecurityId,
                        paymentIntentId: pendingSecurityPaymentIntentId,
                        chargeId: pendingSecurityChargeId
                    }
                });
                if (!r.ok) return message(r.message || 'Unable to settle room security.', 'error');
                pendingSecurityId = 0;
                pendingSecurityMethod = '';
                pendingSecurityPaymentIntentId = '';
                pendingSecurityChargeId = '';
                closeModal('securityModal');
                await refreshReservationUi(regId, r.message || 'Room security settled successfully.', 'success');
            } catch (e) {
                message(e.message || 'Unable to settle room security.', 'error');
            } finally {
                hideBusy();
            }
            return;
        }

        const amount = toNumber(byId('securityAmount')?.value);
        const note = byId('securityNote')?.value.trim() || '';
        const method = String(byId('securityMethod')?.value || 'cash').toLowerCase();

        if (!note) return message('Additional info is required.', 'error');
        if (amount <= 0) return message('Enter a security amount greater than 0.', 'error');

        if (method === 'manualcard') {
            if (app.dataset.stripe !== '1') return message('Stripe is not configured for this hotel.', 'error');
            showBusy('Opening Stripe pre-authorization…');
            try {
                await startManualStripeCheckout(amount, note, true);
                closeModal('securityModal');
            } catch (e) {
                message(e.message || 'Unable to start card pre-authorization.', 'error');
            } finally {
                hideBusy();
            }
            return;
        }

        if (method === 'card') {
            if (app.dataset.stripe !== '1') return message('Stripe Terminal is not configured for this hotel.', 'error');
            const reader = byId('securityReader')?.value || '';
            if (!reader) return message('Select a reader.', 'error');
            if (byId('cardAmount')) byId('cardAmount').value = amount.toFixed(2);
            if (byId('cardNote')) byId('cardNote').value = note;
            if (byId('cardReader')) byId('cardReader').value = reader;
            currentCardProvider = 'stripe';
            currentCardMode = 'terminal';
            cardSecurityMode = true;
            currentPaymentIntentId = '';
            closeModal('securityModal');
            openModal('cardModal');
            return;
        }

        showBusy('Posting cash security deposit…');
        try {
            const r = await api(urls.securityMovement, {
                method: 'POST',
                body: {
                    regId, visitId, amount, note,
                    method: 'Cash', movement: 'deposit', securityId: 0,
                    paymentIntentId: '', chargeId: ''
                }
            });
            if (!r.ok) return message(r.message || 'Unable to save room security.', 'error');
            closeModal('securityModal');
            await refreshReservationUi(regId, r.message || 'Room security updated.', 'success');
        } catch (e) {
            message(e.message || 'Unable to save room security.', 'error');
        } finally {
            hideBusy();
        }
    }

    async function settleSecurity(id) {
        const row = document.querySelector(`tr[data-security-id="${id}"]`);
        const amount = Math.max(0, toNumber(row?.dataset.securityAmount));
        if (!row || amount <= 0) return message('This security deposit is not available for settlement.', 'error');

        pendingSecurityId = id;
        pendingSecurityPaymentIntentId = row.dataset.securityPi || '';
        pendingSecurityChargeId = row.dataset.securityCharge || '';
        pendingSecurityMethod = row.dataset.securityMethod || '';

        const isCard = !!pendingSecurityPaymentIntentId || !!pendingSecurityChargeId ||
            /card|pdq|stripe/i.test(pendingSecurityMethod);
        if (byId('securityLockedMethod')) byId('securityLockedMethod').value = isCard ? 'card' : 'cash';
        if (byId('securityCurrentAmount')) byId('securityCurrentAmount').value = amount.toFixed(2);
        if (byId('securityRefundAmount')) byId('securityRefundAmount').value = amount.toFixed(2);
        if (byId('securitySettleNote')) byId('securitySettleNote').value = '';

        setSecurityMode('settle', row);
        openModal('securityModal');
        setTimeout(() => byId('securityRefundAmount')?.focus(), 0);
    }

    function openSecurityDeposit() {
        pendingSecurityId = 0;
        pendingSecurityMethod = '';
        pendingSecurityPaymentIntentId = '';
        pendingSecurityChargeId = '';
        if (byId('securityAmount')) byId('securityAmount').value = '';
        if (byId('securityCurrentAmount')) byId('securityCurrentAmount').value = '';
        if (byId('securityRefundAmount')) byId('securityRefundAmount').value = '';
        if (byId('securityNote')) byId('securityNote').value = '';
        if (byId('securitySettleNote')) byId('securitySettleNote').value = '';
        setSecurityMode('deposit');
        openModal('securityModal');
    }

    function setTerminalStatus(text, detail = '') {
        const box = byId('cardTerminalStatus');
        if (!box) return;
        const b = $('b', box), s = $('span', box);
        if (b) b.textContent = text;
        if (s) s.textContent = detail;
    }


    function setCardMode(mode) {
        currentCardMode = mode === 'manual' ? 'manual' : 'terminal';
        $$('[data-card-mode]').forEach(x => x.classList.toggle('active', x.dataset.cardMode === currentCardMode));
        byId('terminalCardArea')?.classList.toggle('hidden', currentCardMode !== 'terminal');
        byId('manualCardArea')?.classList.toggle('hidden', currentCardMode !== 'manual');
        byId('cardSimulationWrap')?.classList.toggle('hidden', currentCardMode === 'manual');

        const chargeButton = byId('chargeCardNow');
        if (chargeButton) {
            chargeButton.innerHTML = currentCardMode === 'manual'
                ? '<i class="fa-brands fa-stripe"></i> Open Stripe Checkout'
                : '<i class="fa-solid fa-credit-card"></i> Charge Now';
        }

        if (currentCardMode === 'manual') {
            setTerminalStatus('Ready', 'Enter the amount and continue to secure Stripe Checkout.');
        } else {
            setTerminalStatus('Ready', currentCardProvider === 'clover'
                ? 'Enter amount and send it to the Clover PDQ device.'
                : 'Select a reader, enter amount and send it to the Stripe terminal.');
        }
    }

    function stopManualCheckoutPoll() {
        if (manualCheckoutPoll) {
            clearInterval(manualCheckoutPoll);
            manualCheckoutPoll = 0;
        }
    }

    async function completeManualStripePayment(result, amount, note, securityHold = false) {
        stopManualCheckoutPoll();
        try {
            if (manualCheckoutWindow && !manualCheckoutWindow.closed) manualCheckoutWindow.close();
        } catch (_) { }
        manualCheckoutWindow = null;
        manualCheckoutContext = null;

        const actualAmount = toNumber(result?.amount) > 0 ? toNumber(result.amount) : amount;

        if (securityHold) {
            const hold = await api(urls.securityMovement, {
                method: 'POST',
                body: {
                    regId, visitId, amount: actualAmount,
                    note: note || 'Card security pre-authorization',
                    method: 'Card Pre-Authorization',
                    movement: 'deposit',
                    securityId: 0,
                    paymentIntentId: result.paymentIntentId || '',
                    chargeId: result.chargeId || ''
                }
            });
            if (!hold.ok) {
                setTerminalStatus('Authorized', 'Stripe authorization succeeded, but the PMS security row could not be saved.');
                return message(hold.message || 'Security pre-authorization succeeded but could not be recorded in PMS.', 'error');
            }
            setTerminalStatus('Authorized', 'Security deposit pre-authorization completed and recorded.');
            message('Security deposit pre-authorization recorded successfully.', 'success');
            closeModal('securityModal');
            closeModal('cardModal');
            await refreshReservationUi(regId);
            return;
        }

        const save = await api(urls.recordPayment, {
            method: 'POST',
            body: {
                regId, visitId, amount: actualAmount,
                method: 'Card Payment',
                roomSecurity: 0,
                note: note || 'Stripe Checkout card payment',
                paymentId: result.paymentIntentId || '',
                chargeId: result.chargeId || '',
                receiptUrl: result.receiptUrl || '',
                paymentStatus: result.status || 'succeeded',
                payMessage: result.message || 'Stripe Checkout payment completed.'
            }
        });

        if (!save.ok) {
            setTerminalStatus('Payment received', 'Stripe succeeded, but the PMS payment log could not be saved.');
            return message(save.message || 'Stripe payment succeeded but could not be recorded in PMS.', 'error');
        }

        setTerminalStatus('Approved', 'Stripe Checkout payment completed and recorded.');
        message('Card payment recorded successfully.', 'success');
        closeModal('cardModal');
        await refreshReservationUi(regId);
    }

    async function startManualStripeCheckout(amount, note, securityHold = false) {
        if (app.dataset.stripe !== '1') return message('Stripe is not configured for this hotel.', 'error');

        // Open synchronously from the user click so browser popup blockers allow it.
        manualCheckoutWindow = window.open(
            '',
            'ORA_Stripe_Checkout',
            'width=760,height=850,resizable=yes,scrollbars=yes'
        );
        if (!manualCheckoutWindow) {
            return message('Please allow popups for ORA PMS so secure Stripe Checkout can open.', 'error');
        }

        try {
            manualCheckoutWindow.document.open();
            manualCheckoutWindow.document.write(
                '<!doctype html><html><head><title>Stripe Checkout</title></head>' +
                '<body style="font-family:Arial,sans-serif;background:#f6f9fc;color:#17375e;' +
                'display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0">' +
                '<div style="text-align:center;padding:30px"><h3>Preparing secure Stripe Checkout...</h3>' +
                '<p style="font-size:13px;color:#64748b">Please wait.</p></div></body></html>'
            );
            manualCheckoutWindow.document.close();
        } catch (_) { }

        setTerminalStatus('Preparing', 'Creating secure Stripe Checkout session…');

        let created;
        try {
            created = await api(urls.stripeCheckout, {
                method: 'POST',
                body: { regId, visitId, amount, currency: currencyCode, note, securityHold }
            });
        } catch (e) {
            try { manualCheckoutWindow.close(); } catch (_) { }
            manualCheckoutWindow = null;
            throw e;
        }

        if (!created.success || !created.checkoutUrl || !created.sessionId) {
            try { manualCheckoutWindow.close(); } catch (_) { }
            manualCheckoutWindow = null;
            return message(created.message || 'Unable to create Stripe Checkout session.', 'error');
        }

        manualCheckoutContext = { sessionId: created.sessionId, amount, note, securityHold };

        try {
            manualCheckoutWindow.location.replace(created.checkoutUrl);
        } catch (_) {
            manualCheckoutWindow.location.href = created.checkoutUrl;
        }

        setTerminalStatus('Enter card details', 'Complete the secure Stripe Checkout window.');

        stopManualCheckoutPoll();
        let attempts = 0;
        manualCheckoutPoll = setInterval(async () => {
            attempts++;

            if (!manualCheckoutWindow || manualCheckoutWindow.closed) {
                stopManualCheckoutPoll();
                manualCheckoutWindow = null;
                setTerminalStatus('Checkout closed', 'The Stripe Checkout window was closed before confirmation.');
                return;
            }

            try {
                const result = await api(`${urls.stripeCheckoutStatus}?sessionId=${encodeURIComponent(created.sessionId)}`);
                const status = String(result?.status || '').toLowerCase();
                if (result?.success && (status === 'succeeded' || status === 'paid' || status === 'complete')) {
                    await completeManualStripePayment(result, amount, note, securityHold);
                    return;
                }

                if (['expired', 'canceled', 'cancelled'].includes(status)) {
                    stopManualCheckoutPoll();
                    try { manualCheckoutWindow.close(); } catch (_) { }
                    manualCheckoutWindow = null;
                    setTerminalStatus('Cancelled', result.message || 'Stripe Checkout was cancelled.');
                    return;
                }
            } catch (_) {
                // Keep polling; a transient request failure must not make the user retry a card charge.
            }

            if (attempts >= 120) {
                stopManualCheckoutPoll();
                setTerminalStatus('Pending', 'Payment confirmation is taking longer than expected. Check the Payment Log before retrying.');
            }
        }, 1500);
    }

    async function openStripeCheckoutPayment() {
        if (!regId) return message('Save or load a guest first.', 'error');
        if (app.dataset.stripe !== '1') return message('Stripe is not configured for this hotel.', 'error');

        // WebForms Card Payment uses the current remaining/outstanding amount.
        const amount = Math.max(0, toNumber(state.totals?.remaining));
        if (amount <= 0) return message('There is no outstanding balance to pay.', 'info');

        showBusy('Opening Stripe Checkout…');
        try {
            await startManualStripeCheckout(amount, `Reservation ${regId} card payment`, false);
        } catch (e) {
            message(e.message || 'Unable to start Stripe Checkout.', 'error');
        } finally {
            hideBusy();
        }
    }

    async function chargeCard() {
        if (!regId) return message('Save or load a guest first.', 'error');
        const amount = toNumber(byId('cardAmount')?.value);
        if (amount <= 0) return message('Enter a card amount greater than 0.', 'error');
        const note = byId('cardNote')?.value.trim() || '';

        if (currentCardMode === 'manual') {
            showBusy('Opening Stripe Checkout…');
            try {
                await startManualStripeCheckout(amount, note);
            } catch (e) {
                message(e.message || 'Unable to start Stripe Checkout.', 'error');
            } finally {
                hideBusy();
            }
            return;
        }

        const request = {
            regId,
            visitId,
            readerId: byId('cardReader')?.value || '',
            amount,
            currency: currencyCode,
            note,
            paymentIntentId: currentPaymentIntentId,
            securityHold: cardSecurityMode,
            simulate: !!byId('cardSimulate')?.checked,
            simulationResult: byId('cardSimulationResult')?.value || 'success'
        };
        showBusy('Processing card payment…');
        try {
            if (currentCardProvider === 'clover') {
                setTerminalStatus('Processing', 'Sending payment to Clover…');
                const r = await api(urls.cloverPay, { method: 'POST', body: request });
                if (!r.success) return message(r.message || 'Clover payment failed.', 'error');
                setTerminalStatus('Approved', r.message || 'Payment approved.');
                message(r.message || 'Card payment successful.', 'success');
                await refreshReservationUi(regId);
                return;
            }

            if (!request.readerId && !request.simulate) return message('Select a Stripe reader.', 'error');
            setTerminalStatus('Creating', 'Creating Stripe PaymentIntent…');
            const created = await api(urls.stripeCreate, { method: 'POST', body: request });
            if (!created.success) return message(created.message || 'Unable to create Stripe payment.', 'error');
            currentPaymentIntentId = created.paymentIntentId || '';
            request.paymentIntentId = currentPaymentIntentId;
            setTerminalStatus('Waiting for reader', 'Present the card on the terminal.');
            const processed = await api(urls.stripeProcess, { method: 'POST', body: request });
            if (!processed.success) return message(processed.message || 'Unable to start reader payment.', 'error');

            let final = processed;
            for (let i = 0; i < 25 && ['requires_payment_method', 'requires_confirmation', 'requires_action', 'processing'].includes((final.status || '').toLowerCase()); i++) {
                await new Promise(resolve => setTimeout(resolve, 1200));
                final = await api(`${urls.stripeStatus}?paymentIntentId=${encodeURIComponent(currentPaymentIntentId)}`);
            }
            if (!final.success) return message(final.message || 'Card payment was not completed.', 'error');
            setTerminalStatus(cardSecurityMode ? 'Authorized' : 'Approved', final.message || (cardSecurityMode ? 'Security hold authorized.' : 'Payment approved.'));

            if (cardSecurityMode) {
                const hold = await api(urls.securityMovement, {
                    method: 'POST',
                    body: {
                        regId, visitId, amount,
                        note: request.note || 'Card security pre-authorization',
                        method: 'Card Pre-Authorization', movement: 'deposit', securityId: 0,
                        paymentIntentId: currentPaymentIntentId
                    }
                });
                if (!hold.ok) return message(hold.message || 'The card was authorized but the security hold could not be saved.', 'error');
                cardSecurityMode = false;
                currentPaymentIntentId = '';
                message('Security deposit authorized and saved.', 'success');
                await refreshReservationUi(regId);
                return;
            }

            const save = await api(urls.recordPayment, {
                method: 'POST',
                body: {
                    regId, visitId, amount,
                    method: 'Card', roomSecurity: 0, note: request.note,
                    paymentId: currentPaymentIntentId,
                    chargeId: final.chargeId || '',
                    receiptUrl: final.receiptUrl || '',
                    paymentStatus: final.status || 'succeeded',
                    payMessage: final.message || ''
                }
            });
            if (!save.ok) return message(save.message, 'error');
            message('Card payment recorded successfully.', 'success');
            await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); }
        finally { hideBusy(); }
    }

    async function cancelTerminal() {
        if (currentCardMode === 'manual') {
            stopManualCheckoutPoll();
            try { if (manualCheckoutWindow && !manualCheckoutWindow.closed) manualCheckoutWindow.close(); } catch (_) { }
            manualCheckoutWindow = null;
            manualCheckoutContext = null;
            setTerminalStatus('Cancelled', 'Manual card checkout closed.');
            closeModal('cardModal');
            return;
        }
        if (!currentPaymentIntentId) { closeModal('cardModal'); return; }
        showBusy('Cancelling terminal request…');
        try {
            const r = await api(urls.stripeCancel, { method: 'POST', body: { regId, visitId, readerId: byId('cardReader')?.value || '', amount: toNumber(byId('cardAmount')?.value), currency: currencyCode, paymentIntentId: currentPaymentIntentId } });
            setTerminalStatus(r.success ? 'Cancelled' : 'Cancel failed', r.message || '');
            if (!r.success) message(r.message, 'error');
            else { currentPaymentIntentId = ''; cardSecurityMode = false; message(r.message || 'Terminal request cancelled.', 'success'); }
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function applyFbrTaxModeFromPaymentMethod() {
        if (app.dataset.fbr !== '1' || !regId) return;
        const method = byId('paymentMethod')?.value?.trim() || '';
        if (!method) return;
        try {
            const r = await api(urls.fbrApplyTaxMode, { method: 'POST', body: { regId, visitId, paymentMethod: method } });
            if (!r.ok) return message(r.message || 'Unable to apply payment-method tax mode.', 'error');
            await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); }
    }

    async function postFbr() {
        if (!regId) return;
        const method = await askInput('Post & Print FBR', 'Payment method (Cash or Bank Transfer)', 'Cash', { okText: 'Continue' });
        if (method == null) return;
        if (!['cash', 'bank transfer'].includes(method.trim().toLowerCase())) return message('Select Cash or Bank Transfer for FBR posting.', 'error');
        showBusy('Posting invoice to FBR…');
        try {
            const r = await api(urls.fbrPost, { method: 'POST', body: { regId, visitId, paymentMethod: method.trim() } });
            if (!r.ok) return message(r.message, 'error');
            message(r.message, 'success');
            if (r.redirectUrl) window.open(r.redirectUrl, '_blank', 'noopener');
            else openLegacyInvoice('CHECK-IN');
            await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function manageSource(add) {
        const value = byId('sourceValue')?.value.trim() || '';
        if (!value) return message('Enter a source.', 'error');
        showBusy(add ? 'Adding source…' : 'Deleting source…');
        try {
            const r = await api(add ? urls.addSource : urls.deleteSource, { method: 'POST', body: add ? { value } : { value } });
            if (!r.ok) return message(r.message, 'error');
            message(r.message, 'success'); await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    async function manageCompany(add) {
        const value = byId('companyValue')?.value.trim() || '';
        if (!value) return message('Enter a company.', 'error');
        showBusy(add ? 'Adding company…' : 'Deleting company…');
        try {
            const r = await api(add ? urls.addCompany : urls.deleteCompany, {
                method: 'POST',
                body: add ? {
                    value,
                    contactPerson: byId('companyPerson')?.value.trim() || '',
                    email: byId('companyEmail')?.value.trim() || '',
                    phone: byId('companyPhone')?.value.trim() || '',
                    address: byId('companyAddress')?.value.trim() || '',
                    country: byId('companyCountry')?.value || ''
                } : { value }
            });
            if (!r.ok) return message(r.message, 'error');
            message(r.message, 'success'); await refreshReservationUi(regId);
        } catch (e) { message(e.message, 'error'); } finally { hideBusy(); }
    }

    function utf8Base64(value) {
        const bytes = new TextEncoder().encode(String(value ?? ''));
        let bin = '';
        bytes.forEach(b => { bin += String.fromCharCode(b); });
        return btoa(bin);
    }

    function openLegacyInvoice(pg = 'CHECK-IN') {
        if (!regId) return;
        const current = new URL(window.location.href);
        const qs = new URLSearchParams();
        qs.set('reg_id', utf8Base64(regId));
        qs.set('roomAmount', utf8Base64(String(toNumber(byId('roomSecurity')?.value))));
        qs.set('visit', utf8Base64(visitId));
        qs.set('paidAmount', utf8Base64(String(toNumber(byId('paidAmount')?.value))));
        qs.set('payable', utf8Base64(String(state.totals?.payable ?? state.totals?.grandTotal ?? 0)));
        qs.set('paymethod', utf8Base64(byId('paymentMethod')?.value || ''));
        qs.set('hd', current.searchParams.get('hd') || utf8Base64(state.hotelId || ''));
        qs.set('UN', current.searchParams.get('UN') || utf8Base64(state.userName || ''));
        qs.set('UD', current.searchParams.get('UD') || utf8Base64(state.userId || ''));
        qs.set('PG', utf8Base64(pg));
        qs.set('FBR', utf8Base64(''));
        window.open(`/BookingConfirmationInvoice.aspx?${qs.toString()}`, '_blank', 'noopener');
    }

    function openLaundryInvoice() {
        if (!regId) return;
        const total = (state.laundry || []).reduce((n, x) => n + toNumber(x.amount), 0);
        const current = new URL(window.location.href);
        const qs = new URLSearchParams();
        qs.set('reg_id', utf8Base64(regId));
        qs.set('total', utf8Base64(String(total)));
        qs.set('hd', current.searchParams.get('hd') || utf8Base64(state.hotelId || ''));
        qs.set('UN', current.searchParams.get('UN') || utf8Base64(state.userName || ''));
        qs.set('UD', current.searchParams.get('UD') || utf8Base64(state.userId || ''));
        window.open(`/invoiceLaundry.aspx?${qs.toString()}`, '_blank', 'noopener');
    }

    function initializeFromState() {
        const g = state.guest || {};
        setSelectValue('country', g.country || '');
        setSelectValue('city', g.city || '');
        setSelectValue('source', g.source || '');
        setSelectValue('company', g.company || '');
        setSelectValue('paymentMethod', state.totals?.paymentMethod || '');
        setSelectValue('reservationType', state.reservationType || g.reservationType || 'Individual');
        if ((state.reservationType || g.reservationType || '').toLowerCase() === 'group') {
            setSelectValue('reservationDateMode', (state.reservationDateMode || 'groupSame') === 'groupDifferent' ? 'groupDifferent' : 'groupSame');
        }
        if (!canEditDates) {
            byId('stayDateDisplay')?.setAttribute('aria-disabled', 'true');
            byId('stayDateDisplay')?.classList.add('is-disabled');
        }
        applyReservationMode();
        updateStayCount();
        syncCounter(byId('adults'));
        syncCounter(byId('children'));
        updateChargeMode();
        syncChargeOptionalColumns();
    }

    function syncFixedActionBar() {
        const bar = byId('checkinFixedActions');
        if (!bar || !app) return;
        const rect = app.getBoundingClientRect();
        bar.style.left = `${Math.max(0, Math.round(rect.left))}px`;
    }

    window.addEventListener('resize', syncFixedActionBar);
    window.addEventListener('orientationchange', syncFixedActionBar);
    setTimeout(syncFixedActionBar, 0);
    setTimeout(syncFixedActionBar, 350);

    // ---------- Event wiring ----------
    byId('checkinForm')?.addEventListener('submit', e => e.preventDefault());
    bindGuestKeyboardSaves();
    byId('guestSearchButton')?.addEventListener('click', () => searchGuests().catch(e => message(e.message, 'error')));
    byId('guestSearch')?.addEventListener('input', () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => searchGuests().catch(e => message(e.message, 'error')), 250);
    });
    byId('searchResults')?.addEventListener('click', e => {
        const hit = e.target.closest('[data-reg]');
        if (hit) reloadReservation(hit.dataset.reg || '');
    });
    document.addEventListener('click', e => {
        const path = typeof e.composedPath === 'function' ? e.composedPath() : [];
        const searchWrap = document.querySelector('.search-wrap');
        const stayPicker = byId('stayDatePicker');
        const insideSearch = path.length ? path.includes(searchWrap) : !!e.target.closest?.('.search-wrap');
        const insideStayPicker = path.length ? path.includes(stayPicker) : !!e.target.closest?.('#stayDatePicker');

        if (!insideSearch) byId('searchResults')?.classList.add('hidden');
        // Do not close after the first date click. renderCalendar() replaces the clicked
        // day element, so composedPath is required to preserve the original click path.
        if (!insideStayPicker) setCalendarOpen(false);
    });

    byId('phone')?.addEventListener('blur', e => tryAutofillGuest(e.target.value).catch(() => { }));
    byId('email')?.addEventListener('blur', e => tryAutofillGuest(e.target.value).catch(() => { }));
    byId('country')?.addEventListener('change', () => loadCities('').catch(e => message(e.message, 'error')));

    byId('stayDateDisplay')?.addEventListener('click', () => setCalendarOpen(byId('dateCalendar')?.classList.contains('hidden')));
    byId('dateCalendar')?.addEventListener('click', e => e.stopPropagation());
    byId('calendarDays')?.addEventListener('click', e => {
        e.preventDefault();
        e.stopPropagation();
        const day = e.target.closest('[data-calendar-date]');
        if (day) selectCalendarDate(day.dataset.calendarDate);
    });
    byId('calendarDays')?.addEventListener('mouseover', e => {
        if (calendarStage !== 1) return;
        const day = e.target.closest('[data-calendar-date]');
        if (!day || calendarHoverDate === day.dataset.calendarDate) return;
        calendarHoverDate = day.dataset.calendarDate || null;
        renderCalendar();
    });
    byId('calendarDays')?.addEventListener('mouseleave', () => {
        if (calendarStage !== 1 || !calendarHoverDate) return;
        calendarHoverDate = null;
        renderCalendar();
    });
    byId('previousMonth')?.addEventListener('click', () => { calendarView = new Date(calendarView.getFullYear(), calendarView.getMonth() - 1, 1); renderCalendar(); });
    byId('nextMonth')?.addEventListener('click', () => { calendarView = new Date(calendarView.getFullYear(), calendarView.getMonth() + 1, 1); renderCalendar(); });

    $$('[data-target][data-delta]').forEach(button => button.addEventListener('click', () => {
        const input = byId(button.dataset.target);
        if (!input) return;
        input.value = String(toNumber(input.value) + toNumber(button.dataset.delta));
        syncCounter(input);
    }));
    $$('.counter input').forEach(input => input.addEventListener('input', () => syncCounter(input)));

    byId('reservationType')?.addEventListener('change', applyReservationMode);
    byId('reservationDateMode')?.addEventListener('change', applyReservationMode);
    byId('chargeArrival')?.addEventListener('change', () => { updateChargeStayCount(); loadRoomsAndPlans().catch(() => { }); quoteRate().catch(() => { }); });
    byId('chargeDeparture')?.addEventListener('change', () => { updateChargeStayCount(); loadRoomsAndPlans().catch(() => { }); quoteRate().catch(() => { }); });

    byId('saveGuest')?.addEventListener('click', () => saveGuest());
    bindGuestActionButtons();

    byId('chargeCategory')?.addEventListener('change', () => {
        updateChargeMode();
        if (isRoomChargeMode()) loadRoomsAndPlans().catch(e => message(e.message, 'error'));
    });
    byId('ratePlan')?.addEventListener('change', () => quoteRate().catch(e => message(e.message, 'error')));
    byId('monthlyRate')?.addEventListener('input', () => quoteRate().catch(() => { }));
    byId('addCharge')?.addEventListener('click', addCharge);
    byId('chargeRows')?.addEventListener('click', e => {
        const roomChange = e.target.closest('[data-room-change]');
        if (roomChange) {
            const row = roomChange.closest('tr[data-charge-id]');
            if (row) openRoomChangeForRow(row, Number(roomChange.dataset.roomChange || row.dataset.chargeId || 0));
            return;
        }
        const button = e.target.closest('[data-action]');
        const row = button?.closest('tr[data-charge-id]');
        if (button && row) chargeRowAction(button, row);
    });
    // Match the WebForms grid interaction: room guest name and rate are editable
    // directly from their cells. Enter saves; Escape cancels (handled by editor).
    byId('chargeRows')?.addEventListener('dblclick', e => {
        const row = e.target.closest('tr[data-charge-id]');
        if (!row) return;
        const guestCell = e.target.closest('.guest-cell');
        if (guestCell && guestCell.dataset.canEditGuest === '1') { startInlineChargeEdit(row, 'guest-name'); return; }
        const rateCell = e.target.closest('.rate-cell');
        if (rateCell && rateCell.dataset.canEditRate === '1') startInlineChargeEdit(row, 'rate');
    });
    byId('roomChangeSave')?.addEventListener('click', saveRoomChange);

    byId('recordPayment')?.addEventListener('click', recordPayment);
    byId('paymentMethod')?.addEventListener('change', applyFbrTaxModeFromPaymentMethod);
    byId('paidAmount')?.addEventListener('input', () => {
        const total = toNumber(state.totals?.grandTotal);
        const already = toNumber(state.totals?.paidAmount);
        byId('balance').textContent = amountOnly(Math.max(0, total - already - toNumber(byId('paidAmount').value)));
    });
    bindFixedActionButtons();

    $$('.utility-tab').forEach(tab => tab.addEventListener('click', () => {
        $$('.utility-tab').forEach(x => x.classList.toggle('active', x === tab));
        $$('.utility-panel').forEach(x => x.classList.toggle('hidden', x.id !== tab.dataset.panel));
        byId(tab.dataset.panel)?.classList.add('active');
        if (tab.dataset.panel === 'laundryPanel' && !byId('laundryCategory')?.dataset.loaded) {
            loadLaundryCategories().then(() => { byId('laundryCategory').dataset.loaded = '1'; }).catch(e => message(e.message, 'error'));
        }
    }));
    byId('laundryCategory')?.addEventListener('change', () => loadLaundryItems().catch(e => message(e.message, 'error')));
    byId('laundryItem')?.addEventListener('change', updateLaundryAmount);
    byId('laundryQty')?.addEventListener('input', updateLaundryAmount);
    byId('addLaundry')?.addEventListener('click', addLaundry);
    byId('laundryPanel')?.addEventListener('click', e => {
        const b = e.target.closest('[data-laundry-delete]');
        if (b) deleteLaundry(Number(b.dataset.laundryDelete || 0));
    });
    byId('applyDiscount')?.addEventListener('click', applyDiscount);

    byId('paymentLogRows')?.addEventListener('click', e => {
        const refund = e.target.closest('[data-payment-refund]');
        if (refund) {
            const row = refund.closest('tr');
            openRefundPaymentModal(Number(refund.dataset.paymentRefund || 0), toNumber(row?.dataset.refundable || row?.dataset.amount));
            return;
        }
        const audit = e.target.closest('[data-payment-audit]');
        if (audit) showPaymentAudit(Number(audit.dataset.paymentAudit || 0));
    });

    window.addEventListener('message', async event => {
        if (event.origin !== window.location.origin) return;
        if (event.data?.type !== 'ora-stripe-checkout' || !manualCheckoutContext) return;
        const ctx = manualCheckoutContext;
        const sessionId = event.data.sessionId || ctx.sessionId;
        if (!sessionId) return;
        try {
            const result = await api(`${urls.stripeCheckoutStatus}?sessionId=${encodeURIComponent(sessionId)}`);
            const status = String(result?.status || '').toLowerCase();
            if (result?.success && ['succeeded', 'paid', 'complete', 'requires_capture'].includes(status)) {
                await completeManualStripePayment(result, ctx.amount, ctx.note, ctx.securityHold);
            } else if (['expired', 'canceled', 'cancelled'].includes(status)) {
                stopManualCheckoutPoll();
                manualCheckoutContext = null;
                message(result?.message || 'Stripe Checkout was cancelled.', 'info');
            }
        } catch (e) {
            message(e.message || 'Unable to confirm Stripe Checkout payment.', 'error');
        }
    });

    byId('openRoomSecurity')?.addEventListener('click', openSecurityDeposit);
    byId('saveSecurity')?.addEventListener('click', saveSecurity);
    byId('securityDepositTab')?.addEventListener('click', () => { pendingSecurityId = 0; setSecurityMode('deposit'); });
    byId('securitySettleTab')?.addEventListener('click', () => setSecurityMode('settle'));
    byId('securityRefundAmount')?.addEventListener('input', updateSecurityDeductionPreview);
    $$('[data-security-method]').forEach(btn => btn.addEventListener('click', () => setSecurityPaymentMethod(btn.dataset.securityMethod || 'cash')));
    $('.security-table')?.addEventListener('click', e => {
        const b = e.target.closest('[data-security-settle]');
        if (b) settleSecurity(Number(b.dataset.securitySettle || 0));
    });

    byId('openPdqPayment')?.addEventListener('click', () => openPdqPaymentLikeWebForms());
    byId('openStripeCardPayment')?.addEventListener('click', openStripeCheckoutPayment);
    byId('refundPaymentSubmit')?.addEventListener('click', submitRefundPayment);
    $$('[data-card-mode]').forEach(tab => tab.addEventListener('click', () => setCardMode(tab.dataset.cardMode || 'terminal')));
    $$('[data-card-provider]').forEach(tab => tab.addEventListener('click', () => {
        currentCardProvider = tab.dataset.cardProvider || 'stripe';
        $$('[data-card-provider]').forEach(x => x.classList.toggle('active', x === tab));
        byId('cardReaderWrap')?.classList.toggle('hidden', currentCardProvider !== 'stripe');
        if (currentCardMode === 'terminal') {
            setTerminalStatus('Ready', currentCardProvider === 'clover'
                ? 'Enter amount and send it to the Clover PDQ device.'
                : 'Select a reader, enter amount and send it to the Stripe terminal.');
        }
    }));
    byId('chargeCardNow')?.addEventListener('click', chargeCard);
    byId('cancelTerminal')?.addEventListener('click', cancelTerminal);

    byId('manageSource')?.addEventListener('click', () => openModal('sourceModal'));
    byId('addSource')?.addEventListener('click', () => manageSource(true));
    byId('deleteSource')?.addEventListener('click', () => manageSource(false));
    byId('manageCompany')?.addEventListener('click', () => openModal('companyModal'));
    byId('addCompany')?.addEventListener('click', () => manageCompany(true));
    byId('deleteCompany')?.addEventListener('click', () => manageCompany(false));

    byId('dateTaxContinue')?.addEventListener('click', () => resolveDateChangeTaxSelection(true));
    byId('dateTaxCancel')?.addEventListener('click', () => resolveDateChangeTaxSelection(false));
    byId('dateTaxClose')?.addEventListener('click', () => resolveDateChangeTaxSelection(false));

    $$('[data-close-modal]').forEach(button => button.addEventListener('click', () => closeModal(button.dataset.closeModal)));
    $$('.modal-backdrop').forEach(modal => modal.addEventListener('click', e => {
        if (e.target !== modal || modal.id === 'confirmDialog') return;
        if (modal.id === 'dateTaxModal') resolveDateChangeTaxSelection(false);
        else closeModal(modal.id);
    }));
    byId('confirmCancel')?.addEventListener('click', () => resolveConfirm(false));
    byId('confirmOk')?.addEventListener('click', () => resolveConfirm(true));
    byId('inputDialogCancel')?.addEventListener('click', () => resolveInput(false));
    byId('inputDialogClose')?.addEventListener('click', () => resolveInput(false));
    byId('inputDialogOk')?.addEventListener('click', () => resolveInput(true));
    byId('inputDialogValue')?.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); resolveInput(true); } });
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') {
            if (!byId('confirmDialog')?.classList.contains('hidden')) resolveConfirm(false);
            else if (!byId('inputDialog')?.classList.contains('hidden')) resolveInput(false);
            else if (!byId('dateTaxModal')?.classList.contains('hidden')) resolveDateChangeTaxSelection(false);
            else $$('.modal-backdrop:not(.hidden)').forEach(x => closeModal(x.id));
            setCalendarOpen(false);
        }
    });

    initializeFromState();
    lastQuickNameSnapshot = currentNameSnapshot();
    showQueuedReloadToast();
})();
