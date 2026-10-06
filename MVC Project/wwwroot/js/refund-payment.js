(() => {
  'use strict';

  const root = document.getElementById('orfRefundPayment');
  if (!root) return;

  const $ = id => document.getElementById(id);
  const refundUrl = String(root.dataset.refundUrl || '/CheckIn/RefundPayment').trim();
  let ctx = null;
  let processing = false;

  const number = value => {
    const n = Number(value);
    return Number.isFinite(n) ? n : 0;
  };

  const money = value => {
    const symbol = String(ctx?.currencySymbol || '£');
    return `${symbol}${number(value).toLocaleString('en-GB', {minimumFractionDigits:2, maximumFractionDigits:2})}`;
  };

  function token() {
    return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
  }

  function message(text, isError=false) {
    const el = $('orfMessage');
    if (!el) return;
    el.textContent = text || '';
    el.classList.toggle('orf-is-error', !!isError);
    el.hidden = !text;
  }

  function busy(show) {
    processing = !!show;
    const overlay = $('orfBusy');
    const submit = $('orfSubmit');
    if (overlay) overlay.hidden = !show;
    if (submit) {
      submit.disabled = !!show;
      submit.classList.toggle('orf-is-busy', !!show);
    }
    root.querySelectorAll('[data-orf-close]').forEach(x => x.disabled = !!show);
  }

  function close() {
    if (processing) return;
    root.hidden = true;
    root.setAttribute('aria-hidden', 'true');
    document.body.classList.remove('orf-modal-open');
    ctx = null;
    message('');
  }

  function open(options={}) {
    const max = Math.max(0, number(options.refundableAmount));
    const logId = Number(options.logId || 0);
    const regId = String(options.regId || '').trim();

    if (!regId || !logId || max <= 0) {
      const notify = typeof options.notify === 'function' ? options.notify : null;
      if (notify) notify('This payment has no refundable balance.', true);
      return false;
    }

    ctx = {
      ...options,
      regId,
      logId,
      refundableAmount: max,
      currencySymbol: String(options.currencySymbol || '£')
    };

    $('orfLogId').value = String(logId);
    $('orfRegId').textContent = regId;
    $('orfMaximum').textContent = money(max);
    $('orfCurrency').textContent = ctx.currencySymbol;
    $('orfAmount').value = max.toFixed(2);
    $('orfAmount').max = max.toFixed(2);
    $('orfAmount').dataset.maxRefund = max.toFixed(2);
    $('orfReason').value = '';
    message('');
    busy(false);

    root.hidden = false;
    root.setAttribute('aria-hidden', 'false');
    document.body.classList.add('orf-modal-open');
    setTimeout(() => $('orfAmount')?.focus(), 40);
    return true;
  }

  async function submit() {
    if (!ctx || processing) return;

    const amountEl = $('orfAmount');
    const reasonEl = $('orfReason');
    const amount = number(amountEl?.value);
    const max = number(amountEl?.dataset.maxRefund || ctx.refundableAmount);
    const reason = String(reasonEl?.value || '').trim();

    if (amount <= 0 || amount > max + 0.00001) {
      message(`Enter a refund amount between ${money(0.01)} and ${money(max)}.`, true);
      amountEl?.focus();
      return;
    }
    if (!reason) {
      message('Refund reason is required.', true);
      reasonEl?.focus();
      return;
    }

    busy(true);
    message('');
    try {
      const headers = {'Accept':'application/json','Content-Type':'application/json'};
      const antiForgery = token();
      if (antiForgery) headers['RequestVerificationToken'] = antiForgery;

      const response = await fetch(refundUrl, {
        method:'POST',
        credentials:'same-origin',
        headers,
        body: JSON.stringify({
          regId: ctx.regId,
          logId: ctx.logId,
          amount,
          reason
        })
      });

      let data = null;
      try { data = await response.json(); } catch {}

      if (response.status === 401) {
        location.href = '/LoginHMS';
        return;
      }
      if (!response.ok || data?.ok === false) {
        throw new Error(data?.message || `Refund failed (${response.status}).`);
      }

      const done = ctx;
      processing = false;
      busy(false);
      root.hidden = true;
      root.setAttribute('aria-hidden','true');
      document.body.classList.remove('orf-modal-open');
      ctx = null;

      if (typeof done.notify === 'function')
        done.notify(data?.message || 'Refund processed successfully.', false);

      if (typeof done.onCompleted === 'function')
        await done.onCompleted(data || {});
    } catch (e) {
      message(e?.message || 'Unable to process refund.', true);
      if (ctx && typeof ctx.notify === 'function')
        ctx.notify(e?.message || 'Unable to process refund.', true);
    } finally {
      if (ctx) busy(false);
    }
  }

  root.addEventListener('click', e => {
    if (e.target === root || e.target.closest('[data-orf-close]')) {
      e.preventDefault();
      close();
    }
  });

  $('orfSubmit')?.addEventListener('click', submit);
  $('orfReason')?.addEventListener('keydown', e => {
    if (e.key === 'Enter' && e.ctrlKey) {
      e.preventDefault();
      submit();
    }
  });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && !root.hidden) close();
  });

  window.RefundPayment = { open, close };
})();