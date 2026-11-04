/* Scoped fiscal report interactions: only the results region is replaced. */
(() => {
  'use strict';
  const app = document.getElementById('fbrReportApp');
  if (!app) return;
  const resultRegion = document.getElementById('fbrReportResults');
  const form = document.getElementById('fbrFilters');
  const exportLink = document.getElementById('fbrExport');
  const flash = document.getElementById('fbrFlash');
  const modal = document.getElementById('fbrRetryModal');
  const retry = document.getElementById('fbrSendRetry');
  const authority = document.getElementById('fbrAuthority');
  const retryError = document.getElementById('fbrRetryError');
  const reconcileRow = document.getElementById('fbrReconcileRow');
  const reconcileCheck = document.getElementById('fbrReconcileConfirmed');
  const priorInfo = document.getElementById('fbrPriorAttempt');
  let reportRequest = null;
  let filterDebounce = null;
  let currentRegId = '';
  let requiresReconciliation = false;
  let isSending = false;
  let lastFocusedElement = null;
  let previousBodyOverflow = '';

  const showFlash = (message, isError = false) => {
    flash.textContent = message;
    flash.hidden = !message;
    flash.classList.toggle('is-error', isError);
  };
  const showRetryError = message => {
    retryError.textContent = message;
    retryError.hidden = !message;
  };
  const isExpired = response =>
    response.status === 401 || response.redirected || /\/LoginHMS(?:\/|\?|$)/i.test(response.url || '');
  async function readResult(response, label, expectJson) {
    if (isExpired(response)) throw new Error('Session expired. Sign in again.');
    const type = (response.headers.get('content-type') || '').toLowerCase();
    if (!response.ok) {
      if (type.includes('json')) {
        try {
          const data = await response.json();
          throw new Error(data.message || label + ' failed (HTTP ' + response.status + ').');
        } catch (error) {
          if (error instanceof SyntaxError) throw new Error(label + ' returned invalid JSON.');
          throw error;
        }
      }
      throw new Error(label + ' failed (HTTP ' + response.status + ').');
    }
    if (expectJson) {
      if (!type.includes('json')) throw new Error(label + ' returned HTML instead of JSON.');
      const data = await response.json();
      if (!data || data.ok !== true) throw new Error(data?.message || label + ' failed.');
      return data;
    }
    if (!type.includes('html')) throw new Error(label + ' returned an unexpected response.');
    return response.text();
  }
  function setBusy(busy) {
    resultRegion.setAttribute('aria-busy', String(busy));
    resultRegion.classList.toggle('is-loading', busy);
  }
  function indexUrlWithForm(page = 1) {
    const url = new URL(app.dataset.indexUrl, window.location.origin);
    const query = new URLSearchParams(new FormData(form));
    query.set('page', String(page));
    url.search = query.toString();
    return url;
  }
  function syncInputsAndLinks(partial) {
    const { search, status, from, to, page } = partial.dataset;
    form.elements.search.value = search || '';
    form.elements.status.value = status || 'ALL';
    form.elements.from.value = from || '';
    form.elements.to.value = to || '';
    const displayUrl = indexUrlWithForm(Number(page || 1));
    const exportUrl = new URL(app.dataset.exportUrl, window.location.origin);
    exportUrl.search = displayUrl.search;
    exportLink.href = exportUrl.toString();
    return displayUrl;
  }
  async function loadReport(target, historyMode = 'push') {
    if (reportRequest) reportRequest.abort();
    const controller = new AbortController();
    reportRequest = controller;
    setBusy(true);
    const desired = new URL(target, window.location.origin);
    const endpoint = new URL(app.dataset.resultsUrl, window.location.origin);
    endpoint.search = desired.search;
    try {
      const response = await fetch(endpoint.toString(), {
        credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
        headers: { Accept: 'text/html', 'X-Requested-With': 'XMLHttpRequest' }
      });
      const markup = await readResult(response, 'Fiscal invoice report', false);
      if (controller.signal.aborted) return;
      const template = document.createElement('template');
      template.innerHTML = markup.trim();
      const dynamic = template.content.querySelector('.fbr-dynamic');
      if (!dynamic) throw new Error('Report returned an invalid layout. Rebuild and publish the new partial view.');
      resultRegion.replaceChildren(dynamic);
      const displayUrl = syncInputsAndLinks(dynamic);
      if (historyMode === 'push') window.history.pushState(null, '', displayUrl);
      if (historyMode === 'replace') window.history.replaceState(null, '', displayUrl);
      showFlash('');
    } catch (error) {
      if (error.name !== 'AbortError') showFlash(error.message || 'Unable to refresh the report.', true);
    } finally {
      if (reportRequest === controller) {
        reportRequest = null;
        setBusy(false);
      }
    }
  }
  form.addEventListener('submit', event => {
    event.preventDefault();
    window.clearTimeout(filterDebounce);
    loadReport(indexUrlWithForm(), 'push');
  });
  const scheduleSearch = () => {
    window.clearTimeout(filterDebounce);
    filterDebounce = window.setTimeout(() => loadReport(indexUrlWithForm(), 'replace'), 500);
  };
  document.getElementById('fbrSearch').addEventListener('input', scheduleSearch);
  for (const id of ['fbrStatus', 'fbrFrom', 'fbrTo']) {
    document.getElementById(id).addEventListener('change', () => {
      window.clearTimeout(filterDebounce);
      loadReport(indexUrlWithForm(), 'push');
    });
  }
  document.getElementById('fbrRefresh').addEventListener('click', () =>
    loadReport(indexUrlWithForm(Number(resultRegion.querySelector('.fbr-dynamic')?.dataset.page || 1)), 'replace'));
  document.getElementById('fbrReset').addEventListener('click', event => {
    event.preventDefault();
    window.clearTimeout(filterDebounce);
    loadReport(app.dataset.indexUrl, 'push');
  });
  window.addEventListener('popstate', () => loadReport(window.location.href, null));

  const updateRetryButton = () => {
    retry.disabled = isSending || retry.dataset.blocked === '1' || !authority.value ||
      (requiresReconciliation && !reconcileCheck.checked);
  };
  const closeModal = () => {
    if (isSending) return;
    modal.classList.remove('open');
    document.body.style.overflow = previousBodyOverflow;
    if (lastFocusedElement?.isConnected) lastFocusedElement.focus();
  };
  document.querySelectorAll('[data-close-retry]').forEach(button => button.addEventListener('click', closeModal));
  modal.addEventListener('click', event => { if (event.target === modal) closeModal(); });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') closeModal(); });

  async function openRetry(button) {
    lastFocusedElement = button;
    const regId = button.dataset.fbrRetry;
    currentRegId = '';
    requiresReconciliation = false;
    retry.dataset.blocked = '1';
    retry.disabled = true;
    reconcileCheck.checked = true;
    reconcileRow.hidden = true;
    priorInfo.hidden = true;
    priorInfo.textContent = '';
    showRetryError('');
    document.getElementById('fbrGuest').textContent = 'Loading…';
    document.getElementById('fbrReg').textContent = regId;
    document.getElementById('fbrMethod').textContent = 'Checking…';
    authority.replaceChildren(new Option('Loading…', ''));
    previousBodyOverflow = document.body.style.overflow;
    modal.classList.add('open');
    document.body.style.overflow = 'hidden';
    try {
      const url = new URL(app.dataset.detailsUrl, window.location.origin);
      url.searchParams.set('regId', regId);
      const response = await fetch(url.toString(), {
        credentials: 'same-origin', cache: 'no-store',
        headers: { Accept: 'application/json', 'X-Requested-With': 'XMLHttpRequest' }
      });
      const json = await readResult(response, 'Retry details', true);
      if (!modal.classList.contains('open') || document.getElementById('fbrReg').textContent !== regId) return;
      const data = json.data;
      const method = data.paymentMethod || '';
      document.getElementById('fbrGuest').textContent = data.guestName || 'Guest';
      document.getElementById('fbrMethod').textContent = method +
        (data.paymentMethodSource ? ' (' + data.paymentMethodSource + ')' : '');
      authority.replaceChildren();
      const routes = data.availableAuthorities || [];
      if (routes.length !== 1) authority.add(new Option(routes.length ? 'Select fiscal authority' : 'No fiscal authority', ''));
      for (const route of routes) authority.add(new Option(route, route));
      if (routes.length === 1) authority.value = routes[0];
      requiresReconciliation = Boolean(data.needsPreviousAttemptReconciliation);
      reconcileRow.hidden = !requiresReconciliation;
      if (requiresReconciliation) {
        priorInfo.hidden = false;
        priorInfo.textContent = data.previousAttemptSummary || 'Check the fiscal portal before reconciling.';
      }
      if (data.retryBlockedReason) showRetryError(data.retryBlockedReason);
      retry.dataset.blocked = data.retryBlockedReason || !method ? '1' : '0';
      currentRegId = regId;
      updateRetryButton();
    } catch (error) {
      showRetryError(error.message || 'Unable to verify the invoice.');
      document.getElementById('fbrGuest').textContent = 'Unavailable';
      document.getElementById('fbrMethod').textContent = 'Unable to verify';
      authority.replaceChildren(new Option('Unavailable', ''));
    }
  }
  // Delegation is needed because filtering/paging replaces these buttons.
  resultRegion.addEventListener('click', event => {
    const retryButton = event.target.closest('[data-fbr-retry]');
    if (retryButton) { event.preventDefault(); openRetry(retryButton); return; }
    const pageLink = event.target.closest('a[data-fbr-page]');
    if (pageLink && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) {
      event.preventDefault();
      loadReport(pageLink.href, 'push');
    }
  });
  authority.addEventListener('change', updateRetryButton);
  reconcileCheck.addEventListener('change', updateRetryButton);
  retry.addEventListener('click', async () => {
    if (!currentRegId || retry.disabled || !authority.value || isSending) return;
    if (requiresReconciliation && !reconcileCheck.checked) return;
    if (!window.confirm('Have you verified that the fiscal portal has not accepted this invoice? Retrying may create a duplicate.')) return;
    showRetryError('');
    isSending = true;
    updateRetryButton();
    retry.setAttribute('aria-busy', 'true');
    retry.textContent = 'Sending…';
    try {
      const token = document.querySelector('#fbrRetryAntiforgery input[name="__RequestVerificationToken"]')?.value || '';
      const response = await fetch(app.dataset.retryUrl, {
        method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json',
          'X-Requested-With': 'XMLHttpRequest', RequestVerificationToken: token },
        body: JSON.stringify({ regId: currentRegId, authority: authority.value,
          confirmedNoInvoice: requiresReconciliation && reconcileCheck.checked })
      });
      const json = await readResult(response, 'Fiscal retry', true);
      isSending = false;
      closeModal();
      const displayUrl = indexUrlWithForm(Number(resultRegion.querySelector('.fbr-dynamic')?.dataset.page || 1));
      await loadReport(displayUrl, 'replace');
      showFlash(json.message || 'Fiscal invoice retry completed.');
    } catch (error) {
      showRetryError((error.message || 'Fiscal retry failed.') + ' Verify the fiscal portal before retrying.');
      retry.dataset.blocked = '1';
    } finally {
      isSending = false;
      retry.removeAttribute('aria-busy');
      retry.textContent = 'Confirm & Retry';
      updateRetryButton();
    }
  });
  // The initial report is rendered by MVC; subsequent changes are loaded in place.
})();
