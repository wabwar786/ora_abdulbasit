(function () {
    'use strict';

    const state = window.oraDashboardData || {};

    function initProgressRings() {
        const circumference = 339.292;
        document.querySelectorAll('.progress-ring').forEach(function (ring) {
            const current = Number(ring.dataset.current || 0);
            const total = Number(ring.dataset.total || 0);
            const pct = total > 0 ? Math.max(0, Math.min(1, current / total)) : 0;
            const circle = ring.querySelector('.ring-value');
            if (circle) circle.style.strokeDashoffset = String(circumference * (1 - pct));
        });
    }

    function initBookingsChart() {
        const canvas = document.getElementById('ReservationChartOption');
        if (!canvas || typeof Chart === 'undefined') return;
        if (window.oraReservationChartInstance) window.oraReservationChartInstance.destroy();
        window.oraReservationChartInstance = new Chart(canvas.getContext('2d'), {
            type: 'bar',
            data: {
                labels: state.bookingLabels || [],
                datasets: state.bookingDatasets || []
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: {
                        position: 'top', align: 'start',
                        labels: { boxWidth: 12, boxHeight: 8, padding: 12, color: '#6B778C', font: { size: 10, weight: '400' } }
                    },
                    tooltip: { mode: 'index', intersect: false, backgroundColor: '#172033', titleColor: '#fff', bodyColor: '#fff', padding: 10 }
                },
                datasets: { bar: { borderRadius: 0, borderSkipped: false, categoryPercentage: .72, barPercentage: .88 } },
                scales: {
                    x: { stacked: true, grid: { display: false }, ticks: { color: '#6B778C', font: { size: 9 }, maxRotation: 0, minRotation: 0 } },
                    y: { stacked: true, beginAtZero: true, grid: { color: 'rgba(107,119,140,.15)' }, ticks: { precision: 0, color: '#6B778C', font: { size: 9 } } }
                }
            }
        });
    }

    const overlay = document.getElementById('dashboardModalOverlay');
    const modalTitle = document.getElementById('dashboardModalTitle');
    const modalSubtitle = document.getElementById('dashboardModalSubtitle');
    const modalSummary = document.getElementById('dashboardModalSummary');
    const modalHead = document.getElementById('dashboardModalHead');
    const modalBody = document.getElementById('dashboardModalBody');
    const modalEmpty = document.getElementById('dashboardModalEmpty');

    function closeModal() {
        if (!overlay) return;
        overlay.classList.remove('open');
        overlay.setAttribute('aria-hidden', 'true');
    }

    function showLoading(title) {
        if (!overlay) return;
        modalTitle.textContent = title || 'Details';
        modalSubtitle.textContent = 'Loading...';
        modalSummary.innerHTML = '';
        modalHead.innerHTML = '';
        modalBody.innerHTML = '<tr><td style="padding:25px;text-align:center;color:#8190a4">Loading details...</td></tr>';
        modalEmpty.hidden = true;
        overlay.classList.add('open');
        overlay.setAttribute('aria-hidden', 'false');
    }

    function renderPopup(data) {
        modalTitle.textContent = data.title || 'Details';
        modalSubtitle.textContent = data.subtitle || '';
        modalSummary.innerHTML = '';
        Object.entries(data.summary || {}).forEach(function (entry) {
            const pill = document.createElement('div');
            pill.className = 'summary-pill';
            const label = document.createElement('span');
            label.textContent = entry[0];
            const value = document.createElement('strong');
            value.textContent = entry[1];
            pill.append(label, value);
            modalSummary.appendChild(pill);
        });

        const columns = data.columns || [];
        const rows = data.rows || [];
        modalHead.innerHTML = '';
        modalBody.innerHTML = '';
        if (columns.length) {
            const tr = document.createElement('tr');
            columns.forEach(function (column) {
                const th = document.createElement('th');
                th.textContent = column;
                tr.appendChild(th);
            });
            modalHead.appendChild(tr);
        }
        rows.forEach(function (row) {
            const tr = document.createElement('tr');
            columns.forEach(function (column) {
                const td = document.createElement('td');
                td.textContent = row[column] == null ? '' : row[column];
                tr.appendChild(td);
            });
            modalBody.appendChild(tr);
        });
        modalEmpty.hidden = rows.length !== 0;
    }

    async function openPopup(type) {
        showLoading(popupLabel(type));
        try {
            const params = new URLSearchParams({ type: type, start: state.start || '', end: state.end || '' });
            const response = await fetch('/Dashboard/Popup?' + params.toString(), { credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
            const data = await response.json();
            if (!response.ok) throw new Error(data.message || 'Unable to load dashboard details.');
            renderPopup(data);
        } catch (error) {
            renderPopup({ title: popupLabel(type), subtitle: '', columns: [], rows: [], summary: { Error: error.message || 'Unable to load details.' } });
        }
    }

    function popupLabel(type) {
        return ({
            checkin: 'Today’s Check-ins', checkout: 'Today’s Completed Check-outs', available: 'Available Rooms', occupied: 'Occupied Rooms', blocked: 'Blocked Rooms', dirty: 'Dirty Rooms', receivables: 'Receivables', expense: 'Total Expenses', profitloss: 'Operational P/L', noshow: 'No Show - Selected Dates', cancellation: 'Cancellations - Selected Dates'
        })[type] || 'Details';
    }

    function formatMoney(value) {
        const n = Number(value || 0);
        return n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }

    function formatRange(start, end) {
        return (start || '') + (start && end ? ' - ' : '') + (end || '');
    }

    document.querySelectorAll('[data-popup]').forEach(function (element) {
        element.addEventListener('click', function (event) {
            const type = element.getAttribute('data-popup');
            if (type) { event.preventDefault(); openPopup(type); }
        });
    });

    const modalClose = document.getElementById('dashboardModalClose');
    if (modalClose) modalClose.addEventListener('click', closeModal);
    if (overlay) overlay.addEventListener('click', function (event) { if (event.target === overlay) closeModal(); });
    document.addEventListener('keydown', function (event) { if (event.key === 'Escape') { closeModal(); closeActionMenus(); closeCalendar(); } });

    function closeActionMenus(except) {
        document.querySelectorAll('.dashboard-action-wrap.open').forEach(function (wrap) {
            if (except && wrap === except) return;
            wrap.classList.remove('open');
            const menu = wrap.querySelector('.dashboard-action-menu');
            if (menu) { menu.style.top = ''; menu.style.left = ''; }
        });
    }

    document.querySelectorAll('.btn-actions').forEach(function (button) {
        button.addEventListener('click', function (event) {
            event.preventDefault(); event.stopPropagation();
            const wrap = button.closest('.dashboard-action-wrap');
            if (!wrap) return;
            const menu = wrap.querySelector('.dashboard-action-menu');
            if (!menu) return;
            const wasOpen = wrap.classList.contains('open');
            closeActionMenus(wrap);
            if (wasOpen) { wrap.classList.remove('open'); return; }
            const rect = button.getBoundingClientRect();
            const width = 220;
            let left = Math.max(8, rect.right - width);
            if (left + width > window.innerWidth - 8) left = window.innerWidth - width - 8;
            wrap.classList.add('open');
            menu.style.left = left + 'px';
            menu.style.top = (rect.bottom + 5) + 'px';
        });
    });
    document.addEventListener('click', function (event) { if (!event.target.closest('.dashboard-action-wrap')) closeActionMenus(); });
    window.addEventListener('resize', function () { closeActionMenus(); closeCalendar(); });
    window.addEventListener('scroll', function () { closeActionMenus(); }, true);

    // Dual-month range calendar, replacing the Web Forms postback calendar without changing its behavior.
    const calendar = document.getElementById('calendarPopup');
    const toggle = document.getElementById('calendarToggle');
    const leftGrid = document.getElementById('leftCalendar');
    const rightGrid = document.getElementById('rightCalendar');
    const leftHeader = document.getElementById('leftMonthHeader');
    const rightHeader = document.getElementById('rightMonthHeader');
    const rangeInput = document.getElementById('dashboardDateRange');
    const startInput = document.getElementById('dashboardStartDate');
    const endInput = document.getElementById('dashboardEndDate');
    const rangeText = document.getElementById('selectedRangeText');
    let selectedStart = parseIso(startInput && startInput.value);
    let selectedEnd = parseIso(endInput && endInput.value);
    let viewMonth = new Date((selectedStart || new Date()).getFullYear(), (selectedStart || new Date()).getMonth(), 1);

    function parseIso(value) {
        if (!value) return null;
        const parts = value.split('-').map(Number);
        return parts.length === 3 ? new Date(parts[0], parts[1] - 1, parts[2]) : null;
    }
    function iso(date) {
        return date.getFullYear() + '-' + String(date.getMonth() + 1).padStart(2, '0') + '-' + String(date.getDate()).padStart(2, '0');
    }
    function display(date) { return String(date.getDate()).padStart(2, '0') + '/' + String(date.getMonth() + 1).padStart(2, '0') + '/' + date.getFullYear(); }
    function sameDay(a, b) { return !!a && !!b && a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }
    function dayValue(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime(); }

    function renderMonth(grid, header, date) {
        if (!grid || !header) return;
        header.textContent = date.toLocaleString(undefined, { month: 'long', year: 'numeric' });
        grid.innerHTML = '';
        ['Su','Mo','Tu','We','Th','Fr','Sa'].forEach(function (d) { const el = document.createElement('div'); el.className = 'weekday'; el.textContent = d; grid.appendChild(el); });
        const first = new Date(date.getFullYear(), date.getMonth(), 1);
        const days = new Date(date.getFullYear(), date.getMonth() + 1, 0).getDate();
        for (let i = 0; i < first.getDay(); i++) { const blank = document.createElement('span'); grid.appendChild(blank); }
        for (let day = 1; day <= days; day++) {
            const d = new Date(date.getFullYear(), date.getMonth(), day);
            const btn = document.createElement('button');
            btn.type = 'button'; btn.className = 'calendar-day'; btn.textContent = day;
            if (sameDay(d, selectedStart) || sameDay(d, selectedEnd)) btn.classList.add('selected');
            else if (selectedStart && selectedEnd && dayValue(d) > dayValue(selectedStart) && dayValue(d) < dayValue(selectedEnd)) btn.classList.add('in-range');
            btn.addEventListener('click', function () { chooseDate(d); });
            grid.appendChild(btn);
        }
    }
    function renderCalendars() {
        renderMonth(leftGrid, leftHeader, viewMonth);
        renderMonth(rightGrid, rightHeader, new Date(viewMonth.getFullYear(), viewMonth.getMonth() + 1, 1));
        if (rangeText) rangeText.textContent = selectedStart ? display(selectedStart) + (selectedEnd ? ' - ' + display(selectedEnd) : ' - Select end date') : 'Select date range';
    }
    function chooseDate(date) {
        if (!selectedStart || selectedEnd) { selectedStart = date; selectedEnd = null; }
        else if (dayValue(date) < dayValue(selectedStart)) { selectedEnd = selectedStart; selectedStart = date; }
        else { selectedEnd = date; }
        renderCalendars();
    }
    function closeCalendar() { if (calendar) { calendar.classList.remove('open'); calendar.setAttribute('aria-hidden', 'true'); } }
    function openCalendar() { if (calendar) { calendar.classList.add('open'); calendar.setAttribute('aria-hidden', 'false'); renderCalendars(); } }
    if (toggle) toggle.addEventListener('click', function (event) { event.stopPropagation(); calendar && calendar.classList.contains('open') ? closeCalendar() : openCalendar(); });
    if (calendar) calendar.addEventListener('click', function (event) { event.stopPropagation(); });
    document.addEventListener('click', function () { closeCalendar(); });
    document.querySelectorAll('[data-cal-nav]').forEach(function (button) {
        button.addEventListener('click', function () {
            const nav = button.dataset.calNav;
            if (nav === 'left-prev') viewMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() - 1, 1);
            if (nav === 'left-next') viewMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() + 1, 1);
            if (nav === 'right-prev') viewMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() - 1, 1);
            if (nav === 'right-next') viewMonth = new Date(viewMonth.getFullYear(), viewMonth.getMonth() + 1, 1);
            renderCalendars();
        });
    });
    const clear = document.getElementById('clearDateSelection');
    if (clear) clear.addEventListener('click', function () { selectedStart = null; selectedEnd = null; renderCalendars(); });
    const apply = document.getElementById('applyDateSelection');
    if (apply) apply.addEventListener('click', function () {
        if (!selectedStart) return;
        if (!selectedEnd) selectedEnd = selectedStart;
        if (startInput) startInput.value = iso(selectedStart);
        if (endInput) endInput.value = iso(selectedEnd);
        if (rangeInput) rangeInput.value = display(selectedStart) + ' - ' + display(selectedEnd);
        state.start = iso(selectedStart); state.end = iso(selectedEnd);
        closeCalendar();
    });

    initProgressRings();
    initBookingsChart();
    renderCalendars();
})();
