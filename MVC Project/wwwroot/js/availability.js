(function () {
    'use strict';

    const page = document.getElementById('availabilityPage');
    if (!page) return;

    const token = document.querySelector('#availabilityAntiForgery input[name="__RequestVerificationToken"]')?.value || '';
    const saveUrl = page.dataset.saveUrl || '';
    const gridUrl = page.dataset.gridUrl || '';
    const autoUpdateUrl = page.dataset.autoUpdateUrl || '';
    const logUrl = page.dataset.logUrl || '';
    const bulkPreviewUrl = page.dataset.bulkPreviewUrl || '';
    const bulkRateSaveUrl = page.dataset.bulkRateSaveUrl || '';
    const restrictionRangeUrl = page.dataset.restrictionRangeUrl || '';
    const bulkRestrictionSaveUrl = page.dataset.bulkRestrictionSaveUrl || '';

    const saveButton = document.getElementById('saveChangesButton');
    const dirtyCount = document.getElementById('dirtyCount');
    const autoUpdateButton = document.getElementById('autoUpdateButton');
    const categorySelect = document.getElementById('categoryId');
    let appliedCategoryValue = categorySelect?.value || '';
    const startInput = document.getElementById('start');
    const endInput = document.getElementById('end');
    const filterForm = document.getElementById('inventoryFilterForm');
    const toast = document.getElementById('availabilityToast');
    const gridHost = document.getElementById('inventoryGridHost');
    const hotelBaseRate = Number(page.dataset.hotelBaseRate || '0');
    let gridRequestController = null;
    const restrictionRangeCache = new Map();

    function showToast(message, type) {
        if (!toast) return;
        toast.textContent = message || '';
        toast.className = 'inventory-toast show ' + (type || 'success');
        window.clearTimeout(showToast.timer);
        showToast.timer = window.setTimeout(() => toast.classList.remove('show'), 3500);
    }

    function setLoading(button, loading, text) {
        if (!button) return;
        if (loading) {
            if (!button.dataset.originalHtml) button.dataset.originalHtml = button.innerHTML;
            button.disabled = true;
            button.innerHTML = '<i class="fa-solid fa-spinner fa-spin"></i><span>' + (text || 'Working') + '</span>';
        } else {
            if (button.dataset.originalHtml) button.innerHTML = button.dataset.originalHtml;
            button.disabled = false;
        }
    }

    async function postJson(url, body) {
        const response = await fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token
            },
            body: JSON.stringify(body)
        });
        let data;
        try { data = await response.json(); }
        catch { throw new Error('The server returned an invalid response.'); }
        if (!response.ok || data?.ok === false) throw new Error(data?.message || 'The operation failed.');
        return data;
    }

    function restrictionCacheKey(categoryId, planId, startDate, endDate) {
        return [categoryId || '', planId || '', startDate || '', endDate || ''].join('||');
    }

    async function getRestrictionRangeData(categoryId, planId, startDate, endDate) {
        const key = restrictionCacheKey(categoryId, planId, startDate, endDate);
        if (restrictionRangeCache.has(key)) return await restrictionRangeCache.get(key);

        // Keep the cache bounded while retaining recently opened plans for instant reopen.
        if (restrictionRangeCache.size > 40) restrictionRangeCache.clear();

        const pending = postJson(restrictionRangeUrl, {
            categoryId: categoryId,
            planId: planId,
            startDate: startDate,
            endDate: endDate
        });
        restrictionRangeCache.set(key, pending);
        try {
            return await pending;
        } catch (error) {
            restrictionRangeCache.delete(key);
            throw error;
        }
    }

    function clearRestrictionRangeCache(categoryId, planId) {
        const prefix = (categoryId || '') + '||' + (planId || '') + '||';
        Array.from(restrictionRangeCache.keys()).forEach(key => {
            if (key.startsWith(prefix)) restrictionRangeCache.delete(key);
        });
    }

    function currentGridScroll() {
        return document.getElementById('inventoryGridScroll');
    }

    function syncAddressBar() {
        try {
            const url = new URL(window.location.href);
            if (startInput?.value) url.searchParams.set('start', startInput.value);
            else url.searchParams.delete('start');
            if (endInput?.value) url.searchParams.set('end', endInput.value);
            else url.searchParams.delete('end');
            if (categorySelect?.value) url.searchParams.set('categoryId', categorySelect.value);
            else url.searchParams.delete('categoryId');
            window.history.replaceState({}, '', url.toString());
        } catch { }
    }

    function hasUnsavedRates() {
        return getDirtyInputs().length > 0;
    }

    async function refreshGrid(options) {
        options = options || {};
        if (!gridHost || !gridUrl) return false;

        if (!options.ignoreDirty && hasUnsavedRates()) {
            showToast('Save or cancel the changed rates before changing the inventory view.', 'warning');
            return false;
        }

        const oldGrid = currentGridScroll();
        const scrollLeft = options.preserveScroll !== false && oldGrid ? oldGrid.scrollLeft : 0;
        const scrollTop = options.preserveScroll !== false && oldGrid ? oldGrid.scrollTop : 0;

        if (gridRequestController) gridRequestController.abort();
        const controller = new AbortController();
        gridRequestController = controller;

        const params = new URLSearchParams();
        if (startInput?.value) params.set('start', startInput.value);
        if (endInput?.value) params.set('end', endInput.value);
        if (categorySelect?.value) params.set('categoryId', categorySelect.value);

        gridHost.classList.add('is-loading');
        gridHost.setAttribute('aria-busy', 'true');

        try {
            const response = await fetch(gridUrl + '?' + params.toString(), {
                method: 'GET',
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' },
                signal: controller.signal
            });
            if (!response.ok) throw new Error('Unable to refresh inventory.');
            const html = await response.text();
            gridHost.innerHTML = html;
            refreshDirtyState();
            syncAddressBar();
            // Paint the new grid first; restore any expanded restriction groups just
            // after the frame so date/category navigation feels immediate.
            window.requestAnimationFrame(() => restoreRestrictionGroups());

            const newGrid = currentGridScroll();
            if (newGrid) {
                newGrid.scrollLeft = scrollLeft;
                newGrid.scrollTop = scrollTop;
            }
            return true;
        } catch (error) {
            if (error?.name !== 'AbortError') showToast(error.message || 'Unable to refresh inventory.', 'error');
            return false;
        } finally {
            if (gridRequestController === controller) {
                gridHost.classList.remove('is-loading');
                gridHost.removeAttribute('aria-busy');
                gridRequestController = null;
            }
        }
    }

    // =============================================================
    // Manual rate editing + Save button count
    // =============================================================
    function sanitizeRateText(value) {
        let text = String(value ?? '').replace(/[^0-9.]/g, '');
        const firstDot = text.indexOf('.');
        if (firstDot >= 0) {
            text = text.slice(0, firstDot + 1) + text.slice(firstDot + 1).replace(/\./g, '');
            const parts = text.split('.');
            parts[1] = (parts[1] || '').slice(0, 2);
            text = parts.join('.');
        }
        return text;
    }

    function getDirtyInputs() {
        return Array.from(page.querySelectorAll('.rate-cell-input:not(:disabled)')).filter(input =>
            String(input.value ?? '').trim() !== String(input.dataset.original ?? '').trim());
    }

    function refreshDirtyState() {
        const dirty = getDirtyInputs();
        if (dirtyCount) dirtyCount.textContent = String(dirty.length);
        if (saveButton) {
            saveButton.disabled = dirty.length === 0;
            saveButton.classList.toggle('has-rate-changes', dirty.length > 0);
        }
        page.classList.toggle('has-unsaved-changes', dirty.length > 0);
    }

    page.addEventListener('input', function (event) {
        const input = event.target.closest?.('.rate-cell-input');
        if (!input || input.disabled) return;
        const clean = sanitizeRateText(input.value);
        if (input.value !== clean) input.value = clean;
        const dirty = String(input.value).trim() !== String(input.dataset.original || '').trim();
        input.classList.toggle('rate-dirty', dirty);
        input.closest('.rate-value-cell')?.classList.toggle('rate-dirty-cell', dirty);
        refreshDirtyState();
    }, true);

    page.addEventListener('keydown', function (event) {
        const input = event.target.closest?.('.rate-cell-input');
        if (!input || input.disabled) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            input.value = input.dataset.original || '';
            input.classList.remove('rate-dirty');
            input.closest('.rate-value-cell')?.classList.remove('rate-dirty-cell');
            refreshDirtyState();
        } else if (event.key === 'Enter') {
            event.preventDefault();
            const row = input.closest('tr');
            const inputs = row ? Array.from(row.querySelectorAll('.rate-cell-input:not(:disabled)')) : [];
            const index = inputs.indexOf(input);
            if (index >= 0 && index + 1 < inputs.length) {
                inputs[index + 1].focus();
                inputs[index + 1].select();
            }
        }
    }, true);

    saveButton?.addEventListener('click', async function () {
        const dirty = getDirtyInputs();
        if (!dirty.length) return;

        const validInputs = [];
        const changes = [];
        const skippedBelowBase = [];

        // Match WebForms Save All: validate every changed date independently.
        // A below-base date is skipped, while every other valid date is saved.
        for (const input of dirty) {
            const value = String(input.value || '').trim();
            if (!/^\d+(?:\.\d{1,2})?$/.test(value) || Number(value) <= 0) {
                input.focus();
                showToast('Enter a valid rate with up to 2 decimal places.', 'warning');
                return;
            }

            const numericRate = Number(value);
            if (Number.isFinite(hotelBaseRate) && hotelBaseRate > 0 && numericRate < hotelBaseRate) {
                skippedBelowBase.push(input);
                input.setAttribute('aria-invalid', 'true');
                input.title = 'Rate cannot be less than the base rate ' + hotelBaseRate.toFixed(2) + '.';
                input.classList.add('rate-below-base');
                continue;
            }

            input.removeAttribute('aria-invalid');
            input.removeAttribute('title');
            input.classList.remove('rate-below-base');
            validInputs.push(input);
            changes.push({
                categoryId: input.dataset.category || '',
                planId: input.dataset.plan || '',
                rowType: 'rate',
                date: input.dataset.date || '',
                value: value
            });
        }

        if (!changes.length) {
            const message = skippedBelowBase.length === 1
                ? 'Rate cannot be less than the base rate ' + hotelBaseRate.toFixed(2) + '. The rate was not saved.'
                : skippedBelowBase.length + ' rates are below the base rate ' + hotelBaseRate.toFixed(2) + ' and were not saved.';
            skippedBelowBase[0]?.focus();
            showToast(message, 'warning');
            refreshDirtyState();
            return;
        }

        setLoading(saveButton, true, 'Saving');
        try {
            const data = await postJson(saveUrl, { changes });
            validInputs.forEach(input => {
                input.dataset.original = String(input.value || '');
                input.classList.remove('rate-dirty', 'rate-below-base');
                input.removeAttribute('aria-invalid');
                input.removeAttribute('title');
                input.closest('.rate-value-cell')?.classList.remove('rate-dirty-cell');
            });
            refreshDirtyState();

            const hasServerSkipped = /below the base rate/i.test(data.message || '');
            const hasSkipped = skippedBelowBase.length > 0 || hasServerSkipped;
            let saveMessage = data.message || 'Rates saved.';
            if (skippedBelowBase.length > 0 && !hasServerSkipped) {
                saveMessage += skippedBelowBase.length === 1
                    ? ' 1 rate below the base rate ' + hotelBaseRate.toFixed(2) + ' was not saved.'
                    : ' ' + skippedBelowBase.length + ' rates below the base rate ' + hotelBaseRate.toFixed(2) + ' were not saved.';
            }
            showToast(saveMessage, hasSkipped ? 'warning' : 'success');

            // Re-read only the grid. This updates derived rates and restores any
            // below-base date to its persisted database value without a full page refresh.
            await refreshGrid({ preserveScroll: true, ignoreDirty: true });
        } catch (error) {
            showToast(error.message || 'Unable to save changes.', 'error');
        } finally {
            setLoading(saveButton, false);
            refreshDirtyState();
        }
    });


    // =============================================================
    // Category + Auto Update
    // =============================================================
    categorySelect?.addEventListener('change', async function () {
        if (hasUnsavedRates()) {
            categorySelect.value = appliedCategoryValue;
            showToast('Save or cancel the changed rates before changing category.', 'warning');
            return;
        }
        const requestedValue = categorySelect.value;
        const refreshed = await refreshGrid({ preserveScroll: false });
        if (refreshed) appliedCategoryValue = requestedValue;
        else categorySelect.value = appliedCategoryValue;
    });

    autoUpdateButton?.addEventListener('click', async function () {
        if (!startInput?.value || !endInput?.value) {
            showToast('Please select Start Date and End Date.', 'warning');
            return;
        }
        setLoading(autoUpdateButton, true, 'Starting');
        try {
            const data = await postJson(autoUpdateUrl, {
                startDate: startInput.value,
                endDate: endInput.value,
                categoryId: categorySelect?.value || ''
            });
            showToast(data.message || 'Auto Update started in background.', 'success');
        } catch (error) {
            showToast(error.message || 'Unable to start Auto Update.', 'error');
        } finally {
            setLoading(autoUpdateButton, false);
        }
    });

    // =============================================================
    // Two-month date range calendar
    // =============================================================
    const rangeDisplay = document.getElementById('inventoryRangeDisplay');
    const rangePopover = document.getElementById('inventoryRangePopover');
    const rangeText = document.getElementById('inventoryRangeText');
    const leftMonthLabel = document.getElementById('leftMonthLabel');
    const rightMonthLabel = document.getElementById('rightMonthLabel');
    const leftCalendarGrid = document.getElementById('leftCalendarGrid');
    const rightCalendarGrid = document.getElementById('rightCalendarGrid');
    const selectedRangeText = document.getElementById('selectedRangeText');
    let calendarLeftMonth = null;
    let draftRangeStart = null;
    let draftRangeEnd = null;

    function parseIsoDate(value) {
        if (!value) return null;
        const p = value.split('-').map(Number);
        if (p.length !== 3 || p.some(Number.isNaN)) return null;
        const date = new Date(p[0], p[1] - 1, p[2]);
        return Number.isNaN(date.getTime()) ? null : date;
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

    function updateRangeText() {
        if (rangeText) rangeText.textContent = formatDisplayDate(parseIsoDate(startInput?.value)) + ' - ' + formatDisplayDate(parseIsoDate(endInput?.value));
    }
    function updateDraftRangeText() {
        if (!selectedRangeText) return;
        if (!draftRangeStart) { selectedRangeText.textContent = 'Select date range'; return; }
        selectedRangeText.textContent = formatDisplayDate(draftRangeStart) + ' - ' + (draftRangeEnd ? formatDisplayDate(draftRangeEnd) : 'Select end date');
    }
    function selectCalendarDate(date) {
        const clicked = new Date(date.getFullYear(), date.getMonth(), date.getDate());
        if (!draftRangeStart || draftRangeEnd) { draftRangeStart = clicked; draftRangeEnd = null; }
        else if (dayTime(clicked) < dayTime(draftRangeStart)) { draftRangeEnd = draftRangeStart; draftRangeStart = clicked; }
        else draftRangeEnd = clicked;
        renderDateRangeCalendars();
    }
    function renderMonth(gridElement, monthDate) {
        if (!gridElement) return;
        gridElement.innerHTML = '';
        const first = monthStart(monthDate);
        const firstVisible = new Date(first.getFullYear(), first.getMonth(), 1 - first.getDay());
        const month = first.getMonth();
        const now = new Date();
        const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
        for (let i = 0; i < 42; i++) {
            const current = new Date(firstVisible.getFullYear(), firstVisible.getMonth(), firstVisible.getDate() + i);
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'inventory-calendar-day';
            button.textContent = String(current.getDate());
            button.dataset.date = toIsoDate(current);
            if (current.getMonth() !== month) button.classList.add('outside-month');
            if (sameDay(current, today)) button.classList.add('today');
            const currentTime = dayTime(current);
            const startTime = draftRangeStart ? dayTime(draftRangeStart) : null;
            const endTime = draftRangeEnd ? dayTime(draftRangeEnd) : null;
            if (startTime !== null && endTime !== null && currentTime >= startTime && currentTime <= endTime) button.classList.add('range-fill');
            if (startTime !== null && currentTime === startTime) button.classList.add('range-start');
            if (endTime !== null && currentTime === endTime) button.classList.add('range-end');
            button.addEventListener('click', e => { e.preventDefault(); e.stopPropagation(); selectCalendarDate(current); });
            gridElement.appendChild(button);
        }
    }
    function renderDateRangeCalendars() {
        if (!calendarLeftMonth) calendarLeftMonth = monthStart(draftRangeStart || parseIsoDate(startInput?.value) || new Date());
        const right = addMonths(calendarLeftMonth, 1);
        if (leftMonthLabel) leftMonthLabel.textContent = monthTitle(calendarLeftMonth);
        if (rightMonthLabel) rightMonthLabel.textContent = monthTitle(right);
        renderMonth(leftCalendarGrid, calendarLeftMonth);
        renderMonth(rightCalendarGrid, right);
        updateDraftRangeText();
    }
    function openRangePopover() {
        if (!rangePopover || !rangeDisplay) return;
        draftRangeStart = parseIsoDate(startInput?.value);
        draftRangeEnd = parseIsoDate(endInput?.value);
        calendarLeftMonth = monthStart(draftRangeStart || new Date());
        renderDateRangeCalendars();
        rangePopover.hidden = false;
        rangeDisplay.setAttribute('aria-expanded', 'true');
    }
    function closeRangePopover() {
        if (!rangePopover || !rangeDisplay) return;
        rangePopover.hidden = true;
        rangeDisplay.setAttribute('aria-expanded', 'false');
    }

    rangeDisplay?.addEventListener('click', e => { e.preventDefault(); e.stopPropagation(); rangePopover?.hidden === false ? closeRangePopover() : openRangePopover(); });
    document.querySelectorAll('[data-calendar-shift]').forEach(button => button.addEventListener('click', e => {
        e.preventDefault(); e.stopPropagation();
        calendarLeftMonth = addMonths(calendarLeftMonth || new Date(), Number(button.dataset.calendarShift || 0));
        renderDateRangeCalendars();
    }));
    document.getElementById('rangeCloseButton')?.addEventListener('click', e => { e.preventDefault(); closeRangePopover(); });
    document.getElementById('rangeClearButton')?.addEventListener('click', e => { e.preventDefault(); draftRangeStart = null; draftRangeEnd = null; renderDateRangeCalendars(); });
    document.getElementById('rangeApplyButton')?.addEventListener('click', async function (event) {
        event.preventDefault();
        if (hasUnsavedRates()) { showToast('Save or cancel the changed rates before changing dates.', 'warning'); return; }
        if (!draftRangeStart) { showToast('Please select a start date.', 'warning'); return; }
        if (!draftRangeEnd) draftRangeEnd = new Date(draftRangeStart);
        const oldStart = startInput?.value || '';
        const oldEnd = endInput?.value || '';
        if (startInput) startInput.value = toIsoDate(draftRangeStart);
        if (endInput) endInput.value = toIsoDate(draftRangeEnd);
        updateRangeText();
        closeRangePopover();
        const refreshed = await refreshGrid({ preserveScroll: false });
        if (!refreshed) {
            if (startInput) startInput.value = oldStart;
            if (endInput) endInput.value = oldEnd;
            updateRangeText();
        }
    });
    document.addEventListener('click', event => {
        if (!rangePopover || rangePopover.hidden) return;
        const picker = document.getElementById('inventoryRangePicker');
        if (picker && !picker.contains(event.target)) closeRangePopover();
    });


    document.getElementById('inventorySearchButton')?.addEventListener('click', function () {
        refreshGrid({ preserveScroll: false });
    });

    filterForm?.addEventListener('submit', function (event) {
        event.preventDefault();
        refreshGrid({ preserveScroll: false });
    });

    page.querySelectorAll('[data-shift-days]').forEach(button => button.addEventListener('click', async function () {
        if (hasUnsavedRates()) {
            showToast('Save or cancel the changed rates before changing dates.', 'warning');
            return;
        }
        const shift = Number(button.dataset.shiftDays || 0);
        const oldStart = startInput?.value || '';
        const oldEnd = endInput?.value || '';
        const startDate = parseIsoDate(oldStart);
        const endDate = parseIsoDate(oldEnd);
        if (!startDate || !endDate || !shift) return;
        startDate.setDate(startDate.getDate() + shift);
        endDate.setDate(endDate.getDate() + shift);
        if (startInput) startInput.value = toIsoDate(startDate);
        if (endInput) endInput.value = toIsoDate(endDate);
        updateRangeText();
        const refreshed = await refreshGrid({ preserveScroll: false });
        if (!refreshed) {
            if (startInput) startInput.value = oldStart;
            if (endInput) endInput.value = oldEnd;
            updateRangeText();
        }
    }));

    // =============================================================
    // Restriction expand/collapse, lazy-loaded on first open
    // =============================================================
    const openGroupsKey = 'Availability.OpenRestrictionGroups.' + (page.dataset.hotel || 'current');
    const restrictionDefinitions = {
        min_stay_arrival: { label: 'Min Stay Arrival', boolean: false, min: 1, max: 999, field: 'minStayArrival' },
        min_stay_through: { label: 'Min Stay Through', boolean: false, min: 1, max: 999, field: 'minStayThrough' },
        max_stay: { label: 'Max Stay', boolean: false, min: 0, max: 999, field: 'maxStay' },
        booking_cutoff: { label: 'Booking Cutoff', boolean: false, min: 0, max: 365, field: 'bookingCutoff' },
        closed_to_arrival: { label: 'Closed To Arrival', boolean: true, min: 0, max: 1, field: 'closedToArrival' },
        closed_to_departure: { label: 'Closed To Departure', boolean: true, min: 0, max: 1, field: 'closedToDeparture' },
        stop_sell: { label: 'Stop Sell', boolean: true, min: 0, max: 1, field: 'stopSell' }
    };

    function groupKey(category, plan) { return (category || '') + '||' + (plan || ''); }
    function readOpenGroups() { try { return JSON.parse(sessionStorage.getItem(openGroupsKey) || '[]'); } catch { return []; } }
    function writeOpenGroups(groups) { try { sessionStorage.setItem(openGroupsKey, JSON.stringify(Array.from(new Set(groups)))); } catch { } }

    function setRestrictionGroup(category, plan, open) {
        const key = groupKey(category, plan);
        page.querySelectorAll('.restriction-row').forEach(row => {
            const matches = groupKey(row.dataset.category, row.dataset.restrictionPlan) === key;
            if (matches) row.hidden = !open;
        });
        page.querySelectorAll('.restriction-toggle').forEach(button => {
            if (groupKey(button.dataset.category, button.dataset.plan) === key) {
                button.classList.toggle('open', open);
                button.setAttribute('aria-expanded', open ? 'true' : 'false');
            }
        });
        const groups = readOpenGroups().filter(x => x !== key);
        if (open) groups.push(key);
        writeOpenGroups(groups);
    }

    function restrictionApiDateToIso(value) {
        const parts = String(value || '').split('/');
        if (parts.length !== 3) return '';
        return parts[2] + '-' + parts[1].padStart(2, '0') + '-' + parts[0].padStart(2, '0');
    }

    function restrictionRawValue(definition, shown) {
        if (!definition?.boolean) return String(shown ?? '');
        return String(shown || '').toLowerCase() === 'closed' ? '1' : '0';
    }

    function restrictionLoadingSelector(categoryId, planId) {
        return '.restriction-loading-placeholder[data-category="' + CSS.escape(categoryId) + '"][data-restriction-plan="' + CSS.escape(planId) + '"]';
    }

    function removeRestrictionLoadingRow(categoryId, planId) {
        page.querySelectorAll(restrictionLoadingSelector(categoryId, planId)).forEach(row => row.remove());
    }

    function insertRestrictionLoadingRow(toggle) {
        if (!toggle) return;
        const categoryId = toggle.dataset.category || '';
        const planId = toggle.dataset.plan || '';
        if (!categoryId || !planId) return;
        if (page.querySelector(restrictionLoadingSelector(categoryId, planId))) return;

        const rateRow = toggle.closest('tr');
        if (!rateRow || !rateRow.parentNode) return;
        const dateCount = page.querySelectorAll('.inventory-date-heading[data-date]').length;
        const tr = document.createElement('tr');
        tr.className = 'restriction-row restriction-loading-placeholder';
        tr.dataset.category = categoryId;
        tr.dataset.restrictionPlan = planId;
        tr.dataset.restriction = '__loading__';

        const label = document.createElement('th');
        label.className = 'sticky-col restriction-label';
        label.innerHTML = '<span class="restriction-tree">↳</span><span>Restrictions</span>';
        tr.appendChild(label);

        const cell = document.createElement('td');
        cell.colSpan = Math.max(1, dateCount);
        cell.className = 'restriction-loading-cell';
        cell.textContent = 'Loading restriction details…';
        tr.appendChild(cell);

        rateRow.parentNode.insertBefore(tr, rateRow.nextSibling);
    }

    function prefetchRestrictionRange(toggle) {
        if (!toggle || toggle.dataset.restrictionsLoaded === '1') return;
        const categoryId = toggle.dataset.category || '';
        const planId = toggle.dataset.plan || '';
        if (!categoryId || !planId) return;
        getRestrictionRangeData(
            categoryId,
            planId,
            startInput?.value || '',
            endInput?.value || ''
        ).catch(function () { });
    }

    async function ensureRestrictionRows(toggle) {
        if (!toggle) return false;
        if (toggle.dataset.restrictionsLoaded === '1') return true;
        if (toggle.dataset.restrictionsLoading === '1') return false;

        const categoryId = toggle.dataset.category || '';
        const planId = toggle.dataset.plan || '';
        const keys = String(toggle.dataset.restrictions || '').split(',').map(x => x.trim()).filter(Boolean);
        if (!categoryId || !planId || !keys.length) return false;

        toggle.dataset.restrictionsLoading = '1';
        try {
            const data = await getRestrictionRangeData(
                categoryId,
                planId,
                startInput?.value || '',
                endInput?.value || '');

            const rowsByDate = new Map();
            (data.rows || []).forEach(row => {
                const iso = restrictionApiDateToIso(row.date);
                if (iso) rowsByDate.set(iso, row);
            });

            const headers = Array.from(page.querySelectorAll('.inventory-date-heading[data-date]'));
            const rateRow = toggle.closest('tr');
            if (!rateRow) return false;
            const fragment = document.createDocumentFragment();
            const hotelToday = page.dataset.hotelToday || '';
            const canEdit = toggle.dataset.canRestriction === '1';
            const categoryName = toggle.dataset.categoryname || '';
            const planName = toggle.dataset.planname || '';

            keys.forEach(key => {
                const definition = restrictionDefinitions[key];
                if (!definition) return;

                const tr = document.createElement('tr');
                tr.className = 'restriction-row';
                tr.hidden = true;
                tr.dataset.restrictionPlan = planId;
                tr.dataset.category = categoryId;
                tr.dataset.restriction = key;

                const label = document.createElement('th');
                label.className = 'sticky-col restriction-label';
                label.innerHTML = '<span class="restriction-tree">↳</span><span>' + escapeHtml(definition.label) + '</span>';
                tr.appendChild(label);

                headers.forEach(header => {
                    const iso = header.dataset.date || '';
                    const apiRow = rowsByDate.get(iso) || {};
                    const shown = apiRow[definition.field] ?? (definition.boolean ? 'Open' : '—');
                    const raw = restrictionRawValue(definition, shown);
                    const isPast = hotelToday && iso < hotelToday;
                    const editable = canEdit && !isPast;

                    const td = document.createElement('td');
                    td.className = 'restriction-cell inventory-drag-cell';
                    if (header.classList.contains('weekend')) td.classList.add('weekend');
                    if (header.classList.contains('today')) td.classList.add('today');
                    if (key === 'stop_sell' && String(shown).toLowerCase() === 'closed') td.classList.add('stop-sell');
                    td.dataset.rowtype = key;
                    td.dataset.category = categoryId;
                    td.dataset.categoryname = categoryName;
                    td.dataset.plan = planId;
                    td.dataset.planname = planName;
                    td.dataset.date = iso;
                    td.dataset.current = raw;
                    td.dataset.key = key;
                    td.dataset.label = definition.label;
                    td.dataset.boolean = definition.boolean ? '1' : '0';
                    td.dataset.min = String(definition.min);
                    td.dataset.max = String(definition.max);
                    td.dataset.canRestriction = editable ? '1' : '0';

                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'restriction-cell-button';
                    button.tabIndex = -1;
                    button.disabled = !editable;
                    const span = document.createElement('span');
                    span.textContent = String(shown || '—');
                    button.appendChild(span);
                    td.appendChild(button);
                    tr.appendChild(td);
                });

                fragment.appendChild(tr);
            });

            // Insert all restriction rows in one DOM operation. This avoids a layout
            // recalculation for every restriction row/date and makes expansion much faster.
            rateRow.parentNode.insertBefore(fragment, rateRow.nextSibling);

            removeRestrictionLoadingRow(categoryId, planId);
            toggle.dataset.restrictionsLoaded = '1';
            return true;
        } catch (error) {
            removeRestrictionLoadingRow(categoryId, planId);
            showToast(error.message || 'Unable to load restrictions.', 'error');
            return false;
        } finally {
            toggle.dataset.restrictionsLoading = '0';
        }
    }

    page.addEventListener('click', async event => {
        const toggle = event.target.closest?.('.restriction-toggle');
        if (!toggle) return;
        event.preventDefault();
        event.stopPropagation();

        const categoryId = toggle.dataset.category || '';
        const planId = toggle.dataset.plan || '';
        const opening = !toggle.classList.contains('open');

        if (!opening) {
            setRestrictionGroup(categoryId, planId, false);
            return;
        }

        // Open immediately. If data is not already in the client/server hot cache, show
        // one lightweight row instead of spinning the chevron while the user waits.
        if (toggle.dataset.restrictionsLoaded !== '1') {
            insertRestrictionLoadingRow(toggle);
        }
        setRestrictionGroup(categoryId, planId, true);

        const loaded = await ensureRestrictionRows(toggle);
        if (!loaded) {
            setRestrictionGroup(categoryId, planId, false);
            return;
        }
        setRestrictionGroup(categoryId, planId, true);
    });

    // Start loading on hover/focus so a normal click is usually instant.
    page.addEventListener('pointerover', event => {
        const toggle = event.target.closest?.('.restriction-toggle');
        if (toggle) prefetchRestrictionRange(toggle);
    });
    page.addEventListener('focusin', event => {
        const toggle = event.target.closest?.('.restriction-toggle');
        if (toggle) prefetchRestrictionRange(toggle);
    });

    function restoreRestrictionGroups() {
        readOpenGroups().forEach(async key => {
            const [category, plan] = String(key).split('||');
            if (!category || !plan) return;
            const toggle = Array.from(page.querySelectorAll('.restriction-toggle')).find(button =>
                button.dataset.category === category && button.dataset.plan === plan);
            if (!toggle) return;
            const loaded = await ensureRestrictionRows(toggle);
            if (loaded) setRestrictionGroup(category, plan, true);
        });
    }
    restoreRestrictionGroups();

    // =============================================================
    // Availability change log drawer
    // =============================================================
    const logOverlay = document.getElementById('availabilityLogModal');
    function setText(id, value) { const el = document.getElementById(id); if (el) el.textContent = value == null || value === '' ? '—' : String(value); }
    function escapeHtml(value) { return String(value ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;'); }
    function openLogDrawer() { logOverlay?.classList.add('is-open'); logOverlay?.setAttribute('aria-hidden', 'false'); document.body.classList.add('availability-log-open'); }
    function closeLogDrawer() { logOverlay?.classList.remove('is-open'); logOverlay?.setAttribute('aria-hidden', 'true'); document.body.classList.remove('availability-log-open'); }
    function showLogState(state) {
        ['availabilityLogLoading', 'availabilityLogError', 'availabilityLogEmpty', 'availabilityLogTimeline'].forEach(id => {
            const el = document.getElementById(id); if (el) el.hidden = id !== state;
        });
    }
    function renderLogRows(rows) {
        rows = Array.isArray(rows) ? rows : [];
        setText('availabilityLogCount', rows.length);
        if (!rows.length) { showLogState('availabilityLogEmpty'); return; }
        const timeline = document.getElementById('availabilityLogTimeline');
        if (!timeline) return;
        timeline.innerHTML = rows.map(row => {
            const changedOn = row.createdOn || ((row.logDate || '') + ' ' + (row.logTime || '')).trim() || '—';
            const previous = String(row.previousAvailability ?? '').trim();
            const current = row.availability ?? '—';
            const user = row.username || row.userId || 'System';
            const status = row.isSuccess ? 'Success' : 'Failed';
            return '<article class="availability-log-item">' +
                '<span class="availability-log-node ' + (row.isSuccess ? '' : 'failed') + '"></span>' +
                '<div class="availability-log-card"><div class="availability-log-card-top"><strong>' + escapeHtml(changedOn) + '</strong>' +
                '<span class="availability-log-badge ' + (row.isSuccess ? 'success' : 'failed') + '">' + status + '</span></div>' +
                '<div class="availability-log-card-body">' +
                (previous ? '<div class="availability-log-change"><span>' + escapeHtml(previous) + '</span><b>→</b><span class="current">' + escapeHtml(current) + '</span></div>' : '<div class="availability-log-change"><small>Set to</small><span class="current">' + escapeHtml(current) + '</span></div>') +
                '<div class="availability-log-meta">Changed by <strong>' + escapeHtml(user) + '</strong> · HTTP <strong>' + escapeHtml(row.httpStatus || '—') + '</strong></div>' +
                ((row.systemName || row.ipAddress) ? '<div class="availability-log-system">' + escapeHtml([row.systemName, row.ipAddress].filter(Boolean).join(' · ')) + '</div>' : '') +
                '</div></div></article>';
        }).join('');
        showLogState('availabilityLogTimeline');
    }
    async function openAvailabilityLog(cell) {
        const categoryId = cell.dataset.category || '';
        const categoryName = cell.dataset.categoryname || 'Availability';
        const date = cell.dataset.date || '';
        if (!categoryId || !date || !logUrl) return;
        setText('availabilityLogDate', date); setText('availabilityLogCategory', categoryName);
        setText('availabilityLogSubtitle', date + ' · ' + categoryName); setText('availabilityLogCount', '—');
        showLogState('availabilityLogLoading'); openLogDrawer();
        try {
            const response = await fetch(logUrl + '?categoryId=' + encodeURIComponent(categoryId) + '&date=' + encodeURIComponent(date), { credentials: 'same-origin' });
            const data = await response.json();
            if (!response.ok || data?.ok === false) throw new Error(data?.message || 'Unable to load change log.');
            renderLogRows(data.rows || []);
        } catch (error) {
            const box = document.getElementById('availabilityLogError'); if (box) box.textContent = error.message || 'Unable to load availability change log.';
            showLogState('availabilityLogError');
        }
    }
    page.addEventListener('click', event => {
        const cell = event.target.closest?.('.availability-log-cell');
        if (!cell) return;
        event.preventDefault(); event.stopPropagation(); openAvailabilityLog(cell);
    });
    document.getElementById('availabilityLogClose')?.addEventListener('click', closeLogDrawer);
    logOverlay?.addEventListener('click', event => { if (event.target === logOverlay) closeLogDrawer(); });

    // =============================================================
    // WebForms-style drag editor for rates and restrictions
    // =============================================================
    const bulkModal = document.getElementById('bulkEditorModal');
    const bulkSaveButton = document.getElementById('bulkSaveButton');
    const drag = { active: false, moved: false, start: null, current: null, cells: [] };
    let suppressCellClickUntil = 0;
    let previewTimer = null;

    function clearBulkSelection() {
        page.querySelectorAll('.inventory-drag-cell.bulk-selected').forEach(cell => cell.classList.remove('bulk-selected'));
    }
    function rowType(cell) { return cell?.dataset.rowtype || ''; }
    function isRateCell(cell) { return rowType(cell) === 'rate'; }
    function isRestrictionCell(cell) { return !!cell && rowType(cell) !== 'rate' && rowType(cell) !== 'availability' && rowType(cell) !== 'net'; }
    function canDragCell(cell) { return isRateCell(cell) ? cell.dataset.canBulk === '1' : isRestrictionCell(cell) && cell.dataset.canRestriction === '1'; }
    function cellValue(cell) {
        if (isRateCell(cell)) return String(cell.querySelector('.rate-cell-input')?.value || '').trim();
        return String(cell.dataset.current ?? cell.textContent ?? '').trim();
    }
    function sameDragGroup(a, b) {
        return a && b && rowType(a) === rowType(b) && a.dataset.category === b.dataset.category && a.dataset.plan === b.dataset.plan && a.closest('tr') === b.closest('tr');
    }
    function selectRangeTo(cell) {
        if (!drag.start || !sameDragGroup(drag.start, cell)) return;
        const rowCells = Array.from(drag.start.closest('tr').querySelectorAll('.inventory-drag-cell')).filter(c => sameDragGroup(drag.start, c));
        const a = rowCells.indexOf(drag.start), b = rowCells.indexOf(cell);
        if (a < 0 || b < 0) return;
        clearBulkSelection();
        drag.cells = rowCells.slice(Math.min(a, b), Math.max(a, b) + 1);
        drag.cells.forEach(c => c.classList.add('bulk-selected'));
        if (cell !== drag.start) drag.moved = true;
        drag.current = cell;
    }
    function selectedSummary(cells) {
        const values = Array.from(new Set(cells.map(cellValue).filter(Boolean)));
        if (!values.length) return '—';
        return values.length === 1 ? values[0] : 'Mixed (' + values.length + ' values)';
    }
    function prettyDate(iso) {
        const d = parseIsoDate(iso); if (!d) return iso;
        return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
    }
    function openModal(modal) { if (modal) { modal.hidden = false; document.body.classList.add('inventory-modal-open'); } }
    function closeModal(modal) { if (modal) { modal.hidden = true; document.body.classList.remove('inventory-modal-open'); } }
    page.querySelectorAll('[data-close-modal]').forEach(button => button.addEventListener('click', () => {
        const modal = document.getElementById(button.dataset.closeModal);
        closeModal(modal);
        if (modal === bulkModal) clearBulkSelection();
    }));

    function setBulkWeekdaysAll() { page.querySelectorAll('.bulk-day').forEach(x => { x.checked = true; }); }
    function getBulkDays() { return Array.from(page.querySelectorAll('.bulk-day:checked')).map(x => Number(x.value)); }
    function configureRestrictionEditor(type, summary) {
        document.getElementById('bulkRateEditor').hidden = true;
        document.getElementById('bulkRestrictionEditor').hidden = false;
        const boolWrap = document.getElementById('bulkRestrictionBooleanWrap');
        const numberWrap = document.getElementById('bulkRestrictionNumberWrap');
        const cutoffEditor = document.getElementById('bulkCutoffEditor');
        const boolTypes = ['closed_to_arrival', 'closed_to_departure', 'stop_sell'];
        const isBool = boolTypes.includes(type), isCutoff = type === 'booking_cutoff';
        boolWrap.hidden = !isBool; numberWrap.hidden = isBool || isCutoff; cutoffEditor.hidden = !isCutoff;
        setText('bulkRestrictionValueLabel', ({ min_stay_arrival: 'Min Stay Arrival', min_stay_through: 'Min Stay Through', max_stay: 'Max Stay' })[type] || 'Value');
        if (isBool) document.getElementById('bulkRestrictionBoolean').value = String(summary).toLowerCase() === 'closed' || summary === '1' ? 'true' : 'false';
        if (!isBool && !isCutoff) {
            const input = document.getElementById('bulkRestrictionNumber');
            input.min = type === 'max_stay' ? '0' : '1'; input.max = '999';
            input.value = /^\d+$/.test(String(summary)) ? summary : '';
        }
        if (isCutoff) setText('bulkRestrictionHint', 'Use Rate Plan Default, disable the cutoff for selected dates, or enter custom days.');
        else if (isBool) setText('bulkRestrictionHint', 'Closed applies the manual restriction. Open removes it for the selected dates.');
        else setText('bulkRestrictionHint', type === 'max_stay' ? '0 removes the maximum stay restriction.' : 'Enter a whole number of nights.');
    }

    async function loadRestrictionDetails() {
        const categoryId = document.getElementById('bulkCategory')?.value || '';
        const planId = document.getElementById('bulkPlan')?.value || '';
        const startDate = document.getElementById('bulkStart')?.value || '';
        const endDate = document.getElementById('bulkEnd')?.value || '';
        const type = document.getElementById('bulkUpdateType')?.value || '';
        const loading = document.getElementById('bulkDetailsLoading');
        if (loading) loading.textContent = 'Loading selected dates...';
        try {
            const data = await getRestrictionRangeData(categoryId, planId, startDate, endDate);
            const rows = data.rows || [];
            if (loading) loading.textContent = rows.length + ' date(s) loaded.';
            const body = document.getElementById('bulkRestrictionDetailsBody');
            if (body) body.innerHTML = rows.length ? rows.map(r => '<tr><td>' + escapeHtml(r.date) + '</td><td>' + escapeHtml(r.minStayArrival) + '</td><td>' + escapeHtml(r.minStayThrough) + '</td><td>' + escapeHtml(r.maxStay) + '</td><td>' + escapeHtml(r.closedToArrival) + '</td><td>' + escapeHtml(r.closedToDeparture) + '</td><td>' + escapeHtml(r.stopSell) + '</td></tr>').join('') : '<tr><td colspan="7">No rows.</td></tr>';
            const summary = data.summary?.[type];
            if (summary) setText('bulkCurrentValue', summary);
            if (type === 'booking_cutoff' && summary) {
                const mode = document.getElementById('bulkCutoffMode');
                const days = document.getElementById('bulkCutoffDays');
                if (/^default/i.test(summary)) mode.value = 'default';
                else if (/disabled/i.test(summary)) mode.value = 'disabled';
                else { mode.value = 'custom'; const match = String(summary).match(/\d+/); days.value = match ? match[0] : ''; }
                updateCutoffEditor();
            }
        } catch (error) {
            if (loading) loading.textContent = error.message || 'Restriction details could not be loaded.';
        }
    }

    function updateCutoffEditor() {
        const mode = document.getElementById('bulkCutoffMode')?.value || 'default';
        const wrap = document.getElementById('bulkCutoffDaysWrap');
        if (wrap) wrap.hidden = mode !== 'custom';
    }
    document.getElementById('bulkCutoffMode')?.addEventListener('change', updateCutoffEditor);

    async function previewBulkRate() {
        const baseRate = Number(document.getElementById('bulkBaseRate')?.value || 0);
        const body = document.getElementById('bulkPreviewTbody');
        if (!body) return;
        if (!(baseRate > 0)) { body.innerHTML = '<tr><td colspan="4">Enter Base Rate to preview.</td></tr>'; return; }
        body.innerHTML = '<tr><td colspan="4">Loading preview...</td></tr>';
        try {
            const data = await postJson(bulkPreviewUrl, {
                categoryId: document.getElementById('bulkCategory')?.value || '',
                planId: document.getElementById('bulkPlan')?.value || '',
                startDate: document.getElementById('bulkStart')?.value || '',
                endDate: document.getElementById('bulkEnd')?.value || '',
                baseRate: baseRate,
                days: getBulkDays()
            });
            body.innerHTML = (data.rows || []).map(row => {
                const adj = Number(row.adjustment || 0);
                const adjText = String(row.changeType).toLowerCase() === 'percentage' ? (adj >= 0 ? '+' : '') + adj.toFixed(0) + '%' : (adj >= 0 ? '+' : '') + adj.toFixed(2);
                return '<tr><td><span class="bulk-plan-badge ' + (row.isParent ? '' : 'derived') + '">' + (row.isParent ? 'Parent' : 'Derived') + '</span> ' + escapeHtml(row.planName || row.planId) + '</td><td>' + escapeHtml(adjText) + '</td><td>' + escapeHtml(row.changeType || '—') + '</td><td class="bulk-rate-result">' + escapeHtml((row.currency ? row.currency + ' ' : '') + Number(row.newRate).toFixed(2)) + '</td></tr>';
            }).join('') || '<tr><td colspan="4">No preview rows.</td></tr>';
        } catch (error) { body.innerHTML = '<tr><td colspan="4" class="bulk-error">' + escapeHtml(error.message) + '</td></tr>'; }
    }
    document.getElementById('bulkBaseRate')?.addEventListener('input', function () { window.clearTimeout(previewTimer); previewTimer = window.setTimeout(previewBulkRate, 250); });

    async function openBulkEditor(cells) {
        if (!cells?.length) return;
        const first = cells[0];
        const type = rowType(first);
        const dates = cells.map(c => c.dataset.date).filter(Boolean).sort();
        const startDate = dates[0], endDate = dates[dates.length - 1];
        document.getElementById('bulkCategory').value = first.dataset.category || '';
        document.getElementById('bulkPlan').value = first.dataset.plan || '';
        document.getElementById('bulkStart').value = startDate || '';
        document.getElementById('bulkEnd').value = endDate || '';
        document.getElementById('bulkUpdateType').value = type;
        setText('bulkCategoryPill', first.dataset.categoryname || 'Category');
        setText('bulkPlanPill', first.dataset.planname || 'Plan');
        setText('bulkDatesPretty', prettyDate(startDate) + (startDate === endDate ? '' : ' → ' + prettyDate(endDate)));
        const summary = selectedSummary(cells);
        setText('bulkCurrentValue', summary);
        setBulkWeekdaysAll();
        const title = document.getElementById('bulkEditorTitle');
        if (isRateCell(first)) {
            if (title) title.textContent = 'Bulk Rate Update';
            document.getElementById('bulkRateEditor').hidden = false;
            document.getElementById('bulkRestrictionEditor').hidden = true;
            document.getElementById('bulkBaseRate').value = '';
            document.getElementById('bulkPreviewTbody').innerHTML = '<tr><td colspan="4">Enter Base Rate to preview.</td></tr>';
            if (bulkSaveButton) bulkSaveButton.textContent = 'Save Rates';
        } else {
            if (title) title.textContent = 'Bulk Restriction Update';
            configureRestrictionEditor(type, summary);
            if (bulkSaveButton) bulkSaveButton.textContent = 'Save Restriction';
        }
        openModal(bulkModal);
        if (!isRateCell(first)) await loadRestrictionDetails();
    }

    function finishDrag() {
        if (!drag.active) return;
        drag.active = false;
        const cells = drag.cells.slice();
        const wasMoved = drag.moved;
        const first = drag.start;
        drag.start = drag.current = null; drag.cells = []; drag.moved = false;
        if (!cells.length) { clearBulkSelection(); return; }
        if (!wasMoved && isRateCell(first)) {
            clearBulkSelection();
            const input = first.querySelector('.rate-cell-input:not(:disabled)');
            if (input) { input.focus(); try { input.select(); } catch { } }
            return;
        }
        suppressCellClickUntil = Date.now() + 350;
        openBulkEditor(cells);
    }

    page.addEventListener('mousedown', function (event) {
        if (event.button !== 0 || event.target.closest?.('.restriction-toggle')) return;
        const cell = event.target.closest?.('.inventory-drag-cell');
        if (!cell || !canDragCell(cell)) return;
        drag.active = true; drag.moved = false; drag.start = cell; drag.current = cell; drag.cells = [cell];
        clearBulkSelection(); cell.classList.add('bulk-selected');
        event.preventDefault();
    }, true);
    page.addEventListener('mouseover', function (event) {
        if (!drag.active || !drag.start) return;
        const cell = event.target.closest?.('.inventory-drag-cell');
        if (cell && sameDragGroup(drag.start, cell)) selectRangeTo(cell);
    }, true);
    document.addEventListener('mouseup', finishDrag, true);
    page.addEventListener('click', function (event) {
        if (Date.now() < suppressCellClickUntil) { const cell = event.target.closest?.('.inventory-drag-cell'); if (cell) { event.preventDefault(); event.stopPropagation(); } }
    }, true);

    bulkSaveButton?.addEventListener('click', async function () {
        const type = document.getElementById('bulkUpdateType')?.value || '';
        const categoryId = document.getElementById('bulkCategory')?.value || '';
        const planId = document.getElementById('bulkPlan')?.value || '';
        const startDate = document.getElementById('bulkStart')?.value || '';
        const endDate = document.getElementById('bulkEnd')?.value || '';
        const days = getBulkDays();
        if (!days.length) { showToast('Select at least one weekday.', 'warning'); return; }
        setLoading(bulkSaveButton, true, 'Saving');
        try {
            let data;
            if (type === 'rate') {
                const baseRate = Number(document.getElementById('bulkBaseRate')?.value || 0);
                if (!(baseRate > 0)) throw new Error('Enter Base Rate.');
                data = await postJson(bulkRateSaveUrl, { categoryId, planId, startDate, endDate, baseRate, days });
            } else {
                let value;
                if (['closed_to_arrival', 'closed_to_departure', 'stop_sell'].includes(type)) {
                    value = document.getElementById('bulkRestrictionBoolean')?.value || 'false';
                } else if (type === 'booking_cutoff') {
                    const mode = document.getElementById('bulkCutoffMode')?.value || 'default';
                    if (mode === 'custom') {
                        const custom = String(document.getElementById('bulkCutoffDays')?.value || '').trim();
                        if (!/^\d+$/.test(custom) || Number(custom) < 1 || Number(custom) > 365) throw new Error('Enter Booking Cutoff days from 1 to 365.');
                        value = custom;
                    } else value = mode;
                } else {
                    value = String(document.getElementById('bulkRestrictionNumber')?.value || '').trim();
                    if (!/^\d+$/.test(value)) throw new Error('Enter a valid whole number.');
                }
                data = await postJson(bulkRestrictionSaveUrl, { categoryId, planId, restrictionKey: type, startDate, endDate, value, days });
                clearRestrictionRangeCache(categoryId, planId);
                setRestrictionGroup(categoryId, planId, true);
            }
            showToast(data.message || 'Changes saved.', 'success');
            closeModal(bulkModal); clearBulkSelection();
            await refreshGrid({ preserveScroll: true, ignoreDirty: true });
        } catch (error) {
            showToast(error.message || 'Unable to save bulk changes.', 'error');
        } finally { setLoading(bulkSaveButton, false); }
    });

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') { closeLogDrawer(); if (bulkModal && !bulkModal.hidden) { closeModal(bulkModal); clearBulkSelection(); } closeRangePopover(); }
    });

    window.addEventListener('beforeunload', function (event) {
        if (!getDirtyInputs().length) return;
        event.preventDefault(); event.returnValue = '';
    });

    refreshDirtyState();
    updateRangeText();
})();
