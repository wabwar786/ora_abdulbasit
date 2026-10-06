(() => {
  'use strict';

  const root = document.getElementById('oraRoomSecurity');
  if (!root) return;

  const $ = id => document.getElementById(id);
  const overlay = $('orsOverlay');
  const rowsHost = $('orsRows');
  const token = root.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
  const urls = {
    state: root.dataset.stateUrl || '/CheckIn/RoomSecurityState',
    movement: root.dataset.movementUrl || '/CheckIn/SecurityMovement',
    stripeCreate: root.dataset.stripeCreateUrl || '/CheckIn/Stripe/Create',
    stripeProcess: root.dataset.stripeProcessUrl || '/CheckIn/Stripe/Process',
    stripeStatus: root.dataset.stripeStatusUrl || '/CheckIn/Stripe/Status',
    stripeCancel: root.dataset.stripeCancelUrl || '/CheckIn/Stripe/Cancel',
    stripeCheckout: root.dataset.stripeCheckoutUrl || '/CheckIn/Stripe/Checkout',
    stripeCheckoutStatus: root.dataset.stripeCheckoutStatusUrl || '/CheckIn/Stripe/CheckoutStatus'
  };

  const state = {
    ctx: null,
    data: null,
    method: 'cash',
    selected: null,
    paymentIntentId: '',
    readerId: '',
    handoffReady: false,
    pollTimer: 0,
    checkoutWindow: null,
    checkoutSessionId: '',
    checkoutPoll: 0,
    busy: false,
    cache: new Map()
  };

  function esc(v) {
    return String(v ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }

  function num(v) {
    const n = Number(String(v ?? '').replace(/,/g, ''));
    return Number.isFinite(n) ? n : 0;
  }

  function money(v) {
    const symbol = state.data?.currency || '£';
    return `${symbol}${num(v).toLocaleString('en-GB', {minimumFractionDigits:2, maximumFractionDigits:2})}`;
  }

  function fmtDate(v) {
    if (!v) return '—';
    const d = new Date(v);
    if (Number.isNaN(d.valueOf())) return String(v);
    return d.toLocaleDateString('en-GB', {day:'2-digit', month:'short', year:'numeric'});
  }

  async function api(url, options = {}) {
    const headers = new Headers(options.headers || {});
    headers.set('Accept', 'application/json');
    if (options.body && !(options.body instanceof FormData)) {
      headers.set('Content-Type', 'application/json');
      if (typeof options.body !== 'string') options.body = JSON.stringify(options.body);
    }
    if (options.method && options.method.toUpperCase() !== 'GET' && token) {
      headers.set('RequestVerificationToken', token);
    }
    const res = await fetch(url, {...options, headers, credentials:'same-origin'});
    if (res.status === 401) {
      location.href = '/LoginHMS';
      throw new Error('Session expired.');
    }
    let json = null;
    try { json = await res.json(); } catch (_) { }
    if (!res.ok) throw new Error(json?.message || `Request failed (${res.status}).`);
    return json;
  }

  function showMessage(text, error = false) {
    const el = $('orsMessage');
    if (!el) return;
    el.textContent = text || '';
    el.classList.toggle('ors-hidden', !text);
    el.classList.toggle('ors-is-error', !!error);
  }

  function setStatus(title, detail = '') {
    if ($('orsStatusTitle')) $('orsStatusTitle').textContent = title || 'Ready';
    if ($('orsStatusText')) $('orsStatusText').textContent = detail || '';
  }

  function setLoader(show, text = 'Working…') {
    const loader = $('orsLoader');
    if (!loader) return;
    if ($('orsLoaderText')) $('orsLoaderText').textContent = text || 'Working…';
    loader.classList.toggle('ors-hidden', !show);
  }

  function setStatusProcessing(show) {
    $('orsStatusSpinner')?.classList.toggle('ors-hidden', !show);
    $('orsStatusBox')?.classList.toggle('ors-is-processing', !!show);
  }

  function setBusy(button, busy, label) {
    state.busy = !!busy;
    if (!button) return;
    if (busy) {
      if (!button.dataset.orsText) button.dataset.orsText = button.textContent || '';
      button.disabled = true;
      button.textContent = label || 'Working…';
      button.classList.add('ors-busy');
    } else {
      if (button.dataset.orsText !== undefined) button.textContent = button.dataset.orsText;
      delete button.dataset.orsText;
      button.disabled = false;
      button.classList.remove('ors-busy');
    }
  }

  function stopPdqPoll() {
    if (state.pollTimer) clearInterval(state.pollTimer);
    state.pollTimer = 0;
  }

  function stopCheckoutPoll() {
    if (state.checkoutPoll) clearInterval(state.checkoutPoll);
    state.checkoutPoll = 0;
  }

  function close() {
    // Do not lose the reference to an authorization that is still in progress.
    // The PDQ request can be explicitly cancelled; an open Checkout window should
    // be completed or closed first so its result can still be reconciled here.
    if (state.handoffReady && state.paymentIntentId) {
      showMessage('Cancel the active PDQ request before closing Room Security.', true);
      return;
    }
    if (state.checkoutSessionId && state.checkoutWindow && !state.checkoutWindow.closed) {
      showMessage('Complete or close the Card Hold window before closing Room Security.', true);
      return;
    }

    stopPdqPoll();
    stopCheckoutPoll();
    state.handoffReady = false;
    state.paymentIntentId = '';
    state.readerId = '';
    state.checkoutSessionId = '';
    state.checkoutWindow = null;
    $('orsSimulate') && ($('orsSimulate').disabled = true);
    overlay?.classList.add('ors-hidden');
    overlay?.setAttribute('aria-hidden', 'true');
    setLoader(false);
    setStatusProcessing(false);
    showMessage('');
  }

  function isCardRow(row) {
    const method = String(row?.method || '').toLowerCase();
    return !!row?.paymentIntentId || !!row?.chargeId || /card|pdq|stripe|pre-auth|preauth/.test(method);
  }

  function rowMode(row) {
    const m = String(row?.method || '').trim();
    if (m) return m;
    return isCardRow(row) ? 'Card Hold' : 'Cash';
  }

  function rowMovementAmount(row) {
    if (num(row?.amount) > 0) return num(row.amount);
    if (num(row?.refunded) > 0) return num(row.refunded);
    return num(row?.deducted);
  }

  function renderRows() {
    const list = Array.isArray(state.data?.securityLog) ? state.data.securityLog : [];
    if (!rowsHost) return;
    if (!list.length) {
      rowsHost.innerHTML = '<tr><td colspan="6" class="ors-empty">No security deposit movement found for this reservation.</td></tr>';
      return;
    }

    rowsHost.innerHTML = list.map(row => {
      const status = String(row.status || '').trim();
      const settled = status.toLowerCase() !== 'deposit';
      const amount = rowMovementAmount(row);
      const receipt = row.receiptUrl
        ? `<a class="ors-receipt" href="${esc(row.receiptUrl)}" target="_blank" rel="noopener">Receipt</a>`
        : '';
      const action = row.canSettle
        ? `<button type="button" class="ors-row-action" data-ors-settle="${Number(row.id || 0)}">Settle</button>`
        : receipt;
      const displayStatus = status.toLowerCase() === 'deposit' ? 'On Hold'
        : status.toLowerCase() === 'refund' ? 'Refunded / Released'
        : status.toLowerCase().startsWith('deduct') ? 'Captured / Deducted'
        : (status || 'Recorded');
      return `<tr>
        <td>${esc(fmtDate(row.date))}</td>
        <td>${esc(rowMode(row))}</td>
        <td class="ors-description-cell" title="${esc(row.description || '')}">${esc(row.description || '—')}</td>
        <td>${esc(money(amount))}</td>
        <td><span class="ors-status-pill ${settled ? 'ors-is-settled' : ''}">${esc(displayStatus)}</span></td>
        <td>${action}</td>
      </tr>`;
    }).join('');
  }

  function renderState() {
    const d = state.data || {};
    $('orsRegId').textContent = d.regId || state.ctx?.regId || '—';
    $('orsBalance').textContent = money(d.securityBalance || 0);
    $('orsCurrency').textContent = d.currency || '£';
    $('orsSettleCurrency').textContent = d.currency || '£';
    $('orsCaptureCurrency').textContent = d.currency || '£';
    $('orsGuestLine').textContent = state.ctx?.guestName
      ? `${state.ctx.guestName} · deposit, hold, refund or capture room security.`
      : 'Deposit, hold, refund or capture room security.';

    const reader = $('orsReader');
    if (reader) {
      const current = reader.value;
      const options = Array.isArray(d.readers) ? d.readers : (Array.isArray(d.stripeReaders) ? d.stripeReaders : []);
      reader.innerHTML = '<option value="">-- Select reader --</option>' +
        options.map(x => `<option value="${esc(x.value)}">${esc(x.text || x.value)}</option>`).join('');
      if (options.some(x => String(x.value) === current)) reader.value = current;
      else if (options.some(x => String(x.value) === state.readerId)) reader.value = state.readerId;
      else if (options.length) reader.value = String(options[0].value || '');
      state.readerId = reader.value || '';
    }

    const cardAllowed = d.isStripeConfigured === true && d.canCardPayment !== false;
    root.querySelectorAll('[data-ors-method="pdq"],[data-ors-method="card"]').forEach(btn => {
      btn.disabled = !cardAllowed;
      btn.title = cardAllowed ? '' : 'Stripe card authorization is not available for this hotel/user.';
    });

    const sim = $('orsSimulation');
    if (sim) sim.classList.toggle('ors-hidden', !(d.showSimulation === true && state.method === 'pdq'));
    renderRows();
  }

  async function loadState(selectSecurityId = 0, options = {}) {
    if (!state.ctx?.regId) throw new Error('Reservation reference is missing.');
    const showLoader = options.showLoader !== false;
    if (showLoader) setLoader(true, options.loaderText || 'Loading room security…');
    try {
      const u = new URL(urls.state, location.origin);
      u.searchParams.set('regId', state.ctx.regId);
      const json = await api(u.toString());
      if (json?.ok === false) throw new Error(json.message || 'Unable to load room security.');
      state.data = json?.data || json;
      state.cache.set(state.ctx.regId, { at: Date.now(), data: state.data });
      renderState();
      if (selectSecurityId > 0) {
        const row = (state.data?.securityLog || []).find(x => Number(x.id) === Number(selectSecurityId));
        if (row?.canSettle) setMode('settle', row);
        else showMessage('This security deposit is already settled or no longer available.', true);
      }
      return state.data;
    } finally {
      if (showLoader) setLoader(false);
    }
  }

  function setMethod(method) {
    if (!['cash','pdq','card'].includes(method)) method = 'cash';
    const btn = root.querySelector(`[data-ors-method="${method}"]`);
    if (btn?.disabled) method = 'cash';
    state.method = method;
    root.querySelectorAll('[data-ors-method]').forEach(x => x.classList.toggle('ors-is-selected', x.dataset.orsMethod === method));
    $('orsReaderField')?.classList.toggle('ors-hidden', method !== 'pdq');
    if (method === 'pdq') {
      const reader = $('orsReader');
      if (reader && !reader.value && reader.options.length > 1) reader.selectedIndex = 1;
      if (reader) state.readerId = reader.value || '';
    }
    $('orsSimulation')?.classList.toggle('ors-hidden', !(method === 'pdq' && state.data?.showSimulation === true));
    $('orsCancelTerminal')?.classList.add('ors-hidden');
    state.handoffReady = false;
    state.paymentIntentId = '';
    if ($('orsSimulate')) $('orsSimulate').disabled = true;

    if (method === 'cash') setStatus('Cash deposit', 'Save a refundable cash security deposit.');
    else if (method === 'pdq') setStatus('PDQ hold', 'Authorize the card on the Stripe Terminal without capturing it.');
    else setStatus('Card hold', 'Open secure Stripe Checkout and authorize the card without capturing it.');
  }

  function setMode(mode, row = null) {
    const settle = mode === 'settle';
    $('orsDepositPanel')?.classList.toggle('ors-hidden', settle);
    $('orsSettlePanel')?.classList.toggle('ors-hidden', !settle);
    state.selected = settle ? row : null;
    if (!settle) {
      setMethod(state.method || 'cash');
      return;
    }

    const current = Math.max(0, num(row?.amount));
    const card = isCardRow(row);
    $('orsSettleHeading').textContent = card ? 'Settle Card Security Hold' : 'Settle Cash Security';
    $('orsSettleHelp').textContent = card
      ? 'Release/refund part of the security and capture the remainder. Full release captures nothing.'
      : 'Refund part of the cash deposit and deduct the remainder.';
    $('orsRefundLabel').textContent = card ? 'Release / Refund Amount' : 'Refund Amount';
    $('orsCaptureLabel').textContent = card ? 'Capture Amount' : 'Deduct Amount';
    $('orsSelectedMode').textContent = rowMode(row);
    $('orsSelectedAmount').textContent = money(current);
    $('orsRefundAmount').max = current.toFixed(2);
    $('orsRefundAmount').value = current.toFixed(2);
    $('orsCaptureAmount').value = '0.00';
    $('orsSettleNote').value = '';
    updateSettlePreview();
    setTimeout(() => $('orsRefundAmount')?.focus(), 0);
  }

  function updateSettlePreview() {
    const row = state.selected;
    if (!row) return;
    const current = Math.max(0, num(row.amount));
    let refund = Math.max(0, num($('orsRefundAmount')?.value));
    if (refund > current) refund = current;
    const capture = Math.max(0, current - refund);
    $('orsCaptureAmount').value = capture.toFixed(2);
    const card = isCardRow(row);
    $('orsSettlePreview').textContent = card
      ? `Release / refund: ${money(refund)} · Capture: ${money(capture)}`
      : `Refund: ${money(refund)} · Security deduction: ${money(capture)}`;
  }

  async function notifyChanged() {
    try {
      if (typeof state.ctx?.onChanged === 'function') await state.ctx.onChanged(state.data);
    } catch (_) { }
  }

  async function saveCash(button) {
    const amount = num($('orsAmount')?.value);
    const note = ($('orsNote')?.value || '').trim();
    if (amount <= 0) return showMessage('Enter a security amount greater than 0.', true);
    if (!note) return showMessage('Additional info is required.', true);
    setBusy(button, true, 'Saving…');
    setLoader(true, 'Saving room security…');
    setStatusProcessing(true);
    try {
      const r = await api(urls.movement, {
        method:'POST',
        body:{
          regId:state.ctx.regId,
          visitId:state.data?.visitId || state.ctx.visitId || '',
          amount,
          note,
          method:'Cash',
          movement:'deposit',
          securityId:0,
          paymentIntentId:'',
          chargeId:''
        }
      });
      if (r?.ok === false) throw new Error(r.message || 'Unable to save room security.');
      showMessage(r.message || 'Cash room security saved.');
      $('orsAmount').value = '';
      $('orsNote').value = '';
      await loadState();
      await notifyChanged();
    } catch (e) {
      showMessage(e.message || 'Unable to save room security.', true);
    } finally {
      setBusy(button, false);
      setLoader(false);
      setStatusProcessing(false);
    }
  }

  async function saveAuthorizedHold(method, amount, note, pi, charge) {
    const r = await api(urls.movement, {
      method:'POST',
      body:{
        regId:state.ctx.regId,
        visitId:state.data?.visitId || state.ctx.visitId || '',
        amount,
        note,
        method,
        movement:'deposit',
        securityId:0,
        paymentIntentId:pi || '',
        chargeId:charge || ''
      }
    });
    if (r?.ok === false) throw new Error(r.message || 'The card was authorized but the security hold could not be saved.');
    await loadState(0, {showLoader:false});
    await notifyChanged();
    return r;
  }

  async function pollPdq(amount, note) {
    stopPdqPoll();
    setStatusProcessing(true);
    let attempts = 0;
    state.pollTimer = setInterval(async () => {
      attempts++;
      try {
        const u = new URL(urls.stripeStatus, location.origin);
        u.searchParams.set('paymentIntentId', state.paymentIntentId);
        const final = await api(u.toString());
        const status = String(final?.status || '').toLowerCase();
        if (status === 'requires_capture') {
          stopPdqPoll();
          state.handoffReady = false;
          $('orsSimulate') && ($('orsSimulate').disabled = true);
          $('orsCancelTerminal')?.classList.add('ors-hidden');
          await saveAuthorizedHold('PDQ Hold', amount, note, state.paymentIntentId, final?.chargeId || '');
          showMessage('PDQ security hold authorized and saved.');
          setStatus('Authorized', 'The card is on hold. Use Settle later to release/refund or capture it.');
          setStatusProcessing(false);
          state.paymentIntentId = '';
          return;
        }
        if (['canceled','cancelled'].includes(status)) {
          stopPdqPoll();
          setStatusProcessing(false);
          setStatus('Cancelled', final?.message || 'The PDQ authorization was cancelled.');
          return;
        }
        setStatus('Authorizing', final?.message || `Waiting for card authorization… (${status || 'pending'})`);
      } catch (_) { }
      if (attempts >= 90) {
        stopPdqPoll();
        setStatusProcessing(false);
        setStatus('Pending', 'Authorization is taking longer than expected. Check the security activity before retrying.');
      }
    }, 1000);
  }

  async function startPdqHold(button) {
    if (state.handoffReady && state.paymentIntentId)
      return showMessage('A PDQ security hold is already in progress. Complete or cancel it before starting another.', true);

    const amount = num($('orsAmount')?.value);
    const note = ($('orsNote')?.value || '').trim();
    const readerId = $('orsReader')?.value || '';
    if (amount <= 0) return showMessage('Enter a security amount greater than 0.', true);
    if (!note) return showMessage('Additional info is required.', true);
    if (!readerId) return showMessage('Select a PDQ reader.', true);
    if (!state.data?.isStripeConfigured || state.data?.canCardPayment === false)
      return showMessage('Stripe Terminal hold is not available for this hotel/user.', true);

    setBusy(button, true, 'Starting PDQ hold…');
    setLoader(true, 'Connecting to PDQ reader…');
    setStatusProcessing(true);
    try {
      showMessage('');
      const request = {
        regId:state.ctx.regId,
        visitId:state.data?.visitId || state.ctx.visitId || '',
        readerId,
        amount,
        currency:state.data?.currencyCode || 'GBP',
        note,
        securityHold:true,
        simulate:false,
        simulationResult:''
      };
      setStatus('Creating', 'Creating manual-capture PaymentIntent…');
      const created = await api(urls.stripeCreate, {method:'POST', body:request});
      if (!created?.success || !created.paymentIntentId) throw new Error(created?.message || 'Unable to create PDQ security hold.');
      state.paymentIntentId = created.paymentIntentId;
      state.readerId = readerId;
      request.paymentIntentId = state.paymentIntentId;
      setStatus('Waiting for reader', 'Sending the authorization to the PDQ reader…');
      const processed = await api(urls.stripeProcess, {method:'POST', body:request});
      if (!processed?.success) throw new Error(processed?.message || 'Unable to send security hold to the reader.');
      state.handoffReady = true;
      setLoader(false);
      $('orsCancelTerminal')?.classList.remove('ors-hidden');
      if ($('orsSimulate')) $('orsSimulate').disabled = !($('orsSimulateEnabled')?.checked);
      setStatus('Waiting for reader', 'Present the card. The amount will be authorized only, not captured.');
      await pollPdq(amount, note);
    } catch (e) {
      showMessage(e.message || 'Unable to create PDQ security hold.', true);
      setStatus('Not completed', e.message || 'PDQ hold failed.');
    } finally {
      setBusy(button, false);
      setLoader(false);
      if (!state.handoffReady) setStatusProcessing(false);
    }
  }

  async function simulatePdq() {
    if (!state.handoffReady || !state.paymentIntentId) return showMessage('Start the PDQ hold first.', true);
    if (!$('orsSimulateEnabled')?.checked) return showMessage('Enable simulated card first.', true);
    const btn = $('orsSimulate');
    btn.disabled = true;
    try {
      const amount = num($('orsAmount')?.value);
      const note = ($('orsNote')?.value || '').trim();
      const result = await api(urls.stripeProcess, {
        method:'POST',
        body:{
          regId:state.ctx.regId,
          visitId:state.data?.visitId || state.ctx.visitId || '',
          readerId:state.readerId || $('orsReader')?.value || '',
          amount,
          currency:state.data?.currencyCode || 'GBP',
          note,
          paymentIntentId:state.paymentIntentId,
          securityHold:true,
          simulate:true,
          simulationResult:$('orsSimulationResult')?.value || 'success'
        }
      });
      if (!result?.success) throw new Error(result?.message || 'Unable to simulate card.');
      setStatus('Simulated card presented', 'Waiting for Stripe to complete the authorization…');
    } catch (e) {
      showMessage(e.message || 'Unable to simulate card.', true);
    } finally {
      btn.disabled = !(state.handoffReady && $('orsSimulateEnabled')?.checked);
    }
  }

  async function cancelPdq() {
    if (!state.paymentIntentId) return;
    const btn = $('orsCancelTerminal');
    setBusy(btn, true, 'Cancelling…');
    setLoader(true, 'Cancelling PDQ request…');
    setStatusProcessing(true);
    try {
      stopPdqPoll();
      const r = await api(urls.stripeCancel, {
        method:'POST',
        body:{
          regId:state.ctx.regId,
          visitId:state.data?.visitId || state.ctx.visitId || '',
          readerId:state.readerId || $('orsReader')?.value || '',
          amount:num($('orsAmount')?.value),
          currency:state.data?.currencyCode || 'GBP',
          paymentIntentId:state.paymentIntentId,
          securityHold:true
        }
      });
      state.paymentIntentId = '';
      state.handoffReady = false;
      $('orsSimulate') && ($('orsSimulate').disabled = true);
      $('orsCancelTerminal')?.classList.add('ors-hidden');
      setStatus(r?.success ? 'Cancelled' : 'Cancel failed', r?.message || '');
      if (!r?.success) showMessage(r?.message || 'Unable to cancel PDQ request.', true);
    } catch (e) {
      showMessage(e.message || 'Unable to cancel PDQ request.', true);
    } finally {
      setBusy(btn, false);
      setLoader(false);
      setStatusProcessing(false);
    }
  }

  async function finalizeCheckoutHold(result, amount, note) {
    const status = String(result?.status || '').toLowerCase();
    if (!result?.success || status !== 'requires_capture') return false;
    stopCheckoutPoll();
    try { if (state.checkoutWindow && !state.checkoutWindow.closed) state.checkoutWindow.close(); } catch (_) { }
    state.checkoutWindow = null;
    await saveAuthorizedHold('Card Hold', amount, note, result.paymentIntentId || '', result.chargeId || '');
    showMessage('Online card security hold authorized and saved.');
    setStatus('Authorized', 'The card is on hold. Use Settle later to release/refund or capture it.');
    setStatusProcessing(false);
    state.checkoutSessionId = '';
    return true;
  }

  async function startCardHold(button) {
    if (state.checkoutSessionId && state.checkoutWindow && !state.checkoutWindow.closed)
      return showMessage('A Card Hold window is already open. Complete or close it before starting another.', true);

    const amount = num($('orsAmount')?.value);
    const note = ($('orsNote')?.value || '').trim();
    if (amount <= 0) return showMessage('Enter a security amount greater than 0.', true);
    if (!note) return showMessage('Additional info is required.', true);
    if (!state.data?.isStripeConfigured || state.data?.canCardPayment === false)
      return showMessage('Online card hold is not available for this hotel/user.', true);

    // Open synchronously from the user's click to avoid popup blockers.
    state.checkoutWindow = window.open('', 'ORA_RoomSecurity_CardHold', 'width=760,height=850,resizable=yes,scrollbars=yes');
    if (!state.checkoutWindow) return showMessage('Please allow pop-ups so secure card authorization can open.', true);
    try {
      state.checkoutWindow.document.open();
      state.checkoutWindow.document.write('<!doctype html><html><head><meta charset="utf-8"><title>ORA PMS - Card Hold</title></head><body style="font-family:Arial,sans-serif;margin:0;display:grid;place-items:center;min-height:100vh;background:#f6f9fc;color:#17375e"><div style="text-align:center"><h3>Preparing secure card authorization…</h3><p style="font-size:13px;color:#64748b">Please wait.</p></div></body></html>');
      state.checkoutWindow.document.close();
    } catch (_) { }

    setBusy(button, true, 'Preparing card hold…');
    setLoader(true, 'Preparing secure card hold…');
    setStatusProcessing(true);
    try {
      showMessage('');
      setStatus('Preparing', 'Creating secure Stripe Checkout authorization…');
      const created = await api(urls.stripeCheckout, {
        method:'POST',
        body:{
          regId:state.ctx.regId,
          visitId:state.data?.visitId || state.ctx.visitId || '',
          amount,
          currency:state.data?.currencyCode || 'GBP',
          note,
          securityHold:true
        }
      });
      if (!created?.success || !created.checkoutUrl || !created.sessionId)
        throw new Error(created?.message || 'Unable to create card hold checkout.');
      state.checkoutSessionId = created.sessionId;
      try { state.checkoutWindow.location.replace(created.checkoutUrl); }
      catch (_) { state.checkoutWindow.location.href = created.checkoutUrl; }
      setLoader(false);
      setStatus('Enter card details', 'Complete the secure authorization window. No money is captured yet.');

      stopCheckoutPoll();
      let attempts = 0;
      state.checkoutPoll = setInterval(async () => {
        attempts++;
        try {
          const u = new URL(urls.stripeCheckoutStatus, location.origin);
          u.searchParams.set('sessionId', created.sessionId);
          const result = await api(u.toString());
          if (await finalizeCheckoutHold(result, amount, note)) return;
          const status = String(result?.status || '').toLowerCase();
          if (['expired','canceled','cancelled'].includes(status)) {
            stopCheckoutPoll();
            state.checkoutSessionId = '';
            try { if (state.checkoutWindow && !state.checkoutWindow.closed) state.checkoutWindow.close(); } catch (_) { }
            state.checkoutWindow = null;
            setStatusProcessing(false);
            setStatus('Cancelled', result?.message || 'Card authorization was cancelled.');
            return;
          }
        } catch (_) { }
        if (attempts >= 120) {
          stopCheckoutPoll();
          setStatusProcessing(false);
          setStatus('Pending', 'Authorization confirmation is taking longer than expected. Check Security Activity before retrying.');
        }
      }, 1500);
    } catch (e) {
      try { if (state.checkoutWindow && !state.checkoutWindow.closed) state.checkoutWindow.close(); } catch (_) { }
      state.checkoutWindow = null;
      showMessage(e.message || 'Unable to create card security hold.', true);
      setStatus('Not completed', e.message || 'Card hold failed.');
    } finally {
      setBusy(button, false);
      setLoader(false);
      if (!state.checkoutSessionId) setStatusProcessing(false);
    }
  }

  async function saveDeposit() {
    if (state.busy) return;
    const btn = $('orsDepositSubmit');
    if (state.method === 'pdq') return startPdqHold(btn);
    if (state.method === 'card') return startCardHold(btn);
    return saveCash(btn);
  }

  async function settleSelected(button) {
    const row = state.selected;
    if (!row) return showMessage('Select an active security deposit.', true);
    const current = Math.max(0, num(row.amount));
    let refund = Math.max(0, num($('orsRefundAmount')?.value));
    const note = ($('orsSettleNote')?.value || '').trim();
    if (!note) return showMessage('Additional info is required.', true);
    if (refund > current + 0.00001) return showMessage('Refund/release amount cannot exceed the selected security deposit.', true);
    if (refund < 0) refund = 0;

    const card = isCardRow(row);
    const capture = Math.max(0, current - refund);
    const question = card
      ? `Release/refund ${money(refund)} and capture ${money(capture)} from this security hold?`
      : `Refund ${money(refund)} and deduct ${money(capture)} from this cash security?`;
    if (!window.confirm(question)) return;

    setBusy(button, true, card ? 'Settling hold…' : 'Settling security…');
    setLoader(true, card ? 'Settling card security hold…' : 'Settling room security…');
    setStatusProcessing(true);
    try {
      const r = await api(urls.movement, {
        method:'POST',
        body:{
          regId:state.ctx.regId,
          visitId:state.data?.visitId || state.ctx.visitId || '',
          amount:refund,
          note,
          method:row.method || (card ? 'Card Hold' : 'Cash'),
          movement:'refund',
          securityId:Number(row.id || 0),
          paymentIntentId:row.paymentIntentId || '',
          chargeId:row.chargeId || ''
        }
      });
      if (r?.ok === false) throw new Error(r.message || 'Unable to settle room security.');
      showMessage(r.message || 'Room security settled successfully.');
      setMode('deposit');
      await loadState();
      await notifyChanged();
    } catch (e) {
      showMessage(e.message || 'Unable to settle room security.', true);
    } finally {
      setBusy(button, false);
      setLoader(false);
      setStatusProcessing(false);
    }
  }

  async function open(options = {}) {
    const regId = String(options.regId || '').trim();
    if (!regId) throw new Error('Reservation reference is missing.');
    state.ctx = {
      regId,
      visitId:String(options.visitId || '').trim(),
      guestName:String(options.guestName || '').trim(),
      securityBalance:Number(options.securityBalance || 0),
      onChanged:options.onChanged
    };
    state.method = 'cash';
    state.selected = null;
    state.readerId = '';
    showMessage('');
    setStatusProcessing(false);
    setMode('deposit');

    // Render the values already known by Check-In/Calendar immediately.  The
    // lightweight endpoint then refreshes history/readers/config in the background.
    $('orsRegId').textContent = regId;
    $('orsBalance').textContent = `${options.currencySymbol || '£'}${num(options.securityBalance || 0).toLocaleString('en-GB',{minimumFractionDigits:2,maximumFractionDigits:2})}`;
    $('orsGuestLine').textContent = state.ctx.guestName
      ? `${state.ctx.guestName} · secure deposits, refunds, releases and deductions`
      : 'Secure deposits, refunds, releases and deductions';

    overlay?.classList.remove('ors-hidden');
    overlay?.setAttribute('aria-hidden', 'false');

    const cached = state.cache.get(regId);
    const cacheFresh = cached && (Date.now() - Number(cached.at || 0)) < 15000;
    if (cacheFresh) {
      state.data = cached.data;
      renderState();
      setLoader(false);
    } else {
      rowsHost.innerHTML = '<tr><td colspan="5" class="ors-empty"><span class="ors-inline-spinner"></span> Loading room security…</td></tr>';
    }

    try {
      await loadState(Number(options.securityId || 0), {
        showLoader: !cacheFresh,
        loaderText: 'Loading room security…'
      });
    } catch (e) {
      showMessage(e.message || 'Unable to load room security.', true);
    }
  }

  // Checkout return page posts this message back to the opener.
  window.addEventListener('message', async event => {
    if (event.origin !== window.location.origin) return;
    if (event.data?.type !== 'ora-stripe-checkout' || !state.checkoutSessionId) return;
    const sessionId = event.data.sessionId || state.checkoutSessionId;
    if (!sessionId) return;
    try {
      const u = new URL(urls.stripeCheckoutStatus, location.origin);
      u.searchParams.set('sessionId', sessionId);
      const result = await api(u.toString());
      const amount = num($('orsAmount')?.value);
      const note = ($('orsNote')?.value || '').trim();
      await finalizeCheckoutHold(result, amount, note);
    } catch (e) {
      showMessage(e.message || 'Unable to confirm card authorization.', true);
    }
  });

  root.querySelectorAll('[data-ors-method]').forEach(btn => btn.addEventListener('click', () => setMethod(btn.dataset.orsMethod || 'cash')));
  $('orsReader')?.addEventListener('change', e => { state.readerId = e.target.value || ''; });
  rowsHost?.addEventListener('click', e => {
    const btn = e.target.closest('[data-ors-settle]');
    if (!btn) return;
    const id = Number(btn.dataset.orsSettle || 0);
    const row = (state.data?.securityLog || []).find(x => Number(x.id) === id);
    if (row?.canSettle) setMode('settle', row);
  });
  $('orsNewDeposit')?.addEventListener('click', () => setMode('deposit'));
  $('orsBackToDeposit')?.addEventListener('click', () => setMode('deposit'));
  $('orsDepositSubmit')?.addEventListener('click', saveDeposit);
  $('orsSettleSubmit')?.addEventListener('click', e => settleSelected(e.currentTarget));
  $('orsRefundAmount')?.addEventListener('input', updateSettlePreview);
  $('orsRefresh')?.addEventListener('click', async () => {
    try { await loadState(0, {showLoader:true, loaderText:'Refreshing room security…'}); showMessage('Room security refreshed.'); }
    catch (e) { showMessage(e.message || 'Unable to refresh room security.', true); }
  });
  $('orsClose')?.addEventListener('click', close);
  overlay?.addEventListener('click', e => { if (e.target === overlay && !state.busy) close(); });
  $('orsSimulate')?.addEventListener('click', simulatePdq);
  $('orsSimulateEnabled')?.addEventListener('change', () => {
    if ($('orsSimulate')) $('orsSimulate').disabled = !(state.handoffReady && $('orsSimulateEnabled')?.checked);
  });
  $('orsCancelTerminal')?.addEventListener('click', cancelPdq);
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && !overlay?.classList.contains('ors-hidden') && !state.busy) close(); });

  window.RoomSecurity = { open, close, refresh: () => loadState() };
})();
