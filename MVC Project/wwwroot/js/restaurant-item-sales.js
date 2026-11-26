/* Restaurant sales: adapted from ORA Dashboard / Availability dual-month calendar. */
(function () {
    "use strict";
    function wireDateRangeFilter() {
        var form = document.getElementById('risDateFilter');
        if (!form) return;
        var startInput = document.getElementById('risStartDate');
        var endInput = document.getElementById('risEndDate');
        var rangeDisplay = document.getElementById('inventoryRangeDisplay');
        var rangePopover = document.getElementById('inventoryRangePopover');
        var rangeText = document.getElementById('inventoryRangeText');
        var leftMonthLabel = document.getElementById('leftMonthLabel');
        var rightMonthLabel = document.getElementById('rightMonthLabel');
        var leftCalendarGrid = document.getElementById('leftCalendarGrid');
        var rightCalendarGrid = document.getElementById('rightCalendarGrid');
        var selectedRangeText = document.getElementById('selectedRangeText');
        var error = document.getElementById('risRangeError');
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
        function positionPopover() {
            if (rangePopover.hidden) return;
            var anchor = rangeDisplay.getBoundingClientRect();
            var width = Math.min(640, document.documentElement.clientWidth - 24);
            rangePopover.style.width = width + 'px';
            var left = Math.max(12, Math.min(anchor.left, document.documentElement.clientWidth - width - 12));
            rangePopover.style.left = (left - anchor.left) + 'px';
        }
        window.addEventListener('resize', positionPopover);
        function openRangePopover() {
            if (!rangePopover || !rangeDisplay) return;
            showError('');
            draftRangeStart = parseIsoDate(startInput.value);
            draftRangeEnd = parseIsoDate(endInput.value);
            calendarLeftMonth = monthStart(draftRangeStart || new Date());
            renderDateRangeCalendars();
            rangePopover.hidden = false;
            positionPopover();
            rangeDisplay.setAttribute('aria-expanded', 'true');
        }
        function closeRangePopover(restoreFocus) {
            if (!rangePopover || !rangeDisplay) return;
            rangePopover.hidden = true;
            rangeDisplay.setAttribute('aria-expanded', 'false');
            if (restoreFocus) rangeDisplay.focus();
        }
        function validateRange(a, b) {
            if (!a || !b) return 'Please select a start and end date.';
            var days = Math.round((Date.UTC(b.getFullYear(), b.getMonth(), b.getDate()) - Date.UTC(a.getFullYear(), a.getMonth(), a.getDate())) / 86400000) + 1;
            if (days < 1) return 'The end date must be on or after the start date.';
            if (days > 366 || a.getFullYear() < 1900) return 'Select a valid date range of up to 366 days.';
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
            if (!rangePopover.hidden && !document.getElementById('inventoryRangePicker').contains(event.target)) closeRangePopover();
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
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", wireDateRangeFilter);
    else wireDateRangeFilter();
})();
