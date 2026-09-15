(function () {
    'use strict';

    function readJson(id) {
        var node = document.getElementById(id);
        if (!node) return [];
        try { return JSON.parse(node.textContent || '[]'); }
        catch (_) { return []; }
    }

    function svgNode(name, attrs) {
        var node = document.createElementNS('http://www.w3.org/2000/svg', name);
        Object.keys(attrs || {}).forEach(function (key) { node.setAttribute(key, String(attrs[key])); });
        return node;
    }

    function addText(svg, x, y, value, anchor) {
        var text = svgNode('text', { x: x, y: y, fill: '#8190a0', 'font-size': '8.5', 'font-weight': '500', 'text-anchor': anchor || 'start' });
        text.textContent = value;
        svg.appendChild(text);
    }

    function linePath(values, x0, x1, y0, y1, min, max) {
        if (!values || values.length === 0) return '';
        var span = max - min || 1;
        var step = values.length <= 1 ? 0 : (x1 - x0) / (values.length - 1);
        return values.map(function (value, index) {
            var x = x0 + step * index;
            var y = y1 - ((Number(value) - min) / span) * (y1 - y0);
            return (index === 0 ? 'M' : 'L') + x.toFixed(1) + ' ' + y.toFixed(1);
        }).join(' ');
    }

    function drawGrid(svg, width, height, min, max, formatter) {
        var left = 38, right = width - 8, top = 10, bottom = height - 23;
        for (var i = 0; i < 5; i++) {
            var ratio = i / 4;
            var y = top + ratio * (bottom - top);
            svg.appendChild(svgNode('line', { x1: left, y1: y, x2: right, y2: y, stroke: '#edf2f6', 'stroke-width': 1 }));
            var val = max - ratio * (max - min);
            addText(svg, 2, y + 3, formatter ? formatter(val) : Math.round(val), 'start');
        }
        return { left: left, right: right, top: top, bottom: bottom };
    }

    function drawLineChart(svg, labels, series, options) {
        if (!svg) return;
        while (svg.firstChild) svg.removeChild(svg.firstChild);
        var vb = (svg.getAttribute('viewBox') || '0 0 560 175').split(/\s+/).map(Number);
        var width = vb[2] || 560, height = vb[3] || 175;
        var all = [];
        series.forEach(function (s) { (s.values || []).forEach(function (v) { if (Number.isFinite(Number(v))) all.push(Number(v)); }); });
        var min = options && options.min != null ? options.min : Math.min(0, all.length ? Math.min.apply(null, all) : 0);
        var max = options && options.max != null ? options.max : (all.length ? Math.max.apply(null, all) : 1);
        if (max <= min) max = min + 1;
        if (!options || options.min == null || options.max == null) max = max * 1.08;
        var plot = drawGrid(svg, width, height, min, max, options && options.yFormatter);

        series.forEach(function (s) {
            var path = linePath(s.values || [], plot.left, plot.right, plot.top, plot.bottom, min, max);
            if (!path) return;
            svg.appendChild(svgNode('path', {
                d: path,
                fill: 'none',
                stroke: s.color || '#5f97c2',
                'stroke-width': s.width || 2,
                'stroke-linecap': 'round',
                'stroke-linejoin': 'round',
                'stroke-dasharray': s.dash || ''
            }));
        });

        if (labels && labels.length) {
            var indexes = [0, Math.floor((labels.length - 1) / 3), Math.floor(2 * (labels.length - 1) / 3), labels.length - 1];
            indexes = indexes.filter(function (v, i, a) { return v >= 0 && a.indexOf(v) === i; });
            indexes.forEach(function (idx) {
                var x = labels.length <= 1 ? plot.left : plot.left + idx * ((plot.right - plot.left) / (labels.length - 1));
                addText(svg, x, height - 6, labels[idx], 'middle');
            });
        }
    }

    function shortDate(value) {
        var d = new Date(value);
        if (Number.isNaN(d.getTime())) return '';
        return d.toLocaleDateString(undefined, { day: '2-digit', month: 'short' });
    }

    function drawOccupancyOnBooks(days) {
        var current = readJson('dashboardOccupancyData').filter(function (x) { return Number(x.roomsSold) >= 0; });
        var lastYear = readJson('dashboardOccupancyLyData');
        if (!current.length) return;
        // Initial first paint contains 30 future days only. The lazy analytics
        // payload contains 15 history days followed by 90 future days.
        var futureStart = current.length > 90 ? 15 : 0;
        var lastYearStart = lastYear.length > 90 ? 15 : 0;
        var rows = current.slice(futureStart, futureStart + days);
        var lyRows = lastYear.slice(lastYearStart, lastYearStart + days);
        drawLineChart(
            document.getElementById('dashboardOccupancyChart'),
            rows.map(function (x) { return shortDate(x.date); }),
            [
                { values: rows.map(function (x) { return Number(x.occupancyPercent || 0); }), color: '#5f97c2', width: 2.2 },
                { values: lyRows.map(function (x) { return Number(x.occupancyPercent || 0); }), color: '#aeb9c4', width: 1.5, dash: '5 4' }
            ],
            { min: 0, max: 100, yFormatter: function (v) { return Math.round(v) + '%'; } }
        );
    }

    function drawRevenueTrend() {
        var rows = readJson('dashboardFinancialData');
        drawLineChart(
            document.getElementById('dashboardRevenueChart'),
            rows.map(function (x) { return shortDate(x.date); }),
            [
                { values: rows.map(function (x) { return Number(x.revenue || 0); }), color: '#5f97c2', width: 2.1 },
                { values: rows.map(function (x) { return Number(x.profit || 0); }), color: '#73ad91', width: 1.9 },
                { values: rows.map(function (x) { return Number(x.lastYearRevenue || 0); }), color: '#aeb9c4', width: 1.4, dash: '5 4' }
            ],
            { yFormatter: function (v) { return Math.abs(v) >= 1000 ? (v / 1000).toFixed(0) + 'k' : Math.round(v); } }
        );
    }

    function drawOccupancyTrend() {
        var rows = readJson('dashboardOccupancyData').slice(0, 30);
        if (!rows.length) return;
        var actual = rows.slice(0, 16);
        var forecast = rows.slice(15);
        var svg = document.getElementById('dashboardOccupancyTrendChart');
        if (!svg) return;
        while (svg.firstChild) svg.removeChild(svg.firstChild);
        var width = 560, height = 175;
        var plot = drawGrid(svg, width, height, 0, 100, function (v) { return Math.round(v) + '%'; });
        var fullStep = rows.length <= 1 ? 0 : (plot.right - plot.left) / (rows.length - 1);
        var actualPath = linePath(actual.map(function (x) { return Number(x.occupancyPercent || 0); }), plot.left, plot.left + fullStep * (actual.length - 1), plot.top, plot.bottom, 0, 100);
        var forecastPath = linePath(forecast.map(function (x) { return Number(x.occupancyPercent || 0); }), plot.left + fullStep * 15, plot.right, plot.top, plot.bottom, 0, 100);
        svg.appendChild(svgNode('path', { d: actualPath, fill: 'none', stroke: '#5f97c2', 'stroke-width': 2.2, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }));
        svg.appendChild(svgNode('path', { d: forecastPath, fill: 'none', stroke: '#7f9ab2', 'stroke-width': 1.8, 'stroke-dasharray': '5 4', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }));
        [0, 7, 15, 22, 29].forEach(function (idx) {
            if (!rows[idx]) return;
            addText(svg, plot.left + fullStep * idx, height - 6, shortDate(rows[idx].date), 'middle');
        });
    }

    function drawExceptionChart() {
        var rows = readJson('dashboardExceptionData');
        var svg = document.getElementById('dashboardExceptionChart');
        if (!svg || !rows.length) return;
        while (svg.firstChild) svg.removeChild(svg.firstChild);
        var width = 420, height = 120, left = 26, right = 410, top = 8, bottom = 94;
        var max = Math.max(1, Math.max.apply(null, rows.map(function (x) { return Math.max(Number(x.cancellations || 0), Number(x.noShows || 0)); })));
        for (var i = 0; i < 4; i++) {
            var y = top + i * ((bottom - top) / 3);
            svg.appendChild(svgNode('line', { x1: left, y1: y, x2: right, y2: y, stroke: '#edf2f6' }));
        }
        var group = (right - left) / rows.length;
        rows.forEach(function (row, idx) {
            var x = left + idx * group + group * 0.18;
            var w = Math.max(5, group * 0.24);
            var c = Number(row.cancellations || 0), n = Number(row.noShows || 0);
            var ch = c / max * (bottom - top), nh = n / max * (bottom - top);
            svg.appendChild(svgNode('rect', { x: x, y: bottom - ch, width: w, height: ch, rx: 2, fill: '#d4837d' }));
            svg.appendChild(svgNode('rect', { x: x + w + 3, y: bottom - nh, width: w, height: nh, rx: 2, fill: '#d9ae5f' }));
            addText(svg, x + w, height - 7, row.label || '', 'middle');
        });
    }

    function normalizeRoomFilterState(value, isDirty) {
        var state = String(value || '').toLowerCase().replace(/[\s_-]+/g, '');

        if (isDirty || state === 'dirty' || state === 'checkout' || state === 'checkedout')
            return 'checkout';

        if (state === 'checkin' || state === 'checkedin' || state === 'inhouse')
            return 'checkin';

        if (state === 'reservation' || state === 'reserved')
            return 'reservation';

        if (state === 'ooo' || state === 'blocked' || state === 'block' || state === 'outoforder')
            return 'ooo';

        return 'vacant';
    }

    var activeRoomFilter = 'all';
    function applyRoomFilter(filter) {
        activeRoomFilter = normalizeRoomFilterState(filter === 'all' ? 'all' : filter, false);
        if (filter === 'all') activeRoomFilter = 'all';

        document.querySelectorAll('[data-room-filter]').forEach(function (button) {
            button.classList.toggle(
                'is-active',
                button.getAttribute('data-room-filter') === activeRoomFilter
            );
        });

        document.querySelectorAll('#dashboardRoomRack [data-room-state]').forEach(function (room) {
            var roomState = normalizeRoomFilterState(
                room.getAttribute('data-room-state'),
                room.classList.contains('dashboard-room-dirty')
            );
            var shouldHide = activeRoomFilter !== 'all' && roomState !== activeRoomFilter;

            room.classList.toggle('dashboard-room-filter-hidden', shouldHide);
            room.setAttribute('aria-hidden', shouldHide ? 'true' : 'false');
        });
    }

    function roomStateLabel(row) {
        if (row.state === 'ooo') return 'Out of order';
        if (row.state === 'checkin') return 'Checked in';
        if (row.state === 'reservation') return 'Reservation';
        if (row.state === 'dirty' || row.isDirty) return 'Checked out';
        if (row.state === 'checkout') return 'Checked out';
        return 'Vacant';
    }

    function roomFilterState(row) {
        return normalizeRoomFilterState(row && row.state, !!(row && row.isDirty));
    }

    function formatRoomDate(value) {
        if (!value) return '';
        var text = String(value).slice(0, 10);
        var parts = text.split('-');
        if (parts.length !== 3) return '';
        var months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
        var month = months[Math.max(0, Math.min(11, Number(parts[1]) - 1))] || '';
        return String(Number(parts[2])) + ' ' + month;
    }

    function updateRoomLegend(rows) {
        var counts = { all: 0, checkin: 0, reservation: 0, checkout: 0, vacant: 0, ooo: 0, pending: 0, partial: 0, paid: 0 };
        (rows || []).forEach(function (row) {
            counts.all += 1;
            var state = roomFilterState(row);
            if (counts[state] != null) counts[state] += 1;
            var payment = row && row.paymentState ? String(row.paymentState).toLowerCase() : '';
            if (counts[payment] != null) counts[payment] += 1;
        });
        Object.keys(counts).forEach(function (key) {
            document.querySelectorAll('[data-room-legend-count="' + key + '"]').forEach(function (node) {
                node.textContent = String(counts[key]);
            });
            document.querySelectorAll('[data-room-filter-count="' + key + '"]').forEach(function (node) {
                node.textContent = String(counts[key]);
            });
        });
    }

    function appendRoomTip(button, row) {
        var tip = document.createElement('span');
        tip.className = 'dashboard-room-tip';
        tip.setAttribute('role', 'tooltip');

        var name = document.createElement('strong');
        name.textContent = row.guestName || ('Room ' + (row.roomNo || ''));
        tip.appendChild(name);

        var meta = document.createElement('span');
        var metaParts = ['Room ' + (row.roomNo || '')];
        if (row.category) metaParts.push(row.category);
        if (row.reservationId) metaParts.push(row.reservationId);
        meta.textContent = metaParts.join(' · ');
        tip.appendChild(meta);

        var status = document.createElement('span');
        var dates = '';
        if (row.arrivalDate && row.departureDate) dates = formatRoomDate(row.arrivalDate) + '–' + formatRoomDate(row.departureDate);
        status.textContent = roomStateLabel(row) + (dates ? ' · ' + dates : '');
        tip.appendChild(status);

        if (Number(row.balance || 0) > 0) {
            var balance = document.createElement('b');
            var paymentState = String(row.paymentState || 'pending').toLowerCase();
            balance.className = paymentState === 'partial' ? 'dashboard-room-balance-partial' : 'dashboard-room-balance-pending';
            var page = document.querySelector('.dashboard-page');
            var currency = page ? (page.getAttribute('data-currency') || '£') : '£';
            balance.textContent = currency + Number(row.balance || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + (paymentState === 'partial' ? ' part paid' : ' pending');
            tip.appendChild(balance);
        } else if (row.state !== 'vacant' && row.state !== 'ooo') {
            var settled = document.createElement('b');
            settled.className = 'dashboard-room-balance-settled';
            settled.textContent = 'Settled';
            tip.appendChild(settled);
        }

        button.appendChild(tip);
    }

    function renderRoomRack(rows) {
        var rack = document.getElementById('dashboardRoomRack');
        if (!rack) return;
        rack.replaceChildren();
        (rows || []).forEach(function (row) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'dashboard-room dashboard-room-' + (row.state || 'vacant');
            button.setAttribute('data-room-state', roomFilterState(row));
            button.setAttribute('data-room-payment', row.paymentState || '');
            button.setAttribute('aria-label', 'Room ' + (row.roomNo || '') + ' ' + roomStateLabel(row));

            var number = document.createElement('span');
            number.className = 'dashboard-room-no';
            number.textContent = row.roomNo || '';
            button.appendChild(number);

            var paymentState = String(row.paymentState || '').toLowerCase();
            if (paymentState === 'pending' || paymentState === 'partial' || paymentState === 'paid') {
                var dot = document.createElement('span');
                dot.className = 'dashboard-room-dot dashboard-room-dot-' + paymentState;
                dot.setAttribute('aria-hidden', 'true');
                button.appendChild(dot);
            }

            appendRoomTip(button, row);
            rack.appendChild(button);
        });
        updateRoomLegend(rows || []);
        applyRoomFilter(activeRoomFilter);
    }

    async function loadRoomDate(date) {
        var page = document.querySelector('.dashboard-page');
        var rack = document.getElementById('dashboardRoomRack');
        if (!page || !rack) return;
        var url = page.getAttribute('data-room-status-url');
        if (!url) return;
        rack.setAttribute('aria-busy', 'true');
        rack.classList.add('is-loading');
        try {
            var response = await fetch(url + '?date=' + encodeURIComponent(date), { headers: { 'X-Requested-With': 'XMLHttpRequest' }, credentials: 'same-origin' });
            var payload = await response.json();
            if (!response.ok || !payload.ok) throw new Error(payload.message || 'Unable to load room status.');
            renderRoomRack(payload.rows || []);
        } catch (error) {
            console.warn(error);
        } finally {
            rack.classList.remove('is-loading');
            rack.removeAttribute('aria-busy');
        }
    }

    function ensureRoomStatusTemplateControls() {
        var card = document.querySelector('.dashboard-room-status-card');
        if (!card) return;

        // Remove the legacy large status-summary boxes if an older view/CSS is
        // still present. The supplied dashboard template uses header filters.
        card.querySelectorAll('.dashboard-room-summary').forEach(function (node) {
            node.remove();
        });

        var head = card.querySelector('.dashboard-room-status-head') || card.querySelector('.dashboard-card-head');
        if (!head) return;

        var controls = head.querySelector('.dashboard-room-controls');
        if (!controls) {
            controls = document.createElement('div');
            controls.className = 'dashboard-room-controls';
            head.appendChild(controls);
        }

        var select = controls.querySelector('#dashboardRoomDate');
        var filters = [
            ['all', 'All'],
            ['checkin', 'Check in'],
            ['reservation', 'Reservation'],
            ['checkout', 'Check out'],
            ['vacant', 'Vacant'],
            ['ooo', 'Blocked']
        ];

        filters.forEach(function (item, index) {
            if (controls.querySelector('[data-room-filter="' + item[0] + '"]')) return;
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'dashboard-pill' + (item[0] === activeRoomFilter ? ' is-active' : '');
            button.setAttribute('data-room-filter', item[0]);
            button.appendChild(document.createTextNode(item[1] + ' '));
            var count = document.createElement('span');
            count.setAttribute('data-room-filter-count', item[0]);
            count.textContent = '0';
            button.appendChild(count);
            controls.insertBefore(button, select || null);
        });

        var body = card.querySelector('.dashboard-room-status-body') || card.querySelector('.dashboard-card-body');
        var rack = card.querySelector('#dashboardRoomRack');
        var legend = card.querySelector('#dashboardRoomLegend');
        if (body && rack && !legend) {
            legend = document.createElement('div');
            legend.id = 'dashboardRoomLegend';
            legend.className = 'dashboard-room-legend';
            legend.setAttribute('aria-label', 'Room status legend');

            [
                ['all', 'dashboard-legend-all', 'Total rooms'],
                ['checkin', 'dashboard-legend-checkin', 'Checked in'],
                ['reservation', 'dashboard-legend-reservation', 'Reservation'],
                ['checkout', 'dashboard-legend-checkout', 'Checked out'],
                ['vacant', 'dashboard-legend-vacant', 'Vacant'],
                ['ooo', 'dashboard-legend-ooo', 'Out of order'],
                ['pending', 'dashboard-legend-pending', 'Not paid'],
                ['partial', 'dashboard-legend-partial', 'Partially paid'],
                ['paid', 'dashboard-legend-paid', 'Fully paid']
            ].forEach(function (item) {
                var pill = document.createElement('span');
                pill.className = 'dashboard-room-legend-item' + (item[0] === 'all' ? ' dashboard-room-total' : '');
                pill.innerHTML = '<i class="' + item[1] + '"></i>' + item[2] + ' <b data-room-legend-count="' + item[0] + '">0</b>';
                legend.appendChild(pill);
            });

            body.appendChild(legend);
        }
    }

    function wireRoomControls() {
        var card = document.querySelector('.dashboard-room-status-card');
        if (card && !card.dataset.roomFilterWired) {
            card.dataset.roomFilterWired = '1';
            card.addEventListener('click', function (event) {
                var button = event.target.closest('[data-room-filter]');
                if (!button || !card.contains(button)) return;
                applyRoomFilter(button.getAttribute('data-room-filter') || 'all');
            });
        }

        var date = document.getElementById('dashboardRoomDate');
        if (date && !date.dataset.roomDateWired) {
            date.dataset.roomDateWired = '1';
            date.addEventListener('change', function () { loadRoomDate(date.value); });
        }
    }

    function wireGuestFilters() {
        document.querySelectorAll('[data-guest-filter]').forEach(function (button) {
            button.addEventListener('click', function () {
                var mode = button.getAttribute('data-guest-filter') || 'all';
                document.querySelectorAll('[data-guest-filter]').forEach(function (x) { x.classList.toggle('is-active', x === button); });
                document.querySelectorAll('[data-guest-due]').forEach(function (row) {
                    var visible = mode === 'all' || (mode === 'due' && row.getAttribute('data-guest-due') === '1') || (mode === 'balance' && row.getAttribute('data-guest-balance') === '1');
                    row.hidden = !visible;
                });
            });
        });
    }

    function wireOccupancyTabs() {
        document.querySelectorAll('[data-occ-days]').forEach(function (button) {
            button.addEventListener('click', function () {
                document.querySelectorAll('[data-occ-days]').forEach(function (x) { x.classList.toggle('is-active', x === button); });
                drawOccupancyOnBooks(Number(button.getAttribute('data-occ-days') || 30));
            });
        });
    }

    function closeStatisticModal() {
        var overlay = document.getElementById('dashboardStatisticOverlay');
        var modal = document.getElementById('dashboardStatisticModal');
        if (overlay) overlay.hidden = true;
        if (modal) modal.hidden = true;
        document.body.classList.remove('dashboard-modal-open');
    }

    function setText(id, value) {
        var node = document.getElementById(id);
        if (node) node.textContent = value == null ? '' : String(value);
    }

    function renderStatisticDetail(detail) {
        var loading = document.getElementById('dashboardStatisticLoading');
        var wrap = document.getElementById('dashboardStatisticTableWrap');
        var empty = document.getElementById('dashboardStatisticEmpty');
        var head = document.getElementById('dashboardStatisticHead');
        var body = document.getElementById('dashboardStatisticBody');
        if (loading) loading.hidden = true;
        setText('dashboardStatisticTitle', detail.title || 'Details');
        setText('dashboardStatisticSubtitle', detail.subtitle || '');
        setText('dashboardStatisticSummaryLabel', detail.summaryLabel || 'Total');
        setText('dashboardStatisticSummaryValue', detail.summaryValue || '—');
        if (head) head.replaceChildren();
        if (body) body.replaceChildren();

        (detail.columns || []).forEach(function (column) {
            var th = document.createElement('th');
            th.textContent = column;
            if (head) head.appendChild(th);
        });
        (detail.rows || []).forEach(function (row) {
            var tr = document.createElement('tr');
            (row || []).forEach(function (value) {
                var td = document.createElement('td');
                td.textContent = value == null ? '' : String(value);
                tr.appendChild(td);
            });
            if (body) body.appendChild(tr);
        });
        var hasRows = Array.isArray(detail.rows) && detail.rows.length > 0;
        if (wrap) wrap.hidden = !hasRows;
        if (empty) empty.hidden = hasRows;
    }

    async function openStatisticModal(type) {
        var page = document.querySelector('.dashboard-page');
        var url = page ? page.getAttribute('data-statistic-url') : '';
        var overlay = document.getElementById('dashboardStatisticOverlay');
        var modal = document.getElementById('dashboardStatisticModal');
        var loading = document.getElementById('dashboardStatisticLoading');
        var wrap = document.getElementById('dashboardStatisticTableWrap');
        var empty = document.getElementById('dashboardStatisticEmpty');
        if (!url || !overlay || !modal) return;

        overlay.hidden = false;
        modal.hidden = false;
        document.body.classList.add('dashboard-modal-open');
        if (loading) loading.hidden = false;
        if (wrap) wrap.hidden = true;
        if (empty) empty.hidden = true;
        setText('dashboardStatisticTitle', 'Loading details…');
        setText('dashboardStatisticSubtitle', '');
        setText('dashboardStatisticSummaryLabel', '');
        setText('dashboardStatisticSummaryValue', '');

        try {
            var dashboardDate = page.getAttribute('data-dashboard-date') || '';
            var response = await fetch(url + '?type=' + encodeURIComponent(type) + '&date=' + encodeURIComponent(dashboardDate), {
                headers: { 'X-Requested-With': 'XMLHttpRequest' },
                credentials: 'same-origin'
            });
            var payload = await response.json();
            if (!response.ok || !payload.ok) throw new Error(payload.message || 'Unable to load dashboard details.');
            renderStatisticDetail(payload.detail || {});
        } catch (error) {
            if (loading) loading.hidden = true;
            if (wrap) wrap.hidden = true;
            if (empty) {
                empty.hidden = false;
                empty.textContent = error && error.message ? error.message : 'Unable to load dashboard details.';
            }
        }
    }

    function wireStatisticDetails() {
        document.querySelectorAll('[data-dashboard-statistic]').forEach(function (node) {
            node.addEventListener('click', function (event) {
                event.preventDefault();
                openStatisticModal(node.getAttribute('data-dashboard-statistic') || '');
            });
        });
        document.querySelectorAll('[data-dashboard-stat-close]').forEach(function (node) {
            node.addEventListener('click', closeStatisticModal);
        });
        var overlay = document.getElementById('dashboardStatisticOverlay');
        if (overlay) overlay.addEventListener('click', closeStatisticModal);
        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape') closeStatisticModal();
        });
    }

    function updateJsonNode(id, value) {
        var node = document.getElementById(id);
        if (node) node.textContent = JSON.stringify(value || []);
    }

    function pageCurrency() {
        var page = document.querySelector('.dashboard-page');
        return page ? (page.getAttribute('data-currency') || '£') : '£';
    }

    function money0(value) {
        var number = Number(value || 0);
        return pageCurrency() + number.toLocaleString(undefined, { maximumFractionDigits: 0 });
    }

    function setHtmlText(id, value) {
        var node = document.getElementById(id);
        if (node) node.textContent = value;
    }

    function createCell(value, right) {
        var td = document.createElement('td');
        td.textContent = value == null ? '' : String(value);
        if (right) td.className = 'dashboard-right';
        return td;
    }

    function renderRevenueSources(rows) {
        var body = document.getElementById('dashboardRevenueSourcesBody');
        if (!body) return;
        body.replaceChildren();
        if (!rows || !rows.length) {
            var emptyRow = document.createElement('tr');
            var empty = createCell('No revenue on the books for this period.', false);
            empty.colSpan = 5;
            empty.className = 'dashboard-empty-cell';
            emptyRow.appendChild(empty);
            body.appendChild(emptyRow);
            return;
        }
        rows.forEach(function (item) {
            var tr = document.createElement('tr');
            tr.appendChild(createCell(item.source || 'Unknown', false));
            tr.appendChild(createCell(item.roomNights || 0, true));
            tr.appendChild(createCell(money0(item.adr), true));
            tr.appendChild(createCell(money0(item.revenue), true));
            tr.appendChild(createCell(Number(item.sharePercent || 0).toFixed(1).replace(/\.0$/, '') + '%', true));
            body.appendChild(tr);
        });
    }

    function renderRoomTypes(rows) {
        var body = document.getElementById('dashboardRoomTypesBody');
        if (!body) return;
        body.replaceChildren();
        if (!rows || !rows.length) {
            var emptyRow = document.createElement('tr');
            var empty = createCell('No room-type performance data found.', false);
            empty.colSpan = 8;
            empty.className = 'dashboard-empty-cell';
            emptyRow.appendChild(empty);
            body.appendChild(emptyRow);
            return;
        }
        rows.forEach(function (item) {
            var tr = document.createElement('tr');
            var name = createCell('', false);
            var strong = document.createElement('strong');
            strong.textContent = item.category || '';
            name.appendChild(strong);
            tr.appendChild(name);
            tr.appendChild(createCell(item.rooms || 0, true));

            var tonight = document.createElement('td');
            var progress = document.createElement('div');
            progress.className = 'dashboard-progress';
            var bar = document.createElement('span');
            bar.style.width = Math.max(0, Math.min(100, Number(item.occupancyTonight || 0))) + '%';
            progress.appendChild(bar);
            var small = document.createElement('small');
            small.textContent = Number(item.occupancyTonight || 0).toFixed(1).replace(/\.0$/, '') + '%';
            tonight.appendChild(progress);
            tonight.appendChild(small);
            tr.appendChild(tonight);

            tr.appendChild(createCell(Number(item.occupancyNext30 || 0).toFixed(1).replace(/\.0$/, '') + '%', true));
            tr.appendChild(createCell(money0(item.adr), true));
            tr.appendChild(createCell(money0(item.revPar), true));
            tr.appendChild(createCell(money0(item.revenue), true));
            tr.appendChild(createCell(Number(item.contributionPercent || 0).toFixed(1).replace(/\.0$/, '') + '%', true));

            body.appendChild(tr);
        });
    }

    function renderPrices(rows) {
        var root = document.getElementById('dashboardPrices');
        if (!root) return;
        root.replaceChildren();
        if (!rows || !rows.length) {
            var empty = document.createElement('div');
            empty.className = 'dashboard-empty-block';
            empty.textContent = 'No active rate-plan data was found for today.';
            root.appendChild(empty);
            return;
        }
        rows.forEach(function (item) {
            var card = document.createElement('article');
            card.className = 'dashboard-price-card';
            var name = document.createElement('span');
            name.textContent = item.category || '';
            var rate = document.createElement('strong');
            var oldRate = document.createElement('s');
            oldRate.textContent = money0(item.currentRate);
            var arrow = document.createElement('i');
            arrow.className = 'fa-solid fa-arrow-right';
            rate.appendChild(oldRate);
            rate.appendChild(arrow);
            rate.appendChild(document.createTextNode(' ' + money0(item.recommendedRate)));
            var note = document.createElement('small');
            var change = Number(item.changePercent || 0);
            note.className = change > 0 ? 'dashboard-positive-text' : change < 0 ? 'dashboard-negative-text' : '';
            note.textContent = (change > 0 ? 'Raise ' : change < 0 ? 'Reduce ' : 'Hold ') + Math.abs(change).toFixed(1).replace(/\.0$/, '') + '% · ' + (item.reason || '');
            card.appendChild(name);
            card.appendChild(rate);
            card.appendChild(note);
            root.appendChild(card);
        });
    }

    function renderDecisions(rows) {
        var root = document.getElementById('dashboardDecisions');
        if (!root) return;
        root.replaceChildren();
        (rows || []).forEach(function (item) {
            var li = document.createElement('li');
            li.className = 'dashboard-decision dashboard-decision-' + (item.tone || 'info');
            var dot = document.createElement('i');
            var copy = document.createElement('div');
            var title = document.createElement('strong');
            var detail = document.createElement('p');
            title.textContent = item.title || '';
            detail.textContent = item.detail || '';
            copy.appendChild(title);
            copy.appendChild(detail);
            li.appendChild(dot);
            li.appendChild(copy);
            root.appendChild(li);
        });
        if (!root.children.length) {
            var li = document.createElement('li');
            li.className = 'dashboard-decision dashboard-decision-positive';
            li.innerHTML = '<i></i><div><strong>No urgent revenue actions</strong><p>No high-priority dashboard exception is currently available.</p></div>';
            root.appendChild(li);
        }
    }

    function renderAnalyticsFailure(message) {
        ['dashboardRevenueSourcesBody', 'dashboardRoomTypesBody'].forEach(function (id) {
            var body = document.getElementById(id);
            if (!body) return;
            var columns = id === 'dashboardRevenueSourcesBody' ? 5 : 8;
            body.replaceChildren();
            var tr = document.createElement('tr');
            var td = createCell(message || 'Analytics could not be loaded.', false);
            td.colSpan = columns;
            td.className = 'dashboard-empty-cell';
            tr.appendChild(td);
            body.appendChild(tr);
        });
        var prices = document.getElementById('dashboardPrices');
        if (prices) prices.innerHTML = '<div class="dashboard-empty-block">Analytics could not be loaded.</div>';
        var decisions = document.getElementById('dashboardDecisions');
        if (decisions) decisions.innerHTML = '<li class="dashboard-decision dashboard-decision-warning"><i></i><div><strong>Analytics unavailable</strong><p>Live operational figures above are still available.</p></div></li>';
    }

    async function loadAnalytics() {
        var page = document.querySelector('.dashboard-page');
        var url = page ? page.getAttribute('data-analytics-url') : '';
        if (!url) return;
        try {
            var response = await fetch(url, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' },
                credentials: 'same-origin'
            });
            var payload = await response.json();
            if (!response.ok || !payload.ok) throw new Error(payload.message || 'Dashboard analytics could not be loaded.');
            var data = payload.analytics || {};

            updateJsonNode('dashboardOccupancyData', data.occupancySeries || []);
            updateJsonNode('dashboardOccupancyLyData', data.occupancyLastYearSeries || []);
            updateJsonNode('dashboardFinancialData', data.financialSeries || []);

            setHtmlText('dashboardOcc30', Number(data.occupancyNext30 || 0).toFixed(1).replace(/\.0$/, '') + '%');
            setHtmlText('dashboardOcc60', Number(data.occupancyNext60 || 0).toFixed(1).replace(/\.0$/, '') + '%');
            setHtmlText('dashboardOcc90', Number(data.occupancyNext90 || 0).toFixed(1).replace(/\.0$/, '') + '%');
            setHtmlText('dashboardForecast7', Number(data.forecastNext7 || 0).toFixed(1).replace(/\.0$/, '') + '%');
            setHtmlText('dashboardForecastTrend7', Number(data.forecastNext7 || 0).toFixed(1).replace(/\.0$/, '') + '%');

            var financial = data.financialSeries || [];
            var revenue30 = financial.reduce(function (sum, x) { return sum + Number(x.revenue || 0); }, 0);
            var profit30 = financial.reduce(function (sum, x) { return sum + Number(x.profit || 0); }, 0);
            var margin = revenue30 > 0 ? profit30 * 100 / revenue30 : 0;
            setHtmlText('dashboardRevenue30', money0(revenue30));
            setHtmlText('dashboardProfit30', money0(profit30));
            setHtmlText('dashboardProfitMargin30', margin.toFixed(1).replace(/\.0$/, '') + '%');

            renderRevenueSources(data.revenueSources || []);
            renderRoomTypes(data.roomTypes || []);
            renderPrices(data.priceRecommendations || []);
            renderDecisions(data.decisions || []);

            document.querySelectorAll('[data-occ-days]').forEach(function (button) { button.disabled = false; });
            drawOccupancyOnBooks(30);
            drawWhenVisible('dashboardRevenueChart', drawRevenueTrend);
            drawWhenVisible('dashboardOccupancyTrendChart', drawOccupancyTrend);
        } catch (error) {
            console.warn(error);
            renderAnalyticsFailure(error && error.message ? error.message : 'Dashboard analytics could not be loaded.');
        }
    }

    function drawWhenVisible(elementId, draw) {
        var element = document.getElementById(elementId);
        if (!element) return;
        if (!('IntersectionObserver' in window)) { draw(); return; }
        var observer = new IntersectionObserver(function (entries) {
            if (!entries.some(function (entry) { return entry.isIntersecting; })) return;
            observer.disconnect();
            window.requestAnimationFrame(draw);
        }, { rootMargin: '220px 0px' });
        observer.observe(element);
    }

    document.addEventListener('DOMContentLoaded', function () {
        ensureRoomStatusTemplateControls();
        wireRoomControls();
        wireGuestFilters();
        wireOccupancyTabs();
        wireStatisticDetails();
        updateRoomLegend(Array.prototype.map.call(document.querySelectorAll('#dashboardRoomRack [data-room-state]'), function (node) {
            return {
                state: node.getAttribute('data-room-state') || 'vacant',
                paymentState: node.getAttribute('data-room-payment') || ''
            };
        }));
        drawWhenVisible('dashboardOccupancyChart', function () { drawOccupancyOnBooks(30); });
        drawWhenVisible('dashboardExceptionChart', drawExceptionChart);

        // Let the live room/guest/financial headline render first, then retrieve
        // heavier charts and rate analysis without holding up first paint.
        var startAnalytics = function () { loadAnalytics(); };
        if ('requestIdleCallback' in window) {
            window.requestIdleCallback(startAnalytics, { timeout: 500 });
        } else {
            window.setTimeout(startAnalytics, 80);
        }
    });
})();
