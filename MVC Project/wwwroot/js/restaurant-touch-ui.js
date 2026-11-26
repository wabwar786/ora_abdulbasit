/* ORA PMS Restaurant unified checkout v4.
 * Single cashier flow for manual, PDQ and Online Card. Room charges post once
 * before collection; no receipt popup or automatic double-settlement.
 * Server-side tax, anti-forgery, card webhooks and folio records are authoritative.
 */
(() => {
  'use strict';
  const root = document.getElementById('restaurant-pos');
  const overlay = document.getElementById('r-touch-pay-dialog');
  if (!root || !overlay) return;
  const $ = id => document.getElementById(id);
  root.classList.add('r-unified-enabled');
  const payment = () => $('r-payment');
  const bill = () => root.querySelector('.r-checkout');
  const hasBill = () => !!root.querySelector('.r-classic-cart-card');
  const room = () => root.dataset.currentBillMode === 'room';
  const money = v => Number.isFinite(Number(v)) ? Number(v) : 0;
  const tabs = [...overlay.querySelectorAll('[data-checkout-tab]')];
  let activeTab = 'manual';
  let stage = 'edit'; // edit -> posting -> collect -> pending -> finished
  let busy = false, awaitingQuote = false, quoteValid = true;
  let checkoutRoom = false, restoredFocus = null;
  let counterType = 'counter', counterBill = '';
  let postedAmount = 0, postedBill = '';
  let verifyingPost = false;
  let tenderDue = null;
  const manualSelect = $('r-pay-manual-method');
  const methodGrid = $('r-manual-method-grid');
  function buildManualGrid() {
    methodGrid.replaceChildren();
    for (const option of manualSelect.options) {
      if (!option.value || option.disabled) continue;
      const button = document.createElement('button');
      button.type = 'button'; button.dataset.manualMethod = option.value;
      button.textContent = option.textContent; button.setAttribute('aria-pressed', 'false');
      methodGrid.append(button);
    }
    const manualCount = methodGrid.children.length;
    for (const tab of tabs.filter(x => x.dataset.checkoutTab !== 'manual')) {
      const button = document.createElement('button');
      button.type = 'button'; button.dataset.gatewayChoice = tab.dataset.checkoutTab;
      button.textContent = tab.dataset.checkoutTab === 'pdq' ? 'PDQ Terminal' : 'Online Card';
      button.setAttribute('aria-pressed', 'false');
      button.className = 'r-gateway-choice'; methodGrid.append(button);
    }
    $('r-manual-select-wrap').hidden = manualCount > 0;
    manualSelect.hidden = manualCount > 0;
  }
  buildManualGrid();
  // Cash tender is a cashier aid only; the existing server settles the exact bill total.
  function dueCents() {
    const value = ($('r-total')?.textContent || '').trim().match(/([0-9]+(?:\.[0-9]+)?)$/);
    return value ? Math.round(Number(value[1]) * 100) : null;
  }
  function tenderCents() {
    const input = $('r-cash-received');
    return input?.value !== '' && Number.isFinite(input?.valueAsNumber) && input.valueAsNumber >= 0
      ? Math.round(input.valueAsNumber * 100) : null;
  }
  function updateTender() {
    const box = $('r-cash-tender'), input = $('r-cash-received');
    const active = stage === 'edit' && !checkoutRoom && activeTab === 'manual' && /^cash$/i.test(manualSelect.value) && hasBill();
    box.hidden = !active;
    const due = dueCents();
    if (active && awaitingQuote) { box.hidden = true; return; }
    if (!active || due === null) { tenderDue = null; return; }
    if (tenderDue !== due) { input.value = (due / 100).toFixed(2); tenderDue = due; }
    const received = tenderCents();
    const change = received === null ? null : received - due;
    $('r-cash-change-label').textContent = change !== null && change < 0 ? 'Amount still due' : 'Change to return';
    $('r-cash-change').textContent = change === null ? '—' : `${$('r-currency')?.value || ''} ${(Math.abs(change) / 100).toFixed(2)}`;
    box.querySelector('.r-cash-change').classList.toggle('short', change === null || change < 0);
  }

  function tell(message, error = false) {
    const box = $('r-unified-notice');
    if (!box) return;
    box.hidden = !message;
    box.textContent = message || '';
    box.classList.toggle('error', error);
  }
  function result(title, description, kind = 'success') {
    const box = $('r-pay-result');
    if (!box) return;
    box.hidden = false;
    box.classList.toggle('pending', kind === 'pending');
    box.classList.toggle('error', kind === 'error');
    $('r-pay-result-title').textContent = title;
    $('r-pay-result-text').textContent = description;
  }
  const note = text => { const label = $('r-unified-payment-hint'); if (label) label.textContent = text; };
  const canUse = tab => {
    if (tab === 'manual') return true;
    if (stage === 'collect' || stage === 'pending' || stage === 'finished') {
      return tab === 'pdq' ? window.RestaurantRoomCheckout?.can('pdq') === true : window.RestaurantRoomCheckout?.can('online') === true;
    }
    if (checkoutRoom && stage === 'edit') return true; // Recheck the room's rights with the server after posting.
    return tab === 'pdq' ? overlay.querySelector('.r-unified-checkout')?.dataset.dinePdq === 'true'
      : overlay.querySelector('.r-unified-checkout')?.dataset.dineOnline === 'true';
  };
  function cardFields() {
    const method = $('r-pay-manual-method')?.value || '';
    const show = /card|visa|mastercard|amex|terminal|pdq/i.test(method);
    if ($('r-pay-card-fields')) $('r-pay-card-fields').hidden = !show || activeTab !== 'manual';
    $('r-manual-card-tip').hidden = !show || activeTab !== 'manual';
  }
  function setTab(tab) {
    if (!canUse(tab)) { tell(`${tab === 'pdq' ? 'PDQ' : 'Online Card'} is not enabled for this hotel or user.`, true); return; }
    activeTab = tab;
    tabs.forEach(btn => {
      const active = btn.dataset.checkoutTab === tab;
      btn.classList.toggle('active', active);
      btn.setAttribute('aria-selected', String(active));
      btn.tabIndex = active ? 0 : -1;
    });
    ['manual','pdq','online'].forEach(key => { const el = $(`r-pay-panel-${key}`); if (el) el.hidden = key !== tab; });
    cardFields();
    if (stage === 'edit' && !checkoutRoom && tab === 'manual') chooseMethod();
    else if (stage === 'edit' && !checkoutRoom && hasBill()) {
      // Existing verified gateway settlement uses the standard Cash tax basis.
      const cash = [...(payment()?.options || [])].find(o => /^cash$/i.test(o.value));
      if (cash) requestTax(cash.value);
      else updateActions(); // server's existing amount mismatch protection remains authoritative
    } else updateActions();
  }
  function updateSummary() {
    $('r-touch-pay-amount').textContent = stage === 'edit' ? (awaitingQuote ? 'Updating…' : !quoteValid ? '—' : $('r-total')?.textContent?.trim() || '—')
      : `${$('r-currency')?.value || '£'} ${money(postedAmount).toFixed(2)}`;
    $('r-touch-pay-number').textContent = stage === 'edit' ? bill()?.dataset.billNumber || '—' : postedBill;
    const summary = $('r-unified-breakdown');
    summary.textContent = stage === 'edit' && awaitingQuote ? 'Checking payment tax…' : stage === 'edit' && !quoteValid ? 'Tax quote unavailable. Select the payment method again.' : stage === 'edit' ? [
      `Subtotal ${$('r-currency')?.value || ''} ${money($('r-subtotal')?.value).toFixed(2)}`,
      `Discount ${$('r-discount-display')?.textContent?.trim() || '—'}`,
      `${$('r-tax-label')?.textContent?.trim() || 'Tax'} ${$('r-tax-amount')?.textContent?.trim() || '—'}`
    ].join(' · ') : '';
    updateTender();
  }
  function updateActions() {
    const confirm = $('r-unified-confirm'), folio = $('r-unified-folio-only');
    if (!confirm || !folio) return;
    const editing = stage === 'edit';
    const collecting = stage === 'collect';
    const blocked = busy || root.classList.contains('r-pos-loading') ||
      (editing && (awaitingQuote || !quoteValid));
    const tools = $('r-unified-editor');
    $('r-checkout-tools').hidden = !editing || tools.hidden;
    $('r-checkout-cancel').hidden = !editing;
    $('r-checkout-cancel').disabled = busy;
    methodGrid.querySelectorAll('button').forEach(btn => {
      const gateway = btn.dataset.gatewayChoice;
      btn.setAttribute('aria-pressed', String(gateway ? activeTab === gateway : activeTab === 'manual' && btn.dataset.manualMethod === manualSelect.value));
      btn.disabled = busy || (gateway ? !canUse(gateway) : manualSelect.disabled);
      btn.hidden = gateway ? !canUse(gateway) : false;
    });
    updateTender();
    if (editing && !tools.hidden) {
      const active = checkoutRoom ? 'room' : counterType;
      overlay.querySelectorAll('[data-order-type]').forEach(btn => {
        btn.setAttribute('aria-pressed', String(btn.dataset.orderType === active));
        btn.disabled = busy || root.classList.contains('r-pos-loading') || (!hasBill() && btn.dataset.orderType === 'room');
      });
      $('r-selected-room').hidden = !checkoutRoom;
      $('r-hold-btn').hidden = checkoutRoom;
      $('r-hold-btn').disabled = busy || !hasBill() || root.classList.contains('r-pos-loading');
      $('r-print-btn').disabled = busy || !hasBill() || root.classList.contains('r-pos-loading');
    }
    folio.hidden = !(editing && checkoutRoom);
    folio.disabled = blocked || !hasBill();
    const method = $('r-pay-manual-method')?.value || '';
    let label = 'Select payment method';
    if (editing && checkoutRoom) label = activeTab === 'manual' ? 'Post & Collect at Counter' : `Post & ${activeTab === 'pdq' ? 'Charge PDQ' : 'Charge Online'}`;
    else if (editing) label = activeTab === 'manual' ? 'Complete Payment' : `Charge ${activeTab === 'pdq' ? 'PDQ Terminal' : 'Online Card'}`;
    else if (collecting) label = activeTab === 'manual' ? 'Record Guest Payment' : activeTab === 'pdq' ? 'Charge PDQ Terminal' : 'Open Secure Card Checkout';
    confirm.textContent = busy ? 'Processing…' : label;
    confirm.hidden = !editing && !collecting;
    confirm.disabled = blocked || ((editing && !hasBill()) || (activeTab === 'manual' && !method));
    if (!$('r-cash-tender').hidden && (tenderCents() === null || dueCents() === null || tenderCents() < dueCents())) confirm.disabled = true;
    const hint = editing && checkoutRoom ? 'Post once to the guest folio, then collect payment.'
      : editing ? '' : collecting ? 'Collect against the posted folio charge.'
      : stage === 'pending' ? 'Do not charge again until this payment is reconciled.' : '';
    note(hint); $('r-unified-payment-hint').hidden = !hint;
  }
  function resetView() {
    delete root.dataset.needsRoomPostCheck;
    delete root.dataset.pendingPostCollect;
    stage = 'edit'; busy = false; verifyingPost = false; tenderDue = null;
    awaitingQuote = false; quoteValid = true; postedAmount = 0; postedBill = '';
    ['r-pay-result','r-pay-finished','r-pay-verify','r-room-collect-amount','r-pay-link'].forEach(id => { if ($(id)) $(id).hidden = true; });
    $('r-unified-editor').hidden = false;
    $('r-unified-payment-stage').hidden = false;
    $('r-unified-confirm').hidden = false;
    $('r-pay-manual-method').disabled = false;
    $('r-room-pay-amount').value = '';
    tell('');
  }
  function populate() { updateSummary(); }
  function updateOrderLabel() {
    $('r-unified-context').textContent = checkoutRoom ? 'Room Charge / Guest Folio' : counterType === 'takeaway' ? 'Takeaway' : 'Walk-in';
  }
  function open(button) {
    if (root.dataset.adjustmentUncertain === '1') { window.alert('The bill adjustment could not be confirmed. Refresh the POS before charging or assigning the bill.'); return; }
    if (!hasBill() || button?.disabled || root.classList.contains('r-pos-loading')) return;
    restoredFocus = button; checkoutRoom = room();
    resetView(); populate();
    if (counterBill !== bill()?.dataset.billNumber) { counterBill = bill()?.dataset.billNumber; counterType = 'counter'; }
    updateOrderLabel();
    const summary = root.querySelector('.r-guest-summary');
    const guest = summary?.querySelector('.r-guest-name')?.textContent?.trim() || '';
    const details = [...(summary?.querySelectorAll('.r-guest-summary-meta span') || [])].map(x => x.textContent.trim());
    $('r-unified-guest').textContent = '';
    $('r-selected-room-name').textContent = guest || 'In-house guest';
    $('r-selected-room-detail').textContent = details.join(' · ');
    overlay.classList.toggle('r-checkout-room-mode', checkoutRoom);
    overlay.classList.toggle('r-checkout-dine-mode', !checkoutRoom);
    overlay.hidden = false; document.body.classList.add('r-touch-pay-open');
    if (manualSelect.options.length > 1) {
      const current = [...manualSelect.options].find(o => o.value && o.value === payment()?.value);
      manualSelect.selectedIndex = current ? current.index : 1;
    }
    setTab('manual'); updateActions();
    if (!checkoutRoom && $('r-dine-card-pending') && !$('r-dine-card-pending').hidden) {
      stage = 'pending';
      result('Card payment already pending','Check the verified payment before starting a new charge.','pending');
      $('r-unified-editor').hidden = true;
      $('r-unified-payment-stage').hidden = true;
      $('r-pay-verify').hidden = false;
      updateActions();
    }
    $('r-pay-tab-manual')?.focus({ preventScroll: true });
  }
  function close() {
    if (busy || stage === 'posting') return;
    overlay.hidden = true; document.body.classList.remove('r-touch-pay-open');
    if (stage !== 'edit' && (checkoutRoom || stage === 'finished')) window.location.reload();
    else restoredFocus?.focus?.({ preventScroll: true });
  }
  function chooseMethod() {
    cardFields();
    if (checkoutRoom || stage !== 'edit' || !hasBill()) { updateActions(); return; }
    requestTax(manualSelect.value);
  }
  function requestTax(method) {
    const select = payment();
    if (!select || ![...select.options].some(opt => opt.value === method)) {
      awaitingQuote = false; quoteValid = false; updateSummary(); updateActions(); return;
    }
    select.value = method;
    // Always quote the current selection, including a return to the original
    // Cash method. The bill panel's initial quotedPayment is not live state.
    awaitingQuote = true; quoteValid = false;
    if ($('r-unified-notice').textContent === 'Cannot verify tax for this payment method.') tell('');
    updateSummary(); updateActions();
    select.dispatchEvent(new Event('change', { bubbles: true }));
  }
  function openRoomPicker(transferBill) {
    if (!window.RestaurantRoomPicker) { tell('Room selection is unavailable.', true); return; }
    overlay.hidden = true; document.body.classList.remove('r-touch-pay-open');
    window.RestaurantRoomPicker.open(transferBill, true, () => {
      overlay.hidden = false; document.body.classList.add('r-touch-pay-open');
      if (!checkoutRoom) setTab(activeTab); else updateActions();
      overlay.querySelector('[data-order-type="room"]')?.focus({preventScroll:true});
    });
  }
  function preflight() {
    if (busy || awaitingQuote || !quoteValid || root.dataset.adjustmentUncertain === '1') { tell('Wait for the current tax quote or refresh the bill.', true); return false; }
    const form = $('r-close-form');
    if (!hasBill() || !form?.reportValidity()) return false;
    if (activeTab === 'manual' && !$('r-pay-manual-method').value) { tell('Select a manual payment method.', true); return false; }
    if (activeTab === 'manual' && !$('r-pay-card-fields').hidden &&
        (!$('r-pay-card-type').value || !$('r-pay-card-ref').value.trim())) {
      tell('Select card type and enter the external terminal reference.', true); return false;
    }
    return true;
  }
  function submitRoom(collect) {
    if (busy || !hasBill()) return;
    if (collect && !preflight()) return;
    const form = $('r-close-form'); if (!form) return;
    postedAmount = money(($('r-total')?.textContent || '').replace(/[^\d.]/g,''));
    postedBill = bill()?.dataset.billNumber || '';
    root.dataset.deferRoomReceipt = '1';
    if (collect) { root.dataset.postAndCollect = '1'; root.dataset.postCollectMethod = activeTab; root.dataset.pendingPostCollect='1'; }
    else { delete root.dataset.postAndCollect; delete root.dataset.postCollectMethod; delete root.dataset.pendingPostCollect; }
    root.dataset.checkoutApproved = '1';
    busy = true; stage = 'posting'; tell('Posting the bill to guest folio…'); updateActions();
    form.requestSubmit();
  }
  function checkout() {
    if (!preflight() || $('r-unified-confirm').disabled) return;
    if (checkoutRoom) { submitRoom(true); return; }
    if (activeTab === 'manual') {
      const method = $('r-pay-manual-method').value;
      if (payment()) payment().value = method;
      if ($('r-dine-card-type')) $('r-dine-card-type').value = $('r-pay-card-fields').hidden ? '' : $('r-pay-card-type').value;
      if ($('r-dine-card-reference')) $('r-dine-card-reference').value = $('r-pay-card-fields').hidden ? '' : $('r-pay-card-ref').value.trim();
      root.dataset.checkoutApproved = '1';
      root.dataset.deferDineReceipt = '1';
      busy = true; stage = 'settling';
      tell('Recording payment and closing bill…'); updateActions();
      $('r-close-form').requestSubmit();
    } else {
      const gateway = $(activeTab === 'pdq' ? 'r-unified-dine-pdq' : 'r-unified-dine-online');
      if (!gateway) { tell('This payment type is not configured.',true); return; }
      // Keep the popup visible. Gateway will open a separate secure window,
      // then the existing server reconciliation will close the bill.
      // Display the preparing state BEFORE triggering the gateway. A synchronous
      // refusal (popup blocked, invalid bill) can then restore the editing state
      // without being immediately overwritten by the caller.
      stage = 'pending';
      result('Preparing card payment','The bill remains unpaid until the provider confirms it.','pending');
      $('r-unified-editor').hidden = true;
      $('r-unified-payment-stage').hidden = true;
      $('r-pay-verify').hidden = false;
      updateActions();
      gateway.click();
    }
  }
  function finish(title, detail, pending = false) {
    stage = pending ? 'pending' : 'finished'; busy = false;
    result(title, detail, pending ? 'pending' : 'success');
    $('r-unified-editor').hidden = true;
    $('r-unified-payment-stage').hidden = true;
    $('r-pay-verify').hidden = !pending;
    $('r-pay-finished').hidden = pending;
    $('r-unified-confirm').hidden = true;
    $('r-unified-folio-only').hidden = true;
    updateActions();
  }
  function payRoom() {
    if (busy || stage !== 'collect') return;
    if (!window.RestaurantRoomCheckout) { tell('Room payment service unavailable. The charge is already posted; do not post it again.',true); return; }
    const method = $('r-pay-manual-method').value;
    const amount = Number($('r-room-pay-amount').value);
    if (!Number.isFinite(amount) || amount <= 0 || amount > postedAmount + .005) { tell('Enter a valid amount, not exceeding this Restaurant charge.',true); return; }
    if (activeTab === 'manual' && !method) { tell('Choose a manual payment method.',true); return; }
    if (activeTab === 'manual' && !$('r-pay-card-fields').hidden && (!$('r-pay-card-type').value || !$('r-pay-card-ref').value.trim())) {
      tell('Card type and external transaction reference are required.',true); return;
    }
    busy = true; updateActions(); tell('');
    void window.RestaurantRoomCheckout.pay(activeTab,{amount,method,reference:$('r-pay-card-ref').value.trim(),cardType:$('r-pay-card-type').value,note:$('r-pay-manual-note').value.trim()}, {
      success: text => finish('Payment recorded',text),
      pending: text => finish('Payment awaiting confirmation',text,true),
      error: text => {busy=false; tell(text,true);updateActions();}
    });
  }
  async function checkRoomPosting({explicitRejection = false, message = ''} = {}) {
    if (verifyingPost || !postedBill) return;
    verifyingPost = true;
    tell('Checking Guest Services for this exact bill…');
    try {
      const url = new URL(root.dataset.roomPostStatusUrl,location.origin);
      url.searchParams.set('billNumber',postedBill);
      const res = await fetch(url,{credentials:'same-origin',cache:'no-store',headers:{Accept:'application/json'}});
      const data = await res.json();
      if(!res.ok || data.ok !== true) throw Error(data.message || `Status check failed (${res.status}).`);
      if(data.posted === true) {
        postedAmount = money(data.amount);
        // Server-confirmed posting; never submit the original bill again.
        window.RestaurantRoomReceipt = {
          bill: postedBill,
          print: () => window.open(data.receiptUrl,'_blank','noopener,noreferrer')
        };
        stage='posting'; // shared handler transitions to collect or posted
        root.dispatchEvent(new CustomEvent('restaurant:room-posted', {
          detail:{amount:postedAmount,billNumber:postedBill,
            collect:root.dataset.pendingPostCollect==='1'}
        }));
        delete root.dataset.pendingPostCollect;
        return;
      }
      if(explicitRejection) {
        // HTTP 4xx means this request was rejected; the authoritative DB check
        // shows no charge for this bill. Allow corrections to the same draft.
        stage='edit';busy=false;
        delete root.dataset.needsRoomPostCheck;
        delete root.dataset.pendingPostCollect;
        $('r-pay-result').hidden=true;
        $('r-unified-editor').hidden=false;
        $('r-unified-payment-stage').hidden=false;
        $('r-pay-verify').hidden=true;
        tell(message || 'The room charge was not posted. Correct the bill details and try again.',true);
      } else {
        finish('Posting not confirmed',
          `No Guest Services charge for bill #${postedBill} was found yet. The last request may still be processing. Refresh the folio before trying again. ${message}`.trim(),true);
        $('r-pay-verify').hidden=false;
      }
    } catch(error) {
      finish('Unable to confirm posting',
        `${error.message} Check Guest Services before attempting another charge. ${message}`.trim(),true);
      $('r-pay-verify').hidden=false;
    } finally { verifyingPost=false;busy=false;updateActions(); }
  }

  overlay.addEventListener('click', event => {
    const el = event.target instanceof Element ? event.target : null; if (!el) return;
    if (el.closest('[data-touch-pay-close]')) { event.preventDefault(); close(); return; }
    const methodButton = el.closest('[data-manual-method]');
    if (methodButton) {
      if (busy || manualSelect.disabled) return;
      manualSelect.value = methodButton.dataset.manualMethod;
      setTab('manual');
      return;
    }
    const gatewayChoice = el.closest('[data-gateway-choice]');
    if (gatewayChoice) { if (!busy && !gatewayChoice.disabled) setTab(gatewayChoice.dataset.gatewayChoice); return; }
    const cashButton = el.closest('[data-cash-exact], [data-cash-add]');
    if (cashButton) {
      if (busy || $('r-cash-tender').hidden) return;
      const cents = cashButton.hasAttribute('data-cash-exact') ? dueCents() : (tenderCents() ?? 0) + Number(cashButton.dataset.cashAdd) * 100;
      if (cents !== null) $('r-cash-received').value = (cents / 100).toFixed(2);
      updateActions(); return;
    }
    const tab = el.closest('[data-checkout-tab]');
    if (tab) { event.preventDefault(); setTab(tab.dataset.checkoutTab); return; }
    const orderType = el.closest('[data-order-type]');
    if (orderType) {
      if (busy || stage !== 'edit' || root.classList.contains('r-pos-loading')) return;
      const selected = orderType.dataset.orderType;
      if (selected === 'room' && !checkoutRoom) {
        if (!hasBill()) return;
        openRoomPicker(bill()?.dataset.billNumber || '');
      } else if (selected !== 'room' && checkoutRoom) {
        if (hasBill() && !window.confirm('This guest bill stays saved under the selected room. Open a separate counter bill?')) return;
        location.assign(root.dataset.restaurantHomeUrl || '/Restaurant/Home');
      } else if (!checkoutRoom) { counterType = selected; updateOrderLabel(); updateActions(); }
      return;
    }
    if (el.closest('#r-room-picker-open')) { if (!busy) openRoomPicker(''); return; }
    if (el.closest('#r-hold-btn')) { if (stage === 'edit' && !busy) close(); return; }
    if (el.closest('#r-unified-folio-only')) { submitRoom(false); return; }
    if (el.closest('#r-unified-confirm')) { stage === 'collect' ? payRoom() : checkout(); return; }
    if (el.closest('#r-pay-verify')) {
      if (checkoutRoom && root.dataset.needsRoomPostCheck === '1')
        { void checkRoomPosting(); return; }
      if (checkoutRoom) void window.RestaurantRoomCheckout?.verify({
        success: text=>finish('Payment reflected in folio',text),
        pending: text=>finish('Awaiting gateway confirmation',text,true),
        error: text=>tell(text,true)
      });
      else $('r-dine-card-check')?.click();
      return;
    }
    if (el.closest('#r-pay-print')) {
      if (checkoutRoom) window.RestaurantRoomReceipt?.print();
      else if (window.RestaurantDineReceipt?.print) window.RestaurantDineReceipt.print();
      else if (window.RestaurantDineReceiptUrl) window.open(window.RestaurantDineReceiptUrl,'_blank','noopener,noreferrer');
      return;
    }
    if (el.closest('#r-pay-done')) { close(); if (!checkoutRoom) window.location.reload(); return; }
    if (el.closest('#r-pay-link-copy')) { navigator.clipboard?.writeText($('r-pay-link-value')?.value||''); return; }
  });
  root.addEventListener('click', event => {
    const btn = event.target instanceof Element ? event.target.closest('#r-touch-pay-open') : null;
    if (btn) { event.preventDefault(); open(btn); }
  });
  $('r-pay-manual-method').addEventListener('change',chooseMethod);
  $('r-cash-received').addEventListener('input',updateActions);
  root.addEventListener('restaurant:pos-mutation-complete',event=>{
    if (overlay.hidden || stage !== 'edit') return;
    populate();
    if (!checkoutRoom && hasBill()) setTab(activeTab); else updateActions();
  });
  root.addEventListener('restaurant:pos-mutation-failed', event=>{
    if (overlay.hidden) return;
    const error=event.detail||{};
    if (stage==='posting') {
      // Never guess whether money was charged: inspect the exact bill on the
      // server and keep the original checkout disabled during reconciliation.
      busy=false;stage='pending';root.dataset.needsRoomPostCheck='1';
      delete root.dataset.postAndCollect;
      delete root.dataset.deferRoomReceipt;delete root.dataset.checkoutApproved;
      result('Checking Guest Services',error.message||'Verifying this bill before any retry.','pending');
      $('r-unified-editor').hidden=true;$('r-unified-payment-stage').hidden=true;
      $('r-unified-confirm').hidden=true;$('r-unified-folio-only').hidden=true;
      $('r-pay-verify').hidden=false;
      void checkRoomPosting({explicitRejection:error.explicitRejection,message:error.message});
      return;
    }
    if (stage==='settling') {
      busy=false;
      if(error.explicitRejection) {
        stage='edit';$('r-unified-editor').hidden=false;$('r-unified-payment-stage').hidden=false;
        tell(error.message||'Payment was not recorded.',true);
      } else {
        finish('Payment status uncertain',
          'The payment/close response is unclear. Check Payments Report and this bill before collecting again. '+(error.message||''),true);
      }
      updateActions();return;
    }
    busy=false;tell(error.message||'Request failed. Please check the bill status.',true);updateActions();
  });
  root.addEventListener('restaurant:tax-quote-ready',event=>{
    if (overlay.hidden || stage!=='edit' || checkoutRoom) return;
    if (event.detail?.paymentMethod!==payment()?.value) return;
    awaitingQuote=false; quoteValid=event.detail.ok!==false;
    if (!quoteValid) tell('Cannot verify tax for this payment method.',true);
    updateSummary(); updateActions();
  });
  root.addEventListener('restaurant:room-posted',event=>{
    if (overlay.hidden || stage!=='posting') return;
    delete root.dataset.needsRoomPostCheck;
    postedBill=event.detail?.billNumber||postedBill;
    postedAmount=money(event.detail?.amount||postedAmount);
    busy=false;
    delete root.dataset.pendingPostCollect;
    if (!event.detail?.collect) {
      finish('Posted to guest folio',`Bill #${postedBill} has been posted as an unpaid Services charge. You can print it now.`);
      return;
    }
    stage='collect';
    result('Folio charge saved',`Bill #${postedBill} posted. Now collect payment; it will not be posted twice.`);
    $('r-unified-editor').hidden = true;
    $('r-unified-payment-stage').hidden = false;
    $('r-room-collect-amount').hidden = false;
    $('r-room-pay-amount').value = postedAmount.toFixed(2);
    $('r-unified-folio-only').hidden = true;
    tell('');updateSummary();
    void window.RestaurantRoomCheckout?.begin(postedAmount).then(ctx=>{
      if (!ctx) return;
      const choices=$('r-pay-manual-method');choices.replaceChildren(new Option('Select payment method',''));
      (ctx.methods||[]).forEach(m=>choices.add(new Option(m.text,m.value)));
      if (choices.options.length>1) choices.selectedIndex=1;
      buildManualGrid(); // folio permissions can supply a different set of payment methods
      const currentTab=activeTab;setTab(canUse(currentTab)?currentTab:'manual');
      updateActions();
    }).catch(err=>{tell(err?.message||'Folio payment context unavailable. You can leave and collect later.',true); updateActions();});
  });
  root.addEventListener('restaurant:dine-bill-closed',event=>{
    if (overlay.hidden || checkoutRoom || stage!=='settling') return;
    finish('Payment saved — bill closed','Payment was recorded. Print the receipt or start a new bill.');
  });
  root.addEventListener('restaurant:dine-payment-state',event=>{
    if (overlay.hidden || checkoutRoom) return;
    const d=event.detail||{};
    if (d.status==='failed') {
      // The server explicitly refused to create a payment. Keep checkout open,
      // explain why, and allow the cashier to correct it (no bill was closed).
      stage='edit';busy=false;
      $('r-unified-editor').hidden=false;
      $('r-unified-payment-stage').hidden=false;
      $('r-pay-result').hidden=true;
      $('r-pay-verify').hidden=true;
      $('r-pay-finished').hidden=true;
      tell(d.message || 'PDQ payment could not start. Check the configured terminal.',true);
      updateActions();
    } else if (d.status==='verified') {
      window.RestaurantDineReceiptUrl=d.receiptUrl || '';
      finish('Payment verified — bill closed','You can print the receipt or start a new bill.');
    } else if(d.status==='pending') {
      stage='pending';finish('Waiting for payment confirmation',d.message||'Do not charge again until verified.',true);
    } else if(d.status==='error') {
      stage='pending';finish('Payment needs attention',d.message||'Check card transaction status before retrying.',true);
    }
  });
  root.addEventListener('restaurant:room-gateway-verified',event=>{
    if (!overlay.hidden && checkoutRoom && stage==='pending') finish('Folio balance updated',event.detail?.message || 'Payment reflected in folio.');
  });
  root.addEventListener('restaurant:room-gateway-pending',event=>{
    if (!overlay.hidden && checkoutRoom && stage==='pending') finish('Awaiting gateway reconciliation',event.detail?.message || 'Payment is not yet reflected.',true);
  });
  root.addEventListener('restaurant:pos-idle',()=>{if (!overlay.hidden) updateActions();});
  const resumeUrl = new URL(location.href);
  if (resumeUrl.searchParams.get('checkout') === '1') {
    resumeUrl.searchParams.delete('checkout');
    history.replaceState(history.state, '', resumeUrl.href);
    if (room() && hasBill()) open($('r-touch-pay-open'));
  }
  document.addEventListener('keydown',event=>{
    if (overlay.hidden || event.defaultPrevented) return;
    if(event.key==='Escape') {event.preventDefault();close();return;}
    if(event.key!=='Tab')return;
    const focusable=[...overlay.querySelectorAll('button:not(:disabled):not([hidden]), input:not(:disabled), select:not(:disabled)')]
      .filter(el=>el.getClientRects().length>0 && !el.closest('[hidden]'));
    if(!focusable.length)return;
    if(event.shiftKey && document.activeElement===focusable[0]){event.preventDefault();focusable.at(-1).focus();}
    else if(!event.shiftKey&&document.activeElement===focusable.at(-1)){event.preventDefault();focusable[0].focus();}
  });
})();
