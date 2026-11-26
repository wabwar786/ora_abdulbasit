/* ORA PMS Restaurant / guest folio payments.
   Reuses the exact Calendar RecordPayment modal and its endpoints.
   Restaurant sales are NOT marked paid until the existing folio payment service
   or the Stripe/PDQ webhook confirms collection. */
(() => {
  'use strict';
  const root = document.getElementById('restaurant-pos');
  if (!root) return;
  const $ = id => document.getElementById(id);
  const messages = $('r-pos-status');
  function notify(message, error = false) {
    if (!messages) return;
    messages.textContent = message;
    messages.hidden = false;
    messages.classList.toggle('error', error);
  }
  const hasDraft = () => !!root.querySelector('.r-classic-cart-card');
  let loading = false;
  async function openExistingFolio(preferred = '', postedAmount = null, defaultMethod = 'Cash') {
    if (loading) return;
    if (root.dataset.currentBillMode !== 'room') {
      notify('Select Room Charge from Bill Type, then choose a checked-in guest.', true);
      return;
    }
    if (root.classList.contains('r-pos-loading')) {
      notify('Finish the current Restaurant operation before taking payment.', true);
      return;
    }
    // A completed Post & Collect may leave an old cart visible if the
    // post-save bill-panel refresh fails. The server has already committed
    // that charge, so rely on the fresh folio balance below instead.
    if (hasDraft() && postedAmount == null) {
      notify('Post the current Restaurant bill first, or choose Post & Collect Payment.', true);
      return;
    }
    if (!window.RecordPayment || typeof window.RecordPayment.open !== 'function') {
      notify('Shared Record Payment component is not available. Refresh the page.', true);
      return;
    }
    loading = true;
    try {
      const response = await fetch(root.dataset.roomPaymentContextUrl, {
        credentials: 'same-origin', cache: 'no-store',
        headers: { 'Accept': 'application/json' }
      });
      const data = await response.json().catch(() => null);
      if (!response.ok || !data) throw new Error(data?.message || 'Unable to load guest folio.');
      if (Number(data.balance) <= 0.005) {
        notify('The guest folio has no outstanding balance to collect.', true);
        return;
      }
      if (postedAmount != null && preferred === 'pdq' && !data.showPdqPayment) {
        preferred = data.showOnlineCard ? 'online' : '';
      }
      if (preferred === 'pdq' && !data.showPdqPayment) {
        notify('PDQ is not configured for this property. Choose Record Payment instead.', true);
        return;
      }
      if (preferred === 'online' && !data.showOnlineCard) {
        notify('Online Card is not configured for this property.', true);
        return;
      }
      window.RecordPayment.open({
        regId: data.regId, visitId: data.visitId, roomNo: data.roomNo,
        guestName: data.guestName, balance: Number(data.balance),
        currencySymbol: data.currencySymbol || '£',
        currencyCode: data.currencyCode || 'GBP',
        hotelId: data.hotelId, userId: data.userId,
        source: data.source || 'GI',
        showPdqPayment: data.showPdqPayment === true,
        showOnlineCard: data.showOnlineCard === true,
        showAutoPay: false,
        preferredMethod: preferred, defaultMethod,
        urls: {
          recordPaymentUrl: '/CheckIn/RecordPayment',
          pdqUrl: '/TerminalCardPayment.aspx'
        },
        onCompleted: () => {
          // Payment provider may commit by webhook after this callback.
          // Reload from the server; never update totals optimistically.
          window.location.reload();
        }
      });
      // The existing guest folio may contain older room charges. When the
      // payment follows a newly posted Restaurant bill, prefill only that
      // amount (capped by the verified total outstanding) instead of charging
      // every room balance without the cashier explicitly choosing it.
      if (Number.isFinite(postedAmount) && postedAmount > 0) {
        const input = $('orpPaymentAmount');
        if (input) input.value = Math.min(Number(data.balance), postedAmount).toFixed(2);
      }
    } catch (error) {
      notify(error?.message || 'Unable to open guest payments.', true);
    } finally {
      loading = false;
    }
  }
  root.addEventListener('click', event => {
    const button = event.target.closest('[data-guest-payment]');
    if (button) {
      event.preventDefault();
      if (!button.disabled) void openExistingFolio(button.dataset.guestPayment || '');
      return;
    }
    if (!event.target.closest('#r-post-and-collect')) return;
    event.preventDefault();
    if (root.classList.contains('r-pos-loading')) return;
    const form = $('r-close-form');
    if (!form || !hasDraft() || root.dataset.currentBillMode !== 'room') return;
    root.dataset.postAndCollect = '1';
    form.requestSubmit(); // the existing POS posting/print workflow and validation
  });
  window.RestaurantGuestPayment = {
    openAfterPosting: (amount, choice = 'pdq') => {
      const preferred = choice === 'pdq' || choice === 'online' ? choice : '';
      const method = choice.startsWith('manual:') ? choice.slice(7) : 'Cash';
      return openExistingFolio(preferred, amount, method);
    }
  };
})();
