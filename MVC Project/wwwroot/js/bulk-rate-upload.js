(() => {
    "use strict";

    const page = document.getElementById("bulkRatePage");
    if (!page) return;

    const previewUrl = page.dataset.previewUrl || "";
    const startUrl = page.dataset.startUrl || "";
    const historyUrl = page.dataset.historyUrl || "";
    const statusUrlTemplate = page.dataset.statusUrlTemplate || "";
    const token = document.querySelector("#bulkRateAntiForgery input[name='__RequestVerificationToken']")?.value || "";
    const hotelTodayIso = page.dataset.hotelToday || "";
    const propertyBaseRate = Number(page.dataset.propertyBaseRate || 0);
    const effectivePropertyBaseRate = Number.isFinite(propertyBaseRate) && propertyBaseRate > 0
        ? propertyBaseRate
        : 0;

    const rangesHost = document.getElementById("bulkRateRanges");
    const previewBody = document.getElementById("bulkRatePreviewBody");
    const statusText = document.getElementById("bulkRateStatus");
    const statusWrap = statusText?.closest(".bulkrate-job-status");
    const previewButton = document.getElementById("bulkRatePreview");
    const saveButton = document.getElementById("bulkRateSave");
    const resetButton = document.getElementById("bulkRateReset");

    const planCount = document.getElementById("bulkRatePlanCount");
    const roomCount = document.getElementById("bulkRateRoomCount");
    const rangeCount = document.getElementById("bulkRateRangeCount");
    const estimatedRows = document.getElementById("bulkRateEstimatedRows");

    const historyPanel = document.getElementById("bulkRateHistoryPanel");
    const historyBody = document.getElementById("bulkRateHistoryBody");
    const historySearch = document.getElementById("bulkRateHistorySearch");
    const historyRefresh = document.getElementById("bulkRateHistoryRefresh");

    const calendarPopover = document.getElementById("bulkRateCalendarPopover");
    const calendarLeft = document.getElementById("bulkRateCalendarLeft");
    const calendarRight = document.getElementById("bulkRateCalendarRight");
    const calendarLeftTitle = document.getElementById("bulkRateCalendarLeftTitle");
    const calendarRightTitle = document.getElementById("bulkRateCalendarRightTitle");
    const calendarText = document.getElementById("bulkRateCalendarText");

    let rangeSequence = 0;
    let previewTimer = 0;
    let previewAbort = null;
    let historyTimer = 0;
    let activeJobId = "";
    let pollTimer = 0;

    // Keep the active background job across a normal browser refresh.
    // Only the job id is stored; form selections are intentionally not stored,
    // so a refreshed page always starts with clean Bulk Rate fields.
    const activeJobStorageKey = `orapms:bulk-rate-upload:active-job:${window.location.pathname.toLowerCase()}`;

    function saveActiveJobId(jobId) {
        try {
            if (jobId) window.sessionStorage.setItem(activeJobStorageKey, jobId);
            else window.sessionStorage.removeItem(activeJobStorageKey);
        } catch (_) {
            // sessionStorage may be unavailable in restricted browser modes.
        }
    }

    function loadActiveJobId() {
        try {
            return window.sessionStorage.getItem(activeJobStorageKey) || "";
        } catch (_) {
            return "";
        }
    }

    let calendarRow = null;
    let calendarPicker = null;
    let calendarStart = null;
    let calendarEnd = null;
    let calendarView = monthStart(new Date());

    function escapeHtml(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#039;");
    }

    function pad2(value) {
        return String(value).padStart(2, "0");
    }

    function toIso(date) {
        return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
    }

    function toDisplay(date) {
        return `${pad2(date.getDate())}/${pad2(date.getMonth() + 1)}/${date.getFullYear()}`;
    }

    function parseIso(value) {
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || "");
        if (!match) return null;
        const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
        return Number.isNaN(date.getTime()) ? null : date;
    }

    function hotelTodayDate() {
        const fromServer = parseIso(hotelTodayIso);
        if (fromServer) return fromServer;
        const now = new Date();
        return new Date(now.getFullYear(), now.getMonth(), now.getDate());
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

    function dayTime(date) {
        return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
    }

    function sameDay(left, right) {
        return !!left && !!right && dayTime(left) === dayTime(right);
    }

    function monthTitle(date) {
        return date.toLocaleDateString(undefined, { month: "long", year: "numeric" });
    }

    function selectedValues(kind) {
        const selector = kind === "plans" ? ".bulkrate-plan-choice:checked" : ".bulkrate-room-choice:checked";
        return Array.from(page.querySelectorAll(selector), input => input.value).filter(Boolean);
    }

    function selectedDays() {
        return Array.from(page.querySelectorAll("#bulkRateDays input[type='checkbox']:checked"), input => Number(input.value));
    }

    function setStatus(text, kind = "ok") {
        if (!statusText || !statusWrap) return;
        const message = String(text || "").trim();
        statusText.textContent = message;
        statusWrap.title = message;
        statusWrap.hidden = message.length === 0;
        statusWrap.classList.remove("bulk-rate-upload-is-busy", "is-warning", "bulk-rate-upload-is-error");
        if (kind === "busy") statusWrap.classList.add("bulk-rate-upload-is-busy");
        else if (kind === "warning") statusWrap.classList.add("is-warning");
        else if (kind === "error") statusWrap.classList.add("bulk-rate-upload-is-error");
    }

    function setJobUi(isBusy) {
        // Keep the rest of the form usable while the server processes the job,
        // but make the Save button clearly show that this hotel's upload is
        // already running in the background.
        if (saveButton) saveButton.disabled = isBusy;
        if (previewButton) previewButton.disabled = false;
        if (resetButton) resetButton.disabled = false;

        if (saveButton) {
            saveButton.classList.toggle("is-loading", isBusy);
            const label = saveButton.querySelector("span");
            if (label) {
                label.textContent = isBusy
                    ? "Running in Background..."
                    : "Save Rates";
            }
        }
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

    function addRangeRow(start = "", end = "", baseRate = null) {
        rangeSequence += 1;
        const initialBaseRate = baseRate == null
            ? effectivePropertyBaseRate
            : Number(baseRate || 0);
        const row = document.createElement("div");
        row.className = "bulkrate-range-row";
        row.dataset.rangeId = String(rangeSequence);
        row.dataset.start = start || "";
        row.dataset.end = end || "";

        const startDate = parseIso(start);
        const endDate = parseIso(end);
        const displayText = startDate && endDate
            ? `${toDisplay(startDate)} - ${toDisplay(endDate)}`
            : "Select date range";

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
            <div class="bulkrate-range-field">
                <label>Base Rate${effectivePropertyBaseRate > 0 ? ` <span class="bulkrate-base-min">(Min ${effectivePropertyBaseRate.toFixed(2)})</span>` : ""}</label>
                <input type="number" class="bulkrate-base-rate" value="${initialBaseRate.toFixed(2)}" min="${effectivePropertyBaseRate > 0 ? effectivePropertyBaseRate.toFixed(2) : "0"}" step="0.01" inputmode="decimal" aria-label="Base rate" />
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
            .map(row => ({
                start: row.dataset.start || "",
                end: row.dataset.end || "",
                baseRate: Number(row.querySelector(".bulkrate-base-rate")?.value || 0)
            }))
            .filter(range => range.start && range.end && Number.isFinite(range.baseRate) && range.baseRate >= 0);
    }

    function buildRequest() {
        return {
            plans: selectedValues("plans"),
            rooms: selectedValues("rooms"),
            ranges: completeRanges(),
            days: selectedDays()
        };
    }

    function requestIsPreviewable(request) {
        return request.plans.length > 0
            && request.rooms.length > 0
            && request.ranges.length > 0
            && request.days.length > 0;
    }

    function countApplicableDates(ranges, days) {
        if (!ranges.length || !days.length) return 0;
        const daySet = new Set(days);
        let count = 0;

        ranges.forEach(range => {
            const start = parseIso(range.start);
            const end = parseIso(range.end);
            if (!start || !end) return;
            for (let date = new Date(start); dayTime(date) <= dayTime(end); date.setDate(date.getDate() + 1)) {
                if (daySet.has(date.getDay())) count += 1;
            }
        });

        return count;
    }

    function updateSummaryCounts() {
        const request = buildRequest();
        const dateCount = countApplicableDates(request.ranges, request.days);
        const estimated = dateCount * request.rooms.length * request.plans.length;
        if (planCount) planCount.textContent = request.plans.length.toLocaleString();
        if (roomCount) roomCount.textContent = request.rooms.length.toLocaleString();
        if (rangeCount) rangeCount.textContent = request.ranges.length.toLocaleString();
        if (estimatedRows) {
            estimatedRows.textContent = estimated.toLocaleString();
            estimatedRows.title = "Selected dates × rooms × root plans. Derived child plans can increase the final saved row count.";
        }
    }

    function showPreviewMessage(message) {
        if (!previewBody) return;
        previewBody.innerHTML = `<tr><td colspan="4" class="bulkrate-empty">${escapeHtml(message)}</td></tr>`;
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
        if (activeJobId) return;
        window.clearTimeout(previewTimer);
        previewTimer = window.setTimeout(() => runPreview(false), 500);
    }

    async function runPreview(manual = false) {
        const request = buildRequest();
        updateSummaryCounts();

        if (!requestIsPreviewable(request)) {
            previewAbort?.abort();
            showPreviewMessage("Select plans, room types and a complete date range to preview rates.");
            if (manual) setStatus("Complete the rate setup before previewing.", "warning");
            return;
        }

        const baseRateValidation = validateBaseRates(request);
        if (baseRateValidation) {
            previewAbort?.abort();
            showPreviewMessage(baseRateValidation);
            if (manual) setStatus(baseRateValidation, "error");
            return;
        }

        previewAbort?.abort();
        previewAbort = new AbortController();
        showPreviewMessage("Calculating preview...");
        if (manual) setStatus("Calculating preview...", "busy");

        try {
            const data = await postJson(previewUrl, request, previewAbort.signal);
            const rows = Array.isArray(data.rows) ? data.rows : [];
            if (rows.length === 0) {
                showPreviewMessage("No rates matched the current selection.");
                setStatus("No preview rows matched.", "warning");
                return;
            }

            const formatter = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
            previewBody.innerHTML = rows.map(row => {
                const currency = row.currency ? `${escapeHtml(row.currency)} ` : "";
                return `<tr>
                    <td>${escapeHtml(row.dateRangeText)}</td>
                    <td>${escapeHtml(row.roomTypeText)}</td>
                    <td>${escapeHtml(row.planText)}</td>
                    <td class="bulkrate-number">${currency}${formatter.format(Number(row.adjustedRate || 0))}</td>
                </tr>`;
            }).join("");
            setStatus(`Preview ready · ${rows.length.toLocaleString()} rows`, "ok");
        } catch (error) {
            if (error.name === "AbortError") return;
            showPreviewMessage(error.message || "Preview failed.");
            setStatus(error.message || "Preview failed.", "error");
        }
    }

    function validateBaseRates(request) {
        if (effectivePropertyBaseRate <= 0) return "";
        const invalid = request.ranges.find(range => Number(range.baseRate) < effectivePropertyBaseRate);
        if (!invalid) return "";
        return `Base rate cannot be less than the property base rate ${effectivePropertyBaseRate.toFixed(2)}.`;
    }

    function validateSaveRequest(request) {
        if (request.plans.length === 0) return "Select at least one rate plan.";
        if (request.rooms.length === 0) return "Select at least one room type.";
        if (request.ranges.length === 0) return "Add at least one complete date range.";
        if (request.days.length === 0) return "Select at least one applicable day.";

        const baseRateValidation = validateBaseRates(request);
        if (baseRateValidation) return baseRateValidation;

        const minimum = hotelTodayDate();
        for (const range of request.ranges) {
            const start = parseIso(range.start);
            const end = parseIso(range.end);
            if (!start || !end) return "Select a valid date range.";
            if (dayTime(end) < dayTime(start)) return "The end date cannot be before the start date.";
            if (dayTime(start) < dayTime(minimum) || dayTime(end) < dayTime(minimum))
                return `Past dates cannot be selected. Earliest date is ${toDisplay(minimum)}.`;
        }
        return "";
    }

    async function startSave() {
        if (activeJobId) {
            setStatus("A bulk rate upload is already running in the background for this hotel.", "warning");
            return;
        }

        const request = buildRequest();
        const validation = validateSaveRequest(request);
        if (validation) {
            setStatus(validation, "error");
            return;
        }

        previewAbort?.abort();
        setJobUi(true);
        setStatus("Queueing background save...", "busy");

        try {
            const data = await postJson(startUrl, request);
            if (!data.ok || !data.jobId) throw new Error(data.message || "Unable to queue rate upload.");
            activeJobId = data.jobId;
            saveActiveJobId(activeJobId);

            // The server owns its own request snapshot now. Clear the submitted
            // selections immediately so the page is ready for a fresh setup.
            // This does NOT cancel or alter the queued/running background job.
            resetPage(false);
            setJobUi(true);

            setStatus(
                "Upload accepted. Rates are processing in the background. The fields have been refreshed and you may leave or refresh this page.",
                "ok"
            );

            // Polling is only for progress/final feedback. A page refresh will
            // restore the job id from sessionStorage and continue polling.
            pollTimer = window.setTimeout(pollStatus, 3000);
        } catch (error) {
            setJobUi(false);
            setStatus(error.message || "Unable to start upload.", "error");
        }
    }

    async function pollStatus() {
        window.clearTimeout(pollTimer);
        if (!activeJobId) return;

        try {
            const statusUrl = statusUrlTemplate.replace("__JOB_ID__", encodeURIComponent(activeJobId));
            const response = await fetch(statusUrl, {
                method: "GET",
                credentials: "same-origin",
                cache: "no-store"
            });
            const data = await response.json().catch(() => ({}));
            if (!response.ok) {
                const statusError = new Error(data.message || "Unable to read upload status.");
                statusError.status = response.status;
                throw statusError;
            }

            const state = String(data.status || "").toLowerCase();
            const processed = Number(data.processedRows || 0);
            const suffix = processed > 0 ? ` · ${processed.toLocaleString()} rows` : "";

            if (state === "queued") {
                setStatus(`Queued${suffix}`, "busy");
            } else if (state === "processing") {
                setStatus(`Processing rates${suffix}`, "busy");
            } else if (state === "completed") {
                setStatus(data.message || `Completed${suffix}`, "ok");
                finishJob();
                return;
            } else if (state === "completed_with_warning") {
                setStatus(data.message || `Completed with warning${suffix}`, "warning");
                finishJob();
                return;
            } else if (state === "failed" || state === "cancelled") {
                setStatus(data.message || "Upload failed.", "error");
                finishJob();
                return;
            } else {
                setStatus(data.message || state || "Processing rates...", "busy");
            }
        } catch (error) {
            if (error && (error.status === 404 || error.status === 401 || error.status === 403)) {
                finishJob();
                setStatus(error.message || "The previous background job is no longer available.", "warning");
                return;
            }
            setStatus(error.message || "Status check failed.", "warning");
        }

        // Status is held in memory by the background worker, so a slower poll keeps
        // the UI responsive and avoids needless requests during very long uploads.
        pollTimer = window.setTimeout(pollStatus, 5000);
    }

    function finishJob() {
        activeJobId = "";
        saveActiveJobId("");
        setJobUi(false);
        window.clearTimeout(pollTimer);
        if (historyPanel?.open) loadHistory();
        schedulePreview();
    }

    function resetPage(clearStatus = true) {
        // Resetting the form does not affect an already queued job because the
        // worker received its own request snapshot when Start was called.
        closeCalendar();
        closeMultiMenus();
        page.querySelectorAll(".bulkrate-plan-choice,.bulkrate-room-choice").forEach(input => { input.checked = false; });
        page.querySelectorAll("#bulkRateDays input[type='checkbox']").forEach(input => { input.checked = true; });
        if (rangesHost) rangesHost.innerHTML = "";
        addRangeRow();
        updateMultiDisplay("plans");
        updateMultiDisplay("rooms");
        updateSummaryCounts();
        showPreviewMessage("Select plans, room types and a complete date range to preview rates.");
        if (clearStatus) setStatus("");
    }

    function updateCalendarRangeText() {
        if (!calendarText) return;
        if (!calendarStart) {
            calendarText.textContent = "Select date range";
            return;
        }
        calendarText.textContent = `${toDisplay(calendarStart)} - ${calendarEnd ? toDisplay(calendarEnd) : "Select end date"}`;
    }

    function selectCalendarDate(date) {
        const clicked = new Date(date.getFullYear(), date.getMonth(), date.getDate());
        if (isBeforeHotelToday(clicked)) return;
        if (!calendarStart || calendarEnd) {
            calendarStart = clicked;
            calendarEnd = null;
        } else if (dayTime(clicked) < dayTime(calendarStart)) {
            calendarEnd = calendarStart;
            calendarStart = clicked;
        } else {
            calendarEnd = clicked;
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

            if (current.getMonth() !== month) button.classList.add("bulk-rate-upload-outside-month");
            if (sameDay(current, today)) button.classList.add("bulk-rate-upload-today");
            if (isBeforeHotelToday(current)) {
                button.disabled = true;
                button.classList.add("is-disabled");
                button.setAttribute("aria-disabled", "true");
                button.title = "Past dates are not available for this hotel.";
            }
            if (startTime !== null && endTime !== null && currentTime >= startTime && currentTime <= endTime)
                button.classList.add("bulk-rate-upload-range-fill");
            if (startTime !== null && currentTime === startTime) button.classList.add("bulk-rate-upload-range-start");
            if (endTime !== null && currentTime === endTime) button.classList.add("bulk-rate-upload-range-end");

            host.appendChild(button);
        }
    }

    function renderCalendars() {
        const right = addMonths(calendarView, 1);
        if (calendarLeftTitle) calendarLeftTitle.textContent = monthTitle(calendarView);
        if (calendarRightTitle) calendarRightTitle.textContent = monthTitle(right);
        renderCalendarMonth(calendarLeft, calendarView);
        renderCalendarMonth(calendarRight, right);

        const currentMonth = monthStart(hotelTodayDate());
        const atMinimumMonth = dayTime(monthStart(calendarView)) <= dayTime(currentMonth);
        page.querySelectorAll("[data-calendar-shift='-1']").forEach(button => {
            button.disabled = atMinimumMonth;
            button.setAttribute("aria-disabled", atMinimumMonth ? "true" : "false");
        });
        updateCalendarRangeText();
    }

    function openCalendar(row) {
        if (!row || !calendarPopover) return;
        closeCalendar();
        calendarRow = row;
        calendarPicker = row.querySelector(".bulkrate-range-picker");
        if (!calendarPicker) return;

        calendarStart = parseIso(row.dataset.start || "");
        calendarEnd = parseIso(row.dataset.end || "");
        calendarView = monthStart(calendarStart || hotelTodayDate());
        calendarPicker.appendChild(calendarPopover);
        calendarPopover.hidden = false;
        row.querySelector(".bulkrate-range-display")?.setAttribute("aria-expanded", "true");
        renderCalendars();
    }

    function closeCalendar() {
        if (!calendarPopover) return;
        if (calendarRow) rowCalendarButton(calendarRow)?.setAttribute("aria-expanded", "false");
        calendarPopover.hidden = true;
        page.appendChild(calendarPopover);
        calendarRow = null;
        calendarPicker = null;
    }

    function rowCalendarButton(row) {
        return row?.querySelector(".bulkrate-range-display") || null;
    }

    function applyCalendar() {
        if (!calendarRow || !calendarStart) {
            if (calendarText) calendarText.textContent = "Please select a start date.";
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
        historyBody.innerHTML = `<tr><td colspan="8" class="bulkrate-empty">Loading recent rate uploads...</td></tr>`;

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
            if (rows.length === 0) {
                historyBody.innerHTML = `<tr><td colspan="8" class="bulkrate-empty">No recent bulk rate uploads found.</td></tr>`;
                return;
            }

            historyBody.innerHTML = rows.map(row => `<tr>
                <td>${escapeHtml(row.dateCreated)}</td>
                <td>${escapeHtml(row.updatedBy)}</td>
                <td>${escapeHtml(row.ratePlan)}</td>
                <td>${escapeHtml(row.roomType)}</td>
                <td>${escapeHtml(row.days)}</td>
                <td>${escapeHtml(row.dateFrom)}</td>
                <td>${escapeHtml(row.dateTo)}</td>
                <td class="bulkrate-number">${escapeHtml(row.baseRateSet)}</td>
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
            page.querySelector(`[data-multi='${kind}']`)?.querySelectorAll("input[type='checkbox']")
                .forEach(input => { input.checked = true; });
            updateMultiDisplay(kind);
            schedulePreview();
            return;
        }

        const clearAll = event.target.closest("[data-clear-all]");
        if (clearAll) {
            const kind = clearAll.dataset.clearAll;
            page.querySelector(`[data-multi='${kind}']`)?.querySelectorAll("input[type='checkbox']")
                .forEach(input => { input.checked = false; });
            updateMultiDisplay(kind);
            schedulePreview();
            return;
        }

        if (event.target.closest("#bulkRateAddRange")) {
            addRangeRow();
            schedulePreview();
            return;
        }

        if (event.target.closest("#bulkRateAllDays")) {
            page.querySelectorAll("#bulkRateDays input[type='checkbox']").forEach(input => { input.checked = true; });
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

        if (event.target.closest("#bulkRateCalendarClose")) {
            closeCalendar();
            return;
        }

        if (event.target.closest("#bulkRateCalendarClear")) {
            calendarStart = null;
            calendarEnd = null;
            renderCalendars();
            return;
        }

        if (event.target.closest("#bulkRateCalendarApply")) {
            applyCalendar();
            return;
        }

        if (event.target.closest("#bulkRatePreview")) {
            window.clearTimeout(previewTimer);
            runPreview(true);
            return;
        }

        if (event.target.closest("#bulkRateSave")) {
            startSave();
            return;
        }

        if (event.target.closest("#bulkRateReset")) {
            resetPage();
            return;
        }

        if (event.target.closest("#bulkRateHistoryRefresh")) {
            loadHistory();
            return;
        }

        if (!event.target.closest(".bulkrate-multi")) closeMultiMenus();
    });

    page.addEventListener("change", event => {
        if (event.target.matches(".bulkrate-base-rate")) {
            const entered = Number(event.target.value || 0);
            if (effectivePropertyBaseRate > 0 && Number.isFinite(entered) && entered < effectivePropertyBaseRate) {
                event.target.value = effectivePropertyBaseRate.toFixed(2);
                setStatus(
                    `Base rate cannot be less than the property base rate ${effectivePropertyBaseRate.toFixed(2)}.`,
                    "warning"
                );
            }
            schedulePreview();
            return;
        }
        if (event.target.matches(".bulkrate-plan-choice")) {
            updateMultiDisplay("plans");
            schedulePreview();
            return;
        }
        if (event.target.matches(".bulkrate-room-choice")) {
            updateMultiDisplay("rooms");
            schedulePreview();
            return;
        }
        if (event.target.matches("#bulkRateDays input[type='checkbox']")) {
            schedulePreview();
        }
    });

    page.addEventListener("input", event => {
        if (event.target.matches(".bulkrate-base-rate")) schedulePreview();
        if (event.target.matches("#bulkRateHistorySearch")) {
            window.clearTimeout(historyTimer);
            historyTimer = window.setTimeout(loadHistory, 350);
        }
    });

    historyPanel?.addEventListener("toggle", () => {
        if (historyPanel.open) loadHistory();
    });

    document.addEventListener("click", event => {
        if (calendarPopover?.hidden !== false || !calendarPicker) return;

        // The calendar re-renders its day buttons as soon as a date is selected.
        // That removes the clicked button from the live DOM before this document-level
        // handler runs, so calendarPicker.contains(event.target) can become false even
        // though the original click happened inside the calendar. The event path is
        // captured when the click starts and remains valid after the re-render.
        const clickPath = typeof event.composedPath === "function"
            ? event.composedPath()
            : [];

        const clickedInsideCalendar = clickPath.includes(calendarPicker)
            || clickPath.includes(calendarPopover);

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
    updateSummaryCounts();

    // On a normal page refresh, keep the form clean but reconnect to an active
    // background job so duplicate saves remain blocked until that job finishes.
    activeJobId = loadActiveJobId();
    if (activeJobId) {
        setJobUi(true);
        setStatus("A previously started rate upload is still processing in the background.", "busy");
        pollTimer = window.setTimeout(pollStatus, 500);
    } else {
        setJobUi(false);
        setStatus("");
    }
})();