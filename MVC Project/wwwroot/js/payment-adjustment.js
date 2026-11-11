(() => {
  'use strict';
  const root = document.getElementById('oraPaymentCorrection');
  if (!root) return;
  const get = id => document.getElementById(id);
  const form = get('oraPcForm'), amount = get('oraPcAmount'), paymentDate = get('oraPcDate');
  const methods = get('oraPcMethod'), error = get('oraPcError'), submit = get('oraPcSubmit'), delta = get('oraPcDelta');
  let context = null, loadNumber = 0;

  const money = (n, symbol) => `${symbol}${Number(n || 0).toLocaleString('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const dateOnly = value => String(value || '').slice(0, 10);
  // The API returns the SQL hotel's wall-clock timestamp. Do not convert it through
  // the browser's timezone (which may differ from the hotel's timezone).
  const localDateTime = value => {
    const match = String(value || '').match(/^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}):(\d{2}))?/);
    return match ? `${match[1]}T${match[2] || '00'}:${match[3] || '00'}` : '';
  };
  const uniqueId = () => window.crypto?.randomUUID ? window.crypto.randomUUID()
    : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
      const v = Math.floor(Math.random() * 16); return (c === 'x' ? v : (v & 3 | 8)).toString(16);
    });

  function showError(text) { error.textContent = text || ''; error.hidden = !text; }
  function close() {
    if (submit.dataset.busy === '1') return;
    loadNumber++;
    root.hidden = true; root.setAttribute('aria-hidden', 'true'); context = null;
  }
  function updateDifference() {
    if (!context || context.mode !== 'adjust') return;
    const change = Number(amount.value) - context.currentAmount;
    delta.textContent = `${change > 0 ? '+' : change < 0 ? '−' : ''}${money(Math.abs(change), context.currency)}`;
    delta.classList.toggle('negative', change < 0);
  }
  async function loadMethods(ticket, chosen) {
    methods.disabled = true;
    methods.replaceChildren(new Option('Loading…', ''));
    try {
      const response = await fetch(root.dataset.methodsUrl || '/CheckIn/PaymentMethods', {
        credentials: 'same-origin', headers: { Accept: 'application/json' }
      });
      if (!response.ok) throw new Error('Unable to load payment methods.');
      const rows = await response.json();
      if (!Array.isArray(rows) || rows.length === 0) throw new Error('No payment methods configured for this hotel.');
      if (ticket !== loadNumber || !context) return;
      const seen = new Set(), options = [];
      for (const row of rows) {
        const value = String(row.value ?? row.Value ?? '').trim();
        const label = String(row.text ?? row.Text ?? value).trim() || value;
        if (!value || seen.has(value.toLowerCase())) continue;
        seen.add(value.toLowerCase()); options.push(new Option(label, value));
      }
      if (!options.length) throw new Error('No payment methods configured for this hotel.');
      const match = options.find(x => x.value.toLowerCase() === String(chosen || '').toLowerCase());
      if (context.mode === 'adjust' && !match) options.unshift(new Option('Select method…', ''));
      methods.replaceChildren(...options);
      methods.value = match ? match.value : context.mode === 'adjust' ? '' : options[0].value;
      methods.disabled = false;
      submit.disabled = false;
      if (context.mode === 'adjust' && !match) showError('Please select a current payment method.');
    } catch (e) {
      if (ticket !== loadNumber || !context) return;
      methods.replaceChildren(new Option('Methods unavailable', ''));
      methods.disabled = true; submit.disabled = true;
      showError(e?.message || 'Unable to load payment methods.');
    }
  }
  function open(options) {
    if (!options?.regId || !['past', 'adjust'].includes(options.mode)) return;
    const adjusting = options.mode === 'adjust';
    context = {
      ...options, currentAmount: Number(options.currentAmount || 0), balance: Number(options.balance || 0),
      currency: options.currency || '£', requestId: uniqueId(),
      // The original timestamp, including seconds, is retained unless the user changes the picker.
      originalTimestamp: adjusting ? String(options.date || '') : '',
      originalMethod: adjusting ? String(options.method || '') : ''
    };
    const ticket = ++loadNumber;
    get('oraPcTitle').textContent = adjusting ? 'Adjust Payment' : 'Past Payment';
    get('oraPcRegId').textContent = options.regId;
    get('oraPcGuest').textContent = [options.guestName, options.roomNo ? `Room ${options.roomNo}` : ''].filter(Boolean).join(' · ');
    get('oraPcBalance').textContent = money(options.balance, context.currency);
    get('oraPcOriginalWrap').hidden = !adjusting;
    get('oraPcOriginal').textContent = money(context.currentAmount, context.currency);
    get('oraPcOriginalDate').textContent = adjusting ? localDateTime(options.date).replace('T', ' ') : '';
    get('oraPcAmountLabel').textContent = adjusting ? 'Corrected amount' : 'Amount';
    get('oraPcDifference').hidden = !adjusting;
    amount.value = adjusting ? context.currentAmount.toFixed(2) : '';
    amount.max = (adjusting ? context.currentAmount + Math.max(0, context.balance) : Math.max(0, context.balance)).toFixed(2);
    // Past payment defaults to 00:00 rather than guessing the hotel's current time.
    const initial = adjusting ? localDateTime(options.date) : localDateTime(options.hotelToday || options.date);
    context.initialDateTime = initial;
    paymentDate.min = '2000-01-01T00:00';
    paymentDate.max = `${dateOnly(options.hotelToday) || dateOnly(new Date().toISOString())}T23:59`;
    paymentDate.value = initial;
    get('oraPcReason').value = '';
    submit.textContent = adjusting ? 'Save Changes' : 'Save Payment';
    submit.disabled = true; submit.dataset.busy = '0'; showError(''); updateDifference();
    root.hidden = false; root.setAttribute('aria-hidden', 'false');
    loadMethods(ticket, adjusting ? options.method : 'Cash');
    amount.focus();
  }
  async function save(event) {
    event.preventDefault();
    if (!context || submit.disabled || submit.dataset.busy === '1') return;
    const adjusting = context.mode === 'adjust';
    const value = Number(amount.value), reason = get('oraPcReason').value.trim();
    const dateValue = paymentDate.value;
    if (!Number.isFinite(value) || value <= 0 || value !== Math.round(value * 100) / 100) {
      showError('Enter a valid positive amount (2 decimals maximum).'); return;
    }
    if (reason.length < 5) { showError('Enter a reason of at least 5 characters.'); return; }
    if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(dateValue)
      || dateValue < paymentDate.min || dateValue > paymentDate.max) {
      showError('Select a valid payment date and time.'); return;
    }
    if (!methods.value || methods.disabled) { showError('Select a hotel payment method.'); return; }
    const change = adjusting ? value - context.currentAmount : value;
    if (change > context.balance + 0.005) { showError('Amount exceeds the outstanding balance.'); return; }
    const changedDate = adjusting && dateValue !== context.initialDateTime;
    // System.Text.Json expects seconds in its ISO date-time input.
    const apiPaymentDate = `${dateValue}:00`;
    const prompt = adjusting
      ? `Save payment as ${money(value, context.currency)}, ${methods.value}, ${dateValue.replace('T', ' ')}? The previous details will be kept in the audit log.`
      : `Record ${money(value, context.currency)} for ${dateValue.replace('T', ' ')}?`;
    if (!window.confirm(prompt)) return;
    submit.disabled = true; submit.dataset.busy = '1'; submit.textContent = 'Saving…'; showError('');
    const payload = adjusting ? {
      requestId: context.requestId, regId: context.regId, logId: context.logId,
      newAmount: value, expectedAmount: context.currentAmount,
      expectedMethod: context.originalMethod, expectedPaymentDate: context.originalTimestamp,
      method: methods.value, paymentDate: apiPaymentDate, changePaymentDate: changedDate, reason
    } : {
      requestId: context.requestId, regId: context.regId, amount: value,
      method: methods.value, paymentDate: apiPaymentDate, reason
    };
    const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value;
    try {
      const response = await fetch(adjusting ? root.dataset.adjustUrl : root.dataset.pastUrl, {
        method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: token || '' },
        body: JSON.stringify(payload)
      });
      if (response.status === 401) throw new Error('Session expired. Sign in and retry.');
      const result = await response.json();
      if (!response.ok || result?.ok === false || result?.success === false)
        throw new Error(result?.message || `Payment could not be saved (${response.status}).`);
      const refresh = context.onCompleted;
      root.hidden = true; root.setAttribute('aria-hidden', 'true'); context = null; loadNumber++;
      if (typeof refresh === 'function') {
        try { await refresh(); } catch (_) { window.alert('Payment saved, but calendar refresh failed. Reload the page.'); }
      }
    } catch (e) { showError(e?.message || 'Unable to save payment.'); }
    finally {
      submit.dataset.busy = '0'; submit.disabled = methods.disabled;
      submit.textContent = adjusting ? 'Save Changes' : 'Save Payment';
    }
  }
  form.addEventListener('submit', save);
  amount.addEventListener('input', updateDifference);
  root.addEventListener('click', event => {
    if (event.target === root || event.target.closest('[data-ora-pc-close]')) close();
  });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && !root.hidden) close(); });
  window.PaymentCorrection = { open, close };
})();
