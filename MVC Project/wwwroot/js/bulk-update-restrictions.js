(() => {
    "use strict";

    const page = document.getElementById("bulkRestrictionPage");
    if (!page) return;

    const previewUrl = page.dataset.previewUrl || "";
    const saveUrl = page.dataset.saveUrl || "";
    const historyUrl = page.dataset.historyUrl || "";
    const hotelTodayIso = page.dataset.hotelToday || "";
    const canUpdate = String(page.dataset.canUpdate || "false").toLowerCase() === "true";
    const token = document.querySelector("#bulkRestrictionAntiForgery input[name='__RequestVerificationToken']")?.value || "";

    const rangesHost = document.getElementById("bulkRestrictionRanges");
    const previewBody = document.getElementById("bulkRestrictionPreviewBody");
    const statusWrap = document.getElementById("bulkRestrictionStatusWrap");
    const statusText = document.getElementById("bulkRestrictionStatus");
    const previewButton = document.getElementById("bulkRestrictionPreview");
    const saveButton = document.getElementById("bulkRestrictionSave");
    const resetButton = document.getElementById("bulkRestrictionReset");
    const planCount = document.getElementById("bulkRestrictionPlanCount");
    const roomCount = document.getElementById("bulkRestrictionRoomCount");
    const rangeCount = document.getElementById("bulkRestrictionRangeCount");
    const estimatedRows = document.getElementById("bulkRestrictionEstimatedRows");
    const clearAll = document.getElementById("bulkRestrictionClearAll");

    const minArrivalEnabled = document.getElementById("bulkRestrictionMinArrivalEnabled");
    const minArrival = document.getElementById("bulkRestrictionMinArrival");
    const minThroughEnabled = document.getElementById("bulkRestrictionMinThroughEnabled");
    const minThrough = document.getElementById("bulkRestrictionMinThrough");
    const maxStayEnabled = document.getElementById("bulkRestrictionMaxStayEnabled");
    const maxStay = document.getElementById("bulkRestrictionMaxStay");
    const cutoffMode = document.getElementById("bulkRestrictionCutoffMode");
    const cutoffCustom = document.getElementById("bulkRestrictionCutoffCustom");
    const cutoff = document.getElementById("bulkRestrictionCutoff");
    const cta = document.getElementById("bulkRestrictionCta");
    const ctd = document.getElementById("bulkRestrictionCtd");
    const sellingStatus = document.getElementById("bulkRestrictionSellingStatus");

    const historyPanel = document.getElementById("bulkRestrictionHistoryPanel");
    const historyBody = document.getElementById("bulkRestrictionHistoryBody");
    const historySearch = document.getElementById("bulkRestrictionHistorySearch");

    const calendarPopover = document.getElementById("bulkRestrictionCalendarPopover");
    const calendarLeft = document.getElementById("bulkRestrictionCalendarLeft");
    const calendarRight = document.getElementById("bulkRestrictionCalendarRight");
    const calendarLeftTitle = document.getElementById("bulkRestrictionCalendarLeftTitle");
    const calendarRightTitle = document.getElementById("bulkRestrictionCalendarRightTitle");
    const calendarText = document.getElementById("bulkRestrictionCalendarText");

    let rangeSequence = 0;
    let previewTimer = 0;
    let previewAbort = null;
    let historyTimer = 0;
    let calendarRow = null;
    let calendarPicker = null;
    let calendarView = monthStart(hotelTodayDate());
    let calendarStart = null;
    let calendarEnd = null;

    function escapeHtml(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#039;");
    }

    function pad2(value) {
        return value < 10 ? `0${value}` : String(value);
    }

    function toIso(date) {
        return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
    }

    function toDisplay(date) {
        return `${pad2(date.getDate())}/${pad2(date.getMonth() + 1)}/${date.getFullYear()}`;
    }

    function parseIso(value) {
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(value || ""));
        if (!match) return null;
        const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
        if (date.getFullYear() !== Number(match[1]) || date.getMonth() !== Number(match[2]) - 1 || date.getDate() !== Number(match[3])) return null;
        return date;
    }

    function hotelTodayDate() {
        const parsed = parseIso(hotelTodayIso);
        if (parsed) return parsed;
        const now = new Date();
        return new Date(now.getFullYear(), now.getMonth(), now.getDate());
    }

    function dayTime(date) {
        return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
    }

    function isBeforeHotelToday(date) {
        return !!date && dayTime(date) < dayTime(hotelTodayDate());
    }

    function monthStart(date) {
        return new Date(date.getFullYear(), date.getMonth(), 1);
    }

    function addMonths(date, amount) {
        return new Date(date.getFullYear(), date.getMonth() + amount, 1);
    }

    function sameDay(left, right) {
        return !!left && !!right && dayTime(left) === dayTime(right);
    }

    function monthTitle(date) {
        return date.toLocaleDateString(undefined, { month: "long", year: "numeric" });
    }

    function setStatus(text, kind = "ok") {
        if (!statusText || !statusWrap) return;
        const message = String(text || "").trim();
        statusText.textContent = message;
        statusWrap.title = message;
        statusWrap.hidden = message.length === 0;
        statusWrap.classList.remove("is-busy", "is-warning", "is-error");
        if (kind === "busy") statusWrap.classList.add("is-busy");
        else if (kind === "warning") statusWrap.classList.add("is-warning");
        else if (kind === "error") statusWrap.classList.add("is-error");
    }

    function selectedValues(kind) {
        const selector = kind === "plans" ? ".bulkrestrict-plan-choice:checked" : ".bulkrestrict-room-choice:checked";
        return Array.from(page.querySelectorAll(selector), input => input.value).filter(Boolean);
    }

    function selectedDays() {
        return Array.from(page.querySelectorAll("#bulkRestrictionDays input[type='checkbox']:checked"), input => Number(input.value));
    }

    function updateMultiDisplay(kind) {
        const wrapper = page.querySelector(`[data-multi='${kind}']`);
        if (!wrapper) return;
        const checked = Array.from(wrapper.querySelectorAll("input[type='checkbox']:checked"));
        const tags = wrapper.querySelector(".bulkrate-multi-tags");
        const placeholder = wrapper.querySelector(".bulkrate-multi-placeholder");
        if (!tags || !placeholder) return;

        tags.innerHTML = "";
        placeholder.hidden = checked.length > 0;
        checked.slice(0, 5).forEach(input => {
            const tag = document.createElement("span");
            tag.className = "bulkrate-tag";
            tag.textContent = input.dataset.label || input.value;
            tags.appendChild(tag);
        });
        if (checked.length > 5) {
            const more = document.createElement("span");
            more.className = "bulkrate-tag bulkrate-tag-more";
            more.textContent = `+${checked.length - 5}`;
            tags.appendChild(more);
        }
    }

    function closeMultiMenus(exceptKind = "") {
        page.querySelectorAll("[data-multi-menu]").forEach(menu => {
            if (menu.dataset.multiMenu !== exceptKind) menu.hidden = true;
        });
        page.querySelectorAll("[data-multi-toggle]").forEach(button => {
            if (button.dataset.multiToggle !== exceptKind) button.setAttribute("aria-expanded", "false");
        });
    }

    function addRangeRow(start = "", end = "") {
        rangeSequence += 1;
        const row = document.createElement("div");
        row.className = "bulkrate-range-row";
        row.dataset.rangeId = String(rangeSequence);
        row.dataset.start = start || "";
        row.dataset.end = end || "";

        const startDate = parseIso(start);
        const endDate = parseIso(end);
        const displayText = startDate && endDate ? `${toDisplay(startDate)} - ${toDisplay(endDate)}` : "Select date range";
        row.innerHTML = `
            <div class="bulkrate-range-field">
                <label>Date Range</label>
                <div class="bulkrate-range-picker">
                    <button type="button" class="bulkrate-range-display" data-open-calendar aria-expanded="false">
                        <span class="bulkrate-range-text">${escapeHtml(displayText)}</span>
                        <span class="bulkrate-calendar-icon"><i class="fa-regular fa-calendar-days"></i></span>
                    </button>
                </div>
            </div>
            <button type="button" class="bulkrate-delete-range" data-delete-range title="Delete range" aria-label="Delete range">
                <i class="fa-solid fa-trash-can"></i>
            </button>`;
        rangesHost?.appendChild(row);
        updateSummaryCounts();
        return row;
    }

    function completeRanges() {
        return Array.from(rangesHost?.querySelectorAll(".bulkrate-range-row") || [])
            .map(row => ({ start: row.dataset.start || "", end: row.dataset.end || "" }))
            .filter(range => range.start && range.end);
    }

    function nullableInt(enabledElement, valueElement) {
        if (!enabledElement?.checked) return null;
        const value = Number(valueElement?.value ?? "");
        return Number.isInteger(value) ? value : null;
    }

    function buildRequest() {
        const isClear = !!clearAll?.checked;
        return {
            plans: selectedValues("plans"),
            rooms: selectedValues("rooms"),
            ranges: completeRanges(),
            days: selectedDays(),
            clearAll: isClear,
            minStayArrival: isClear ? null : nullableInt(minArrivalEnabled, minArrival),
            minStayThrough: isClear ? null : nullableInt(minThroughEnabled, minThrough),
            maxStay: isClear ? null : nullableInt(maxStayEnabled, maxStay),
            cutoffMode: isClear ? "None" : (cutoffMode?.value || "None"),
            cutoff: isClear || cutoffMode?.value !== "Custom" ? null : Number(cutoff?.value || 0),
            closedToArrivalStatus: isClear ? "None" : (cta?.value || "None"),
            closedToDepartureStatus: isClear ? "None" : (ctd?.value || "None"),
            sellingStatus: isClear ? "None" : (sellingStatus?.value || "None")
        };
    }

    function requestIsPreviewable(request) {
        return request.plans.length > 0 && request.rooms.length > 0 && request.ranges.length > 0 && request.days.length > 0;
    }

    function validateRequest(request, requireChange) {
        if (!request.plans.length) return "Select at least one Rate Plan.";
        if (!request.rooms.length) return "Select at least one Room Type.";
        if (!request.ranges.length) return "Add at least one complete Date Range.";
        if (!request.days.length) return "Select at least one Applicable Day.";

        for (const range of request.ranges) {
            const start = parseIso(range.start);
            const end = parseIso(range.end);
            if (!start || !end) return "Every Date Range must have a valid start and end date.";
            if (dayTime(end) < dayTime(start)) return "Date Range end date cannot be earlier than its start date.";
            if (isBeforeHotelToday(start)) return `Past dates cannot be updated. Earliest allowed date is ${toDisplay(hotelTodayDate())}.`;
        }

        if (request.clearAll) return "";
        if (request.minStayArrival !== null && (!Number.isInteger(request.minStayArrival) || request.minStayArrival < 1))
            return "Minimum Stay on Arrival must be a whole number of 1 or greater.";
        if (request.minStayThrough !== null && (!Number.isInteger(request.minStayThrough) || request.minStayThrough < 1))
            return "Minimum Stay Through must be a whole number of 1 or greater.";
        if (request.maxStay !== null && (!Number.isInteger(request.maxStay) || request.maxStay < 0))
            return "Maximum Stay must be a whole number of 0 or greater.";
        if (request.cutoffMode === "Custom" && (!Number.isInteger(request.cutoff) || request.cutoff < 1 || request.cutoff > 365))
            return "Custom Booking Cutoff must be a whole number from 1 to 365.";

        const largestMinimum = Math.max(request.minStayArrival || 0, request.minStayThrough || 0);
        if (request.maxStay !== null && request.maxStay > 0 && largestMinimum > 0 && request.maxStay < largestMinimum)
            return "Maximum Stay cannot be lower than the selected Minimum Stay.";

        if (requireChange) {
            const changed = request.minStayArrival !== null || request.minStayThrough !== null || request.maxStay !== null ||
                request.cutoffMode !== "None" || request.closedToArrivalStatus !== "None" ||
                request.closedToDepartureStatus !== "None" || request.sellingStatus !== "None";
            if (!changed) return "Select at least one restriction to update.";
        }
        return "";
    }

    function selectedIsoDates(ranges, days) {
        const set = new Set();
        if (!ranges.length || !days.length) return set;
        const daySet = new Set(days);
        ranges.forEach(range => {
            const start = parseIso(range.start);
            const end = parseIso(range.end);
            if (!start || !end) return;
            for (const date = new Date(start); dayTime(date) <= dayTime(end); date.setDate(date.getDate() + 1)) {
                if (daySet.has(date.getDay())) set.add(toIso(date));
            }
        });
        return set;
    }

    function updateSummaryCounts() {
        const request = buildRequest();
        const dateCount = selectedIsoDates(request.ranges, request.days).size;
        const estimated = dateCount * request.rooms.length * request.plans.length;
        if (planCount) planCount.textContent = request.plans.length.toLocaleString();
        if (roomCount) roomCount.textContent = request.rooms.length.toLocaleString();
        if (rangeCount) rangeCount.textContent = request.ranges.length.toLocaleString();
        if (estimatedRows) {
            estimatedRows.textContent = estimated.toLocaleString();
            estimatedRows.title = "Unique selected dates × room types × rate plans.";
        }
    }

    function showPreviewMessage(message) {
        if (!previewBody) return;
        previewBody.innerHTML = `<tr><td colspan="10" class="bulkrate-empty">${escapeHtml(message)}</td></tr>`;
    }

    function previewCell(value) {
        const text = String(value || "No Change");
        const neutral = text.toLowerCase() === "no change";
        return `<td class="${neutral ? "is-neutral" : "is-change"}">${escapeHtml(text)}</td>`;
    }

    function renderPreview(rows) {
        if (!previewBody) return;
        if (!Array.isArray(rows) || rows.length === 0) {
            showPreviewMessage("No preview rows matched the selected scope.");
            return;
        }
        previewBody.innerHTML = rows.map(row => `<tr>
            <td>${escapeHtml(row.dateRangeText)}</td>
            <td>${escapeHtml(row.roomTypeText)}</td>
            <td>${escapeHtml(row.planText)}</td>
            ${previewCell(row.minStayArrivalText)}
            ${previewCell(row.minStayThroughText)}
            ${previewCell(row.maxStayText)}
            ${previewCell(row.cutoffText)}
            ${previewCell(row.closedToArrivalText)}
            ${previewCell(row.closedToDepartureText)}
            ${previewCell(row.stopSellText)}
        </tr>`).join("");
    }

    async function postJson(url, body, signal) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "RequestVerificationToken": token
            },
            body: JSON.stringify(body),
            signal
        });
        const data = await response.json().catch(() => ({ ok: false, message: `Request failed (${response.status}).` }));
        if (!response.ok) {
            const error = new Error(data.message || `Request failed (${response.status}).`);
            error.status = response.status;
            throw error;
        }
        return data;
    }

    function schedulePreview() {
        updateSummaryCounts();
        window.clearTimeout(previewTimer);
        previewTimer = window.setTimeout(() => runPreview(false), 450);
    }

    async function runPreview(manual = false) {
        const request = buildRequest();
        updateSummaryCounts();
        if (!requestIsPreviewable(request)) {
            previewAbort?.abort();
            showPreviewMessage("Select plans, room types and a complete date range to preview restrictions.");
            if (manual) setStatus("Complete the update scope before previewing.", "warning");
            return;
        }

        const validation = validateRequest(request, false);
        if (validation) {
            previewAbort?.abort();
            showPreviewMessage(validation);
            if (manual) setStatus(validation, "error");
            return;
        }

        previewAbort?.abort();
        previewAbort = new AbortController();
        if (manual) setStatus("Building preview...", "busy");
        try {
            const data = await postJson(previewUrl, request, previewAbort.signal);
            renderPreview(data.rows || []);
            if (manual) setStatus(`Preview ready: ${(data.rows || []).length.toLocaleString()} combination(s).`);
        } catch (error) {
            if (error.name === "AbortError") return;
            showPreviewMessage(error.message || "Unable to preview restrictions.");
            setStatus(error.message || "Unable to preview restrictions.", "error");
        }
    }

    async function saveRestrictions() {
        const request = buildRequest();
        const validation = validateRequest(request, true);
        if (validation) {
            setStatus(validation, "error");
            return;
        }
        if (!canUpdate) {
            setStatus("You do not have permission to update restrictions.", "error");
            return;
        }

        const rows = selectedIsoDates(request.ranges, request.days).size * request.rooms.length * request.plans.length;
        const actionText = request.clearAll
            ? `Clear supported restrictions for ${rows.toLocaleString()} room/rate-plan/date row(s)?`
            : `Save restriction changes for ${rows.toLocaleString()} room/rate-plan/date row(s)?`;
        if (!window.confirm(actionText)) return;

        if (saveButton) {
            saveButton.disabled = true;
            saveButton.classList.add("is-loading");
            const label = saveButton.querySelector("span");
            if (label) label.textContent = "Saving...";
        }
        setStatus("Saving restrictions...", "busy");

        try {
            const data = await postJson(saveUrl, request);
            setStatus(data.message || "Restrictions saved successfully.");
            await runPreview(false);
            if (historyPanel?.open) await loadHistory();
        } catch (error) {
            setStatus(error.message || "Unable to save restrictions.", "error");
        } finally {
            if (saveButton) {
                saveButton.disabled = !canUpdate;
                saveButton.classList.remove("is-loading");
                const label = saveButton.querySelector("span");
                if (label) label.textContent = "Save Restrictions";
            }
        }
    }

    function updateRuleState() {
        const mappings = [
            [minArrivalEnabled, minArrival, "minStayArrival"],
            [minThroughEnabled, minThrough, "minStayThrough"],
            [maxStayEnabled, maxStay, "maxStay"]
        ];
        mappings.forEach(([toggle, input, key]) => {
            if (input) input.disabled = !!clearAll?.checked || !toggle?.checked;
            page.querySelector(`[data-rule-card='${key}']`)?.classList.toggle("is-enabled", !clearAll?.checked && !!toggle?.checked);
        });

        const isClear = !!clearAll?.checked;
        [cutoffMode, cta, ctd, sellingStatus].forEach(control => { if (control) control.disabled = isClear; });
        if (cutoff) cutoff.disabled = isClear || cutoffMode?.value !== "Custom";
        if (cutoffCustom) cutoffCustom.hidden = isClear || cutoffMode?.value !== "Custom";
        page.classList.toggle("is-clear-all", isClear);

        page.querySelector("[data-rule-card='cutoff']")?.classList.toggle("is-enabled", !isClear && cutoffMode?.value !== "None");
        page.querySelector("[data-rule-card='cta']")?.classList.toggle("is-enabled", !isClear && cta?.value !== "None");
        page.querySelector("[data-rule-card='ctd']")?.classList.toggle("is-enabled", !isClear && ctd?.value !== "None");
        page.querySelector("[data-rule-card='stopSell']")?.classList.toggle("is-enabled", !isClear && sellingStatus?.value !== "None");
    }

    function resetRestrictions() {
        if (clearAll) clearAll.checked = false;
        if (minArrivalEnabled) minArrivalEnabled.checked = false;
        if (minArrival) minArrival.value = "1";
        if (minThroughEnabled) minThroughEnabled.checked = false;
        if (minThrough) minThrough.value = "1";
        if (maxStayEnabled) maxStayEnabled.checked = false;
        if (maxStay) maxStay.value = "0";
        if (cutoffMode) cutoffMode.value = "None";
        if (cutoff) cutoff.value = "1";
        if (cta) cta.value = "None";
        if (ctd) ctd.value = "None";
        if (sellingStatus) sellingStatus.value = "None";
        updateRuleState();
    }

    function resetPage() {
        closeCalendar();
        closeMultiMenus();
        page.querySelectorAll(".bulkrestrict-plan-choice,.bulkrestrict-room-choice").forEach(input => { input.checked = false; });
        updateMultiDisplay("plans");
        updateMultiDisplay("rooms");
        if (rangesHost) rangesHost.innerHTML = "";
        addRangeRow();
        page.querySelectorAll("#bulkRestrictionDays input[type='checkbox']").forEach(input => { input.checked = true; });
        resetRestrictions();
        updateSummaryCounts();
        showPreviewMessage("Select plans, room types and a complete date range to preview restrictions.");
        setStatus("");
    }

    function updateCalendarRangeText() {
        if (!calendarText) return;
        if (!calendarStart) calendarText.textContent = "Select date range";
        else if (!calendarEnd) calendarText.textContent = `${toDisplay(calendarStart)} - Select end date`;
        else calendarText.textContent = `${toDisplay(calendarStart)} - ${toDisplay(calendarEnd)}`;
    }

    function selectCalendarDate(date) {
        if (isBeforeHotelToday(date)) return;
        if (!calendarStart || calendarEnd) {
            calendarStart = new Date(date);
            calendarEnd = null;
        } else if (dayTime(date) < dayTime(calendarStart)) {
            calendarEnd = new Date(calendarStart);
            calendarStart = new Date(date);
        } else {
            calendarEnd = new Date(date);
        }
        renderCalendars();
    }

    function renderCalendarMonth(host, monthDate) {
        if (!host) return;
        host.innerHTML = "";

        const first = monthStart(monthDate);
        const firstVisible = new Date(first.getFullYear(), first.getMonth(), 1 - first.getDay());
        const month = first.getMonth();
        const today = hotelTodayDate();
        const startTime = calendarStart ? dayTime(calendarStart) : null;
        const endTime = calendarEnd ? dayTime(calendarEnd) : null;

        for (let i = 0; i < 42; i += 1) {
            const current = new Date(firstVisible.getFullYear(), firstVisible.getMonth(), firstVisible.getDate() + i);
            const currentTime = dayTime(current);
            const button = document.createElement("button");
            button.type = "button";
            button.className = "bulkrate-calendar-day";
            button.textContent = String(current.getDate());
            button.dataset.date = toIso(current);

            if (current.getMonth() !== month) button.classList.add("outside-month");
            if (sameDay(current, today)) button.classList.add("today");
            if (isBeforeHotelToday(current)) {
                button.disabled = true;
                button.classList.add("is-disabled");
                button.setAttribute("aria-disabled", "true");
                button.title = "Past dates are not available for this hotel.";
            }
            if (startTime !== null && endTime !== null && currentTime >= startTime && currentTime <= endTime)
                button.classList.add("range-fill");
            if (startTime !== null && currentTime === startTime) button.classList.add("range-start");
            if (endTime !== null && currentTime === endTime) button.classList.add("range-end");

            host.appendChild(button);
        }
    }

    function renderCalendars() {
        if (calendarLeftTitle) calendarLeftTitle.textContent = monthTitle(calendarView);
        if (calendarRightTitle) calendarRightTitle.textContent = monthTitle(addMonths(calendarView, 1));
        renderCalendarMonth(calendarLeft, calendarView);
        renderCalendarMonth(calendarRight, addMonths(calendarView, 1));
        const previousButtons = page.querySelectorAll("[data-calendar-shift='-1']");
        const previousDisabled = dayTime(addMonths(calendarView, -1)) < dayTime(monthStart(hotelTodayDate()));
        previousButtons.forEach(button => { button.disabled = previousDisabled; });
        updateCalendarRangeText();
    }

    function rowCalendarButton(row) {
        return row?.querySelector("[data-open-calendar]") || null;
    }

    function openCalendar(row) {
        if (!row || !calendarPopover) return;
        calendarRow = row;
        calendarPicker = row.querySelector(".bulkrate-range-picker");
        calendarStart = parseIso(row.dataset.start || "");
        calendarEnd = parseIso(row.dataset.end || "");
        const initial = calendarStart && !isBeforeHotelToday(calendarStart) ? calendarStart : hotelTodayDate();
        calendarView = monthStart(initial);
        renderCalendars();
        calendarPicker?.appendChild(calendarPopover);
        calendarPopover.hidden = false;
        rowCalendarButton(row)?.setAttribute("aria-expanded", "true");
    }

    function closeCalendar() {
        if (!calendarPopover) return;
        rowCalendarButton(calendarRow)?.setAttribute("aria-expanded", "false");
        calendarPopover.hidden = true;
        page.appendChild(calendarPopover);
        calendarRow = null;
        calendarPicker = null;
    }

    function applyCalendar() {
        if (!calendarRow) return;
        if (!calendarStart) {
            if (calendarText) calendarText.textContent = "Choose a start date.";
            return;
        }
        if (!calendarEnd) calendarEnd = new Date(calendarStart);
        if (isBeforeHotelToday(calendarStart) || isBeforeHotelToday(calendarEnd)) {
            if (calendarText) calendarText.textContent = `Past dates are not available. Earliest date is ${toDisplay(hotelTodayDate())}.`;
            return;
        }
        calendarRow.dataset.start = toIso(calendarStart);
        calendarRow.dataset.end = toIso(calendarEnd);
        const rangeText = calendarRow.querySelector(".bulkrate-range-text");
        if (rangeText) rangeText.textContent = `${toDisplay(calendarStart)} - ${toDisplay(calendarEnd)}`;
        closeCalendar();
        schedulePreview();
    }

    async function loadHistory() {
        if (!historyUrl || !historyBody) return;
        historyBody.innerHTML = `<tr><td colspan="8" class="bulkrate-empty">Loading recent restriction updates...</td></tr>`;
        try {
            const params = new URLSearchParams({ page: "1", pageSize: "30" });
            const search = historySearch?.value?.trim();
            if (search) params.set("search", search);
            const response = await fetch(`${historyUrl}?${params.toString()}`, {
                method: "GET",
                credentials: "same-origin",
                cache: "no-store"
            });
            const data = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(data.message || "Unable to load history.");
            const rows = Array.isArray(data.rows) ? data.rows : [];
            if (!rows.length) {
                historyBody.innerHTML = `<tr><td colspan="8" class="bulkrate-empty">No recent bulk restriction updates found.</td></tr>`;
                return;
            }
            historyBody.innerHTML = rows.map(row => `<tr>
                <td>${escapeHtml(row.dateCreated)}</td>
                <td>${escapeHtml(row.updatedBy)}</td>
                <td>${escapeHtml(row.ratePlan)}</td>
                <td>${escapeHtml(row.roomType)}</td>
                <td>${escapeHtml(row.daysText)}</td>
                <td>${escapeHtml(row.dateFrom)}</td>
                <td>${escapeHtml(row.dateTo)}</td>
                <td>${escapeHtml(row.changesText)}</td>
            </tr>`).join("");
        } catch (error) {
            historyBody.innerHTML = `<tr><td colspan="8" class="bulkrate-empty">${escapeHtml(error.message || "Unable to load history.")}</td></tr>`;
        }
    }

    page.addEventListener("click", event => {
        const multiToggle = event.target.closest("[data-multi-toggle]");
        if (multiToggle) {
            const kind = multiToggle.dataset.multiToggle;
            const menu = page.querySelector(`[data-multi-menu='${kind}']`);
            if (menu) {
                const willOpen = menu.hidden;
                closeMultiMenus(willOpen ? kind : "");
                menu.hidden = !willOpen;
                multiToggle.setAttribute("aria-expanded", willOpen ? "true" : "false");
            }
            return;
        }

        const selectAll = event.target.closest("[data-select-all]");
        if (selectAll) {
            const kind = selectAll.dataset.selectAll;
            page.querySelector(`[data-multi='${kind}']`)?.querySelectorAll("input[type='checkbox']").forEach(input => { input.checked = true; });
            updateMultiDisplay(kind);
            schedulePreview();
            return;
        }

        const clearSelection = event.target.closest("[data-clear-all]");
        if (clearSelection) {
            const kind = clearSelection.dataset.clearAll;
            page.querySelector(`[data-multi='${kind}']`)?.querySelectorAll("input[type='checkbox']").forEach(input => { input.checked = false; });
            updateMultiDisplay(kind);
            schedulePreview();
            return;
        }

        if (event.target.closest("#bulkRestrictionAddRange")) {
            addRangeRow();
            schedulePreview();
            return;
        }

        if (event.target.closest("#bulkRestrictionAllDays")) {
            page.querySelectorAll("#bulkRestrictionDays input[type='checkbox']").forEach(input => { input.checked = true; });
            schedulePreview();
            return;
        }

        if (event.target.closest("#bulkRestrictionClearDays")) {
            page.querySelectorAll("#bulkRestrictionDays input[type='checkbox']").forEach(input => { input.checked = false; });
            schedulePreview();
            return;
        }

        const deleteRange = event.target.closest("[data-delete-range]");
        if (deleteRange) {
            const row = deleteRange.closest(".bulkrate-range-row");
            if (row === calendarRow) closeCalendar();
            row?.remove();
            if (!rangesHost?.querySelector(".bulkrate-range-row")) addRangeRow();
            schedulePreview();
            return;
        }

        const openCalendarButton = event.target.closest("[data-open-calendar]");
        if (openCalendarButton) {
            const row = openCalendarButton.closest(".bulkrate-range-row");
            if (row) {
                if (calendarRow === row && calendarPopover?.hidden === false) closeCalendar();
                else openCalendar(row);
            }
            return;
        }

        const calendarDay = event.target.closest(".bulkrate-calendar-day");
        if (calendarDay) {
            if (calendarDay.disabled || calendarDay.classList.contains("is-disabled")) return;
            const date = parseIso(calendarDay.dataset.date || "");
            if (date) selectCalendarDate(date);
            return;
        }

        const calendarShift = event.target.closest("[data-calendar-shift]");
        if (calendarShift) {
            if (calendarShift.disabled) return;
            const shift = Number(calendarShift.dataset.calendarShift || 0);
            const candidate = addMonths(calendarView, shift);
            if (dayTime(candidate) < dayTime(monthStart(hotelTodayDate()))) return;
            calendarView = candidate;
            renderCalendars();
            return;
        }

        if (event.target.closest("#bulkRestrictionCalendarClose")) { closeCalendar(); return; }
        if (event.target.closest("#bulkRestrictionCalendarClear")) {
            calendarStart = null;
            calendarEnd = null;
            renderCalendars();
            return;
        }
        if (event.target.closest("#bulkRestrictionCalendarApply")) { applyCalendar(); return; }
        if (event.target.closest("#bulkRestrictionPreview")) {
            window.clearTimeout(previewTimer);
            runPreview(true);
            return;
        }
        if (event.target.closest("#bulkRestrictionSave")) { saveRestrictions(); return; }
        if (event.target.closest("#bulkRestrictionReset")) { resetPage(); return; }
        if (event.target.closest("#bulkRestrictionHistoryRefresh")) { loadHistory(); return; }
        if (!event.target.closest(".bulkrate-multi")) closeMultiMenus();
    });

    page.addEventListener("change", event => {
        if (event.target.matches(".bulkrestrict-plan-choice")) {
            updateMultiDisplay("plans");
            schedulePreview();
            return;
        }
        if (event.target.matches(".bulkrestrict-room-choice")) {
            updateMultiDisplay("rooms");
            schedulePreview();
            return;
        }
        if (event.target.matches("#bulkRestrictionDays input[type='checkbox']")) {
            schedulePreview();
            return;
        }
        if (event.target.matches("[data-rule-toggle],#bulkRestrictionCutoffMode,#bulkRestrictionCta,#bulkRestrictionCtd,#bulkRestrictionSellingStatus,#bulkRestrictionClearAll")) {
            updateRuleState();
            schedulePreview();
        }
    });

    page.addEventListener("input", event => {
        if (event.target.matches("#bulkRestrictionMinArrival,#bulkRestrictionMinThrough,#bulkRestrictionMaxStay,#bulkRestrictionCutoff")) {
            schedulePreview();
            return;
        }
        if (event.target.matches("#bulkRestrictionHistorySearch")) {
            window.clearTimeout(historyTimer);
            historyTimer = window.setTimeout(loadHistory, 350);
        }
    });

    historyPanel?.addEventListener("toggle", () => {
        if (historyPanel.open) loadHistory();
    });

    document.addEventListener("click", event => {
        if (calendarPopover?.hidden !== false || !calendarPicker) return;
        const clickPath = typeof event.composedPath === "function" ? event.composedPath() : [];
        const clickedInsideCalendar = clickPath.includes(calendarPicker) || clickPath.includes(calendarPopover);
        if (!clickedInsideCalendar) closeCalendar();
    });

    document.addEventListener("keydown", event => {
        if (event.key !== "Escape") return;
        closeMultiMenus();
        if (calendarPopover?.hidden === false) closeCalendar();
    });

    addRangeRow();
    updateMultiDisplay("plans");
    updateMultiDisplay("rooms");
    resetRestrictions();
    updateSummaryCounts();
    setStatus("");
})();
