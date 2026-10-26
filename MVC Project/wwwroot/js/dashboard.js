(function () {
    'use strict';

    function readJson(id) {
        var node = document.getElementById(id);
        if (!node) return [];
        try { return JSON.parse(node.textContent || '[]'); }
        catch (_) { return []; }
    }

    function selectedPeriod() {
        var page = document.querySelector('.dashboard-page');
        return {
            start: page ? (page.getAttribute('data-range-start') || '') : '',
            end: page ? (page.getAttribute('data-range-end') || '') : ''
        };
    }

    // A single floating tooltip serves every SVG chart. Hover targets are computed from
    // each chart's viewBox, so even thin lines work reliably on touchpads and smaller screens.
    function chartTooltip() {
        var tip = document.getElementById('dashboardChartTooltip');
        if (!tip) {
            tip = document.createElement('div');
            tip.id = 'dashboardChartTooltip';
            tip.className = 'dashboard-chart-tooltip';
            tip.setAttribute('role', 'tooltip');
            tip.hidden = true;
            document.body.appendChild(tip);
        }
        return tip;
    }

    function fullDate(value) {
        if (!value) return '';
        var parts = String(value).slice(0, 10).split('-').map(Number);
        if (parts.length !== 3 || !parts[0] || !parts[1] || !parts[2]) return String(value);
        return new Date(parts[0], parts[1] - 1, parts[2]).toLocaleDateString(undefined, {
            weekday: 'short', day: 'numeric', month: 'short', year: 'numeric'
        });
    }

    function formatChartNumber(value, format) {
        var n = Number(value || 0);
        if (format === 'percent') return n.toLocaleString(undefined, { maximumFractionDigits: 1 }) + '%';
        if (format === 'money') return pageCurrency() + " " + n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        return n.toLocaleString(undefined, { maximumFractionDigits: 0 });
    }

    function attachChartHover(svg, labels, dates, series, extras, plot, min, max, format) {
        if (!svg || !labels.length) return;
        var tooltip = chartTooltip();
        var span = (max - min) || 1;
        // Transparent hit surface makes hover work *between* SVG lines/bars,
        // not only when the cursor lands exactly on a 2px path.
        svg.appendChild(svgNode('rect', {
            x: plot.left, y: plot.top,
            width: Math.max(1, plot.right - plot.left),
            height: Math.max(1, plot.bottom - plot.top),
            fill: 'transparent', 'pointer-events': 'all'
        }));
        var guide = svgNode('line', { y1: plot.top, y2: plot.bottom, stroke: '#6c8098', 'stroke-width': 1, 'stroke-dasharray': '3 3', 'pointer-events': 'none' });
        guide.style.display = 'none';
        svg.appendChild(guide);
        var dots = series.map(function (item) {
            if (item.marker === false) return null;
            var dot = svgNode('circle', { r: 3.6, fill: '#fff', stroke: item.color || '#5f97c2', 'stroke-width': 2, 'pointer-events': 'none' });
            dot.style.display = 'none';
            svg.appendChild(dot);
            return dot;
        });
        var currentIndex = 0;
        function display(index, clientX, clientY) {
            index = Math.max(0, Math.min(labels.length - 1, index));
            currentIndex = index;
            var x = labels.length <= 1 ? (plot.left + plot.right) / 2 : plot.left + index * (plot.right - plot.left) / (labels.length - 1);
            guide.setAttribute('x1', x);
            guide.setAttribute('x2', x);
            guide.style.display = '';
            dots.forEach(function (dot, i) {
                if (!dot) return;
                var value = Number((series[i].values || [])[index]);
                if (!Number.isFinite(value)) { dot.style.display = 'none'; return; }
                var y = plot.bottom - (value - min) / span * (plot.bottom - plot.top);
                dot.setAttribute('cx', x);
                dot.setAttribute('cy', y);
                dot.style.display = '';
            });
            tooltip.replaceChildren();
            var heading = document.createElement('strong');
            heading.className = 'dashboard-chart-tooltip-date';
            heading.textContent = fullDate(dates[index]) || labels[index];
            tooltip.appendChild(heading);
            series.forEach(function (item) {
                var value = (item.values || [])[index];
                if (value == null || !Number.isFinite(Number(value))) return;
                var row = document.createElement('div');
                row.className = 'dashboard-chart-tooltip-row';
                var label = document.createElement('span');
                var dot = document.createElement('i');
                dot.style.backgroundColor = item.color || '#5f97c2';
                label.appendChild(dot);
                label.appendChild(document.createTextNode(item.label || 'Value'));
                var number = document.createElement('b');
                number.textContent = formatChartNumber(value, item.format || format);
                row.appendChild(label);
                row.appendChild(number);
                tooltip.appendChild(row);
            });
            var detail = extras && extras[index];
            if (detail && detail.length) {
                var divider = document.createElement('div');
                divider.className = 'dashboard-chart-tooltip-separator';
                tooltip.appendChild(divider);
                detail.forEach(function (item) {
                    var row = document.createElement('div');
                    row.className = 'dashboard-chart-tooltip-detail';
                    var text = document.createElement('span');
                    text.textContent = item[0];
                    var number = document.createElement('b');
                    number.textContent = String(item[1]);
                    row.appendChild(text);
                    row.appendChild(number);
                    tooltip.appendChild(row);
                });
            }
            tooltip.hidden = false;
            // Keep tooltip inside the viewport, including on narrow dashboard cards.
            var box = tooltip.getBoundingClientRect();
            var left = Math.max(8, Math.min(window.innerWidth - box.width - 8, clientX + 16));
            var top = clientY + 14;
            if (top + box.height > window.innerHeight - 8) top = clientY - box.height - 14;
            tooltip.style.left = Math.max(8, left) + 'px';
            tooltip.style.top = Math.max(8, top) + 'px';
        }
        function hide() {
            tooltip.hidden = true;
            guide.style.display = 'none';
            dots.forEach(function (dot) { if (dot) dot.style.display = 'none'; });
        }
        svg.onmousemove = function (event) {
            var rect = svg.getBoundingClientRect();
            if (!rect.width) return;
            var vb = (svg.getAttribute('viewBox') || '0 0 560 175').split(/\s+/).map(Number);
            var x = (event.clientX - rect.left) * ((vb[2] || 560) / rect.width);
            if (x < plot.left || x > plot.right) { hide(); return; }
            var i = labels.length === 1 ? 0 : Math.round((x - plot.left) * (labels.length - 1) / (plot.right - plot.left));
            display(i, event.clientX, event.clientY);
        };
        svg.onmouseleave = hide;
        svg.setAttribute('tabindex', '0');
        svg.setAttribute('aria-description', 'Hover for daily details, or use left and right arrow keys after focusing this chart.');
        svg.onfocus = function () {
            var rect = svg.getBoundingClientRect();
            display(currentIndex, rect.left + rect.width / 2, rect.top + 20);
        };
        svg.onblur = hide;
        svg.onkeydown = function (event) {
            if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
            event.preventDefault();
            var next = currentIndex + (event.key === 'ArrowRight' ? 1 : -1);
            var rect = svg.getBoundingClientRect();
            display(next, rect.left + rect.width / 2, rect.top + 20);
        };
    }

    // Availability page's two-month range picker, reused for Dashboard dates.
    // The Dashboard changes only what Apply does: submit the selected period.
    function wireDateRangeFilter() {
        var form = document.getElementById('dashboardDateFilter');
        if (!form) return;
        var startInput = document.getElementById('dashboardStartDate');
        var endInput = document.getElementById('dashboardEndDate');
        var rangeDisplay = document.getElementById('inventoryRangeDisplay');
        var rangePopover = document.getElementById('inventoryRangePopover');
        var rangeText = document.getElementById('inventoryRangeText');
        var leftMonthLabel = document.getElementById('leftMonthLabel');
        var rightMonthLabel = document.getElementById('rightMonthLabel');
        var leftCalendarGrid = document.getElementById('leftCalendarGrid');
        var rightCalendarGrid = document.getElementById('rightCalendarGrid');
        var selectedRangeText = document.getElementById('selectedRangeText');
        var error = document.getElementById('dashboardRangeError');
        var calendarLeftMonth = null;
        var draftRangeStart = null;
        var draftRangeEnd = null;

        function parseIsoDate(value) {
            if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
            var p = value.split('-').map(Number);
            var date = new Date(p[0], p[1] - 1, p[2]);
            return date.getFullYear() === p[0] && date.getMonth() === p[1] - 1 && date.getDate() === p[2] ? date : null;
        }
        function toIsoDate(date) {
            return date.getFullYear() + '-' + String(date.getMonth() + 1).padStart(2, '0') + '-' + String(date.getDate()).padStart(2, '0');
        }
        function sameDay(a, b) { return a && b && a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }
        function dayTime(date) { return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime(); }
        function monthStart(date) { return new Date(date.getFullYear(), date.getMonth(), 1); }
        function addMonths(date, amount) { return new Date(date.getFullYear(), date.getMonth() + amount, 1); }
        function formatDisplayDate(date) { return date ? String(date.getDate()).padStart(2, '0') + '/' + String(date.getMonth() + 1).padStart(2, '0') + '/' + date.getFullYear() : ''; }
        function monthTitle(date) { return date.toLocaleString('en-GB', { month: 'short', year: 'numeric' }); }
        function showError(message) {
            if (!error) return;
            error.textContent = message || '';
            error.hidden = !message;
        }
        function updateRangeText() {
            if (rangeText) rangeText.textContent = formatDisplayDate(parseIsoDate(startInput.value)) + ' - ' + formatDisplayDate(parseIsoDate(endInput.value));
        }
        function updateDraftRangeText() {
            if (!selectedRangeText) return;
            if (!draftRangeStart) { selectedRangeText.textContent = 'Select date range'; return; }
            selectedRangeText.textContent = formatDisplayDate(draftRangeStart) + ' - ' + (draftRangeEnd ? formatDisplayDate(draftRangeEnd) : 'Select end date');
        }
        function selectCalendarDate(date) {
            var clicked = new Date(date.getFullYear(), date.getMonth(), date.getDate());
            if (!draftRangeStart || draftRangeEnd) { draftRangeStart = clicked; draftRangeEnd = null; }
            else if (dayTime(clicked) < dayTime(draftRangeStart)) { draftRangeEnd = draftRangeStart; draftRangeStart = clicked; }
            else draftRangeEnd = clicked;
            showError(draftRangeEnd ? validateRange(draftRangeStart, draftRangeEnd) : '');
            // Do not rebuild 84 date buttons after every click. Just update the highlights.
            repaintCalendarSelection();
        }
        function repaintCalendarSelection() {
            var start = draftRangeStart ? toIsoDate(draftRangeStart) : '';
            var end = draftRangeEnd ? toIsoDate(draftRangeEnd) : '';
            [leftCalendarGrid, rightCalendarGrid].forEach(function (grid) {
                if (!grid) return;
                grid.querySelectorAll('button[data-date]').forEach(function (button) {
                    var iso = button.dataset.date;
                    button.classList.toggle('range-fill', !!(start && end && iso >= start && iso <= end));
                    button.classList.toggle('range-start', !!(start && iso === start));
                    button.classList.toggle('range-end', !!(end && iso === end));
                });
            });
            updateDraftRangeText();
        }
        function renderMonth(gridElement, monthDate) {
            if (!gridElement) return;
            var first = monthStart(monthDate);
            var firstVisible = new Date(first.getFullYear(), first.getMonth(), 1 - first.getDay());
            var month = first.getMonth();
            var todayIso = toIsoDate(new Date());
            var startIso = draftRangeStart ? toIsoDate(draftRangeStart) : '';
            var endIso = draftRangeEnd ? toIsoDate(draftRangeEnd) : '';
            var pieces = [];
            for (var i = 0; i < 42; i++) {
                var current = new Date(firstVisible.getFullYear(), firstVisible.getMonth(), firstVisible.getDate() + i);
                var iso = toIsoDate(current);
                var classes = 'inventory-calendar-day';
                if (current.getMonth() !== month) classes += ' outside-month';
                if (iso === todayIso) classes += ' today';
                if (startIso && endIso && iso >= startIso && iso <= endIso) classes += ' range-fill';
                if (startIso && iso === startIso) classes += ' range-start';
                if (endIso && iso === endIso) classes += ' range-end';
                // HTML contains only calculated ISO dates, day numbers and fixed CSS classes.
                pieces.push('<button type="button" class="' + classes + '" data-date="' + iso + '">' + current.getDate() + '</button>');
            }
            // One DOM insertion per month (previously 42 separate insertions).
            gridElement.innerHTML = pieces.join('');
        }
        function renderDateRangeCalendars() {
            if (!calendarLeftMonth) calendarLeftMonth = monthStart(draftRangeStart || parseIsoDate(startInput.value) || new Date());
            var right = addMonths(calendarLeftMonth, 1);
            if (leftMonthLabel) leftMonthLabel.textContent = monthTitle(calendarLeftMonth);
            if (rightMonthLabel) rightMonthLabel.textContent = monthTitle(right);
            renderMonth(leftCalendarGrid, calendarLeftMonth);
            renderMonth(rightCalendarGrid, right);
            updateDraftRangeText();
        }
        // Delegation preserves keyboard/Enter activation and needs only two handlers.
        [leftCalendarGrid, rightCalendarGrid].forEach(function (grid) {
            if (!grid) return;
            grid.addEventListener('click', function (event) {
                var target = event.target.closest('button[data-date]');
                if (!target || !grid.contains(target)) return;
                event.preventDefault();
                event.stopPropagation();
                var date = parseIsoDate(target.dataset.date);
                if (date) selectCalendarDate(date);
            });
        });
        function openRangePopover() {
            if (!rangePopover || !rangeDisplay) return;
            showError('');
            draftRangeStart = parseIsoDate(startInput.value);
            draftRangeEnd = parseIsoDate(endInput.value);
            calendarLeftMonth = monthStart(draftRangeStart || new Date());
            renderDateRangeCalendars();
            rangePopover.hidden = false;
            document.body.classList.add('dashboard-calendar-open');
            // Floating chart tooltips are appended directly to <body>.
            // Dismiss any that remain visible when the calendar opens.
            var floatingChartTip = document.getElementById('dashboardChartTooltip');
            if (floatingChartTip) floatingChartTip.hidden = true;
            rangeDisplay.setAttribute('aria-expanded', 'true');
        }
        function closeRangePopover(restoreFocus) {
            if (!rangePopover || !rangeDisplay) return;
            rangePopover.hidden = true;
            document.body.classList.remove('dashboard-calendar-open');
            rangeDisplay.setAttribute('aria-expanded', 'false');
            if (restoreFocus) rangeDisplay.focus();
        }
        function validateRange(a, b) {
            if (!a || !b) return 'Please select a start and end date.';
            var days = Math.round((Date.UTC(b.getFullYear(), b.getMonth(), b.getDate()) - Date.UTC(a.getFullYear(), a.getMonth(), a.getDate())) / 86400000) + 1;
            if (days < 1) return 'The end date must be on or after the start date.';
            // Six calendar months maximum; the last allowed date is the day before start + 6 months.
            // Match .NET DateTime.AddMonths: clamp the day at month end.
            var monthEndDay = new Date(a.getFullYear(), a.getMonth() + 7, 0).getDate();
            var boundary = new Date(a.getFullYear(), a.getMonth() + 6, Math.min(a.getDate(), monthEndDay));
            if (dayTime(b) >= dayTime(boundary)) return 'Choose a range shorter than six calendar months.';
            return '';
        }
        rangeDisplay.addEventListener('click', function (event) {
            event.preventDefault(); event.stopPropagation();
            if (!rangePopover.hidden) closeRangePopover(); else openRangePopover();
        });
        form.querySelectorAll('[data-calendar-shift]').forEach(function (button) {
            button.addEventListener('click', function (event) {
                event.preventDefault(); event.stopPropagation();
                calendarLeftMonth = addMonths(calendarLeftMonth || new Date(), Number(button.dataset.calendarShift || 0));
                renderDateRangeCalendars();
            });
        });
        document.getElementById('rangeCloseButton').addEventListener('click', function (event) { event.preventDefault(); closeRangePopover(true); });
        document.getElementById('rangeClearButton').addEventListener('click', function (event) {
            event.preventDefault();
            draftRangeStart = null; draftRangeEnd = null;
            showError('');
            repaintCalendarSelection();
        });
        document.getElementById('rangeApplyButton').addEventListener('click', function (event) {
            event.preventDefault();
            var selectionEnd = draftRangeEnd || draftRangeStart;
            var message = validateRange(draftRangeStart, selectionEnd);
            showError(message);
            if (message) return;
            startInput.value = toIsoDate(draftRangeStart);
            endInput.value = toIsoDate(selectionEnd);
            updateRangeText();
            closeRangePopover();
            form.requestSubmit();
        });
        document.addEventListener('click', function (event) {
            if (!rangePopover.hidden && !form.contains(event.target)) closeRangePopover();
        });
        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape' && !rangePopover.hidden) { event.preventDefault(); closeRangePopover(true); }
        });
        form.addEventListener('submit', function (event) {
            var message = validateRange(parseIsoDate(startInput.value), parseIsoDate(endInput.value));
            if (message) {
                event.preventDefault();
                if (rangePopover.hidden) openRangePopover();
            }
            showError(message);
        });
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

    // Keep the SVG's drawing coordinates in step with its CSS size.  A fixed
    // 560x175 viewBox inside a wide, fixed-height chart uses SVG's default
    // "meet" scaling and creates the large blank strips on both sides.
    // Using actual rendered dimensions fills the card without stretching labels.
    function chartSize(svg) {
        var rect = svg.getBoundingClientRect();
        var currentViewBox = (svg.getAttribute('viewBox') || '0 0 560 175').split(/\s+/).map(Number);
        var width = Math.max(1, Math.round(rect.width || svg.clientWidth || currentViewBox[2] || 560));
        var height = Math.max(1, Math.round(rect.height || svg.clientHeight || currentViewBox[3] || 175));
        svg.setAttribute('viewBox', '0 0 ' + width + ' ' + height);
        return { width: width, height: height };
    }

    function drawGrid(svg, width, height, min, max, formatter) {
        var left = 42, right = width - 22, top = 10, bottom = height - 23;
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
        var dimensions = chartSize(svg);
        var width = dimensions.width, height = dimensions.height;
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
        attachChartHover(svg, labels || [], (options && options.dates) || labels || [],
            series, (options && options.extras) || [], plot, min, max, options && options.format);
    }

    function shortDate(value) {
        var d = new Date(value);
        if (Number.isNaN(d.getTime())) return '';
        return d.toLocaleDateString(undefined, { day: '2-digit', month: 'short' });
    }

    var yearlyCharts = null;
    var yearlyChartYear = 0;
    var roomTypeRows = [];
    var roomTypeSort = 'occupancy';

    // Booked occupancy is compared by day for shorter date ranges, or by
    // inventory-weighted weeks for long ranges. Percentages aren't added.
    function occupancyBuckets(rows, lastYear, weekly) {
        var buckets = [];
        var size = weekly ? 7 : 1;
        for (var start = 0; start < rows.length; start += size) {
            var group = rows.slice(start, start + size);
            var historical = lastYear.slice(start, start + size);
            function total(items, key) {
                return items.reduce(function (sum, row) { return sum + Number((row || {})[key] || 0); }, 0);
            }
            function rate(items) {
                var supply = total(items, 'sellableInventory');
                return supply > 0 ? 100 * total(items, 'roomsSold') / supply :
                    items.reduce(function (sum, row) { return sum + Number((row || {}).occupancyPercent || 0); }, 0) / items.length;
            }
            var startDate = group[0].date, endDate = group[group.length - 1].date;
            buckets.push({
                label: shortDate(startDate),
                title: weekly ? shortDate(startDate) + ' – ' + shortDate(endDate) + ' (' + group.length + ' nights)' : startDate,
                currentPercent: rate(group), lastYearPercent: rate(historical),
                currentSold: total(group, 'roomsSold'), currentSupply: total(group, 'sellableInventory'),
                priorSold: total(historical, 'roomsSold'), priorSupply: total(historical, 'sellableInventory')
            });
        }
        return buckets;
    }

    // Chart 1: grouped columns are an immediate visual comparison with last year.
    function drawOccupancyOnBooks() {
        var svg = document.getElementById('dashboardOccupancyChart');
        if (!svg) return;
        svg.replaceChildren();
        if (!yearlyCharts) { showEmptyChart(svg, 'Loading annual occupancy…'); return; }
        var buckets = (yearlyCharts.occupancyMonths || []).map(function (p) {
            return {
                label: p.label, title: p.label + ' ' + yearlyChartYear,
                currentPercent: Number(p.occupancy || 0),
                lastYearPercent: Number(p.previousOccupancy || 0),
                currentSold: Number(p.sold || 0), currentSupply: Number(p.supply || 0),
                priorSold: 0, priorSupply: 0
            };
        });
        if (!buckets.length) { showEmptyChart(svg, 'No annual occupancy data'); return; }
        var description = document.getElementById('dashboardOccupancyDescription');
        if (description) description.textContent = 'Jan–Dec ' + yearlyChartYear + ' · monthly booked room-nights ÷ sellable room-nights · compared with ' + (yearlyChartYear - 1);
        var dimensions = chartSize(svg);
        var width = dimensions.width, height = dimensions.height;
        var plot = drawGrid(svg, width, height, 0, 100, function (v) { return Math.round(v) + '%'; });
        var spacing = buckets.length > 1 ? (plot.right - plot.left) / (buckets.length - 1) : plot.right - plot.left;
        var barWidth = Math.max(1.6, Math.min(12, spacing * 0.3));
        buckets.forEach(function (item, i) {
            var x = buckets.length > 1 ? plot.left + i * spacing : (plot.left + plot.right) / 2;
            [{ value: item.currentPercent, fill: '#376eaa', dx: -barWidth - 1.2 },
            { value: item.lastYearPercent, fill: '#c4d5e7', dx: 1.2 }].forEach(function (bar) {
                var clamped = Math.max(0, Math.min(100, bar.value));
                var barHeight = clamped / 100 * (plot.bottom - plot.top);
                svg.appendChild(svgNode('rect', {
                    x: x + bar.dx, y: plot.bottom - barHeight,
                    width: barWidth, height: Math.max(clamped > 0 ? 1 : 0, barHeight),
                    rx: Math.min(2, barWidth / 3), fill: bar.fill
                }));
            });
        });
        chartXAxis(svg, buckets.map(function (item) { return item.label; }), plot, height);
        attachChartHover(svg, buckets.map(function (b) { return b.label; }),
            buckets.map(function (b) { return b.title; }), [
            { label: 'Year ' + yearlyChartYear, values: buckets.map(function (b) { return b.currentPercent; }), color: '#376eaa', format: 'percent', marker: false },
            { label: 'Year ' + (yearlyChartYear - 1), values: buckets.map(function (b) { return b.lastYearPercent; }), color: '#c4d5e7', format: 'percent', marker: false }
        ], buckets.map(function (b) {
            return [
                ['Booked / sellable room-nights', b.currentSold + ' / ' + b.currentSupply]
            ];
        }), plot, 0, 100, 'percent');
    }

    function chartXAxis(svg, labels, plot, height) {
        if (!labels.length) return;
        var monthChart = labels.length === 12 && labels.every(function (label) { return /^[A-Z][a-z]{2}$/.test(label); });
        var indexes = monthChart ? labels.map(function (_, i) { return i; }).filter(function (i) { return plot.right - plot.left > 415 || i % 2 === 0; }) :
            [0, Math.floor((labels.length - 1) / 3), Math.floor((labels.length - 1) * 2 / 3), labels.length - 1];
        indexes.filter(function (v, i, a) { return a.indexOf(v) === i; }).forEach(function (index) {
            var x = labels.length <= 1 ? (plot.left + plot.right) / 2 :
                plot.left + index * (plot.right - plot.left) / (labels.length - 1);
            addText(svg, x, height - 6, labels[index], 'middle');
        });
    }

    function showEmptyChart(svg, message) {
        var size = chartSize(svg);
        var empty = svgNode('text', { x: size.width / 2, y: size.height / 2, 'text-anchor': 'middle', fill: '#788aa0', 'font-size': 12 });
        empty.textContent = message;
        svg.appendChild(empty);
    }

    // Chart 2: columns show revenue, the solid line shows profit, and the
    // dashed line compares total revenue with last year on the same £ scale.
    function drawRevenueTrend() {
        var svg = document.getElementById('dashboardRevenueChart');
        if (!svg) return;
        svg.replaceChildren();
        if (!yearlyCharts) { showEmptyChart(svg, 'Loading annual revenue…'); return; }
        var buckets = (yearlyCharts.financialMonths || []).map(function (p) {
            return {
                label: p.label, title: p.label + ' ' + yearlyChartYear,
                revenue: Number(p.revenue || 0), profit: Number(p.profit || 0),
                lastYearRevenue: Number(p.lastYearRevenue || 0),
                expense: Number(p.expense || 0), lastYearProfit: Number(p.lastYearProfit || 0)
            };
        });
        if (!buckets.length) { showEmptyChart(svg, 'No annual revenue data'); return; }
        var description = document.getElementById('dashboardRevenueDescription');
        if (description) description.textContent = 'Jan–Dec ' + yearlyChartYear + ' · monthly revenue (blue), profit (green) and previous-year revenue (dashed)';
        var all = [0];
        buckets.forEach(function (b) { all.push(b.revenue, b.profit, b.lastYearRevenue); });
        var min = Math.min.apply(null, all), max = Math.max.apply(null, all);
        var range = max - min || 1;
        if (max > 0) max += range * 0.09;
        if (min < 0) min -= range * 0.08;
        if (max <= min) max = min + 1;
        var size = chartSize(svg), width = size.width, height = size.height;
        var plot = drawGrid(svg, width, height, min, max, function (v) {
            var abs = Math.abs(v);
            return abs >= 1000 ? (v / 1000).toFixed(abs >= 10000 ? 0 : 1).replace(/\.0$/, '') + 'k' : Math.round(v);
        });
        var toY = function (v) { return plot.bottom - (v - min) / (max - min) * (plot.bottom - plot.top); };
        var base = toY(0);
        svg.appendChild(svgNode('line', { x1: plot.left, x2: plot.right, y1: base, y2: base, stroke: '#bbcbd9', 'stroke-width': 1 }));
        var spacing = buckets.length <= 1 ? plot.right - plot.left : (plot.right - plot.left) / (buckets.length - 1);
        var barWidth = Math.max(3, Math.min(22, spacing * 0.60));
        buckets.forEach(function (b, i) {
            var x = buckets.length <= 1 ? (plot.left + plot.right) / 2 : plot.left + i * spacing;
            var valueY = toY(b.revenue);
            svg.appendChild(svgNode('rect', {
                x: x - barWidth / 2, y: Math.min(valueY, base), width: barWidth,
                height: Math.max(Math.abs(base - valueY), Math.abs(b.revenue) > 0 ? 1 : 0),
                rx: 2, fill: '#80a9d2', opacity: 0.85
            }));
        });
        [{ key: 'profit', color: '#2d9470', width: 2.4 },
        { key: 'lastYearRevenue', color: '#a4afbf', width: 1.8, dash: '5 4' }].forEach(function (series) {
            var path = linePath(buckets.map(function (b) { return b[series.key]; }), plot.left, plot.right, plot.top, plot.bottom, min, max);
            if (path) svg.appendChild(svgNode('path', {
                d: path, fill: 'none', stroke: series.color, 'stroke-width': series.width,
                'stroke-dasharray': series.dash || '', 'stroke-linecap': 'round', 'stroke-linejoin': 'round'
            }));
        });
        if (buckets.length === 1) {
            [{ key: 'profit', color: '#2d9470' }, { key: 'lastYearRevenue', color: '#a4afbf' }].forEach(function (series) {
                svg.appendChild(svgNode('circle', {
                    cx: (plot.left + plot.right) / 2, cy: toY(buckets[0][series.key]),
                    r: 4.2, fill: '#fff', stroke: series.color, 'stroke-width': 2.2
                }));
            });
        }
        chartXAxis(svg, buckets.map(function (b) { return b.label; }), plot, height);
        attachChartHover(svg, buckets.map(function (b) { return b.label; }), buckets.map(function (b) { return b.title; }), [
            { label: 'Revenue', color: '#80a9d2', format: 'money', marker: false, values: buckets.map(function (b) { return b.revenue; }) },
            { label: 'Profit', color: '#2d9470', format: 'money', values: buckets.map(function (b) { return b.profit; }) },
            { label: 'Last year revenue', color: '#a4afbf', format: 'money', values: buckets.map(function (b) { return b.lastYearRevenue; }) }
        ], buckets.map(function (b) {
            return [
                ['Expenses', formatChartNumber(b.expense, 'money')],
                ['Last year profit', formatChartNumber(b.lastYearProfit, 'money')]
            ];
        }), plot, min, max, 'money');
    }

    // Chart 3: this is not a statistical prediction. It visualizes booked
    // occupancy by individual night, showing upcoming reservations separately
    // from past dates. Calendar cells don't squeeze 90 nights into a line.
    function drawOccupancyTrend() {
        var element = document.getElementById('dashboardOccupancyTrendChart');
        if (!element) return;
        var period = selectedPeriod();
        var rows = yearlyCharts ? (yearlyCharts.monthDaily || []).slice().sort(function (a, b) { return a.date.localeCompare(b.date); }) : [];
        element.replaceChildren();
        if (!rows.length) {
            var empty = document.createElement('p');
            empty.className = 'dashboard-heatmap-empty';
            empty.textContent = yearlyCharts ? 'No occupancy data available for the current month.' : 'Loading the current-month calendar…';
            element.appendChild(empty);
            return;
        }
        var today = document.querySelector('.dashboard-page').getAttribute('data-hotel-today') || period.end;
        var byDate = Object.create(null);
        var scroll = document.createElement('div');
        scroll.className = 'dashboard-heatmap-scroll';
        var grid = document.createElement('div');
        grid.className = 'dashboard-heatmap-grid';
        var fragment = document.createDocumentFragment();
        ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].forEach(function (day) {
            var heading = document.createElement('span');
            heading.className = 'dashboard-heatmap-weekday';
            heading.textContent = day;
            fragment.appendChild(heading);
        });
        var firstParts = rows[0].date.slice(0, 10).split('-').map(Number);
        var first = new Date(firstParts[0], firstParts[1] - 1, firstParts[2]);
        for (var p = 0; p < (first.getDay() + 6) % 7; p++) {
            var blank = document.createElement('span');
            blank.className = 'dashboard-heatmap-blank';
            fragment.appendChild(blank);
        }
        var futureNights = 0;
        var shortMonths = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
            'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
        rows.forEach(function (row) {
            var iso = row.date.slice(0, 10);
            var booked = Math.max(0, Math.min(100, Number(row.occupancyPercent || 0)));
            byDate[iso] = { row: row, booked: booked };
            var button = document.createElement('button');
            button.type = 'button';
            button.dataset.date = iso;
            button.className = 'dashboard-heatmap-day ' + (booked >= 90 ? 'dashboard-heatmap-critical' :
                booked >= 75 ? 'dashboard-heatmap-high' : booked >= 50 ? 'dashboard-heatmap-medium' :
                    booked >= 25 ? 'dashboard-heatmap-moderate' : 'dashboard-heatmap-low');
            if (iso > today) { button.classList.add('dashboard-heatmap-upcoming'); futureNights++; }
            if (iso === today) button.classList.add('dashboard-heatmap-today');
            button.setAttribute('aria-label', fullDate(iso) + ': ' + booked.toFixed(1) + '% occupied, ' +
                Number(row.roomsSold || 0) + ' rooms booked of ' + Number(row.sellableInventory || 0) + ' sellable. ' +
                (iso > today ? 'Upcoming reservations on the books.' : 'Past or current date.'));
            var dateLabel = document.createElement('span');
            dateLabel.className = 'dashboard-heatmap-date';
            // Show day and abbreviated month within each compact occupancy cell.
            // Use the ISO month instead of converting to local time, so hotel dates do not shift.
            dateLabel.textContent = Number(iso.slice(8, 10)) + ' ' + shortMonths[Number(iso.slice(5, 7)) - 1];
            var percentLabel = document.createElement('strong');
            percentLabel.className = 'dashboard-heatmap-percent';
            percentLabel.textContent = booked.toFixed(0) + '%';
            button.append(dateLabel, percentLabel);
            fragment.appendChild(button);
        });
        grid.appendChild(fragment);
        // One delegated set of hover/focus listeners for up to six months, instead of
        // two closures and listeners per calendar cell on every data refresh.
        var activeIso = '';
        function showFor(target) {
            if (!target || document.body.classList.contains('dashboard-calendar-open')) return;
            var iso = target.dataset.date;
            if (!iso || !byDate[iso]) return;
            var data = byDate[iso], row = data.row, booked = data.booked;
            var tip = chartTooltip();
            if (activeIso !== iso || tip.hidden) {
                tip.replaceChildren();
                var title = document.createElement('strong');
                title.className = 'dashboard-chart-tooltip-date';
                title.textContent = fullDate(iso);
                tip.appendChild(title);
                [
                    ['Booked occupancy', formatChartNumber(booked, 'percent')],
                    ['Rooms sold / sellable', Number(row.roomsSold || 0) + ' / ' + Number(row.sellableInventory || 0)],
                    ['Rooms still available', Math.max(0, Number(row.sellableInventory || 0) - Number(row.roomsSold || 0))],
                    ['ADR', formatChartNumber(row.adr || 0, 'money')],
                    ['Date type', iso > today ? 'Upcoming — booked so far' : 'Past / current']
                ].forEach(function (item) {
                    var r = document.createElement('div');
                    r.className = 'dashboard-chart-tooltip-detail';
                    var k = document.createElement('span'); k.textContent = item[0];
                    var v = document.createElement('b'); v.textContent = item[1];
                    r.append(k, v); tip.appendChild(r);
                });
                activeIso = iso;
            }
            tip.hidden = false;
            var rect = target.getBoundingClientRect();
            var box = tip.getBoundingClientRect();
            var left = Math.max(8, Math.min(window.innerWidth - box.width - 8, rect.left + rect.width / 2 + 9));
            var top = rect.bottom + 9;
            if (top + box.height > window.innerHeight - 8) top = rect.top - box.height - 9;
            tip.style.left = left + 'px'; tip.style.top = Math.max(8, top) + 'px';
        }
        function hide() { chartTooltip().hidden = true; activeIso = ''; }
        function dayTarget(target) { return target && target.closest ? target.closest('button.dashboard-heatmap-day') : null; }
        grid.addEventListener('pointerover', function (event) { showFor(dayTarget(event.target)); });
        grid.addEventListener('pointerout', function (event) {
            var before = dayTarget(event.target), after = dayTarget(event.relatedTarget);
            if (before && before !== after) hide();
        });
        grid.addEventListener('focusin', function (event) { showFor(dayTarget(event.target)); });
        grid.addEventListener('focusout', function (event) {
            if (dayTarget(event.target) && dayTarget(event.target) !== dayTarget(event.relatedTarget)) hide();
        });
        scroll.appendChild(grid);
        element.appendChild(scroll);
        var futureCount = document.getElementById('dashboardFutureNightsCount');
        if (futureCount) futureCount.textContent = futureNights + ' night' + (futureNights === 1 ? '' : 's');
        var footer = document.createElement('div');
        footer.className = 'dashboard-heatmap-legend';
        [['0–24%', 'low'], ['25–49%', 'moderate'], ['50–74%', 'medium'],
        ['75–89%', 'high'], ['90–100%', 'critical']].forEach(function (item) {
            var label = document.createElement('span');
            var swatch = document.createElement('i');
            swatch.className = 'dashboard-heatmap-legend-' + item[1];
            label.appendChild(swatch);
            label.appendChild(document.createTextNode(item[0]));
            footer.appendChild(label);
        });
        element.appendChild(footer);
        if (rows.length > 42) {
            var hint = document.createElement('p');
            hint.className = 'dashboard-heatmap-scroll-hint';
            hint.textContent = 'Scroll to review every date in this month.';
            element.appendChild(hint);
        }
    }

    function drawExceptionChart() {
        var rows = yearlyCharts ? (yearlyCharts.exceptionMonths || []) : [];
        var svg = document.getElementById('dashboardExceptionChart');
        if (!svg || !rows.length) return;
        while (svg.firstChild) svg.removeChild(svg.firstChild);
        var dimensions = chartSize(svg);
        var width = dimensions.width, height = dimensions.height;
        var left = 18, right = width - 18, top = 8, bottom = height - 25;
        var max = Math.max(1, Math.max.apply(null, rows.map(function (x) { return Math.max(Number(x.cancellations || 0), Number(x.noShows || 0)); })));
        for (var i = 0; i < 4; i++) {
            var y = top + i * ((bottom - top) / 3);
            svg.appendChild(svgNode('line', { x1: left, y1: y, x2: right, y2: y, stroke: '#edf2f6' }));
        }
        // Spread week groups from near the left edge to near the right edge,
        // rather than leaving a half-group of blank space on both sides.
        var inset = Math.min(44, Math.max(24, Math.round(width * 0.064)));
        var spacing = rows.length > 1 ? (width - 2 * inset) / (rows.length - 1) : 0;
        var w = Math.max(5, Math.min(32, (rows.length > 1 ? spacing : Math.min(width - 2 * inset, 140)) * 0.15));
        rows.forEach(function (row, idx) {
            var center = rows.length > 1 ? inset + idx * spacing : width / 2;
            var x = center - w - 2;
            var c = Number(row.cancellations || 0), n = Number(row.noShows || 0);
            var ch = c / max * (bottom - top), nh = n / max * (bottom - top);
            svg.appendChild(svgNode('rect', { x: x, y: bottom - ch, width: w, height: ch, rx: 2, fill: '#d4837d' }));
            svg.appendChild(svgNode('rect', { x: center + 2, y: bottom - nh, width: w, height: nh, rx: 2, fill: '#d9ae5f' }));
            addText(svg, center, height - 7, row.label || '', 'middle');
        });
        attachChartHover(svg, rows.map(function (x) { return x.label || ''; }),
            rows.map(function (x) { return x.label || ''; }),
            [
                { label: 'Cancellations', color: '#d4837d', values: rows.map(function (x) { return Number(x.cancellations || 0); }) },
                { label: 'No-shows', color: '#d9ae5f', values: rows.map(function (x) { return Number(x.noShows || 0); }) }
            ], rows.map(function (x) { return [['Month', (x.label || '') + ' ' + yearlyChartYear]]; }),
            { left: rows.length > 1 ? inset : width / 2, right: rows.length > 1 ? width - inset : width / 2, top: top, bottom: bottom }, 0, max, 'number');
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
            balance.textContent = currency + " " + Number(row.balance || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + (paymentState === 'partial' ? ' part paid' : ' pending');
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
            if (['checkin', 'checkout', 'available', 'occupied', 'blocked', 'dirty'].indexOf(type) >= 0)
                dashboardDate = page.getAttribute('data-hotel-today') || dashboardDate;
            var period = selectedPeriod();
            var response = await fetch(url + '?type=' + encodeURIComponent(type)
                + '&date=' + encodeURIComponent(dashboardDate)
                + '&startDate=' + encodeURIComponent(period.start)
                + '&endDate=' + encodeURIComponent(period.end), {
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

    // No info icons: the entire dashboard card displays its explanation on hover/focus.
    function wireStatisticInfo() {
        var cards = Array.from(document.querySelectorAll(
            '.dashboard-kpi[data-info], .dashboard-hero-stat[data-info], .dashboard-operation[data-info], .dashboard-mini-stats [data-info]'));
        if (!cards.length) return;
        var tip = document.createElement('div');
        tip.className = 'dashboard-info-floating';
        tip.setAttribute('role', 'tooltip');
        tip.hidden = true;
        document.body.appendChild(tip);
        var current = null;
        function position() {
            if (!current || tip.hidden) return;
            var rect = current.getBoundingClientRect();
            var width = tip.offsetWidth, height = tip.offsetHeight;
            tip.style.left = Math.max(8, Math.min(window.innerWidth - width - 8,
                rect.left + rect.width / 2 - width / 2)) + 'px';
            var above = rect.top - height - 10;
            tip.style.top = (above >= 8 ? above : rect.bottom + 9) + 'px';
        }
        function show(card) {
            if (document.body.classList.contains('dashboard-calendar-open')) return;
            var info = card.getAttribute('data-info');
            if (!info) return;
            current = card;
            tip.textContent = info;
            tip.hidden = false;
            position();
        }
        function hide(card) {
            if (card && card !== current) return;
            current = null;
            tip.hidden = true;
        }
        cards.forEach(function (card) {
            card.addEventListener('mouseenter', function () { show(card); });
            card.addEventListener('mouseleave', function () { hide(card); });
            card.addEventListener('focus', function () { show(card); });
            card.addEventListener('blur', function () { hide(card); });
        });
        document.addEventListener('scroll', position, { passive: true, capture: true });
        window.addEventListener('resize', position, { passive: true });
        document.addEventListener('keydown', function (e) { if (e.key === 'Escape') hide(); });
    }

    function wireStatisticDetails() {
        wireStatisticInfo();
        document.querySelectorAll('.dashboard-operations [data-dashboard-statistic]').forEach(function (node) {
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
        return pageCurrency() + " " + number.toLocaleString(undefined, { maximumFractionDigits: 0 });
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

    function progressShareCell(value, tone) {
        var pct = Math.max(0, Math.min(100, Number(value || 0)));
        var td = document.createElement('td');
        td.className = 'dashboard-right dashboard-share-cell';
        var label = document.createElement('span');
        label.className = 'dashboard-share-value';
        label.textContent = pct.toFixed(1).replace(/\.0$/, '') + '%';
        var track = document.createElement('span');
        track.className = 'dashboard-share-track';
        var fill = document.createElement('span');
        fill.className = 'dashboard-share-fill ' + (tone || '');
        fill.style.width = pct + '%';
        track.appendChild(fill);
        td.append(label, track);
        return td;
    }

    function renderRevenueSources(rows) {
        var body = document.getElementById('dashboardRevenueSourcesBody');
        if (!body) return;
        body.replaceChildren();
        if (!rows || !rows.length) {
            var emptyRow = document.createElement('tr');
            var empty = createCell('No revenue on the books for the selected dates.', false);
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
            tr.appendChild(progressShareCell(item.sharePercent, 'dashboard-share-revenue'));
            body.appendChild(tr);
        });
    }

    // The same cached date-range API supplies both revenue and occupancy.
    // Grouped bars use different scales: occupancy out of 100%, and revenue
    // relative to the highest room type in the selected period.
    function renderRoomPerformance(rows) {
        var graph = document.getElementById('dashboardRoomPerformanceGraph');
        if (!graph) return;
        var items = (rows || []).slice().sort(function (a, b) {
            return Number(b.occupancyNext30 || 0) - Number(a.occupancyNext30 || 0);
        });
        graph.replaceChildren();
        var total = document.getElementById('dashboardPerformanceTotalTypes');
        if (total) total.textContent = items.length + ' room type' + (items.length === 1 ? '' : 's');
        if (!items.length) {
            var empty = document.createElement('p');
            empty.className = 'dashboard-performance-empty';
            empty.textContent = 'No room-type data for this date range.';
            graph.appendChild(empty);
            return;
        }
        var peakRevenue = Math.max(0, ...items.map(function (row) {
            return Math.max(0, Number(row.revenue || 0));
        }));
        items.forEach(function (item) {
            var occ = Math.max(0, Math.min(100, Number(item.occupancyNext30 || 0)));
            var revenue = Math.max(0, Number(item.revenue || 0));
            var rooms = Math.max(0, Number(item.rooms || 0));
            var name = item.category || 'Uncategorised';
            var occupancyLabel = occ.toLocaleString(undefined, { maximumFractionDigits: 1 }) + '%';
            var detail = name + ' · ' + rooms + ' rooms · Occupancy ' + occupancyLabel
                + ' · Revenue ' + money0(revenue) + ' · ADR ' + money0(item.adr);

            var column = document.createElement('div');
            column.className = 'dashboard-performance-column';
            column.tabIndex = 0;
            column.setAttribute('role', 'group');
            column.setAttribute('aria-label', detail);
            column.title = detail;

            var value = document.createElement('strong');
            value.className = 'dashboard-performance-column-value';
            value.textContent = occupancyLabel;

            var track = document.createElement('div');
            track.className = 'dashboard-performance-column-track';
            var revenueBar = document.createElement('span');
            revenueBar.className = 'dashboard-performance-column-fill dashboard-performance-column-revenue';
            revenueBar.style.height = (peakRevenue > 0 ? revenue / peakRevenue * 100 : 0) + '%';
            revenueBar.title = name + ': revenue ' + money0(revenue);
            var occupancyBar = document.createElement('span');
            occupancyBar.className = 'dashboard-performance-column-fill dashboard-performance-column-occupancy';
            occupancyBar.style.height = occ + '%';
            occupancyBar.title = name + ': occupancy ' + occupancyLabel;
            track.append(revenueBar, occupancyBar);

            var label = document.createElement('span');
            label.className = 'dashboard-performance-column-name';
            label.textContent = name;
            label.title = name;
            var roomCount = document.createElement('small');
            roomCount.className = 'dashboard-performance-column-rooms';
            roomCount.textContent = rooms + ' room' + (rooms === 1 ? '' : 's');
            column.append(value, track, label, roomCount);
            graph.appendChild(column);
        });
    }

    async function loadRoomPerformance() {
        var page = document.querySelector('.dashboard-page');
        var url = page && page.getAttribute('data-room-performance-url');
        if (!url) return;
        var period = selectedPeriod();
        try {
            var response = await fetch(url + '?startDate=' + encodeURIComponent(period.start)
                + '&endDate=' + encodeURIComponent(period.end), {
                credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' }
            });
            var result = await response.json();
            if (!response.ok || !result.ok) throw new Error(result.message || 'Could not load room types.');
            renderRoomPerformance(result.roomTypes || []);
        } catch (error) {
            var graph = document.getElementById('dashboardRoomPerformanceGraph');
            if (graph) {
                graph.replaceChildren();
                var message = document.createElement('p');
                message.className = 'dashboard-performance-empty';
                message.textContent = error.message || 'Room type performance is unavailable.';
                graph.appendChild(message);
            }
        }
    }

    function renderRoomTypes(rows) {
        var body = document.getElementById('dashboardRoomTypesBody');
        if (!body) return;
        if (rows) roomTypeRows = rows.slice();
        var ordered = roomTypeRows.slice();
        ordered.sort(function (a, b) {
            if (roomTypeSort === 'share') return Number(b.contributionPercent || 0) - Number(a.contributionPercent || 0);
            if (roomTypeSort === 'name') return String(a.category || '').localeCompare(String(b.category || ''));
            return Number(b.occupancyNext30 || 0) - Number(a.occupancyNext30 || 0);
        });
        body.replaceChildren();
        if (!ordered.length) {
            var tr = document.createElement('tr');
            var empty = createCell('No room-type performance data found.', false);
            empty.colSpan = 7; empty.className = 'dashboard-empty-cell';
            tr.appendChild(empty); body.appendChild(tr);
            return;
        }
        ordered.forEach(function (item) {
            var tr = document.createElement('tr');
            var name = createCell('', false);
            var strong = document.createElement('strong');
            strong.textContent = item.category || '';
            name.appendChild(strong);
            tr.appendChild(name);
            tr.appendChild(createCell(item.rooms || 0, true));
            var occ = document.createElement('td');
            var bar = document.createElement('div');
            bar.className = 'dashboard-progress';
            var fill = document.createElement('span');
            fill.style.width = Math.max(0, Math.min(100, Number(item.occupancyNext30 || 0))) + '%';
            bar.appendChild(fill);
            var value = document.createElement('small');
            value.textContent = Number(item.occupancyNext30 || 0).toFixed(1).replace(/\.0$/, '') + '%';
            occ.append(bar, value); tr.appendChild(occ);
            tr.appendChild(createCell(money0(item.adr), true));
            tr.appendChild(createCell(money0(item.revPar), true));
            tr.appendChild(createCell(money0(item.revenue), true));
            tr.appendChild(progressShareCell(item.contributionPercent, 'dashboard-share-room'));
            body.appendChild(tr);
        });
    }

    // Pricing recommendations are advisory, not automatically applied.
    var pricingRows = [];
    var pricingFilter = 'all';
    var pricingSort = 'occupancy';
    function pricingAction(item) {
        var change = Number(item.changePercent || 0);
        return change > 0 ? 'raise' : change < 0 ? 'reduce' : 'hold';
    }
    function pricingPercent(value) {
        return Number(value || 0).toLocaleString(undefined, { maximumFractionDigits: 1 }) + '%';
    }
    function pricingMoney(value) {
        var number = Number(value || 0);
        // Preserve pennies in price proposals; round whole amounts for readability.
        return pageCurrency() + " " + number.toLocaleString(undefined, {
            minimumFractionDigits: Math.abs(number - Math.round(number)) < 0.00001 ? 0 : 2,
            maximumFractionDigits: 2
        });
    }
    function pricingVisibleRows() {
        var rows = pricingRows.filter(function (item) {
            return pricingFilter === 'all' || pricingAction(item) === pricingFilter;
        });
        rows.sort(function (a, b) {
            if (pricingSort === 'category') return String(a.category || '').localeCompare(String(b.category || ''));
            if (pricingSort === 'change') return Math.abs(Number(b.changePercent || 0)) - Math.abs(Number(a.changePercent || 0));
            return Number(b.occupancyNext30 || 0) - Number(a.occupancyNext30 || 0);
        });
        return rows;
    }
    function pricingElement(tag, className, content) {
        var element = document.createElement(tag);
        if (className) element.className = className;
        if (content !== undefined) element.textContent = content;
        return element;
    }
    function drawPricingCards() {
        var root = document.getElementById('dashboardPrices');
        if (!root) return;
        root.replaceChildren();
        var rows = pricingVisibleRows();
        var copy = document.getElementById('dashboardPricingCopy');
        if (copy) copy.disabled = !rows.length;
        var showing = document.getElementById('dashboardPricingShowing');
        if (showing) showing.textContent = 'Showing ' + rows.length + ' of ' + pricingRows.length + ' rate plans';
        if (!pricingRows.length || !rows.length) {
            root.appendChild(pricingElement('div', 'dashboard-empty-block dashboard-pricing-empty',
                pricingRows.length ? 'No suggestions match this filter.' : 'No active rate-plan data was found for the selected period.'));
            return;
        }
        var fragment = document.createDocumentFragment();
        rows.forEach(function (item) {
            var action = pricingAction(item);
            var change = Number(item.changePercent || 0);
            var occupancy = Math.max(0, Math.min(100, Number(item.occupancyNext30 || 0)));
            var card = pricingElement('article', 'dashboard-price-card dashboard-pricing-item dashboard-pricing-' + action);
            var header = pricingElement('div', 'dashboard-pricing-item-head');
            var headingWrap = pricingElement('div', 'dashboard-pricing-item-name');
            headingWrap.appendChild(pricingElement('strong', '', item.category || 'Room category'));
            if (item.planName) headingWrap.appendChild(pricingElement('span', '', item.planName));
            var status = pricingElement('span', 'dashboard-pricing-status dashboard-pricing-status-' + action,
                action === 'raise' ? 'Raise ' + pricingPercent(Math.abs(change)) : action === 'reduce' ? 'Reduce ' + pricingPercent(Math.abs(change)) : 'Hold rate');
            header.appendChild(headingWrap);
            header.appendChild(status);
            card.appendChild(header);

            var rates = pricingElement('div', 'dashboard-pricing-rate-row');
            var current = pricingElement('div', 'dashboard-pricing-rate-cell');
            current.appendChild(pricingElement('span', '', 'Current rate'));
            current.appendChild(pricingElement('strong', '', pricingMoney(item.currentRate)));
            var arrow = pricingElement('span', 'dashboard-pricing-rate-arrow', '→');
            arrow.setAttribute('aria-hidden', 'true');
            var suggested = pricingElement('div', 'dashboard-pricing-rate-cell dashboard-pricing-rate-recommended');
            suggested.appendChild(pricingElement('span', '', 'Suggested rate'));
            suggested.appendChild(pricingElement('strong', '', pricingMoney(item.recommendedRate)));
            rates.appendChild(current);
            rates.appendChild(arrow);
            rates.appendChild(suggested);
            card.appendChild(rates);

            var occupancyRow = pricingElement('div', 'dashboard-pricing-occupancy-meta');
            occupancyRow.appendChild(pricingElement('span', '', 'Booked occupancy'));
            occupancyRow.appendChild(pricingElement('strong', '', pricingPercent(occupancy)));
            card.appendChild(occupancyRow);
            var track = pricingElement('div', 'dashboard-pricing-meter');
            track.setAttribute('role', 'progressbar');
            track.setAttribute('aria-label', (item.category || 'Room category') + ' booked occupancy');
            track.setAttribute('aria-valuenow', String(occupancy));
            track.setAttribute('aria-valuemin', '0');
            track.setAttribute('aria-valuemax', '100');
            var fill = pricingElement('span', 'dashboard-pricing-meter-fill');
            fill.style.width = occupancy + '%';
            track.appendChild(fill);
            card.appendChild(track);
            var note = pricingElement('p', 'dashboard-pricing-reason', item.reason || item.demand || 'Based on occupancy in the selected period.');
            note.title = note.textContent; // Full explanation remains available if the compact line is truncated.
            card.appendChild(note);
            fragment.appendChild(card);
        });
        root.appendChild(fragment);
    }
    function renderPrices(rows) {
        pricingRows = Array.isArray(rows) ? rows.slice() : [];
        ['total', 'raise', 'reduce', 'hold'].forEach(function (category) {
            var id = 'dashboardPricing' + category.charAt(0).toUpperCase() + category.slice(1);
            var node = document.getElementById(id);
            if (!node) return;
            node.textContent = category === 'total' ? pricingRows.length : pricingRows.filter(function (item) { return pricingAction(item) === category; }).length;
        });
        drawPricingCards();
    }
    function wirePricingGuidance() {
        var buttons = document.querySelectorAll('[data-price-filter]');
        buttons.forEach(function (button) {
            button.addEventListener('click', function () {
                pricingFilter = button.getAttribute('data-price-filter') || 'all';
                buttons.forEach(function (node) {
                    var selected = node === button;
                    node.classList.toggle('is-active', selected);
                    node.setAttribute('aria-pressed', selected ? 'true' : 'false');
                });
                drawPricingCards();
            });
        });
        var sort = document.getElementById('dashboardPricingSort');
        if (sort) sort.addEventListener('change', function () {
            pricingSort = sort.value;
            drawPricingCards();
        });
        var copy = document.getElementById('dashboardPricingCopy');
        if (copy) copy.addEventListener('click', async function () {
            var rows = pricingVisibleRows();
            if (!rows.length) return;
            var header = 'Room category\tRate plan\tCurrent rate\tSuggested rate\tChange\tBooked occupancy\tReason';
            var text = [header].concat(rows.map(function (item) {
                return [item.category || '', item.planName || '', pricingMoney(item.currentRate), pricingMoney(item.recommendedRate),
                pricingPercent(item.changePercent), pricingPercent(item.occupancyNext30), item.reason || '']
                    .map(function (value) { return String(value).replace(/[\t\r\n]+/g, ' '); }).join('\t');
            })).join('\n');
            var successful = false;
            try {
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    await navigator.clipboard.writeText(text);
                    successful = true;
                } else {
                    var input = document.createElement('textarea');
                    input.value = text;
                    input.style.position = 'fixed';
                    input.style.left = '-9999px';
                    document.body.appendChild(input);
                    input.select();
                    successful = document.execCommand('copy');
                    input.remove();
                }
            } catch (error) { successful = false; }
            var label = copy.querySelector('span');
            if (!label) {
                label = document.createElement('span');
                label.textContent = 'Copy visible suggestions';
                // The markup already contains text after the icon; replace that text once.
                Array.from(copy.childNodes).forEach(function (node) { if (node.nodeType === 3) node.remove(); });
                copy.appendChild(label);
            }
            label.textContent = successful ? 'Copied to clipboard' : 'Copy failed — try again';
            copy.setAttribute('aria-label', label.textContent);
            // Reset on the next user action instead of a timer.
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
            var columns = id === 'dashboardRevenueSourcesBody' ? 5 : 7;
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
        var period = selectedPeriod();
        url += '?startDate=' + encodeURIComponent(period.start) + '&endDate=' + encodeURIComponent(period.end);
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

            // Annual occupancy footer is updated by the independent Yearly endpoint.
            setHtmlText('dashboardOcc60', Number(data.occupancyNext60 || 0).toFixed(1).replace(/\.0$/, '') + '%');
            setHtmlText('dashboardOcc90', Number(data.occupancyNext90 || 0).toFixed(1).replace(/\.0$/, '') + '%');


            // The financial chart and its totals are annual; selected dates only
            // drive revenue-source, room-type and pricing analytics.

            renderRevenueSources(data.revenueSources || []);
            renderRoomTypes(data.roomTypes || []);
            renderRoomPerformance(data.roomTypes || []);
            renderPrices(data.priceRecommendations || []);
            renderDecisions(data.decisions || []);

            // Annual charts and today's month calendar load independently.
        } catch (error) {
            console.warn(error);
            renderAnalyticsFailure(error && error.message ? error.message : 'Dashboard analytics could not be loaded.');
        }
    }

    var annualRequestGroup = null;
    var annualRequestVersion = 0;

    function annualChartPlaceholder(id, message) {
        var svg = document.getElementById(id);
        if (!svg) return;
        svg.replaceChildren();
        showEmptyChart(svg, message);
    }

    function setOccupancyBusy(isBusy, message) {
        var shell = document.getElementById('dashboardOccupancyShell');
        var loader = document.getElementById('dashboardOccupancyLoader');
        if (!shell || !loader) return;
        shell.setAttribute('aria-busy', isBusy ? 'true' : 'false');
        loader.hidden = !isBusy;
        if (isBusy && message) {
            var label = loader.querySelector('.dashboard-annual-loading-label');
            if (label) label.textContent = message;
        }
    }

    // Annual charts load as three independent requests. The original combined
    // endpoint waited for all heavy queries before showing any chart.
    function loadYearlyCharts(year) {
        var page = document.querySelector('.dashboard-page');
        var url = page && page.getAttribute('data-yearly-url');
        if (!url) return;
        var version = ++annualRequestVersion;
        if (annualRequestGroup) annualRequestGroup.forEach(function (request) { request.abort(); });
        annualRequestGroup = [];
        yearlyChartYear = year;
        yearlyCharts = { occupancyMonths: [], financialMonths: [], exceptionMonths: [], monthDaily: [] };
        document.querySelectorAll('.dashboard-year-select').forEach(function (select) { select.value = String(year); });
        setHtmlText('dashboardOccYear', String(year));
        ['dashboardOcc30', 'dashboardOccYearLy', 'dashboardOccRoomNights',
            'dashboardRevenue30', 'dashboardProfit30', 'dashboardProfitMargin30'].forEach(function (id) { setHtmlText(id, '—'); });
        setOccupancyBusy(true, 'Loading ' + year + ' occupancy…');
        annualChartPlaceholder('dashboardOccupancyChart', 'Loading ' + year + ' occupancy…');
        annualChartPlaceholder('dashboardRevenueChart', 'Loading ' + year + ' revenue…');
        annualChartPlaceholder('dashboardExceptionChart', 'Loading ' + year + ' exceptions…');
        var monthCalendar = document.getElementById('dashboardOccupancyTrendChart');
        if (monthCalendar) monthCalendar.textContent = 'Loading current-month occupancy…';

        function loadSection(section, svgId, onSuccess) {
            var controller = new AbortController();
            annualRequestGroup.push(controller);
            var timedOut = false;
            var timeout = window.setTimeout(function () { timedOut = true; controller.abort(); }, 65000);
            return fetch(url + '?year=' + encodeURIComponent(year) + '&section=' + encodeURIComponent(section), {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }, credentials: 'same-origin', signal: controller.signal
            }).then(async function (response) {
                var payload;
                try { payload = await response.json(); }
                catch (_) { throw new Error('Invalid response from annual chart endpoint (HTTP ' + response.status + ').'); }
                if (!response.ok || !payload.ok || !payload.charts)
                    throw new Error(payload.message || 'Annual ' + section + ' request failed (HTTP ' + response.status + ').');
                if (version !== annualRequestVersion) return;
                onSuccess(payload.charts);
            }).catch(function (error) {
                if (version !== annualRequestVersion) return;
                console.warn('Dashboard ' + section + ' yearly data:', error);
                var detail = timedOut ? 'Request timed out. Refresh to retry.' : 'Could not load. Refresh to retry.';
                if (section === 'calendar') {
                    if (monthCalendar) monthCalendar.textContent = 'Current-month occupancy could not be loaded. Refresh to retry.';
                } else {
                    annualChartPlaceholder(svgId, detail);
                    if (section === 'occupancy') setOccupancyBusy(false);
                }
            }).finally(function () { window.clearTimeout(timeout); });
        }

        loadSection('occupancy', 'dashboardOccupancyChart', function (charts) {
            yearlyCharts.occupancyMonths = charts.occupancyMonths || [];
            var rows = yearlyCharts.occupancyMonths;
            var supplied = rows.reduce(function (s, p) { return s + Number(p.supply || 0); }, 0);
            var sold = rows.reduce(function (s, p) { return s + Number(p.sold || 0); }, 0);
            var priorSupply = rows.reduce(function (s, p) { return s + Number(p.previousSupply || 0); }, 0);
            var priorSold = rows.reduce(function (s, p) { return s + Number(p.previousSold || 0); }, 0);
            setHtmlText('dashboardOcc30', (supplied ? 100 * sold / supplied : 0).toFixed(1) + '%');
            setHtmlText('dashboardOccYearLy', (priorSupply ? 100 * priorSold / priorSupply : 0).toFixed(1) + '%');
            setHtmlText('dashboardOccRoomNights', sold.toLocaleString());
            drawOccupancyOnBooks();
            // Keep the spinner until the SVG has been painted, not merely until
            // the fetch resolves. Never let an old year hide the new loader.
            window.requestAnimationFrame(function () {
                window.requestAnimationFrame(function () {
                    if (version === annualRequestVersion) setOccupancyBusy(false);
                });
            });
        });
        // The monthly calendar has its own request so it cannot hold the annual chart open.
        loadSection('calendar', 'dashboardOccupancyChart', function (charts) {
            yearlyCharts.monthDaily = charts.monthDaily || [];
            drawOccupancyTrend();
        });
        loadSection('financial', 'dashboardRevenueChart', function (charts) {
            yearlyCharts.financialMonths = charts.financialMonths || [];
            var rows = yearlyCharts.financialMonths;
            var revenue = rows.reduce(function (s, p) { return s + Number(p.revenue || 0); }, 0);
            var profit = rows.reduce(function (s, p) { return s + Number(p.profit || 0); }, 0);
            setHtmlText('dashboardRevenue30', money0(revenue));
            setHtmlText('dashboardProfit30', money0(profit));
            setHtmlText('dashboardProfitMargin30', (revenue ? 100 * profit / revenue : 0).toFixed(1) + '%');
            drawRevenueTrend();
        });
        loadSection('exceptions', 'dashboardExceptionChart', function (charts) {
            yearlyCharts.exceptionMonths = charts.exceptionMonths || [];
            var rows = yearlyCharts.exceptionMonths;
            var cancelled = rows.reduce(function (s, p) { return s + Number(p.cancellations || 0); }, 0);
            var noShows = rows.reduce(function (s, p) { return s + Number(p.noShows || 0); }, 0);
            document.querySelectorAll('[data-exception-annual]').forEach(function (tr) {
                var isCancel = (tr.getAttribute('data-exception-annual') || '').toLowerCase().indexOf('cancel') >= 0;
                var cells = tr.querySelectorAll('td');
                if (cells.length >= 3) {
                    var count = isCancel ? cancelled : noShows;
                    cells[1].textContent = String(count);
                    cells[2].textContent = (cancelled + noShows ? 100 * count / (cancelled + noShows) : 0).toFixed(1) + '%';
                }
            });
            drawExceptionChart();
        });
    }

    function wireYearlyCharts() {
        var selects = document.querySelectorAll('.dashboard-year-select');
        if (!selects.length) return;
        selects.forEach(function (select) {
            select.addEventListener('change', function () {
                var year = Number(select.value);
                if (year >= 2000 && year <= 2099) loadYearlyCharts(year);
            });
        });
        var today = document.querySelector('.dashboard-page').getAttribute('data-hotel-today');
        whenDashboardSectionNear('dashboardOccupancyChart', function () {
            loadYearlyCharts(Number(String(today || '').slice(0, 4)) || new Date().getFullYear());
        });
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

    // WebForms Bookings graph, rewritten as responsive SVG so Dashboard does
    // not need an additional Chart.js dependency. Calendar-year source counts
    // are independent of the selected six-month occupancy/financial date range.
    var bookingsChartData = null;
    var bookingsHiddenSources = new Set();
    var bookingsColors = ['#7FAEDB', '#9FD3F2', '#79CDB9', '#F1C77A', '#B9A4E6', '#F2A8B8', '#A7C7A0', '#C6CEDA'];

    function bookingsVisibleSources(sources) {
        sources = (sources || []).slice().sort(function (a, b) { return Number(b.total || 0) - Number(a.total || 0); });
        if (sources.length <= 8) return sources;
        var primary = sources.slice(0, 7);
        var other = Array(12).fill(0);
        sources.slice(7).forEach(function (source) {
            (source.counts || []).forEach(function (value, i) { if (i < 12) other[i] += Number(value) || 0; });
        });
        primary.push({ source: 'Other sources', counts: other, total: other.reduce(function (a, b) { return a + b; }, 0) });
        return primary;
    }

    function drawBookingsChart() {
        var svg = document.getElementById('dashboardBookingsChart');
        if (!svg || !bookingsChartData) return;
        svg.replaceChildren();
        var data = bookingsChartData;
        var allSources = bookingsVisibleSources(data.sources);
        var sources = allSources.filter(function (source) { return !bookingsHiddenSources.has(source.source); });
        var labels = (data.months || []).slice(0, 12);
        var values = Array.from({ length: 12 }, function (_, i) {
            return sources.reduce(function (sum, source) { return sum + Number((source.counts || [])[i] || 0); }, 0);
        });
        var width = chartSize(svg).width, height = Number(svg.getAttribute('viewBox').split(' ')[3]);
        var left = 37, right = width - 10, top = 15, bottom = height - 25;
        var max = Math.max.apply(null, values.concat([1]));
        var tickMax = Math.max(4, Math.ceil(max / 4) * 4);
        for (var step = 0; step <= 4; step++) {
            var tick = tickMax * step / 4;
            var y = bottom - step / 4 * (bottom - top);
            svg.appendChild(svgNode('line', { x1: left, x2: right, y1: y, y2: y, stroke: '#edf2f6', 'stroke-width': 1 }));
            addText(svg, left - 7, y + 3, String(Math.round(tick)), 'end');
        }
        var slot = (right - left) / 12;
        var barWidth = Math.max(7, Math.min(slot * .60, 35));
        var tip = chartTooltip();
        var activeMonth = 0;
        function showTooltip(i, x, y) {
            activeMonth = i;
            tip.replaceChildren();
            var h = document.createElement('strong');
            h.className = 'dashboard-chart-tooltip-date';
            h.textContent = (labels[i] || 'Month') + ' ' + data.year;
            tip.appendChild(h);
            sources.forEach(function (source, sourceIndex) {
                var count = Number((source.counts || [])[i] || 0);
                if (!count) return;
                var row = document.createElement('div');
                row.className = 'dashboard-chart-tooltip-row';
                var label = document.createElement('span');
                var dot = document.createElement('i');
                dot.style.backgroundColor = bookingsColors[allSources.indexOf(source) % bookingsColors.length];
                label.appendChild(dot);
                label.appendChild(document.createTextNode(source.source));
                var value = document.createElement('b');
                value.textContent = count.toLocaleString();
                row.appendChild(label); row.appendChild(value); tip.appendChild(row);
            });
            var line = document.createElement('div');
            line.className = 'dashboard-chart-tooltip-separator';
            tip.appendChild(line);
            var total = document.createElement('div');
            total.className = 'dashboard-chart-tooltip-detail';
            var name = document.createElement('span'); name.textContent = 'Total bookings';
            var count = document.createElement('b'); count.textContent = values[i].toLocaleString();
            total.appendChild(name); total.appendChild(count); tip.appendChild(total);
            tip.hidden = false;
            var rect = tip.getBoundingClientRect();
            tip.style.left = Math.max(8, Math.min(window.innerWidth - rect.width - 8, x + 14)) + 'px';
            tip.style.top = Math.max(8, y + rect.height + 16 > window.innerHeight ? y - rect.height - 14 : y + 14) + 'px';
        }
        for (var i = 0; i < 12; i++) {
            var x = left + slot * (i + 0.5);
            var heightSoFar = 0;
            sources.forEach(function (source, sourceIndex) {
                var val = Number((source.counts || [])[i] || 0);
                if (val <= 0) return;
                var segmentHeight = val / tickMax * (bottom - top);
                svg.appendChild(svgNode('rect', {
                    x: (x - barWidth / 2).toFixed(1),
                    y: (bottom - heightSoFar - segmentHeight).toFixed(1),
                    width: barWidth.toFixed(1), height: Math.max(0.6, segmentHeight).toFixed(1),
                    fill: bookingsColors[allSources.indexOf(source) % bookingsColors.length], rx: 1.5,
                    'pointer-events': 'none'
                }));
                heightSoFar += segmentHeight;
            });
            addText(svg, x, height - 7, labels[i] || '', 'middle');
            var hit = svgNode('rect', {
                x: (x - slot / 2).toFixed(1), y: top, width: slot.toFixed(1), height: bottom - top,
                fill: 'transparent', 'pointer-events': 'all', 'data-month-index': i
            });
            (function (index) {
                hit.addEventListener('mousemove', function (e) { showTooltip(index, e.clientX, e.clientY); });
                hit.addEventListener('mouseenter', function (e) { showTooltip(index, e.clientX, e.clientY); });
            })(i);
            svg.appendChild(hit);
        }
        if (!sources.length && allSources.length) {
            addText(svg, (left + right) / 2, (top + bottom) / 2, 'All sources hidden — use legend to show bookings', 'middle');
        }
        svg.onmouseleave = function () { tip.hidden = true; };
        svg.onblur = function () { tip.hidden = true; };
        svg.onfocus = function () {
            var rect = svg.getBoundingClientRect();
            showTooltip(activeMonth, rect.left + rect.width / 2, rect.top + 15);
        };
        svg.onkeydown = function (e) {
            if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
            e.preventDefault();
            activeMonth = Math.max(0, Math.min(11, activeMonth + (e.key === 'ArrowRight' ? 1 : -1)));
            var rect = svg.getBoundingClientRect();
            showTooltip(activeMonth, rect.left + rect.width / 2, rect.top + 15);
        };
        var legend = document.getElementById('dashboardBookingsLegend');
        if (legend) {
            legend.replaceChildren();
            allSources.forEach(function (source, index) {
                var item = document.createElement('button');
                var isVisible = !bookingsHiddenSources.has(source.source);
                item.type = 'button';
                item.className = 'dashboard-booking-legend-button' + (isVisible ? '' : ' is-hidden');
                item.dataset.source = source.source;
                item.setAttribute('aria-pressed', isVisible ? 'true' : 'false');
                item.setAttribute('aria-label', (isVisible ? 'Hide ' : 'Show ') + source.source + ' bookings');
                item.title = (isVisible ? 'Hide ' : 'Show ') + source.source;
                var dot = document.createElement('i');
                dot.style.backgroundColor = bookingsColors[index % bookingsColors.length];
                item.appendChild(dot);
                item.appendChild(document.createTextNode(source.source));
                item.addEventListener('click', function () {
                    if (bookingsHiddenSources.has(source.source)) bookingsHiddenSources.delete(source.source);
                    else bookingsHiddenSources.add(source.source);
                    var tip = document.getElementById('dashboardChartTooltip');
                    if (tip) tip.hidden = true;
                    drawBookingsChart();
                    var buttons = legend.querySelectorAll('button');
                    for (var k = 0; k < buttons.length; k++) {
                        if (buttons[k].dataset.source === source.source) { buttons[k].focus(); break; }
                    }
                });
                legend.appendChild(item);
            });
        }
    }

    async function loadBookingsChart() {
        var selector = document.getElementById('dashboardBookingsYear');
        var message = document.getElementById('dashboardBookingsMessage');
        var svg = document.getElementById('dashboardBookingsChart');
        if (!selector || !svg) return;
        var year = Number(selector.value);
        if (message) { message.hidden = false; message.textContent = 'Loading ' + year + ' bookings…'; }
        selector.disabled = true;
        try {
            var url = selector.getAttribute('data-bookings-url') + '?year=' + encodeURIComponent(year);
            var response = await fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' }, credentials: 'same-origin' });
            var payload = await response.json();
            if (!response.ok || !payload.ok) throw new Error(payload.message || 'Unable to load bookings.');
            if (Number(selector.value) !== year) return;
            bookingsChartData = payload.bookings;
            bookingsHiddenSources.clear();
            setHtmlText('dashboardBookingsYearLabel', String(year));
            setHtmlText('dashboardBookingsTotal', Number(bookingsChartData.totalBookings || 0).toLocaleString());
            setHtmlText('dashboardBookingsTopSource', (bookingsChartData.sources || []).length ? bookingsChartData.sources[0].source : '—');
            drawBookingsChart();
            if (message) {
                message.hidden = Number(bookingsChartData.totalBookings || 0) > 0;
                if (!message.hidden) message.textContent = 'No booking records found for ' + year + '.';
            }
        } catch (error) {
            bookingsChartData = null;
            svg.replaceChildren();
            var legend = document.getElementById('dashboardBookingsLegend');
            if (legend) legend.replaceChildren();
            if (message) { message.hidden = false; message.textContent = (error && error.message) || 'Bookings could not be loaded.'; }
            setHtmlText('dashboardBookingsTotal', '—');
            setHtmlText('dashboardBookingsTopSource', '—');
        } finally {
            selector.disabled = false;
        }
    }

    // Load below-fold data only when a chart comes near the viewport. This
    // avoids five competing report queries during the initial KPI page paint.
    function whenDashboardSectionNear(elementId, callback) {
        var node = document.getElementById(elementId);
        if (!node || !('IntersectionObserver' in window)) {
            window.setTimeout(callback, 50);
            return;
        }
        var observer = new IntersectionObserver(function (entries) {
            if (!entries.some(function (entry) { return entry.isIntersecting; })) return;
            observer.disconnect();
            callback();
        }, { rootMargin: '320px 0px' });
        observer.observe(node);
    }

    function wireBookingsChart() {
        var year = document.getElementById('dashboardBookingsYear');
        if (!year) return;
        year.addEventListener('change', loadBookingsChart);
        whenDashboardSectionNear('dashboardBookingsChart', loadBookingsChart);
    }

    function wireResponsiveCharts() {
        var charts = [
            ['dashboardOccupancyChart', drawOccupancyOnBooks],
            ['dashboardRevenueChart', drawRevenueTrend],
            ['dashboardExceptionChart', drawExceptionChart],
            ['dashboardBookingsChart', drawBookingsChart],
            // The occupancy calendar is an HTML/CSS grid and resizes natively.
        ];
        var observed = [];
        var rafPending = false;
        function redrawSizedCharts() {
            rafPending = false;
            observed.forEach(function (item) {
                var rect = item.svg.getBoundingClientRect();
                if (rect.width < 1 || rect.height < 1) return;
                var width = Math.round(rect.width);
                var height = Math.round(rect.height);
                if (Math.abs(width - item.lastWidth) < 2 && Math.abs(height - item.lastHeight) < 2) return;
                item.lastWidth = width;
                item.lastHeight = height;
                // Keep lazy charts lazy; do not force-fetch analytics or draw
                // before their existing visibility/data workflow completes.
                if (item.svg.childNodes.length) item.draw();
            });
        }
        function scheduleRedraw() {
            if (rafPending) return;
            rafPending = true;
            window.requestAnimationFrame(redrawSizedCharts);
        }
        var observer = ('ResizeObserver' in window) ? new ResizeObserver(scheduleRedraw) : null;
        charts.forEach(function (entry) {
            var svg = document.getElementById(entry[0]);
            if (!svg) return;
            var rect = svg.getBoundingClientRect();
            observed.push({ svg: svg, draw: entry[1], lastWidth: Math.round(rect.width), lastHeight: Math.round(rect.height) });
            // The card body changes width when its column or sidebar changes.
            if (observer) observer.observe(svg.parentElement);
        });
        window.addEventListener('resize', scheduleRedraw, { passive: true });
    }

    document.addEventListener('DOMContentLoaded', function () {
        ensureRoomStatusTemplateControls();
        wireRoomControls();
        wireDateRangeFilter();
        wireBookingsChart();
        wireYearlyCharts();
        var roomTypeSortSelect = document.getElementById('dashboardRoomTypeSort');
        if (roomTypeSortSelect) roomTypeSortSelect.addEventListener('change', function () {
            roomTypeSort = roomTypeSortSelect.value;
            renderRoomTypes();
        });
        wirePricingGuidance();
        wireResponsiveCharts();
        wireGuestFilters();
        wireStatisticDetails();
        loadRoomPerformance();
        updateRoomLegend(Array.prototype.map.call(document.querySelectorAll('#dashboardRoomRack [data-room-state]'), function (node) {
            return {
                state: node.getAttribute('data-room-state') || 'vacant',
                paymentState: node.getAttribute('data-room-payment') || ''
            };
        }));
        drawWhenVisible('dashboardOccupancyChart', drawOccupancyOnBooks);
        drawWhenVisible('dashboardExceptionChart', drawExceptionChart);

        // Pricing/revenue breakdowns are below the fold. Defer their heavy
        // SQL work while the KPI and room-type chart become interactive.
        whenDashboardSectionNear('dashboardRoomTypesBody', loadAnalytics);
    });
})();
