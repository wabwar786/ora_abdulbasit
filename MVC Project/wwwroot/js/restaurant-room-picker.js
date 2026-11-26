/* ORA PMS: additional POS room picker. Existing Calendar -> Restaurant flow is unchanged. */
(() => {
  'use strict';
  const root = document.getElementById('restaurant-pos');
  const modal = document.getElementById('r-room-picker-dialog');
  const roomList = document.getElementById('r-room-picker-list');
  const search = document.getElementById('r-room-picker-search');
  const status = document.getElementById('r-room-picker-status');
  const form = document.getElementById('r-room-picker-form');
  const regField = document.getElementById('r-room-picker-reg');
  const roomField = document.getElementById('r-room-picker-number');
  if (!root || !modal || !roomList || !form) return;

  const transferField = document.getElementById('r-room-picker-transfer');
  const continueField = document.getElementById('r-room-picker-continue');
  let rooms = [];
  let loading = false;
  let selecting = false;
  let previousFocus = null, onBack = null, selectedRoom = null, lookupVersion = 0;
  const confirmButton = document.getElementById('r-room-picker-confirm');
  const choice = document.getElementById('r-room-picker-choice');
  function updateChoice() {
    confirmButton.disabled = !selectedRoom || selecting || loading;
    choice.textContent = selectedRoom ? `Room ${selectedRoom.roomNo} · ${selectedRoom.guestName || 'Guest'}` : 'Select a room to continue.';
    roomList.querySelectorAll('button').forEach(button => {
      const active = selectedRoom && button.dataset.reg === String(selectedRoom.regId) && button.dataset.room === String(selectedRoom.roomNo);
      button.classList.toggle('is-selected', !!active);
      button.setAttribute('aria-selected', String(!!active));
    });
  }
  const hasBill = () => !!root.querySelector('.r-classic-cart-card');

  function setStatus(message, isError = false) {
    status.textContent = message;
    status.classList.toggle('is-error', isError);
    status.hidden = !message;
  }
  async function chooseRoom(room) {
    if (selecting || root.classList.contains('r-pos-loading')) return;
    // Continue is the explicit confirmation; no additional browser dialog.
    if (!room) return;
    selecting = true; search.disabled = true;
    regField.value = room.regId;
    roomField.value = room.roomNo;
    setStatus(`Opening Room ${room.roomNo}…`);
    confirmButton.textContent = 'Opening…'; updateChoice();
    modal.querySelectorAll('[data-room-picker-close]').forEach(button => { if (button.tagName === 'BUTTON') button.disabled = true; });
    roomList.querySelectorAll('button').forEach(button => button.disabled = true);
    // SelectRoom validates the live guest and redirects via the protected Calendar-compatible token.
    form.requestSubmit();
  }
  function render() {
    roomList.replaceChildren();
    const query = (search.value || '').trim().toLocaleLowerCase();
    const matches = rooms.filter(x => `${x.roomNo} ${x.guestName} ${x.regId}`.toLocaleLowerCase().includes(query));
    for (const room of matches) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'r-room-picker-option';
      button.setAttribute('role', 'option');
      button.setAttribute('aria-selected', 'false');
      const label = document.createElement('span');
      label.className = 'r-room-picker-number';
      label.textContent = `Room ${room.roomNo}`;
      const description = document.createElement('span');
      description.className = 'r-room-picker-guest';
      description.textContent = room.guestName || 'Guest';
      const detail = document.createElement('small');
      detail.textContent = `Reg ${room.regId}`;
      button.append(label, description, detail);
      button.dataset.reg = String(room.regId); button.dataset.room = String(room.roomNo);
      button.addEventListener('click', () => { if (!selecting) { selectedRoom = room; updateChoice(); } });
      roomList.append(button);
    }
    if (!matches.length && !loading) setStatus(rooms.length ? 'No guests match your search.' : 'No checked-in rooms are currently available.');
    else if (!loading) setStatus('');
    updateChoice();
  }
  function close() {
    if (selecting) return;
    ++lookupVersion;
    const back = onBack; onBack = null;
    modal.hidden = true;
    root.classList.remove('r-room-picker-active');
    transferField.value = '';
    continueField.value = 'false';
    if (back) back(); else previousFocus?.focus();
  }
  async function open(transferBill = '', continueCheckout = false, back = null) {
    if (!modal.hidden) return;
    onBack = back;
    const version = ++lookupVersion;
    transferField.value = transferBill;
    continueField.value = String(continueCheckout);
    previousFocus = document.activeElement;
    modal.hidden = false;
    root.classList.add('r-room-picker-active');
    selecting = false; selectedRoom = null; rooms = []; search.disabled = false;
    confirmButton.textContent = transferBill ? 'Assign & Continue' : hasBill() ? 'Keep Draft & Switch' : 'Continue';
    modal.querySelectorAll('[data-room-picker-close]').forEach(button => { if (button.tagName === 'BUTTON') button.disabled = false; });
    updateChoice();
    search.value = '';
    roomList.replaceChildren();
    loading = true;
    updateChoice();
    setStatus('Loading checked-in rooms…');
    search.focus();
    try {
      const response = await fetch(root.dataset.inhouseRoomsUrl, {
        credentials: 'same-origin', cache: 'no-store',
        headers: { 'Accept': 'application/json', 'X-Requested-With': 'XMLHttpRequest' }
      });
      if (!response.ok) throw new Error(`Room lookup failed (${response.status}).`);
      const result = await response.json();
      if (version !== lookupVersion) return;
      rooms = Array.isArray(result.rooms) ? result.rooms : [];
      loading = false;
      if (!modal.hidden) render();
    } catch (error) {
      if (version !== lookupVersion) return;
      rooms = [];
      if (!modal.hidden) setStatus('Unable to load in-house rooms. Close and retry.', true);
    } finally { if (version === lookupVersion) { loading = false; updateChoice(); } }
  }

  window.RestaurantRoomPicker = { open };
  confirmButton.addEventListener('click', () => { if (!confirmButton.disabled) void chooseRoom(selectedRoom); });
  modal.querySelectorAll('[data-room-picker-close]').forEach(button => button.addEventListener('click', close));
  search.addEventListener('input', () => { selectedRoom = null; render(); });
  document.addEventListener('keydown', event => {
    if (modal.hidden) return;
    if (event.key === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); close(); }
    if (event.key === 'Tab') {
      const controls = [...modal.querySelectorAll('button:not(:disabled), input:not(:disabled)')]
        .filter(el => el.getClientRects().length);
      if (!controls.length) return;
      const first = controls[0];
      const last = controls[controls.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }
  });
  form.addEventListener('submit', event => {
    if (!regField.value || !roomField.value || root.classList.contains('r-pos-loading')) {
      event.preventDefault();
      selecting = false; search.disabled = false; confirmButton.textContent = transferField.value ? 'Assign & Continue' : 'Continue';
      modal.querySelectorAll('[data-room-picker-close]').forEach(button => { if (button.tagName === 'BUTTON') button.disabled = false; });
      render(); setStatus('Choose an available in-house room.', true);
    }
  });
})();
