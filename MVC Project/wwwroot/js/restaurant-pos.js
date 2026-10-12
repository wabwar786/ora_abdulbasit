/* ORA PMS Restaurant POS: direct card Add and asynchronous in-place bill updates.
   Existing RestaurantController endpoints and antiforgery validation are unchanged. */
(() => {
  'use strict';
  const root = document.getElementById('restaurant-pos');
  if (!root) return;

  const $ = id => document.getElementById(id);
  const toast = $('r-pos-status');
  let toastTimeout;
  let pending = Promise.resolve();
  let busyCount = 0;
  let taxRequest = 0;
  const quantityTimers = new Map();
  const quantityDelayMs = 280; // kept for compatibility with any older quantity input elsewhere
  let activeQtyForm = null;
  let activeDiscountMode = 'amount';
  const dialogs = ['r-qty-dialog', 'r-discount-dialog', 'r-tax-dialog'];
  function openDialog(id) {
    dialogs.forEach(x => { const el = $(x); if (el) el.hidden = x !== id; });
    document.body.classList.add('r-pos-has-dialog');
    $(id)?.querySelector('input:not([type="hidden"]), button:not([data-pos-dismiss])')?.focus({ preventScroll: true });
  }
  function closeDialog(id) {
    const el = $(id);
    if (el) el.hidden = true;
    if (!dialogs.some(x => $(x) && !$(x).hidden)) document.body.classList.remove('r-pos-has-dialog');
    if (id === 'r-qty-dialog') activeQtyForm = null;
  }
  document.addEventListener('click', event => {
    const dismiss = event.target instanceof Element ? event.target.closest('[data-pos-dismiss]') : null;
    if (dismiss) closeDialog(dismiss.dataset.posDismiss);
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      const open = dialogs.find(x => $(x) && !$(x).hidden);
      if (open) { event.preventDefault(); closeDialog(open); }
    }
  });

  const clamp = value => Math.max(1, Math.min(999, Number.isFinite(+value) ? Math.trunc(+value) : 1));
  const format = (value, currency) => `${currency} ${Number(value || 0).toFixed(2)}`;
  const money = id => Number($(id)?.value || 0);
  const htmlDocument = body => new DOMParser().parseFromString(body, 'text/html');

  function notify(text, isError = false, autoHide = true) {
    if (!toast) return;
    clearTimeout(toastTimeout);
    toast.textContent = text;
    toast.hidden = !text;
    toast.classList.toggle('error', !!isError);
    if (text && autoHide) toastTimeout = setTimeout(() => { toast.hidden = true; }, isError ? 7500 : 2600);
  }

  function setBusy() {
    root.classList.toggle('r-pos-loading', busyCount > 0);
    // Do not allow a checkout while an earlier Add/remove is awaiting confirmation.
    root.querySelectorAll('#r-close-form [type="submit"], #r-kitchen-btn, #r-hold-btn, #r-save-adjustment')
      .forEach(button => {
        if (busyCount > 0) {
          if (!button.disabled) { button.dataset.busyDisabled = '1'; button.disabled = true; }
        } else if (button.dataset.busyDisabled === '1') {
          button.disabled = false;
          delete button.dataset.busyDisabled;
        }
      });
  }

  function queue(action) {
    busyCount += 1;
    setBusy();
    const next = pending.catch(() => {}).then(action);
    pending = next.catch(() => {});
    next.finally(() => { busyCount -= 1; setBusy(); }).catch(() => {});
    return next;
  }

  function setFooterPosition() {
    const footer = root.querySelector('.r-bill-bottom');
    const panel = root.querySelector('.r-checkout');
    if (footer && panel) panel.style.setProperty('--r-bill-footer-height', `${footer.offsetHeight}px`);
  }

  function totals() {
    const discount = $('r-discount');
    const comp = $('r-complimentary');
    const subtotal = money('r-subtotal');
    const currency = $('r-currency')?.value || '';
    const rate = Number(root.dataset.currentTaxRate || money('r-tax-default'));
    const amount = Number(root.dataset.currentTaxAmount ?? (subtotal * rate / 100));
    const complimentary = !!comp?.checked;
    if (discount) discount.disabled = complimentary;
    const reduction = complimentary ? 0 : Math.max(0, Math.min(Number(discount?.value || 0) || 0, subtotal + amount));
    const tax = complimentary ? 0 : amount;
    const payable = complimentary ? 0 : Math.max(0, subtotal + tax - reduction);
    const taxName = root.dataset.currentTaxName || 'Tax';
    if ($('r-tax-label')) $('r-tax-label').textContent = `${taxName} (${rate.toFixed(2).replace(/\.00$/, '')}%)`;
    if ($('r-tax-amount')) $('r-tax-amount').textContent = format(tax, currency);
    if ($('r-discount-display')) $('r-discount-display').textContent = `− ${format(reduction, currency)}`;
    if ($('r-total')) $('r-total').textContent = format(payable, currency);
  }

  async function taxQuote() {
    const payment = $('r-payment');
    const request = ++taxRequest;
    const base = money('r-subtotal');
    const baseRate = money('r-tax-default');
    root.dataset.currentTaxRate = String(baseRate);
    root.dataset.currentTaxAmount = String(Number((base * baseRate / 100).toFixed(2)));
    root.dataset.currentTaxName = 'Tax';
    totals();
    if (!payment?.value || !root.dataset.taxUrl) return;
    try {
      const url = new URL(root.dataset.taxUrl, location.origin);
      url.searchParams.set('paymentMethod', payment.value);
      const response = await fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error('Tax quote unavailable');
      const json = await response.json();
      if (request !== taxRequest) return;
      root.dataset.currentTaxRate = String(Number(json.rate || 0));
      root.dataset.currentTaxAmount = String(Number(json.amount || 0));
      root.dataset.currentTaxName = json.name || 'Tax';
      totals();
    } catch (error) {
      if (request !== taxRequest) return;
      if ($('r-tax-label')) $('r-tax-label').textContent = 'Tax estimate unavailable';
      console.warn('Restaurant tax quote:', error);
    }
  }

  // Tax is already calculated by the server when it returns the bill panel.
  // Only request a second tax quote if the cashier chose a different method.
  function syncPanelTax() {
    const panel = root.querySelector('.r-checkout');
    if (!panel) return;
    ++taxRequest; // invalidate any in-flight quote for the previous bill
    root.dataset.currentTaxRate = panel.dataset.quotedTaxRate || '0';
    root.dataset.currentTaxAmount = panel.dataset.quotedTaxAmount || '0';
    root.dataset.currentTaxName = panel.dataset.quotedTaxName || 'Tax';
    totals();
    const selected = $('r-payment')?.value || 'Cash';
    if (selected.toLowerCase() !== (panel.dataset.quotedPayment || 'Cash').toLowerCase()) {
      void taxQuote();
    }
  }

  function renderBill(doc, action) {
    const fresh = doc.querySelector('#restaurant-pos .r-checkout') || doc.querySelector('.r-checkout');
    const existing = root.querySelector('.r-checkout');
    if (!fresh || !existing) throw new Error('Restaurant session expired or an unexpected response was returned. Please refresh the page.');

    const beforePayment = $('r-payment')?.value;
    const beforeDiscount = $('r-discount')?.value;
    const beforeComp = $('r-complimentary')?.checked;
    const previousScroll = root.querySelector('.r-cart-scroll')?.scrollTop || 0;
    const heldOpen = !['hold','resume','close'].includes(action) && !$('r-held-list')?.hidden;
    existing.replaceWith(document.importNode(fresh, true));
    // Keep unsaved form choices when adding or adjusting cart lines.
    if (action !== 'hold' && action !== 'adjustment' && action !== 'resume' && action !== 'close') {
      const freshDiscount = $('r-discount');
      const freshComp = $('r-complimentary');
      if (freshDiscount && beforeDiscount !== undefined) freshDiscount.value = beforeDiscount;
      if (freshComp && beforeComp !== undefined) freshComp.checked = beforeComp;
    }
    const freshPayment = $('r-payment');
    if (freshPayment && beforePayment && [...freshPayment.options].some(o => o.value === beforePayment)) freshPayment.value = beforePayment;
    const cart = root.querySelector('.r-cart-scroll');
    if (cart) cart.scrollTop = previousScroll;
    const heldList = $('r-held-list');
    if (heldList && heldOpen) {
      heldList.hidden = false;
      $('r-held-toggle')?.setAttribute('aria-expanded', 'true');
    }
    setFooterPosition();
    setBusy();
    syncPanelTax();
  }

  async function expandInlineVariants(form) {
    if (form.querySelector('[name="VariantBarcode"]')) return;
    try {
      const url = new URL(root.dataset.variantUrl, location.origin);
      url.searchParams.set('barcode', form.dataset.barcode || '');
      const response = await fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
      if (!response.ok) throw Error('Unable to load variation choices');
      const options = await response.json();
      if (!Array.isArray(options) || !options.length) return;
      const wrap = document.createElement('div');
      wrap.className = 'r-inline-choices';
      const label = document.createElement('label');
      label.textContent = 'Choose variation';
      const select = document.createElement('select');
      select.name = 'VariantBarcode';
      select.required = true;
      select.append(new Option('Select variation', ''));
      options.forEach(choice => select.add(new Option(`${choice.name} · ${Number(choice.price || 0).toFixed(2)}`, choice.barcode)));
      label.append(select);
      wrap.append(label);
      form.prepend(wrap);
      form.classList.add('r-has-variants');
      select.addEventListener('change', async () => {
        wrap.querySelector('[name="SideBarcode"]')?.parentElement?.remove();
        if (!select.value) return;
        const expected = select.value;
        try {
          const sideUrl = new URL(root.dataset.sideUrl, location.origin);
          sideUrl.searchParams.set('barcode', expected);
          const sideResponse = await fetch(sideUrl, { credentials: 'same-origin' });
          if (!sideResponse.ok) throw Error('Sides could not be loaded');
          const sides = await sideResponse.json();
          if (select.value !== expected || !Array.isArray(sides) || !sides.length) return;
          const sideLabel = document.createElement('label');
          sideLabel.textContent = 'Side (optional)';
          const sideSelect = document.createElement('select');
          sideSelect.name = 'SideBarcode';
          sideSelect.add(new Option('No side', ''));
          sides.forEach(choice => sideSelect.add(new Option(`${choice.name} · ${Number(choice.price || 0).toFixed(2)}`, choice.barcode)));
          sideLabel.append(sideSelect);
          wrap.append(sideLabel);
        } catch (error) { notify(error.message, true); }
      });
      notify('Please select a variation directly on the item card.', true);
      select.focus();
    } catch (error) { notify(error.message, true); }
  }

  function openPrintWindow(title) {
    // Opening synchronously with the user click avoids pop-up blockers after await.
    const win = window.open('', '_blank');
    if (!win) { notify('Allow pop-ups for this site to print the ticket or receipt.', true); return null; }
    win.opener = null; // Legacy print views must never force a full reload of the POS page.
    win.document.open();
    win.document.write('<!doctype html><title>Preparing print...</title><p style="font:14px Arial;padding:24px">Preparing ' + title + '…</p>');
    win.document.close();
    return win;
  }

  function writePrintWindow(win, markup) {
    if (!win || win.closed) return;
    win.document.open();
    win.document.write(markup);
    win.document.close();
  }

  // Queue a single quantity write after rapid +/- taps or number editing. A
  // subsequent kitchen/close/add action flushes unsaved edits first.
  function scheduleQuantitySave(form, immediate = false) {
    if (!form || !form.isConnected || form.dataset.posSubmitting === '1') return;
    const input = form.querySelector('input[name="Quantity"]');
    if (!input) return;
    const value = clamp(input.value);
    input.value = String(value);
    const rowId = form.querySelector('input[name="RowId"]')?.value;
    if (!rowId) return;
    const previous = quantityTimers.get(rowId);
    if (previous) clearTimeout(previous.timer);
    const entry = { form, timer: 0 };
    const send = () => {
      if (quantityTimers.get(rowId) !== entry) return;
      quantityTimers.delete(rowId);
      if (form.isConnected && form.dataset.posSubmitting !== '1') form.requestSubmit();
    };
    entry.timer = setTimeout(send, immediate ? 0 : quantityDelayMs);
    quantityTimers.set(rowId, entry);
  }

  function flushQuantitySaves() {
    for (const [rowId, entry] of quantityTimers) {
      clearTimeout(entry.timer);
      quantityTimers.delete(rowId);
      if (entry.form.isConnected && entry.form.dataset.posSubmitting !== '1') {
        entry.form.requestSubmit();
      }
    }
  }

  // Keep the label in the DOM for accessibility and restore it automatically;
  // CSS replaces its *visual* appearance with a small round spinner.
  function markActionLoading(button, loading) {
    if (!button) return;
    button.classList.toggle('r-action-spinning', loading);
    if (loading) {
      button.setAttribute('aria-busy', 'true');
      button.disabled = true;
    } else {
      button.removeAttribute('aria-busy');
      button.disabled = false;
    }
  }

  // Cart POSTs now return only the bill panel; no Home redirect and no menu reload.
  // Kitchen and close actions return dedicated printable documents.
  async function submitMutation(form, kind) {
    if (form.dataset.posSubmitting === '1') return;
    if (kind !== 'update') flushQuantitySaves();
    if (!form.reportValidity()) return;
    const payload = new FormData(form); // capture the requested qty/discount at click time
    const button = form.querySelector('[type="submit"]');
    const printKind = ['kitchen', 'close', 'cancel'].includes(kind);
    const printWindow = printKind ? openPrintWindow(kind === 'close' ? 'receipt' : 'kitchen ticket') : null;
    if (printKind && !printWindow) return;
    form.dataset.posSubmitting = '1';
    const subtleAction = kind === 'add' || kind === 'remove' || kind === 'update';
    if (button) markActionLoading(button, true);
    if (kind === 'update') {
      form.classList.add('r-qty-saving');
      form.querySelectorAll('button, input[name="Quantity"]').forEach(el => { el.disabled = true; });
    }
    await queue(async () => {
      if (!subtleAction) notify('Processing…', false, false);
      let printCompleted = false;
      try {
        const response = await fetch(form.action, {
          method: 'POST', body: payload, credentials: 'same-origin',
          headers: { 'X-Requested-With': 'XMLHttpRequest', Accept: 'text/html' }
        });
        const contentType = response.headers.get('content-type') || '';
        if (contentType.includes('application/json')) {
          const result = await response.json();
          if (result.committed) {
            notify(result.message || 'Operation saved. Please refresh the bill panel.', false, false);
            void refreshBillAfterPrint(kind);
            return;
          }
          throw new Error(result.message || 'The operation could not be completed.');
        }
        const html = await response.text();
        const doc = htmlDocument(html);
        if (printKind) {
          if (!response.ok || !doc.querySelector('.print-screen')) {
            printWindow?.close();
            throw new Error(doc.querySelector('.print-error, .restaurant-alert.error, main')?.textContent?.trim().slice(0, 240) || 'The operation was not completed.');
          }
          writePrintWindow(printWindow, html);
          printCompleted = true;
          if (kind === 'cancel') $('r-cancel-dialog').hidden = true;
          // Payment/kitchen mutation is committed at this point. Never say it failed
          // or automatically repeat it if the subsequent bill refresh fails.
          try {
            const refresh = await fetch(root.dataset.billPanelUrl, {
              credentials: 'same-origin', cache: 'no-store',
              headers: { 'X-Requested-With': 'XMLHttpRequest', Accept: 'text/html' }
            });
            if (!refresh.ok) throw new Error('Unable to refresh POS');
            renderBill(htmlDocument(await refresh.text()), kind);
            notify(kind === 'close' ? 'Bill completed. Receipt is ready to print.' : 'Kitchen ticket is ready to print.');
          } catch (refreshError) {
            notify('Operation completed and printout is ready. Refresh the POS to update the bill.', true, false);
            console.warn('POS post-print refresh:', refreshError);
          }
          return;
        }
        if (!response.ok) throw new Error('The server could not process this request. Please try again.');
        if (!doc.querySelector('.r-checkout')) {
          throw new Error('Your hotel session has expired or the POS returned an unexpected page. Refresh and sign in again.');
        }
        renderBill(doc, kind);
        if (kind === 'update') closeDialog('r-qty-dialog');
        if (kind === 'adjustment') closeDialog('r-discount-dialog');
        if (kind === 'tax') closeDialog('r-tax-dialog');
        if (kind === 'add') { const qty = form.querySelector('.r-product-qty'); if (qty) qty.value = '1'; }
        const captions = { add: 'Item added to bill.', remove: 'Item removed.',
          update: 'Quantity updated.', hold: 'Bill placed on hold.',
          resume: 'Bill resumed.', adjustment: 'Discount saved.', tax: 'Tax rate updated.' };
        if (subtleAction) notify('');
        else notify(captions[kind] || 'Saved.');
      } catch (error) {
        if (!printCompleted && printWindow && !printWindow.closed) printWindow.close();
        const message = error?.message || 'Request failed. Check your connection and retry.';
        notify(message, true, false);
        if (kind === 'add' && /select a variation/i.test(message)) void expandInlineVariants(form);
        // Never retry a POST automatically: it might already have reached the server.
      }
    });
    delete form.dataset.posSubmitting;
    if (button && button.isConnected) markActionLoading(button, false);
    if (kind === 'update' && form.isConnected) {
      form.classList.remove('r-qty-saving');
      form.querySelectorAll('button, input[name="Quantity"]').forEach(el => { el.disabled = false; });
    }
  }

  function populateAdjustment(form) {
    form.querySelector('[name="discount"]').value = $('r-complimentary')?.checked ? '0' : ($('r-discount')?.value || '0');
    form.querySelector('[name="complimentary"]').value = $('r-complimentary')?.checked ? 'true' : 'false';
  }

  document.addEventListener('submit', event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const kind = form.dataset.posAsync;
    if (!kind) return;
    event.preventDefault();
    // Draft items can be removed directly; sent-to-kitchen cancellations still confirm.
    if (kind === 'cancel' && !confirm('Cancel the item and create a kitchen cancellation ticket?')) return;
    if (kind === 'close' && !window.restaurantConfirmClose(form)) return;
    void submitMutation(form, kind);
  });

  root.addEventListener('click', event => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    // Clicking the product image uses the same direct-add form and anti-forgery token.
    const imageAdd = target.closest('.r-product-photo-add');
    if (imageAdd) {
      const addForm = imageAdd.closest('.r-product')?.querySelector('.r-product-add-form');
      if (addForm && !addForm.dataset.posSubmitting) addForm.requestSubmit();
      return;
    }
    // Quantity editing belongs in the calculator popup, not inline on the cart.
    const cartEditor = target.closest('.r-cart-qty-edit');
    if (cartEditor) {
      const rowId = cartEditor.dataset.row;
      activeQtyForm = [...root.querySelectorAll('form.r-modern-hidden-quantity')]
        .find(f => f.querySelector('[name="RowId"]')?.value === rowId) || null;
      if (!activeQtyForm) return;
      $('r-qty-item-name').textContent = cartEditor.dataset.item || 'Item';
      $('r-qty-display').value = String(clamp(cartEditor.dataset.quantity));
      openDialog('r-qty-dialog');
      return;
    }
    const cartCard = target.closest('.r-classic-cart-card.r-cart-editable');
    if (cartCard && !target.closest('.r-classic-remove, form')) {
      cartCard.querySelector('.r-cart-qty-edit')?.click();
      return;
    }
    if (target.closest('[data-empty-reset]')) {
      const search = root.querySelector('.r-classic-search input[type=search]');
      if (search) search.value = '';
      root.querySelector('.r-menu-categories a')?.click();
      return;
    }
    if (target.closest('#r-edit-discount')) { openDiscountEditor(); return; }
    if (target.closest('#r-edit-tax')) { openTaxEditor(); return; }
    const control = target.closest('button');
    if (!control) return;
    const cartStepper = control.closest('.r-cart-qty-stepper');
    if (cartStepper && control.hasAttribute('data-cart-step')) {
      const form = cartStepper.closest('form[data-pos-async="update"]');
      const input = cartStepper.querySelector('input[name="Quantity"]');
      if (input && form) {
        const current = clamp(input.value);
        const updated = clamp(current + Number(control.dataset.cartStep || 0));
        if (current !== updated) { input.value = String(updated); scheduleQuantitySave(form); }
      }
      return;
    }
    const stepper = control.closest('.r-classic-stepper');
    if (stepper && (control.matches('.r-product-step-up') || control.matches('.r-product-step-down'))) {
      const qty = stepper.querySelector('.r-product-qty');
      if (qty) qty.value = clamp(Number(qty.value) + (control.matches('.r-product-step-up') ? 1 : -1));
    }
    if (control.hasAttribute('data-held-close')) {
      const modal = $('r-held-list');
      if (modal) modal.hidden = true;
      $('r-held-toggle')?.setAttribute('aria-expanded', 'false');
      return;
    }
    if (control.id === 'r-held-toggle') {
      const list = $('r-held-list');
      if (list) { list.hidden = !list.hidden; control.setAttribute('aria-expanded', String(!list.hidden)); setFooterPosition(); }
    }
    if (control.id === 'r-hold-btn' && confirm('Place this bill on hold?')) {
      const form = $('r-hold-form'); populateAdjustment(form); form.requestSubmit();
    }
    if (control.id === 'r-save-adjustment') {
      const form = $('r-save-form'); populateAdjustment(form); form.requestSubmit();
    }
    if (control.id === 'r-kitchen-btn' && confirm('Send all pending items to kitchen and prepare a kitchen ticket?')) {
      $('r-kitchen-form').requestSubmit();
    }
    if (control.id === 'r-print-btn') {
      const url = new URL(root.dataset.previewUrl, location.origin);
      if ($('r-payment')?.value) url.searchParams.set('paymentMethod', $('r-payment').value);
      if ($('r-discount')?.value) url.searchParams.set('discount', $('r-discount').value);
      url.searchParams.set('complimentary', $('r-complimentary')?.checked ? 'true' : 'false');
      const win = window.open(url.toString(), '_blank');
      if (win) win.opener = null;
      else notify('Allow pop-ups for this site to print the bill.', true);
    }
    const cancel = control.closest('.r-cancel-kitchen');
    if (cancel) {
      $('r-cancel-row').value = cancel.dataset.row || '';
      $('r-cancel-item').value = cancel.dataset.item || '';
      $('r-cancel-reason').value = '';
      $('r-cancel-dialog').hidden = false;
      $('r-cancel-reason').focus();
    }
  });

  root.addEventListener('input', event => {
    if (!(event.target instanceof Element)) return;
    if (event.target.matches('#r-discount')) totals();
    if (event.target.matches('.r-cart-qty-stepper input[name="Quantity"]')) {
      const form = event.target.closest('form[data-pos-async="update"]');
      if (form && event.target.value !== '') scheduleQuantitySave(form);
    }
  });
  root.addEventListener('change', event => {
    if (!(event.target instanceof Element)) return;
    if (event.target.matches('#r-payment')) void taxQuote();
    if (event.target.matches('#r-complimentary')) { totals(); setFooterPosition(); }
    if (event.target.matches('.r-product-qty')) event.target.value = clamp(event.target.value);
    if (event.target.matches('.r-cart-qty-stepper input[name="Quantity"]')) {
      const form = event.target.closest('form[data-pos-async="update"]');
      if (form) scheduleQuantitySave(form, true);
    }
  });

  // Calculator keypad (1–999); changes are committed only when Apply is clicked.
  function setQtyDisplay(value) { $('r-qty-display').value = String(Math.max(0, Math.min(999, Number(value) || 0))); }
  $('r-qty-dialog')?.addEventListener('click', event => {
    const key = event.target instanceof Element ? event.target.closest('[data-digit]') : null;
    if (!key) return;
    const current = $('r-qty-display').value;
    const digit = key.dataset.digit;
    if (digit === 'C') setQtyDisplay(0);
    else if (digit === '⌫') setQtyDisplay(current.slice(0, -1));
    else setQtyDisplay(Number((current === '0' ? '' : current) + digit));
  });
  $('r-qty-apply')?.addEventListener('click', () => {
    if (!activeQtyForm) return;
    const quantity = Number($('r-qty-display').value);
    if (!Number.isInteger(quantity) || quantity < 1 || quantity > 999) { notify('Select a quantity from 1 to 999.', true); return; }
    activeQtyForm.querySelector('[name="Quantity"]').value = String(quantity);
    activeQtyForm.requestSubmit();
  });
  document.addEventListener('keydown', event => {
    if ($('r-qty-dialog')?.hidden !== false) return;
    if (/^[0-9]$/.test(event.key)) {
      event.preventDefault();
      const current = $('r-qty-display').value;
      setQtyDisplay(Number((current === '0' ? '' : current) + event.key));
    } else if (event.key === 'Backspace') {
      event.preventDefault(); setQtyDisplay($('r-qty-display').value.slice(0, -1));
    } else if (event.key === 'Enter') { event.preventDefault(); $('r-qty-apply').click(); }
  });

  function openDiscountEditor() {
    if (!$('r-discount') || $('r-edit-discount').disabled) return;
    activeDiscountMode = 'amount';
    $('r-discount-editor').value = Number($('r-discount').value || 0).toFixed(2);
    refreshDiscountPreview();
    openDialog('r-discount-dialog');
  }
  function refreshDiscountPreview() {
    const raw = Number($('r-discount-editor')?.value || 0);
    const subtotal = money('r-subtotal');
    const selectedAmount = activeDiscountMode === 'percent' ? subtotal * raw / 100 : raw;
    const currency = $('r-currency')?.value || '';
    const display = Number.isFinite(selectedAmount) ? Math.max(0, selectedAmount) : 0;
    if ($('r-discount-preview')) $('r-discount-preview').textContent = `Discount on bill: ${format(display, currency)}`;
    document.querySelectorAll('[data-discount-mode]').forEach(btn => {
      btn.classList.toggle('active', btn.dataset.discountMode === activeDiscountMode);
      btn.setAttribute('aria-pressed', String(btn.dataset.discountMode === activeDiscountMode));
    });
    const editor = $('r-discount-editor');
    if (editor) { editor.max = activeDiscountMode === 'percent' ? '100' : String(Math.max(0, subtotal + Number(root.dataset.currentTaxAmount || 0))); }
  }
  $('r-discount-dialog')?.addEventListener('click', event => {
    const btn = event.target instanceof Element ? event.target.closest('[data-discount-mode]') : null;
    if (!btn) return;
    const current = Number($('r-discount-editor').value || 0);
    const subtotal = money('r-subtotal');
    const amount = activeDiscountMode === 'percent' ? subtotal * current / 100 : current;
    activeDiscountMode = btn.dataset.discountMode;
    $('r-discount-editor').value = activeDiscountMode === 'percent'
      ? (subtotal > 0 ? (amount / subtotal * 100).toFixed(2) : '0') : amount.toFixed(2);
    refreshDiscountPreview();
  });
  $('r-discount-editor')?.addEventListener('input', refreshDiscountPreview);
  $('r-discount-apply')?.addEventListener('click', () => {
    const editor = $('r-discount-editor');
    if (!editor || !editor.reportValidity()) return;
    const raw = Number(editor.value);
    if (!Number.isFinite(raw) || raw < 0 || (activeDiscountMode === 'percent' && raw > 100)) {
      notify('Enter a valid discount.', true); return;
    }
    const amount = activeDiscountMode === 'percent' ? money('r-subtotal') * raw / 100 : raw;
    const max = money('r-subtotal') + Number(root.dataset.currentTaxAmount || 0);
    if (amount > max) { notify('Discount cannot exceed the bill including tax.', true); return; }
    $('r-discount').value = amount.toFixed(2);
    totals();
    const save = $('r-save-form');
    if (save) { populateAdjustment(save); save.requestSubmit(); }
  });

  function openTaxEditor() {
    if (!$('r-tax-form') || $('r-edit-tax')?.disabled) return;
    $('r-tax-form')?.querySelector('[name="useDefault"]')?.remove();
    $('r-tax-bill').value = root.querySelector('.r-checkout')?.dataset.billNumber || '';
    $('r-tax-edit-rate').value = Number(root.dataset.currentTaxRate || money('r-tax-default')).toFixed(2);
    updateTaxPreview();
    openDialog('r-tax-dialog');
  }
  function updateTaxPreview() {
    const rate = Number($('r-tax-edit-rate')?.value || 0);
    $('r-tax-preview').textContent = `Estimated tax: ${format(money('r-subtotal') * rate / 100, $('r-currency')?.value || '')}`;
  }
  $('r-tax-edit-rate')?.addEventListener('input', updateTaxPreview);
  $('r-tax-reset')?.addEventListener('click', () => {
    const form = $('r-tax-form');
    if (!form) return;
    const existing = form.querySelector('[name="useDefault"]');
    if (existing) existing.remove();
    const flag = document.createElement('input'); flag.type='hidden'; flag.name='useDefault'; flag.value='true';
    form.append(flag); form.querySelector('#r-tax-edit-rate').disabled=true;
    form.requestSubmit();
    form.querySelector('#r-tax-edit-rate').disabled=false;
  });
  $('r-tax-form')?.addEventListener('submit', () => {
    const flag = $('r-tax-form').querySelector('[name="useDefault"]');
    if (flag && flag.value !== 'true') flag.remove();
  });

  const cancelDialog = $('r-cancel-dialog');
  cancelDialog?.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => { cancelDialog.hidden = true; }));

  window.restaurantConfirmClose = () => {
    const roomMode = new URL($('r-close-form')?.action || '', location.origin).pathname.toLowerCase().endsWith('/posttoroom');
    if (!roomMode && !$('r-complimentary')?.checked && !$('r-payment')?.value) { notify('Select a payment method.', true); return false; }
    if (!Number.isFinite(+$('r-discount')?.value) || +$('r-discount')?.value < 0) { notify('Enter a valid discount.', true); return false; }
    return confirm(roomMode ? `Post ${$('r-total')?.textContent} to this guest's Services as an unpaid room charge?` : `Confirm payment of ${$('r-total')?.textContent} and close this bill? This cannot be undone.`);
  };

  // Menu cards are already loaded on the first page. Filter locally instead
  // of fetching Home and repeating all POS SQL reads on every keystroke.
  const searchForm = root.querySelector('.r-classic-search');
  const searchInput = searchForm?.querySelector('[name="search"]');
  const productCards = [...root.querySelectorAll('.r-product-grid .r-product')];
  const existingEmpty = root.querySelector('.r-product-grid > .r-pos-empty');
  const emptyNote = existingEmpty || document.createElement('div');
  if (!existingEmpty) {
    emptyNote.className = 'restaurant-empty r-local-empty r-pos-empty';
    emptyNote.innerHTML = '<div class="r-pos-empty-icon" aria-hidden="true">⌕</div><h3>No menu items found</h3><p>Try another search term or select a category.</p><button type="button" class="r-pos-empty-reset" data-empty-reset>Clear filters</button>';
    root.querySelector('.r-product-grid')?.append(emptyNote);
  }
  emptyNote.hidden = true;
  let currentCategory = new URL(location.href).searchParams.get('category') || '';

  function filterMenu(writeHistory = false) {
    const term = (searchInput?.value || '').trim().toLocaleLowerCase();
    let visible = 0;
    for (const card of productCards) {
      const matchesCategory = !currentCategory || card.dataset.category === currentCategory;
      const matchesSearch = !term || (card.dataset.search || '').includes(term);
      card.hidden = !(matchesCategory && matchesSearch);
      if (!card.hidden) visible++;
    }
    emptyNote.hidden = visible > 0;
    root.querySelectorAll('.r-menu-categories a').forEach(link => {
      const category = new URL(link.href, location.origin).searchParams.get('category') || '';
      const isActive = category === currentCategory;
      link.classList.toggle('active', isActive);
      if (isActive) link.setAttribute('aria-current', 'page');
      else link.removeAttribute('aria-current');
    });
    const hiddenCategory = searchForm?.querySelector('input[name="category"]');
    if (hiddenCategory) hiddenCategory.value = currentCategory;
    else if (currentCategory && searchForm) {
      const input = document.createElement('input');
      input.type = 'hidden'; input.name = 'category'; input.value = currentCategory;
      searchForm.prepend(input);
    }
    if (writeHistory) {
      const url = new URL(location.href);
      if (currentCategory) url.searchParams.set('category', currentCategory);
      else url.searchParams.delete('category');
      if (term) url.searchParams.set('search', searchInput.value.trim());
      else url.searchParams.delete('search');
      history.replaceState({}, '', url);
    }
  }
  root.addEventListener('click', event => {
    const link = event.target instanceof Element ? event.target.closest('.r-menu-categories a') : null;
    if (!link || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.button !== 0) return;
    event.preventDefault();
    currentCategory = new URL(link.href, location.origin).searchParams.get('category') || '';
    filterMenu(true);
  });
  searchForm?.addEventListener('submit', event => { event.preventDefault(); filterMenu(true); });
  let filterFrame = 0;
  searchInput?.addEventListener('input', () => {
    cancelAnimationFrame(filterFrame);
    filterFrame = requestAnimationFrame(() => filterMenu(true));
  });
  window.addEventListener('popstate', () => {
    const url = new URL(location.href);
    currentCategory = url.searchParams.get('category') || '';
    if (searchInput) searchInput.value = url.searchParams.get('search') || '';
    filterMenu(false);
  });
  filterMenu(false);

  async function refreshBillAfterPrint(kind) {
    try {
      const response = await fetch(root.dataset.billPanelUrl, {credentials: 'same-origin',cache:'no-store'});
      if (!response.ok) throw new Error('Bill refresh unavailable');
      renderBill(htmlDocument(await response.text()), kind);
    } catch (error) {console.warn('POS bill refresh:', error);}
  }
  window.addEventListener('resize', setFooterPosition, { passive: true });
  setFooterPosition();
  syncPanelTax();
})();
