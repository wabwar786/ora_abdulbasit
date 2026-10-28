(() => {
  'use strict';
  const root = document.getElementById('remainingBalancesPage');
  if (!root) return;

  const get = id => document.getElementById(id);
  const endpoint = root.dataset.dataUrl;
  const state = {
    start: root.dataset.start || null,
    end: root.dataset.end || null,
    search: '',
    source: '',
    status: 'all',
    page: 1,
    pageSize: Number(root.dataset.pageSize) || 50,
    total: 0,
    totals: { all: 0, activeOnly: 0 },
    draftStart: null,
    draftEnd: null,
    month: null,
    abort: null
  };

  const today = new Date();
  const isoToday = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2,'0')}-${String(today.getDate()).padStart(2,'0')}`;
  const currency = root.dataset.currency || '£';
  const money = value => currency + ' ' + Number(value || 0).toLocaleString('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const parseDate = value => value ? new Date(`${value}T12:00:00`) : null;
  const iso = date => `${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}`;
  const addDays = (date, days) => { const d = new Date(date); d.setDate(d.getDate() + days); return d; };
  const escapeHtml = text => String(text == null ? '' : text).replace(/[&<>"']/g, char => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[char]));
  const formatDisplayDate = value => {
    if (!value) return '—';
    const match = String(value).match(/^(\d{4})-(\d{2})-(\d{2})/);
    return match ? `${match[3]}-${match[2]}-${match[1]}` : '—';
  };
  const formatShortDate = value => {
    if (!value) return '';
    const match = String(value).match(/^(\d{4})-(\d{2})-(\d{2})/);
    return match ? `${match[3]}/${match[2]}/${match[1]}` : '';
  };

  const dateTrigger = get('rbDateTrigger');
  const picker = get('rbDatePicker');
  let debounce;

  function selectedRangeLabel(start = state.start, end = state.end) {
    return start && end ? `${formatShortDate(start)} - ${formatShortDate(end)}` : 'All dates';
  }

  function statusCss(status) {
    return String(status || '').toLowerCase().replace(/\s+/g, '-');
  }

  function statusBadge(status) {
    return `<span class="rb-status ${escapeHtml(statusCss(status))}">${escapeHtml(status)}</span>`;
  }

  function guestInitial(name) {
    return escapeHtml(String(name || 'G').trim().charAt(0).toUpperCase() || 'G');
  }

  function updateDateLabels() {
    get('rbDateLabel').textContent = selectedRangeLabel();
    const badge = get('rbTableRangeBadge');
    if (badge) badge.textContent = selectedRangeLabel();
  }

  function updateReceivablesSummary() {
    const excludeClosed = get('rbExcludeClosed').checked;
    const total = excludeClosed ? state.totals.activeOnly : state.totals.all;
    get('rbReceivablesTotal').textContent = money(total);
    get('rbRemainingChip').textContent = money(total);
    const note = get('rbReceivablesDescription');
    if (note) {
      note.textContent = excludeClosed
        ? 'Cancelled and no-show balances are excluded from the total.'
        : 'Outstanding balance for the selected filters.';
    }
  }

  function renderRows(rows) {
    const tbody = get('rbTableBody');
    tbody.innerHTML = rows.map((row, index) => {
      const guestName = row.guestName || 'Guest';
      const canReceive = row.canReceivePayment === true;
      const actionHtml = canReceive
        ? `<button type="button" class="rb-pay" data-reg="${escapeHtml(row.regId)}" data-visit="${escapeHtml(row.visitId)}" data-name="${escapeHtml(guestName)}" data-balance="${escapeHtml(row.remaining)}" data-source="${escapeHtml(row.sourceTable)}" data-arrival="${escapeHtml(row.arrival || '')}" data-departure="${escapeHtml(row.departure || '')}"><i class="fa-solid fa-credit-card"></i> Collect Payment</button>`
        : '<span class="rb-muted" title="Reservation record unavailable for payment">Unavailable</span>';

      return `<tr>
        <td class="rb-rowno">${((state.page - 1) * state.pageSize) + index + 1}</td>
        <td><strong>${escapeHtml(row.regId)}</strong></td>
        <td><div class="rb-guest"><span class="rb-avatar">${guestInitial(guestName)}</span><div><strong>${escapeHtml(guestName)}</strong><small>${escapeHtml(row.phone || row.email || '')}</small></div></div></td>
        <td><strong>${formatDisplayDate(row.arrival)} – ${formatDisplayDate(row.departure)}</strong><small>${escapeHtml(row.roomCategory || 'Room not assigned')}</small></td>
        <td>${escapeHtml(row.bookingSource || 'Walk In')}</td>
        <td>${statusBadge(row.status)}</td>
        <td class="numeric">${money(row.grandTotal)}</td>
        <td class="numeric">${money(row.paid)}</td>
        <td class="numeric rb-due">${money(row.remaining)}</td>
        <td>${actionHtml}</td>
      </tr>`;
    }).join('');

    get('rbEmpty').hidden = rows.length !== 0;
  }

  function renderPagination(total) {
    const pages = Math.max(1, Math.ceil(total / state.pageSize));
    const container = get('rbPagination');
    const pageButton = (label, page, active, disabled) => `<button type="button" data-page="${page}"${active ? ' class="active" aria-current="page"' : ''}${disabled ? ' disabled' : ''}>${label}</button>`;
    const firstPage = Math.max(1, Math.min(state.page - 2, pages - 4));
    const lastPage = Math.min(pages, firstPage + 4);
    let html = pageButton('‹', state.page - 1, false, state.page <= 1);
    for (let i = firstPage; i <= lastPage; i++) html += pageButton(i, i, i === state.page, false);
    html += pageButton('›', state.page + 1, false, state.page >= pages);
    container.innerHTML = html;

    const startRow = total ? (state.page - 1) * state.pageSize + 1 : 0;
    const endRow = Math.min(total, state.page * state.pageSize);
    get('rbShowingBottom').textContent = `Showing ${startRow}–${endRow} of ${total.toLocaleString('en-GB')}`;
  }

  function renderSummary(result) {
    state.total = Number(result.totalRecords) || 0;
    state.totals.all = Number(result.totalReceivables) || 0;
    state.totals.activeOnly = Number(result.totalReceivablesActiveOnly) || 0;

    updateReceivablesSummary();
    get('rbGuestsCount').textContent = state.total.toLocaleString('en-GB');
    get('rbBilledTotal').textContent = money(result.totalBilled);
    get('rbPaidTotal').textContent = money(result.totalPaid);
    // The compact report header does not render rbShowingTop.
    // Update it only when a parent layout supplies that optional element.
    const showingTop = get('rbShowingTop');
    if (showingTop) showingTop.textContent = `Showing ${state.total.toLocaleString('en-GB')}`;
    get('rbAllCount').textContent = state.total.toLocaleString('en-GB');
    const rowsCaption = get('rbRowsCaption');
    if (rowsCaption) rowsCaption.textContent = `${state.total.toLocaleString('en-GB')} rows`;
    renderRows(result.rows || []);
    renderPagination(state.total);
  }

  async function load() {
    if (state.abort) state.abort.abort();
    const controller = new AbortController();
    state.abort = controller;
    root.classList.add('loading');
    get('rbError').hidden = true;

    const url = new URL(endpoint, location.origin);
    Object.entries({
      start: state.start || '',
      end: state.end || '',
      search: state.search,
      source: state.source,
      status: state.status,
      page: state.page,
      pageSize: state.pageSize
    }).forEach(([key, value]) => url.searchParams.set(key, value));

    try {
      const response = await fetch(url, { signal: controller.signal, credentials: 'same-origin', headers: { 'Accept': 'application/json' } });
      const json = await response.json().catch(() => null);
      if (response.status === 401) {
        location.href = '/LoginHMS';
        return;
      }
      if (!response.ok || !json || json.ok !== true) throw new Error(json?.message || 'Unable to load remaining balances.');
      if (state.page > 1 && !json.result?.rows?.length) {
        state.page = 1;
        return await load();
      }
      renderSummary(json.result);
    } catch (error) {
      if (error.name !== 'AbortError') {
        get('rbError').textContent = error.message || 'Unable to load balances.';
        get('rbError').hidden = false;
      }
    } finally {
      if (state.abort === controller) {
        root.classList.remove('loading');
        state.abort = null;
      }
    }
  }

  function runSearch() {
    state.search = get('rbSearch').value.trim();
    state.source = get('rbSource').value;
    state.page = 1;
    load();
  }

  function clearFilters() {
    get('rbSearch').value = '';
    get('rbSource').value = '';
    state.search = '';
    state.source = '';
    state.status = 'all';
    state.page = 1;
    get('rbStatusTabs').querySelectorAll('[data-status]').forEach(button => button.classList.toggle('active', button.dataset.status === 'all'));
    load();
  }

  get('rbSearchButton').addEventListener('click', runSearch);
  get('rbClearFilters').addEventListener('click', clearFilters);
  get('rbSearch').addEventListener('input', () => {
    clearTimeout(debounce);
    debounce = setTimeout(runSearch, 400);
  });
  get('rbSearch').addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      clearTimeout(debounce);
      runSearch();
    }
  });
  get('rbSource').addEventListener('change', runSearch);
  get('rbExcludeClosed').addEventListener('change', updateReceivablesSummary);
  get('rbStatusTabs').addEventListener('click', event => {
    const button = event.target.closest('[data-status]');
    if (!button) return;
    state.status = button.dataset.status;
    state.page = 1;
    get('rbStatusTabs').querySelectorAll('[data-status]').forEach(item => item.classList.toggle('active', item === button));
    load();
  });
  get('rbPageSize').addEventListener('change', event => {
    state.pageSize = Number(event.target.value) || 50;
    state.page = 1;
    load();
  });
  get('rbPagination').addEventListener('click', event => {
    const button = event.target.closest('[data-page]');
    if (!button || button.disabled) return;
    state.page = Number(button.dataset.page);
    load();
  });

  get('rbTableBody').addEventListener('click', event => {
    const button = event.target.closest('button.rb-pay');
    if (!button) return;
    if (!window.RecordPayment?.open) {
      get('rbError').textContent = 'The shared Record Payment component is unavailable.';
      get('rbError').hidden = false;
      return;
    }
    window.RecordPayment.open({
      regId: button.dataset.reg,
      visitId: button.dataset.visit || '',
      guestName: button.dataset.name || 'Guest',
      balance: Number(button.dataset.balance) || 0,
      currencySymbol: currency,
      hotelId: root.dataset.hotelId || '',
      userId: root.dataset.userId || '',
      currencyCode: root.dataset.currencyCode || 'GBP',
      source: button.dataset.source || 'NR',
      arrival: button.dataset.arrival || '',
      departure: button.dataset.departure || '',
      showOnlineCard: root.dataset.canOnlineCard === '1',
      showPdqPayment: root.dataset.canPdqPayment === '1',
      showAutoPay: false,
      onCompleted: () => {
        try {
          sessionStorage.setItem('oraRemainingBalancesAfterPayment', JSON.stringify({
            path: window.location.pathname, at: Date.now(),
            start: state.start, end: state.end, search: state.search,
            source: state.source, status: state.status, page: state.page,
            pageSize: state.pageSize
          }));
        } catch (_) { /* Refresh even when browser storage is blocked. */ }
        window.location.reload();
      }
    });
  });

  function changeMonth(offset) {
    state.month.setMonth(state.month.getMonth() + offset);
    renderCalendars();
  }

  function renderCalendars() {
    const firstMonth = state.month;
    const months = [new Date(firstMonth.getFullYear(), firstMonth.getMonth(), 1), new Date(firstMonth.getFullYear(), firstMonth.getMonth() + 1, 1)];
    get('rbCalendarMonths').innerHTML = months.map(month => `<span>${escapeHtml(month.toLocaleDateString('en-GB', { month: 'long', year: 'numeric' }))}</span>`).join('');
    get('rbCalendars').innerHTML = months.map(month => {
      const daysInMonth = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate();
      const offset = (new Date(month.getFullYear(), month.getMonth(), 1).getDay() + 6) % 7;
      let html = '<div class="rb-calendar">' + ['Mon','Tue','Wed','Thu','Fri','Sat','Sun'].map(day => `<span class="rb-weekday">${day}</span>`).join('');
      for (let i = 0; i < offset; i++) html += '<button type="button" class="rb-day" disabled> </button>';
      for (let day = 1; day <= daysInMonth; day++) {
        const dateValue = iso(new Date(month.getFullYear(), month.getMonth(), day));
        const cssClass = dateValue === state.draftStart ? 'start' : dateValue === state.draftEnd ? 'end' : (state.draftStart && state.draftEnd && dateValue > state.draftStart && dateValue < state.draftEnd ? 'range' : '');
        html += `<button type="button" class="rb-day ${cssClass}" data-date="${dateValue}" aria-label="${dateValue}">${day}</button>`;
      }
      return html + '</div>';
    }).join('');
    get('rbDraftLabel').textContent = selectedRangeLabel(state.draftStart, state.draftEnd);
  }

  function hidePicker() {
    picker.hidden = true;
    dateTrigger.setAttribute('aria-expanded', 'false');
  }

  function showPicker() {
    state.draftStart = state.start;
    state.draftEnd = state.end;
    const firstDate = parseDate(state.start) || today;
    state.month = new Date(firstDate.getFullYear(), firstDate.getMonth(), 1);
    renderCalendars();
    picker.hidden = false;
    dateTrigger.setAttribute('aria-expanded', 'true');
  }

  dateTrigger.addEventListener('click', () => picker.hidden ? showPicker() : hidePicker());
  picker.addEventListener('click', event => event.stopPropagation());
  get('rbPreviousMonth').addEventListener('click', () => changeMonth(-1));
  get('rbNextMonth').addEventListener('click', () => changeMonth(1));
  get('rbCalendars').addEventListener('click', event => {
    const button = event.target.closest('[data-date]');
    if (!button) return;
    const value = button.dataset.date;
    if (!state.draftStart || state.draftEnd) {
      state.draftStart = value;
      state.draftEnd = null;
    } else if (value < state.draftStart) {
      state.draftEnd = state.draftStart;
      state.draftStart = value;
    } else {
      state.draftEnd = value;
    }
    renderCalendars();
  });
  get('rbDatePicker').querySelectorAll('[data-rb-preset]').forEach(button => button.addEventListener('click', () => {
    const preset = button.dataset.rbPreset;
    state.draftEnd = isoToday;
    if (preset === 'today') state.draftStart = isoToday;
    else if (preset === '7') state.draftStart = iso(addDays(today, -6));
    else if (preset === '30') state.draftStart = iso(addDays(today, -29));
    else if (preset === 'month') state.draftStart = iso(new Date(today.getFullYear(), today.getMonth(), 1));
    else {
      state.draftStart = null;
      state.draftEnd = null;
    }
    renderCalendars();
  }));
  get('rbDateCancel').addEventListener('click', hidePicker);
  get('rbDateApply').addEventListener('click', () => {
    if ((state.draftStart && !state.draftEnd) || (!state.draftStart && state.draftEnd)) {
      get('rbDraftLabel').textContent = 'Select the end date.';
      return;
    }
    state.start = state.draftStart;
    state.end = state.draftEnd;
    updateDateLabels();
    hidePicker();
    state.page = 1;
    load();
  });

  document.addEventListener('click', event => {
    if (!picker.hidden && !picker.contains(event.target) && !dateTrigger.contains(event.target)) hidePicker();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') hidePicker();
  });

  updateDateLabels();
  renderPagination(Number(get('rbGuestsCount').textContent.replaceAll(',', '')) || 0);

  // Restore only the one-time search/filter context of the page that collected payment.
  try {
    const key = 'oraRemainingBalancesAfterPayment';
    const raw = sessionStorage.getItem(key);
    if (raw) {
      sessionStorage.removeItem(key);
      const saved = JSON.parse(raw);
      if (saved.path === window.location.pathname && Date.now() - saved.at < 60000) {
        state.start = saved.start || null;
        state.end = saved.end || null;
        state.search = String(saved.search || '').slice(0,160);
        state.source = String(saved.source || '');
        state.status = String(saved.status || 'all');
        state.page = Math.max(1, Number(saved.page) || 1);
        state.pageSize = [25,50,100].includes(Number(saved.pageSize)) ? Number(saved.pageSize) : 50;
        get('rbSearch').value = state.search;
        get('rbSource').value = state.source;
        get('rbPageSize').value = String(state.pageSize);
        get('rbStatusTabs').querySelectorAll('[data-status]').forEach(button =>
          button.classList.toggle('active', button.dataset.status === state.status));
        updateDateLabels();
        void load();
      }
    }
  } catch (_) { /* Keep normal report loading when storage is unavailable. */ }
})();
