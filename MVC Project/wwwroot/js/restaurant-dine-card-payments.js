/* ORA PMS: verified Stripe PDQ / online card collection for Dine In POS bills.
   Posting to the payment report remains webhook-controlled; no client response
   is ever treated as proof of payment. Room bills use the guest-folio modal. */
(() => {
  'use strict';
  const root = document.getElementById('restaurant-pos');
  if (!root) return;
  const $ = id => document.getElementById(id);
  const box = () => $('r-dine-card-pending');
  const message = () => $('r-dine-card-message');
  let chargeWindow = null;
  let busy = false;
  let pendingMode = '';
  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
  const emit = (status, message, receiptUrl = '') => root.dispatchEvent(
    new CustomEvent('restaurant:dine-payment-state', { detail: {status, message, receiptUrl} }));

  function status(text, error = false) {
    if (box()) {
      box().hidden = false;
      box().classList.toggle('is-error', error);
    }
    if (message()) message().textContent = text;
  }
  const hasItems = () => !!root.querySelector('.r-classic-cart-card');
  // Keep the terminal window visible when the server refuses to start a charge.
  // v4 closed it immediately in catch(), hiding the actionable backend error.
  function showGatewayError(popup, error, pending) {
    if (!popup || popup.closed) return;
    try {
      const doc = popup.document;
      doc.open();
      doc.write('<!doctype html><html lang="en"><head><meta charset="utf-8"><title>ORA PMS — PDQ Status</title><style>body{margin:0;padding:30px;font:15px system-ui,sans-serif;background:#f2f6fa;color:#213b56}main{max-width:440px;margin:40px auto;padding:26px;background:white;border:1px solid #dce5ed;border-radius:12px;box-shadow:0 8px 26px #1233}h1{font-size:19px;margin:0 0 12px}p{line-height:1.55;overflow-wrap:anywhere}.note{color:#6b7c8c;font-size:13px}button{border:0;border-radius:7px;background:#1d496a;color:#fff;padding:10px 18px;cursor:pointer}</style></head><body><main><h1 id="status-title"></h1><p id="status-text"></p><p id="status-note" class="note"></p><button type="button" onclick="window.close()">Close</button></main></body></html>');
      doc.close();
      doc.getElementById('status-title').textContent = pending ? 'Check PDQ payment status' : 'PDQ payment could not start';
      doc.getElementById('status-text').textContent = String(error || 'The payment service returned an error.');
      doc.getElementById('status-note').textContent = pending
        ? 'The status is uncertain. Check payment reconciliation before trying another charge.'
        : 'No payment was confirmed. Check the message and configuration, then return to the Restaurant checkout.';
      popup.focus();
    } catch (_) {
      // Cross-origin windows cannot be redrawn. Keep the status on the POS page.
    }
  }
  const token = () => root.querySelector('#r-close-form input[name="__RequestVerificationToken"]')?.value || '';
  function headers() {
    return {
      'Accept': 'application/json',
      'X-Requested-With': 'XMLHttpRequest',
      'X-Restaurant-Mode': root.dataset.currentBillMode || 'dine-in',
      'RequestVerificationToken': token()
    };
  }
  async function readResult(response) {
    const result = await response.json().catch(() => null);
    if (!response.ok || !result?.ok) {
      const e = new Error(result?.message || `Payment request failed (${response.status}).`);
      // A definite validation/configuration refusal can be retried after fixing it.
      // A 409, server error, or lost response may mean the payment was started:
      // never suggest another charge until reconciliation.
      e.httpStatus = response.status;
      e.pending = result?.pending === true || response.status === 409 || response.status >= 500;
      throw e;
    }
    return result;
  }
  async function start(mode) {
    // Opening synchronously avoids Chrome's popup blocker after an AJAX call.
    if (busy || root.dataset.currentBillMode !== 'dine-in' || !hasItems() ||
        root.classList.contains('r-pos-loading')) return;
    const form = $('r-close-form');
    if (!form?.reportValidity()) { emit('failed', 'Complete the bill details before charging the card.'); return; }
    if ($('r-complimentary')?.checked) {
      status('A complimentary bill does not require card payment.', true);
      emit('failed','A complimentary bill does not require card payment.');
      return;
    }
    const popup = window.open('about:blank', 'oraRestaurantCard',
      'width=530,height=760,resizable=yes,scrollbars=yes');
    if (!popup) {
      status('Allow pop-ups to take a PDQ or card payment.', true);
      emit('failed','Allow pop-ups to take a PDQ or card payment.');
      return;
    }
    chargeWindow = popup;
    popup.document.write('<!doctype html><title>ORA PMS — Card Payment</title><p style="font:15px system-ui;padding:28px">Preparing secure card payment…</p>');
    busy = true;
    try {
      const fd = new FormData(form);
      // The server has a dedicated card-start DTO; do not send/validate the
      // dropdown's manual payment method to initiate a gateway charge.
      // Read the number from the latest AJAX-refreshed bill panel.
      const activeBill = (root.querySelector('.r-checkout')?.dataset.billNumber || '').trim();
      if (!activeBill) throw new Error('The bill has changed. Refresh the POS before charging.');
      fd.set('BillNumber', activeBill);
      fd.set('Discount', ($('r-discount')?.value || '0').trim() || '0');
      fd.set('Complimentary', $('r-complimentary')?.checked ? 'true' : 'false');
      // Cross-check the server's tax/discount quote with the exact amount the
      // cashier saw before opening the PDQ or online card window. Never use
      // the displayed value as the authoritative Stripe charge amount.
      const displayedTotal = Number(($('r-total')?.textContent || '').replace(/[^0-9.]/g, ''));
      if (!Number.isFinite(displayedTotal) || displayedTotal <= 0)
        throw new Error('The payable amount is unavailable. Refresh the bill before charging.');
      fd.set('ExpectedAmount', displayedTotal.toFixed(2));
      fd.delete('PaymentMethod');
      fd.set('paymentMode', mode);
      const response = await fetch(root.dataset.cardStartUrl, {
        method: 'POST', body: fd, credentials: 'same-origin', headers: headers()
      });
      const result = await readResult(response);
      pendingMode = mode;
      status(`${mode === 'pdq' ? 'PDQ' : 'Card'} payment started for £${Number(result.amount).toFixed(2)}. Complete the payment in the new window.`);
      emit('pending','Payment started. Complete the card transaction in the secure window.');
      popup.location.replace(result.url);
      popup.focus();
    } catch (error) {
      const uncertain = !(Number.isInteger(error.httpStatus) && error.httpStatus < 500 && !error.pending);
      const msg = error?.message || 'Unable to start card payment.';
      // Never close a just-opened PDQ window automatically on a server error.
      showGatewayError(popup, msg, uncertain);
      if (!uncertain) pendingMode = '';
      status(msg, true);
      emit(uncertain ? 'pending' : 'failed', msg);
    } finally { busy = false; }
  }
  async function reconcile(retries = 1) {
    if (busy || root.dataset.currentBillMode !== 'dine-in') return;
    busy = true;
    const button = $('r-dine-card-check');
    if (button) button.disabled = true;
    try {
      for (let attempt = 0; attempt < retries; attempt++) {
        status('Checking verified card payment…');
        try {
          const fd = new FormData();
          const response = await fetch(root.dataset.cardFinishUrl, {
            method: 'POST', body: fd, headers: headers(), credentials: 'same-origin'
          });
          const result = await readResult(response);
          status('Payment verified and bill closed. Receipt is ready to print.');
          window.RestaurantDineReceiptUrl = result.receiptUrl || '';
          emit('verified','Bill closed after verified payment.',result.receiptUrl || '');
          return;
        } catch (error) {
          if (!error.pending || attempt === retries - 1) {
            status(error.message || 'Unable to verify payment.', true);
            emit('pending',error.message || 'Unable to verify payment yet.');
            return;
          }
        }
        await sleep(1600);
      }
    } finally {
      busy = false;
      if (button) button.disabled = false;
    }
  }
  root.addEventListener('click', event => {
    const button = event.target.closest('[data-dine-gateway]');
    if (button && !button.disabled) {
      event.preventDefault();
      void start(button.dataset.dineGateway);
      return;
    }
    if (event.target.closest('#r-dine-card-check')) {
      event.preventDefault();
      void reconcile(2);
    }
  });
  window.addEventListener('message', event => {
    if (!chargeWindow || event.source !== chargeWindow || event.origin !== window.location.origin) return;
    const data = event.data || {};
    if (data.type === 'PDQ_PAYMENT_SAVED' && pendingMode === 'pdq' &&
        data.regId === `Resturent Bill_${root.querySelector('[name="BillNumber"]')?.value || ''}`) {
      // A genuine popup event only triggers a webhook-ledger check; it does not mark the bill paid.
      void reconcile(10);
    } else if (data.type === 'ORA_DINE_CARD_RETURN' && pendingMode === 'online') {
      if (data.cancelled) {
        status('Online checkout cancelled. Check payment before trying another charge.', true);
        emit('pending','Online checkout cancelled. Verify the existing attempt before retrying.');
      } else void reconcile(10);
    }
  });
})();
