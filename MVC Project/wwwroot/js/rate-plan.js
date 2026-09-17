(() => {
    'use strict';

    const page = document.getElementById('ratePlanPage');
    if (!page) return;

    const $ = (id) => document.getElementById(id);
    const token = document.querySelector('#ratePlanAntiForgery input[name="__RequestVerificationToken"]')?.value || '';
    let toastTimer = 0;
    let navigationRequest = null;

    const numberValue = (id, fallback = 0) => {
        const raw = String($(id)?.value ?? '').trim();
        if (raw === '') return fallback;
        const value = Number(raw);
        return Number.isFinite(value) ? value : fallback;
    };

    const nullableInt = (id) => {
        const raw = String($(id)?.value ?? '').trim();
        if (raw === '') return null;
        const value = Number.parseInt(raw, 10);
        return Number.isFinite(value) ? value : null;
    };

    function setActionBusy(button, busy, text = 'Saving...') {
        if (!button) return;

        if (busy) {
            if (!button.dataset.originalHtml) button.dataset.originalHtml = button.innerHTML;
            button.disabled = true;
            button.setAttribute('aria-busy', 'true');
            button.innerHTML = `<i class="fa-solid fa-spinner fa-spin" aria-hidden="true"></i><span>${text}</span>`;
        } else {
            button.disabled = false;
            button.removeAttribute('aria-busy');
            if (button.dataset.originalHtml) {
                button.innerHTML = button.dataset.originalHtml;
                delete button.dataset.originalHtml;
            }
        }
    }

    function setNavigationBusy(busy) {
        const overlay = page.querySelector('.rp-right-column [data-loading-overlay]');
        if (overlay) overlay.hidden = !busy;
    }

    function showMessage(id, message, kind = 'success') {
        const element = $(id);
        if (!element) return;
        element.textContent = message || '';
        element.classList.remove('is-success', 'is-error', 'is-warning');
        if (message) element.classList.add(`is-${kind}`);
    }

    function showToast(message, kind = 'success') {
        const toast = $('ratePlanToast');
        if (!toast) return;

        window.clearTimeout(toastTimer);
        toast.textContent = message || '';
        toast.classList.remove('is-success', 'is-error', 'is-warning');
        toast.classList.add(`is-${kind}`);
        toast.hidden = false;

        toastTimer = window.setTimeout(() => {
            toast.hidden = true;
        }, 4200);
    }

    async function postJson(url, body) {
        const response = await fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            keepalive: true,
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token,
                'X-Requested-With': 'XMLHttpRequest'
            },
            body: JSON.stringify(body)
        });

        let payload = null;
        try {
            payload = await response.json();
        } catch {
            payload = null;
        }

        if (!response.ok) {
            throw new Error(payload?.message || `Request failed (${response.status}).`);
        }

        return payload || { ok: false, message: 'The server returned an empty response.' };
    }

    function selectedPlanUrl(planName) {
        const url = new URL(page.dataset.indexUrl, window.location.origin);
        if (planName) url.searchParams.set('plan', planName);
        return url;
    }

    function currentRatePlanUrl() {
        const indexUrl = new URL(page.dataset.indexUrl, window.location.origin);
        const current = new URL(window.location.href);

        // Preserve only the selected plan when we are on the Rate Plan route.
        if (normalisePath(current.pathname) === normalisePath(indexUrl.pathname)) {
            const plan = current.searchParams.get('plan');
            if (plan) indexUrl.searchParams.set('plan', plan);
        }

        return indexUrl;
    }

    function normalisePath(path) {
        const value = String(path || '/').replace(/\/+$/, '');
        return value || '/';
    }

    function isRatePlanNavigation(url) {
        const target = new URL(url, window.location.origin);
        const index = new URL(page.dataset.indexUrl, window.location.origin);
        return target.origin === window.location.origin &&
            normalisePath(target.pathname) === normalisePath(index.pathname);
    }

    function updateTitleCount(incomingPage) {
        const currentBadge = page.querySelector('.rp-title-badge');
        const incomingBadge = incomingPage.querySelector('.rp-title-badge');
        if (currentBadge && incomingBadge) currentBadge.textContent = incomingBadge.textContent;
    }

    function replaceSection(selector, incomingPage) {
        const current = page.querySelector(selector);
        const incoming = incomingPage.querySelector(selector);
        if (!current || !incoming) return false;
        current.replaceWith(incoming);
        return true;
    }

    async function fetchRatePlanState(url, options = {}) {
        const {
            historyMode = 'none',
            showLoader = true,
            refreshList = true,
            refreshEditor = true,
            refreshBase = false,
            scrollEditorIntoView = false
        } = options;

        if (navigationRequest) navigationRequest.abort();
        navigationRequest = new AbortController();

        if (showLoader) setNavigationBusy(true);

        try {
            const target = new URL(url, window.location.origin);
            const response = await fetch(target.href, {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest',
                    'Cache-Control': 'no-cache'
                },
                signal: navigationRequest.signal
            });

            if (!response.ok) {
                throw new Error(`Unable to refresh Rate Plans (${response.status}).`);
            }

            const html = await response.text();
            const documentCopy = new DOMParser().parseFromString(html, 'text/html');
            const incomingPage = documentCopy.getElementById('ratePlanPage');
            if (!incomingPage) throw new Error('The updated Rate Plan content was not returned.');

            if (refreshBase) replaceSection('.rp-base-card', incomingPage);
            if (refreshList) replaceSection('.rp-plan-card', incomingPage);
            if (refreshEditor) replaceSection('.rp-editor-card', incomingPage);
            updateTitleCount(incomingPage);

            if (historyMode === 'push') {
                window.history.pushState({ ratePlanAjax: true }, '', target.pathname + target.search + target.hash);
            } else if (historyMode === 'replace') {
                window.history.replaceState({ ratePlanAjax: true }, '', target.pathname + target.search + target.hash);
            }

            syncAll();

            if (scrollEditorIntoView) {
                page.querySelector('.rp-editor-card')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
            }
        } catch (error) {
            if (error?.name === 'AbortError') return;
            throw error;
        } finally {
            if (showLoader) setNavigationBusy(false);
        }
    }

    function refreshQuietly(url, options = {}) {
        fetchRatePlanState(url, {
            showLoader: false,
            historyMode: 'none',
            ...options
        }).catch(error => {
            // The save itself has already succeeded. A background UI refresh failure
            // should not turn a successful save into a visible error.
            console.warn('Rate Plan background refresh failed:', error);
        });
    }

    function syncBookingCutoff() {
        const enabled = $('chkBookingCutoffEnabled');
        const days = $('txtBookingCutoffDays');
        const field = $('bookingCutoffDaysField');
        if (!enabled || !days || !field) return;

        days.disabled = !enabled.checked;
        field.classList.toggle('is-disabled', !enabled.checked);
        if (!enabled.checked) days.value = '';
    }

    function syncAutoCharge(source = '') {
        const instant = $('chkinstpay');
        const schedule = $('ddlChargeLeadHours');
        if (!instant || !schedule) return;

        if (source === 'instant' && instant.checked) schedule.value = '';
        if (source === 'schedule' && schedule.value !== '') instant.checked = false;

        schedule.disabled = instant.checked;
        $('instantChargeOption')?.classList.toggle('is-selected', instant.checked);
        $('scheduledChargeOption')?.classList.toggle('is-selected', !instant.checked && schedule.value !== '');
    }

    function syncDerivedMode() {
        const derive = $('chkDerive')?.checked === true;
        const derivedPanel = $('derivedPanel');
        const manualPanel = $('manualRatesPanel');
        if (derivedPanel) derivedPanel.hidden = !derive;
        if (manualPanel) manualPanel.hidden = derive;
    }

    function syncAll() {
        syncBookingCutoff();
        syncAutoCharge();
        syncDerivedMode();
    }

    function collectRooms() {
        return [...page.querySelectorAll('[data-rate-room]')].map(row => ({
            categoryId: row.dataset.categoryId || '',
            roomType: row.dataset.roomType || '',
            perRoom: Number(row.querySelector('[data-room-adjustment]')?.value || 0),
            active: String(row.querySelector('[data-room-active]')?.value || 'true').toLowerCase() !== 'false'
        }));
    }

    function updateVisiblePlanCount(delta) {
        const badge = page.querySelector('.rp-title-badge');
        const countBadge = page.querySelector('.rp-count-badge');
        if (!badge) return;

        const match = badge.textContent.match(/\d+/);
        if (!match) return;

        const next = Math.max(0, Number.parseInt(match[0], 10) + delta);
        badge.textContent = `${next} ${next === 1 ? 'plan' : 'plans'}`;
        if (countBadge) countBadge.textContent = String(next);
    }

    async function saveBaseRate(button) {
        showMessage('baseRateMessage', '');
        const rate = numberValue('txtBaseRate', NaN);
        if (!Number.isFinite(rate) || rate < 0) {
            showMessage('baseRateMessage', 'Enter a valid base rate.', 'error');
            return;
        }

        const applyToAll = $('chkApplyBaseRateToAllPlans')?.checked === true;

        try {
            setActionBusy(button, true, 'Saving...');
            const result = await postJson(page.dataset.saveBaseUrl, {
                newBaseRate: rate,
                applyToAllRatePlans: applyToAll
            });

            const kind = result.ok ? (result.warning ? 'warning' : 'success') : 'error';
            showMessage('baseRateMessage', result.message, kind);
            showToast(result.message, kind);

            if (result.ok && applyToAll) {
                // Base-rate cascade can change values shown in the active editor.
                refreshQuietly(currentRatePlanUrl(), { refreshList: false, refreshEditor: true });
            }
        } catch (error) {
            showMessage('baseRateMessage', error.message, 'error');
            showToast(error.message, 'error');
        } finally {
            setActionBusy(button, false);
        }
    }

    async function savePlanDetails(button) {
        showMessage('detailsSaveMessage', '');

        const planName = String($('txtPlanName')?.value || '').trim();
        if (!planName) {
            showMessage('detailsSaveMessage', 'Plan Name is required.', 'error');
            return;
        }

        const cutoffEnabled = $('chkBookingCutoffEnabled')?.checked === true;
        const cutoffDays = nullableInt('txtBookingCutoffDays');
        if (cutoffEnabled && (!cutoffDays || cutoffDays < 1 || cutoffDays > 365)) {
            showMessage('detailsSaveMessage', 'Booking Cutoff must be between 1 and 365 days.', 'error');
            return;
        }

        try {
            setActionBusy(button, true, 'Saving...');
            const result = await postJson(page.dataset.saveDetailsUrl, {
                localPlanId: Number.parseInt($('localPlanId')?.value || '0', 10) || 0,
                planName,
                displayOrder: Number.parseInt($('txtOrderid')?.value || '0', 10) || 0,
                description: String($('txtDescription')?.value || ''),
                bookingCutoffEnabled: cutoffEnabled,
                bookingCutoffDays: cutoffEnabled ? cutoffDays : null,
                instantPayment: $('chkinstpay')?.checked === true,
                chargeLeadHours: $('chkinstpay')?.checked === true ? null : nullableInt('ddlChargeLeadHours')
            });

            const kind = result.ok ? (result.warning ? 'warning' : 'success') : 'error';
            showMessage('detailsSaveMessage', result.message, kind);
            showToast(result.message, kind);

            if (result.ok) {
                const url = selectedPlanUrl(result.planName || planName);

                // Update the browser URL without navigating away from the page.
                window.history.replaceState(
                    { ratePlanAjax: true },
                    '',
                    url.pathname + url.search
                );

                // Refresh list/editor silently so new IDs, readonly state, cutoff text,
                // order and auto-charge display are server-accurate without a page reload.
                refreshQuietly(url, { refreshList: true, refreshEditor: true });
            }
        } catch (error) {
            showMessage('detailsSaveMessage', error.message, 'error');
            showToast(error.message, 'error');
        } finally {
            setActionBusy(button, false);
        }
    }

    async function saveRates(button) {
        showMessage('ratesSaveMessage', '');

        const planName = String($('txtPlanName')?.value || '').trim();
        if (!planName) {
            showMessage('ratesSaveMessage', 'Save Plan Details first.', 'error');
            return;
        }

        const derive = $('chkDerive')?.checked === true;
        if (derive && !String($('ddlDerivedFrom')?.value || '').trim()) {
            showMessage('ratesSaveMessage', 'Please select a parent plan (Derived From).', 'error');
            return;
        }

        try {
            setActionBusy(button, true, 'Saving...');
            const result = await postJson(page.dataset.saveRatesUrl, {
                planName,
                derive,
                derivedFrom: String($('ddlDerivedFrom')?.value || ''),
                operator: String($('ddlOperator')?.value || 'Plus'),
                deriveAmount: numberValue('txtDeriveAmount', 0),
                deriveType: String($('ddlDeriveType')?.value || 'Value'),
                rooms: collectRooms()
            });

            const kind = result.ok ? (result.warning ? 'warning' : 'success') : 'error';
            showMessage('ratesSaveMessage', result.message, kind);
            showToast(result.message, kind);

            if (result.ok) {
                // No navigation and no full-page refresh. The current editor stays in place.
                // Refresh metadata silently so "Derived from" and server-calculated values
                // remain correct without interrupting the user.
                refreshQuietly(selectedPlanUrl(result.planName || planName), {
                    refreshList: true,
                    refreshEditor: false
                });
            }
        } catch (error) {
            showMessage('ratesSaveMessage', error.message, 'error');
            showToast(error.message, 'error');
        } finally {
            setActionBusy(button, false);
        }
    }

    async function deletePlan(button) {
        const id = Number.parseInt(button.dataset.planId || '0', 10) || 0;
        const name = button.dataset.planName || '';
        if (!id) return;

        if (!window.confirm(`Are you sure you want to permanently delete "${name}" and all its related rate data?`)) {
            return;
        }

        const row = button.closest('tr');
        const deletingCurrentPlan = row?.classList.contains('is-current-plan') === true;

        try {
            setActionBusy(button, true, '');
            const result = await postJson(page.dataset.deleteUrl, {
                localPlanId: id,
                planName: name
            });

            const kind = result.ok ? (result.warning ? 'warning' : 'success') : 'error';
            showToast(result.message, kind);

            if (result.ok) {
                // Immediate visual removal makes delete feel instant.
                row?.remove();
                updateVisiblePlanCount(-1);

                if (deletingCurrentPlan) {
                    const url = selectedPlanUrl('');
                    window.history.replaceState({ ratePlanAjax: true }, '', url.pathname + url.search);
                    await fetchRatePlanState(url, {
                        historyMode: 'none',
                        showLoader: true,
                        refreshList: true,
                        refreshEditor: true
                    });
                } else {
                    // Update parent-plan choices quietly in case the deleted plan was
                    // available in the Derived From dropdown.
                    refreshQuietly(currentRatePlanUrl(), {
                        refreshList: true,
                        refreshEditor: true
                    });
                }
            }
        } catch (error) {
            showToast(error.message, 'error');
            setActionBusy(button, false);
        }
    }

    // Event delegation means the handlers continue to work after editor/list HTML
    // is replaced by the AJAX fragment refresh.
    page.addEventListener('change', event => {
        const target = event.target;
        if (!(target instanceof HTMLElement)) return;

        if (target.id === 'chkBookingCutoffEnabled') syncBookingCutoff();
        if (target.id === 'chkinstpay') syncAutoCharge('instant');
        if (target.id === 'ddlChargeLeadHours') syncAutoCharge('schedule');
        if (target.id === 'chkDerive') syncDerivedMode();
    });

    page.addEventListener('click', async event => {
        const target = event.target;
        if (!(target instanceof Element)) return;

        const saveBase = target.closest('#saveBaseRateButton');
        if (saveBase) {
            event.preventDefault();
            await saveBaseRate(saveBase);
            return;
        }

        const saveDetails = target.closest('#savePlanDetailsButton');
        if (saveDetails) {
            event.preventDefault();
            await savePlanDetails(saveDetails);
            return;
        }

        const saveRatesButton = target.closest('#saveRatesButton');
        if (saveRatesButton) {
            event.preventDefault();
            await saveRates(saveRatesButton);
            return;
        }

        const deleteButton = target.closest('[data-delete-plan]');
        if (deleteButton) {
            event.preventDefault();
            await deletePlan(deleteButton);
            return;
        }

        const link = target.closest('a[href]');
        if (!link || link.target === '_blank' || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        if (!isRatePlanNavigation(link.href)) return;

        event.preventDefault();

        try {
            await fetchRatePlanState(link.href, {
                historyMode: 'push',
                showLoader: true,
                refreshList: true,
                refreshEditor: true,
                scrollEditorIntoView: false
            });
        } catch (error) {
            showToast(error.message, 'error');
        }
    });

    window.addEventListener('popstate', () => {
        if (!isRatePlanNavigation(window.location.href)) return;

        fetchRatePlanState(window.location.href, {
            historyMode: 'none',
            showLoader: true,
            refreshList: true,
            refreshEditor: true
        }).catch(error => showToast(error.message, 'error'));
    });

    syncAll();
})();
