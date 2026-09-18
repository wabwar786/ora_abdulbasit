(() => {
    "use strict";

    const root = document.getElementById("yieldManagementPage");
    if (!root) return;

    const q = (s, p = root) => p.querySelector(s);
    const qa = (s, p = root) => Array.from(p.querySelectorAll(s));
    const token = q('#yieldAntiForgery input[name="__RequestVerificationToken"]')?.value || "";
    const editor = q("#yieldRuleEditor");
    const saveButton = q("#yieldSaveRule");
    const today = root.dataset.hotelToday || "";
    let pendingConfirm = null;

    const dateRangeButton = q("#yieldDateRangeButton");
    const dateRangeText = q("#yieldDateRangeText");
    const calendarPopover = q("#yieldCalendarPopover");
    const calendarLeft = q("#yieldCalendarLeft");
    const calendarRight = q("#yieldCalendarRight");
    const calendarLeftTitle = q("#yieldCalendarLeftTitle");
    const calendarRightTitle = q("#yieldCalendarRightTitle");
    const calendarText = q("#yieldCalendarText");
    let calendarView = monthStart(hotelTodayDate());
    let calendarStart = null;
    let calendarEnd = null;

    function get(selector) {
        return q(selector);
    }

    function setValue(selector, value) {
        const node = get(selector);
        if (node) node.value = value == null ? "" : String(value);
        return node;
    }

    function setText(selector, value) {
        const node = get(selector);
        if (node) node.textContent = value == null ? "" : String(value);
        return node;
    }

    function setChecked(selector, value) {
        const node = get(selector);
        if (node) node.checked = Boolean(value);
        return node;
    }

    function pad2(value) {
        return value < 10 ? `0${value}` : String(value);
    }

    function parseIso(value) {
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(value || ""));
        if (!match) return null;
        const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
        if (date.getFullYear() !== Number(match[1]) || date.getMonth() !== Number(match[2]) - 1 || date.getDate() !== Number(match[3])) return null;
        return date;
    }

    function toIso(date) {
        return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
    }

    function toDisplay(date) {
        return `${pad2(date.getDate())}/${pad2(date.getMonth() + 1)}/${date.getFullYear()}`;
    }

    function hotelTodayDate() {
        const parsed = parseIso(today);
        if (parsed) return parsed;
        const now = new Date();
        return new Date(now.getFullYear(), now.getMonth(), now.getDate());
    }

    function dayTime(date) {
        return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
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

    function syncDateRangeDisplay() {
        if (!dateRangeText) return;
        const start = parseIso(get("#yieldStayFrom")?.value || "");
        const end = parseIso(get("#yieldStayTo")?.value || "");
        if (!start || !end) {
            dateRangeText.textContent = "Select date range";
            return;
        }
        dateRangeText.textContent = `${toDisplay(start)} - ${toDisplay(end)}`;
    }

    function updateCalendarRangeText() {
        if (!calendarText) return;
        if (!calendarStart) calendarText.textContent = "Select date range";
        else if (!calendarEnd) calendarText.textContent = `${toDisplay(calendarStart)} - Select end date`;
        else calendarText.textContent = `${toDisplay(calendarStart)} - ${toDisplay(calendarEnd)}`;
    }

    function selectCalendarDate(date) {
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
        const currentToday = hotelTodayDate();
        const startTime = calendarStart ? dayTime(calendarStart) : null;
        const endTime = calendarEnd ? dayTime(calendarEnd) : null;

        for (let i = 0; i < 42; i += 1) {
            const current = new Date(firstVisible.getFullYear(), firstVisible.getMonth(), firstVisible.getDate() + i);
            const currentTime = dayTime(current);
            const button = document.createElement("button");
            button.type = "button";
            button.className = "yieldm-calendar-day";
            button.textContent = String(current.getDate());
            button.dataset.date = toIso(current);
            if (current.getMonth() !== month) button.classList.add("outside-month");
            if (sameDay(current, currentToday)) button.classList.add("today");
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
        updateCalendarRangeText();
    }

    function openCalendar() {
        if (!calendarPopover) return;
        closeMultis();
        calendarStart = parseIso(get("#yieldStayFrom")?.value || "");
        calendarEnd = parseIso(get("#yieldStayTo")?.value || "");
        calendarView = monthStart(calendarStart || hotelTodayDate());
        renderCalendars();
        calendarPopover.hidden = false;
        dateRangeButton?.setAttribute("aria-expanded", "true");
    }

    function closeCalendar() {
        if (!calendarPopover) return;
        calendarPopover.hidden = true;
        dateRangeButton?.setAttribute("aria-expanded", "false");
    }

    function applyCalendar() {
        if (!calendarStart) {
            if (calendarText) calendarText.textContent = "Choose a start date.";
            return;
        }
        if (!calendarEnd) calendarEnd = new Date(calendarStart);
        setValue("#yieldStayFrom", toIso(calendarStart));
        setValue("#yieldStayTo", toIso(calendarEnd));
        syncDateRangeDisplay();
        closeCalendar();
    }

    // Confirmation UI must always start closed. The dialog is opened only
    // by an explicit status-toggle or delete click.
    const confirmBox = q("#yieldConfirm");
    if (confirmBox) confirmBox.hidden = true;

    const urls = {
        rule: id => (root.dataset.ruleUrlTemplate || "").replace("__ID__", encodeURIComponent(id)),
        save: root.dataset.saveUrl || "",
        toggle: id => (root.dataset.toggleUrlTemplate || "").replace("__ID__", encodeURIComponent(id)),
        del: id => (root.dataset.deleteUrlTemplate || "").replace("__ID__", encodeURIComponent(id))
    };

    async function api(url, options = {}) {
        const headers = Object.assign({ "Accept": "application/json" }, options.headers || {});
        if (options.method && options.method.toUpperCase() !== "GET") {
            headers["RequestVerificationToken"] = token;
            if (options.body && !headers["Content-Type"]) headers["Content-Type"] = "application/json";
        }
        const response = await fetch(url, Object.assign({}, options, { headers }));
        let data = {};
        try { data = await response.json(); } catch { data = { ok: false, message: "Unexpected server response." }; }
        if (!response.ok || data.ok === false) throw new Error(data.message || "Unable to complete the request.");
        return data;
    }

    function toast(message, isError = false) {
        const box = q("#yieldToast");
        if (!box) return;
        box.textContent = message;
        box.classList.toggle("is-error", isError);
        box.hidden = false;
        clearTimeout(box._timer);
        box._timer = setTimeout(() => { box.hidden = true; }, 4200);
    }

    function showFormMessage(message, success = false) {
        const box = q("#yieldEditorMessage");
        if (!box) return;
        if (!message) { box.hidden = true; box.textContent = ""; return; }
        box.textContent = message;
        box.classList.toggle("is-success", success);
        box.hidden = false;
    }

    function selectedValues(selector) {
        return qa(selector).filter(x => x.checked).map(x => x.value);
    }

    function setCheckedValues(selector, values) {
        const set = new Set((values || []).map(String));
        qa(selector).forEach(x => { x.checked = set.has(String(x.value)); });
    }

    function updateMulti(kind) {
        const wrap = q(`[data-yield-multi="${kind}"]`);
        if (!wrap) return;
        const choices = qa(kind === "plans" ? ".yield-plan-choice" : ".yield-room-choice", wrap).filter(x => x.checked);
        const tags = q(".yieldm-multi-tags", wrap);
        const placeholder = q(".yieldm-multi-placeholder", wrap);
        tags.innerHTML = "";
        placeholder.hidden = choices.length > 0;
        choices.slice(0, 3).forEach(c => {
            const b = document.createElement("b");
            b.textContent = c.dataset.label || c.value;
            tags.appendChild(b);
        });
        if (choices.length > 3) {
            const b = document.createElement("b");
            b.textContent = `+${choices.length - 3}`;
            tags.appendChild(b);
        }
    }

    function closeMultis(exceptKind = "") {
        qa("[data-yield-multi-menu]").forEach(menu => {
            if (menu.dataset.yieldMultiMenu !== exceptKind) menu.hidden = true;
        });
    }

    qa("[data-yield-multi-toggle]").forEach(button => {
        button.addEventListener("click", e => {
            e.stopPropagation();
            const kind = button.dataset.yieldMultiToggle;
            const menu = q(`[data-yield-multi-menu="${kind}"]`);
            const next = menu.hidden;
            closeMultis(kind);
            menu.hidden = !next;
        });
    });
    document.addEventListener("click", () => closeMultis());
    qa("[data-yield-multi-menu]").forEach(menu => menu.addEventListener("click", e => e.stopPropagation()));
    qa(".yield-plan-choice,.yield-room-choice").forEach(c => c.addEventListener("change", () => updateMulti(c.classList.contains("yield-plan-choice") ? "plans" : "rooms")));
    qa("[data-yield-select-all]").forEach(btn => btn.addEventListener("click", () => {
        const kind = btn.dataset.yieldSelectAll;
        qa(kind === "plans" ? ".yield-plan-choice" : ".yield-room-choice").forEach(x => x.checked = true);
        updateMulti(kind);
    }));
    qa("[data-yield-clear]").forEach(btn => btn.addEventListener("click", () => {
        const kind = btn.dataset.yieldClear;
        qa(kind === "plans" ? ".yield-plan-choice" : ".yield-room-choice").forEach(x => x.checked = false);
        updateMulti(kind);
    }));

    function setEditorOpen(open) {
        if (!editor) return;

        if (open) {
            editor.removeAttribute("hidden");
            editor.style.display = "block";
            // Wait for layout so the browser always scrolls to the now-visible editor.
            window.requestAnimationFrame(() => {
                editor.scrollIntoView({ behavior: "smooth", block: "start" });
            });
        } else {
            closeCalendar();
            editor.setAttribute("hidden", "hidden");
            editor.style.display = "none";
        }
    }

    function resetEditor() {
        // Keep New Rule resilient: optional/missing fields must never prevent
        // the editor from opening. This also makes the page tolerant of
        // browser cache / partial-view differences while deploying updates.
        setValue("#yieldRuleId", 0);
        setValue("#yieldRuleName", "");
        setValue("#yieldPriority", 0);
        setValue("#yieldStayFrom", today);
        setValue("#yieldStayTo", today);
        syncDateRangeDisplay();
        setValue("#yieldRuleType", "OCC_PCT_PROPERTY");
        setValue("#yieldChangeType", "");
        setValue("#yieldChangeValue", "");
        setValue("#yieldChangeUnit", "");
        setValue("#yieldOccMin", "");
        setValue("#yieldOccMax", "");
        setValue("#yieldThMin", "");
        setValue("#yieldThMax", "");
        setValue("#yield-time-from", "");
        setValue("#yield-time-to", "");
        setChecked("#yieldIsActive", true);
        qa("#yieldDays input").forEach(x => { x.checked = true; });
        qa(".yield-plan-choice,.yield-room-choice").forEach(x => { x.checked = false; });
        setText("#yieldEditorMode", "NEW RULE");
        setText("#yieldEditorTitle", "Create Yield Rule");
        setText("#yieldSaveRule span", "Save Rule");
        showFormMessage("");
        updateMulti("plans");
        updateMulti("rooms");
        updateThresholds();
        updateActiveText();
        updateActionPreview();
    }

    q("#yieldEditorClose")?.addEventListener("click", () => setEditorOpen(false));
    q("#yieldCancelEdit")?.addEventListener("click", () => setEditorOpen(false));

    q("#yieldAllDays")?.addEventListener("click", () => {
        qa("#yieldDays input").forEach(x => { x.checked = true; });
    });
    q("#yieldClearDays")?.addEventListener("click", () => {
        qa("#yieldDays input").forEach(x => { x.checked = false; });
    });

    dateRangeButton?.addEventListener("click", event => {
        event.preventDefault();
        event.stopPropagation();
        if (calendarPopover?.hidden === false) closeCalendar();
        else openCalendar();
    });
    calendarPopover?.addEventListener("click", event => event.stopPropagation());
    q("#yieldCalendarClose")?.addEventListener("click", closeCalendar);
    q("#yieldCalendarClear")?.addEventListener("click", () => {
        calendarStart = null;
        calendarEnd = null;
        renderCalendars();
    });
    q("#yieldCalendarApply")?.addEventListener("click", applyCalendar);
    qa("[data-yield-calendar-shift]").forEach(button => button.addEventListener("click", () => {
        const shift = Number(button.dataset.yieldCalendarShift || 0);
        calendarView = addMonths(calendarView, shift);
        renderCalendars();
    }));
    [calendarLeft, calendarRight].forEach(host => host?.addEventListener("click", event => {
        const button = event.target.closest(".yieldm-calendar-day");
        if (!button) return;
        const date = parseIso(button.dataset.date || "");
        if (date) selectCalendarDate(date);
    }));
    document.addEventListener("click", event => {
        if (calendarPopover?.hidden !== false) return;
        const picker = q("#yieldDateRangePicker");
        if (picker && picker.contains(event.target)) return;
        closeCalendar();
    });

    function updateThresholds() {
        const typeNode = get("#yieldRuleType");
        const type = typeNode ? typeNode.value : "OCC_PCT_PROPERTY";
        const occ = get("#yieldOccupancyThreshold");
        const advance = get("#yieldAdvanceThreshold");
        const timed = get("#yield-time-threshold");
        if (occ) occ.hidden = !(type === "OCC_PCT_PROPERTY" || type === "OCC_PCT_ROOMTYPE" || type === "CLOSE_AT_OCCUPANCY");
        if (advance) advance.hidden = type !== "ADVANCE_BOOKING";
        if (timed) timed.hidden = type !== "TIMED_DISCOUNT";
    }
    q("#yieldRuleType")?.addEventListener("change", updateThresholds);

    function updateActiveText() {
        const active = get("#yieldIsActive");
        setText("#yieldActiveText", active && active.checked ? "Active" : "Inactive");
    }
    q("#yieldIsActive")?.addEventListener("change", updateActiveText);

    function updateActionPreview() {
        const typeNode = get("#yieldChangeType");
        const valueNode = get("#yieldChangeValue");
        const unitNode = get("#yieldChangeUnit");
        const box = get("#yieldActionPreview span");
        if (!box) return;
        const type = typeNode ? typeNode.value : "";
        const value = valueNode ? valueNode.value : "";
        const unit = unitNode ? unitNode.value : "";
        if (!type || value === "" || !unit) {
            box.textContent = "Select an action to preview the pricing instruction.";
            return;
        }
        const verb = type === "INCREASE" ? "Increase" : type === "DECREASE" ? "Decrease" : "Set rate to";
        const suffix = unit === "PERCENT" ? "%" : " in the property's rate currency";
        box.textContent = type === "SET" && unit === "PERCENT"
            ? `Set the rate to ${value}% when this rule matches.`
            : `${verb} ${value}${suffix} when this rule matches.`;
    }
    ["#yieldChangeType", "#yieldChangeValue", "#yieldChangeUnit"].forEach(s => q(s)?.addEventListener("input", updateActionPreview));

    async function editRule(id) {
        if (!editor) {
            toast("Rule editor is unavailable on this page version. Please rebuild the MVC project once.", true);
            return;
        }

        // Show the editor immediately so the click always has visible feedback.
        // Then populate it asynchronously from the selected rule.
        setEditorOpen(true);
        resetEditor();
        setText("#yieldEditorMode", "LOADING");
        setText("#yieldEditorTitle", "Loading Yield Rule...");
        showFormMessage("");

        try {
            const data = await api(urls.rule(id));
            const r = data.rule || {};
            setValue("#yieldRuleId", r.id || r.Id || id);
            setValue("#yieldRuleName", r.ruleName ?? r.RuleName ?? "");
            setValue("#yieldPriority", r.priority ?? r.Priority ?? 0);
            setValue("#yieldStayFrom", r.stayFrom ?? r.StayFrom ?? "");
            setValue("#yieldStayTo", r.stayTo ?? r.StayTo ?? "");
            syncDateRangeDisplay();
            setValue("#yieldRuleType", r.ruleType ?? r.RuleType ?? "OCC_PCT_PROPERTY");
            setValue("#yieldChangeType", r.changeType ?? r.ChangeType ?? "");
            setValue("#yieldChangeValue", r.changeValue ?? r.ChangeValue ?? "");
            setValue("#yieldChangeUnit", r.changeUnit ?? r.ChangeUnit ?? "");
            setValue("#yieldOccMin", r.occupancyMin ?? r.OccupancyMin ?? "");
            setValue("#yieldOccMax", r.occupancyMax ?? r.OccupancyMax ?? "");
            setValue("#yieldThMin", r.thresholdMin ?? r.ThresholdMin ?? "");
            setValue("#yieldThMax", r.thresholdMax ?? r.ThresholdMax ?? "");
            setValue("#yield-time-from", r.timeFrom ?? r.TimeFrom ?? "");
            setValue("#yield-time-to", r.timeTo ?? r.TimeTo ?? "");
            setChecked("#yieldIsActive", r.isActive ?? r.IsActive);
            setCheckedValues("#yieldDays input", r.days ?? r.Days ?? []);
            setCheckedValues(".yield-plan-choice", r.ratePlanIds ?? r.RatePlanIds ?? []);
            setCheckedValues(".yield-room-choice", r.roomTypeIds ?? r.RoomTypeIds ?? []);
            setText("#yieldEditorMode", "EDIT RULE");
            setText("#yieldEditorTitle", "Edit Yield Rule");
            setText("#yieldSaveRule span", "Update Rule");
            updateMulti("plans"); updateMulti("rooms"); updateThresholds(); updateActiveText(); updateActionPreview();
            setEditorOpen(true);
            window.requestAnimationFrame(() => get("#yieldRuleName")?.focus());
        } catch (e) {
            setText("#yieldEditorMode", "EDIT RULE");
            setText("#yieldEditorTitle", "Edit Yield Rule");
            showFormMessage(e.message || "Unable to load this yield rule.");
            toast(e.message || "Unable to load this yield rule.", true);
        }
    }
    // One delegated click handler keeps New/Edit reliable even if the rule grid is
    // refreshed or rows are replaced later. It also avoids losing handlers during
    // partial UI updates.
    root.addEventListener("click", event => {
        const newButton = event.target.closest("#yieldNewRule");
        if (newButton) {
            event.preventDefault();
            if (!editor) {
                console.error("Yield Rule editor markup (#yieldRuleEditor) was not found in the page.");
                toast("Rule editor is unavailable on this page version. Please rebuild the MVC project once.", true);
                return;
            }

            // Open first. Field initialisation is deliberately non-blocking so one
            // optional field can never stop the editor from appearing.
            setEditorOpen(true);
            resetEditor();
            window.requestAnimationFrame(() => get("#yieldRuleName")?.focus());
            return;
        }

        const editButton = event.target.closest("[data-edit-rule]");
        if (editButton) {
            event.preventDefault();
            const id = editButton.dataset.editRule;
            if (id) editRule(id);
        }
    });

    function nullableNumber(selector) {
        const node = get(selector);
        if (!node) return null;
        const v = String(node.value || "").trim();
        return v === "" ? null : Number(v);
    }

    function buildRequest() {
        return {
            id: Number(q("#yieldRuleId").value || 0),
            ruleName: q("#yieldRuleName").value.trim(),
            priority: Number(q("#yieldPriority").value || 0),
            stayFrom: q("#yieldStayFrom").value,
            stayTo: q("#yieldStayTo").value,
            days: selectedValues("#yieldDays input").map(Number),
            ratePlanIds: selectedValues(".yield-plan-choice"),
            roomTypeIds: selectedValues(".yield-room-choice"),
            ruleType: q("#yieldRuleType").value,
            changeType: q("#yieldChangeType").value,
            changeValue: nullableNumber("#yieldChangeValue"),
            changeUnit: q("#yieldChangeUnit").value,
            thresholdMin: nullableNumber("#yieldThMin"),
            thresholdMax: nullableNumber("#yieldThMax"),
            occupancyMin: nullableNumber("#yieldOccMin"),
            occupancyMax: nullableNumber("#yieldOccMax"),
            timeFrom: q("#yield-time-from").value,
            timeTo: q("#yield-time-to").value,
            isActive: q("#yieldIsActive").checked
        };
    }

    saveButton?.addEventListener("click", async () => {
        const request = buildRequest();
        if (!request.ruleName) return showFormMessage("Rule name is required.");
        if (request.ruleName.length > 50) return showFormMessage("Rule name cannot exceed 50 characters.");
        if (request.priority < 0 || request.priority > 999) return showFormMessage("Priority must be between 0 and 999.");
        if (!request.stayFrom || !request.stayTo) return showFormMessage("Select the staying date range.");
        if (!request.days.length) return showFormMessage("Select at least one applicable day.");
        if (!request.ratePlanIds.length) return showFormMessage("Select at least one rate plan.");
        if (!request.roomTypeIds.length) return showFormMessage("Select at least one room type.");
        if (["OCC_PCT_PROPERTY", "OCC_PCT_ROOMTYPE", "CLOSE_AT_OCCUPANCY"].includes(request.ruleType) &&
            (request.occupancyMin === null || request.occupancyMax === null))
            return showFormMessage("Enter both occupancy percentage values.");
        if (request.ruleType === "ADVANCE_BOOKING" && (request.thresholdMin === null || request.thresholdMax === null))
            return showFormMessage("Enter both advance-booking day values.");
        if (request.ruleType === "ADVANCE_BOOKING" &&
            (request.thresholdMin < 0 || request.thresholdMax > 365 || request.thresholdMax < request.thresholdMin))
            return showFormMessage("Advance-booking days must be between 0 and 365.");
        if (request.ruleType === "TIMED_DISCOUNT" && (!request.timeFrom || !request.timeTo))
            return showFormMessage("Enter both Time From and Time To values.");
        if (!request.changeType || request.changeValue === null || !request.changeUnit) return showFormMessage("Complete the rate action fields.");
        if (request.changeValue < 0 || request.changeValue > 999999.99) return showFormMessage("Change value must be between 0 and 999999.99.");
        saveButton.disabled = true;
        const original = saveButton.innerHTML;
        saveButton.innerHTML = '<i class="fa-solid fa-spinner fa-spin"></i><span>Saving...</span>';
        showFormMessage("");
        try {
            const data = await api(urls.save, { method: "POST", body: JSON.stringify(request) });
            showFormMessage(data.message || "Yield rule saved.", true);
            toast(data.message || "Yield rule saved.");
            setTimeout(() => window.location.reload(), 450);
        } catch (e) {
            showFormMessage(e.message || "Unable to save the yield rule.");
        } finally {
            saveButton.disabled = false;
            saveButton.innerHTML = original;
        }
    });

    function confirmAction(title, text, note, okText, action) {
        q("#yieldConfirmTitle").textContent = title;
        q("#yieldConfirmText").textContent = text;
        q("#yieldConfirmNote").textContent = note;
        q("#yieldConfirmOk").textContent = okText;
        if (confirmBox) confirmBox.hidden = false;
        pendingConfirm = action;
    }
    q("#yieldConfirmCancel")?.addEventListener("click", () => { if (confirmBox) confirmBox.hidden = true; pendingConfirm = null; });
    q("#yieldConfirmOk")?.addEventListener("click", async () => {
        const action = pendingConfirm;
        if (!action) return;
        const button = q("#yieldConfirmOk");
        button.disabled = true;
        try { await action(); if (confirmBox) confirmBox.hidden = true; pendingConfirm = null; }
        catch (e) { toast(e.message, true); }
        finally { button.disabled = false; }
    });

    qa("[data-toggle-rule]").forEach(btn => btn.addEventListener("click", () => {
        const id = btn.dataset.toggleRule;
        const active = btn.dataset.isActive === "true";
        confirmAction(
            active ? "Make yield rule inactive?" : "Make yield rule active?",
            active ? "The rule will stop participating in future yield processing." : "The rule will be available for future yield processing.",
            active ? "Future rates currently marked with this Yield Rule ID will be restored to category-plan defaults using a set-based update, then the existing channel upload queue will sync the changed rates." : "Activating the rule does not immediately alter existing rates; it makes the rule eligible for the existing yield process.",
            active ? "Deactivate Rule" : "Activate Rule",
            async () => {
                const data = await api(urls.toggle(id), { method: "POST" });
                toast(data.message || "Status updated.");
                setTimeout(() => window.location.reload(), 350);
            }
        );
    }));

    qa("[data-delete-rule]").forEach(btn => btn.addEventListener("click", () => {
        const id = btn.dataset.deleteRule;
        const name = btn.dataset.ruleName || "this rule";
        confirmAction(
            "Delete yield rule?",
            `Delete “${name}”? This removes the rule definition and its rate-plan/room-type assignments.`,
            "This keeps the legacy delete behaviour: deleting does not restore rates already applied by the rule. If you need those future rates restored first, make the rule inactive before deleting it.",
            "Delete Rule",
            async () => {
                const data = await api(urls.del(id), { method: "POST" });
                toast(data.message || "Yield rule deleted.");
                q(`[data-rule-row="${id}"]`)?.remove();
                filterRules();
            }
        );
    }));

    function filterRules() {
        const term = (q("#yieldRuleSearch")?.value || "").trim().toLowerCase();
        let visible = 0;
        qa("[data-rule-row]").forEach(row => {
            const show = !term || (row.dataset.search || row.textContent || "").toLowerCase().includes(term);
            row.hidden = !show;
            if (show) visible++;
        });
        const count = q("#yieldVisibleRuleCount");
        if (count) count.textContent = String(visible);
    }
    q("#yieldRuleSearch")?.addEventListener("input", filterRules);

    setEditorOpen(false);
    try {
        resetEditor();
        updateMulti("plans");
        updateMulti("rooms");
    } catch (error) {
        // Do not disable New/Edit if a non-essential field fails during page startup.
        console.error("Yield Management editor initialisation warning.", error);
    }
})();
