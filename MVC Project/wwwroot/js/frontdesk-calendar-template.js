(() => {
  'use strict';

  const page = document.getElementById('frontDeskCalendarPage');
  if (!page) return;

  const $ = (id) => document.getElementById(id);

  function ensureDrawerHoldStyles(){
    if(document.getElementById('fdcHoldRuntimeStyles')) return;
    const style=document.createElement('style');
    style.id='fdcHoldRuntimeStyles';
    style.textContent=`
      .fdc-holds-panel{border-color:#c9d9ea!important;background:#fbfdff!important}
      .fdc-holds-help{margin:-1px 0 8px;color:#6b7d90;font-size:9px;line-height:1.35}
      .fdc-holds-list{display:grid;gap:7px}
      .fdc-hold-card{padding:8px;border:1px solid #d5e0eb;border-radius:8px;background:#fff}
      .fdc-hold-summary{display:flex;align-items:flex-start;justify-content:space-between;gap:8px}
      .fdc-hold-summary>div{min-width:0}.fdc-hold-summary b{display:block;color:#102a43;font-size:11px}
      .fdc-hold-summary small{display:block;margin-top:2px;color:#73859a;font-size:8.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
      .fdc-hold-status{flex:0 0 auto;padding:2px 6px;border-radius:999px;background:#fff4da;color:#8a5a00;font-size:7.5px;font-weight:900;letter-spacing:.06em}
      .fdc-hold-note{margin-top:6px;padding-top:5px;border-top:1px dashed #e0e7ef;color:#667a8f;font-size:8.5px;line-height:1.3;overflow-wrap:anywhere}
      .fdc-hold-capture{display:grid;grid-template-columns:auto minmax(95px,1fr);gap:7px;align-items:center;margin-top:7px}
      .fdc-hold-capture label{font-size:8.5px;font-weight:800;color:#536a80}
      .fdc-hold-capture-input{width:100%;height:30px;padding:0 8px;border:1px solid #cbd8e5;border-radius:7px;background:#fff;color:#172b40;font:inherit;font-size:9.5px;font-weight:750;outline:none}
      .fdc-hold-capture-input:focus{border-color:#2867e8;box-shadow:0 0 0 2px rgba(40,103,232,.10)}
      .fdc-hold-buttons{display:grid;grid-template-columns:1fr 1fr;gap:6px;margin-top:7px}
      .fdc-hold-btn{min-height:30px;border:1px solid #d6e0ea;border-radius:7px;background:#fff;color:#102a43;font:inherit;font-size:9px;font-weight:850;cursor:pointer}
      .fdc-hold-btn.capture{background:#117653;border-color:#117653;color:#fff}.fdc-hold-btn.release{background:#fff6f7;border-color:#e7b7bd;color:#b8333f}
      .fdc-hold-btn:disabled{opacity:.48;cursor:not-allowed}.fdc-hold-btn.fdc-button-loading{cursor:wait!important}
      @media(max-width:520px){.fdc-hold-capture{grid-template-columns:1fr}.fdc-hold-buttons{grid-template-columns:1fr 1fr}}
    `;
    document.head.appendChild(style);
  }
  ensureDrawerHoldStyles();
  const calendar = $('fdcCalendar');
  const scroll = $('fdcScroll');
  const drawer = $('fdcDrawer');
  const drawerBody = $('fdcDrawerBody');
  const drawerPay = $('fdcDrawerPay');
  const drawerFoot = $('fdcDrawerFoot');
  const overlay = $('fdcOverlay');
  const modalTitle = $('fdcModalTitle');
  const modalBody = $('fdcModalBody');
  const modalFoot = $('fdcModalFoot');
  const context = $('fdcContext');
  const tip = $('fdcTip');
  const rangePicker = $('fdcRangePicker');
  const toast = $('fdcToast');

  const cfg = Object.fromEntries(Array.from(page.attributes)
    .filter(a => a.name.startsWith('data-'))
    .map(a => [a.name.substring(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase()), a.value]));

  const token = document.querySelector('#fdcAntiForgery input[name="__RequestVerificationToken"]')?.value || '';
  const state = {
    start: dateOnly(cfg.start) || dateOnly(cfg.hotelToday) || todayLocal(),
    days: Math.max(7, Math.min(45, Number(cfg.viewDays || 20))),
    hotelToday: dateOnly(cfg.hotelToday) || todayLocal(),
    currency: cfg.currency || '£',
    payload: null,
    loading: false,
    request: null,
    collapsed: new Set(),
    selectedCell: null,
    currentDetails: null,
    currentHolds: [],
    drag: null,
    resize: null
  };

  function dateOnly(value) {
    if (!value) return null;
    if (value instanceof Date) return new Date(value.getFullYear(), value.getMonth(), value.getDate());
    const m = String(value).match(/^(\d{4})-(\d{2})-(\d{2})/);
    if (m) return new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
    const d = new Date(value);
    return Number.isNaN(d.valueOf()) ? null : new Date(d.getFullYear(), d.getMonth(), d.getDate());
  }
  function todayLocal() { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), d.getDate()); }
  function addDays(d, n) { const x = new Date(d); x.setDate(x.getDate() + n); return x; }
  function diffDays(a, b) { return Math.round((dateOnly(b) - dateOnly(a)) / 86400000); }
  function iso(d) { const x = dateOnly(d); return `${x.getFullYear()}-${String(x.getMonth()+1).padStart(2,'0')}-${String(x.getDate()).padStart(2,'0')}`; }
  function fmt(d, long = false) { const x = dateOnly(d); return x ? x.toLocaleDateString('en-GB', long ? {day:'2-digit',month:'short',year:'numeric'} : {day:'2-digit',month:'short'}) : ''; }
  function esc(v) { return String(v ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c])); }
  function money(v) { const n = Number(v || 0); return `${state.currency}${n.toLocaleString('en-GB',{minimumFractionDigits:2,maximumFractionDigits:2})}`; }
  const zeroDecimalCurrencies=new Set(['BIF','CLP','DJF','GNF','JPY','KMF','KRW','MGA','PYG','RWF','UGX','VND','VUV','XAF','XOF','XPF']);
  function holdMinorFactor(currency){return zeroDecimalCurrencies.has(String(currency||'').toUpperCase())?1:100;}
  function holdMajor(minor,currency){return Number(minor||0)/holdMinorFactor(currency);}
  function holdMinor(major,currency){return Math.round(Number(major||0)*holdMinorFactor(currency));}
  function initials(name) { return String(name || 'G').trim().split(/\s+/).slice(0,2).map(x=>x[0]||'').join('').toUpperCase() || 'G'; }
  function sameDate(a,b) { return iso(a) === iso(b); }
  function isWeekend(d) { const x=dateOnly(d).getDay(); return x===5 || x===6; }
  function normalizeStatus(v) { return String(v || '').trim().toLowerCase(); }
  function normalizeStatusKey(v) { return normalizeStatus(v).replace(/[^a-z0-9]+/g,''); }
  function isCheckedInStatus(v) {
    const s=normalizeStatusKey(v);
    return s==='checkin' || s==='checkedin' || s==='inhouse';
  }
  function isCheckedOutStatus(v) {
    const s=normalizeStatusKey(v);
    return s==='checkout' || s==='checkedout';
  }
  function base64(v) { try { return btoa(unescape(encodeURIComponent(v))); } catch { return v; } }
  function base64Url(v) {
    try { return btoa(unescape(encodeURIComponent(String(v ?? '')))).replace(/=+$/,'').replace(/\+/g,'-').replace(/\//g,'_'); }
    catch { return String(v ?? ''); }
  }

  function showToast(message, isError=false) {
    toast.textContent = message || (isError ? 'Action failed.' : 'Done.');
    toast.classList.toggle('error', isError);
    toast.classList.add('show');
    clearTimeout(showToast.t); showToast.t = setTimeout(()=>toast.classList.remove('show'), 2800);
  }
  function setButtonBusy(button, show, label='Working…') {
    if (!button || !(button instanceof HTMLElement)) return;
    if (show) {
      if (!button.dataset.fdcOriginalHtml) button.dataset.fdcOriginalHtml = button.innerHTML;
      button.disabled = true;
      button.classList.add('fdc-button-loading');
      button.innerHTML = `<span class="fdc-button-spinner" aria-hidden="true"></span><span>${esc(label)}</span>`;
      button.setAttribute('aria-busy','true');
    } else {
      const original = button.dataset.fdcOriginalHtml;
      if (original !== undefined) button.innerHTML = original;
      button.disabled = false;
      button.classList.remove('fdc-button-loading');
      button.removeAttribute('aria-busy');
      delete button.dataset.fdcOriginalHtml;
    }
  }
  function busy() { /* Button events use inline button loaders; no separate global loader. */ }
  function waitForPaint() {
    return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  }

  async function api(url, options={}) {
    const headers = new Headers(options.headers || {});
    headers.set('Accept','application/json');
    if (options.body && !(options.body instanceof FormData)) headers.set('Content-Type','application/json');
    if (options.method && options.method.toUpperCase() !== 'GET' && token) headers.set('RequestVerificationToken', token);
    const res = await fetch(url, {...options, headers, credentials:'same-origin'});
    if (res.status === 401) { location.href = '/LoginHMS'; throw new Error('Session expired.'); }
    let json = null;
    try { json = await res.json(); } catch { /* ignore */ }
    if (!res.ok) {
      const err = new Error(json?.message || `Request failed (${res.status}).`);
      err.status = res.status;
      err.url = String(url || '');
      throw err;
    }
    return json;
  }

  // Build calendar action URLs from the Data endpoint that is already known
  // to work in this application. This avoids conventional/attribute-route
  // differences causing a 404 for newer calendar actions.
  function calendarActionUrl(actionName) {
    try {
      const dataUrl = new URL(cfg.dataUrl || '/Calendar/Data', location.origin);
      let path = dataUrl.pathname.replace(/\/Data\/?$/i, '');
      if (!path || path === '/') path = '/Calendar';
      return `${path}/${actionName}`.replace(/\/{2,}/g, '/');
    } catch {
      return `/Calendar/${actionName}`;
    }
  }

  // Resolve legacy WebForms pages from the same application root as /Calendar/Data.
  // This keeps Guest History / Invoice working when the PMS is hosted in a virtual directory.
  function legacyPageUrl(pageName) {
    try {
      const dataUrl = new URL(cfg.dataUrl || '/Calendar/Data', location.origin);
      const path = dataUrl.pathname || '/Calendar/Data';
      const lower = path.toLowerCase();
      const marker = lower.lastIndexOf('/calendar/');
      const appRoot = marker >= 0 ? path.substring(0, marker + 1) : '/';
      return new URL(`${appRoot}${String(pageName || '').replace(/^\/+/, '')}`, location.origin);
    } catch {
      return new URL(`/${String(pageName || '').replace(/^\/+/, '')}`, location.origin);
    }
  }

  function queryOrB64(name, fallbackValue) {
    const existing = new URLSearchParams(location.search).get(name);
    return existing || base64(String(fallbackValue ?? ''));
  }

  async function loadCalendar(start=state.start, keepScroll=false) {
    if (state.request) state.request.abort();
    state.request = new AbortController();
    state.loading = true;
    const oldLeft=scroll.scrollLeft, oldTop=scroll.scrollTop;
    calendar.style.setProperty('--view-days', state.days);
    calendar.innerHTML='<div class="fdc-loading"><span></span><b>Loading calendar…</b></div>';
    try {
      const url = new URL(cfg.dataUrl, location.origin);
      url.searchParams.set('start', iso(start));
      url.searchParams.set('days', state.days);
      const json = await api(url.toString(), {signal:state.request.signal});
      state.payload = json.data;
      state.start = dateOnly(json.data.startDate) || start;
      state.hotelToday = dateOnly(json.data.hotelToday) || state.hotelToday;
      state.currency = json.data.currencySymbol || state.currency;
      render();
      if (keepScroll) { scroll.scrollLeft=oldLeft; scroll.scrollTop=oldTop; }
    } catch (e) {
      if (e.name !== 'AbortError') calendar.innerHTML=`<div class="fdc-empty"><b>Calendar could not be loaded.</b><br>${esc(e.message)}</div>`;
    } finally { state.loading=false; }
  }

  function render() {
    const p = state.payload;
    if (!p) return;
    calendar.style.setProperty('--view-days', p.viewDays || state.days);
    const dates = Array.from({length:p.viewDays || state.days}, (_,i)=>addDays(state.start,i));
    const cats = p.categories || [];
    const rooms = p.rooms || [];
    const bookings = p.bookings || [];
    const blocks = p.blocks || [];
    const roomGroups = new Map();
    for (const c of cats) roomGroups.set(String(c.id), []);
    for (const r of rooms) {
      if (!roomGroups.has(String(r.categoryId))) roomGroups.set(String(r.categoryId), []);
      roomGroups.get(String(r.categoryId)).push(r);
    }
    const unassignedByCat = new Set(bookings.filter(b => !b.roomNo || String(b.roomNo).toUpperCase()==='UNASSIGNED').map(b=>String(b.categoryId)));

    let html = `<div class="fdc-date-head"><div class="fdc-rooms-head">Rooms</div>`;
    for (const [dayIndex,d] of dates.entries()) html += `<div class="fdc-day ${isWeekend(d)?'weekend':''} ${sameDate(d,state.hotelToday)?'today':''}" data-day="${dayIndex}" data-date="${iso(d)}"><span>${esc(d.toLocaleDateString('en-GB',{weekday:'short'}))}</span><b>${d.getDate()}</b><small>${esc(d.toLocaleDateString('en-GB',{month:'short'}))}</small></div>`;
    html += '</div>';

    for (const cat of cats) {
      const cid=String(cat.id), collapsed=state.collapsed.has(cid);
      const cr=roomGroups.get(cid) || [];
      html += `<div class="fdc-category" data-category="${esc(cid)}"><div class="fdc-category-label" data-collapse="${esc(cid)}"><span class="arrow">${collapsed?'▸':'▾'}</span><span title="${esc(cat.name)}">${esc(cat.name)}</span></div><div class="fdc-category-grid"></div></div>`;
      if (collapsed) continue;
      for (const r of cr) html += roomRow(r, cat, dates, false);
      // Keep one Unassigned lane per category; this is also a drag/drop destination.
      html += roomRow({roomNo:'UNASSIGNED',categoryId:cid,categoryName:cat.name,condition:'Unassigned'}, cat, dates, true);
    }
    calendar.innerHTML=html;
    placeBars(bookings, blocks, dates);
    updateRangeButton();
  }

  function roomRow(room, cat, dates, unassigned) {
    const dirty=normalizeStatus(room.condition).includes('dirty');
    let html=`<div class="fdc-room-row ${unassigned?'unassigned-row':''}" data-room="${esc(room.roomNo)}" data-category="${esc(room.categoryId)}" data-category-name="${esc(room.categoryName||cat.name)}">`;
    html += `<div class="fdc-room-label ${dirty?'dirty':''} ${unassigned?'unassigned':''}" data-room-label><b>${unassigned?'Unassigned':esc(room.roomNo)}</b></div>`;
    dates.forEach((d,i)=>{
      const dirtyToday=dirty && (!room.dirtyDate || sameDate(d,room.dirtyDate));
      html += `<div class="fdc-cell ${isWeekend(d)?'weekend':''} ${dirtyToday?'has-dirty':''}" data-day="${i}" data-date="${iso(d)}" data-room="${esc(room.roomNo)}" data-category="${esc(room.categoryId)}" data-category-name="${esc(room.categoryName||cat.name)}">${dirtyToday?'<span class="fdc-dirty-tag">Dirty</span>':''}</div>`;
    });
    return html+'</div>';
  }

  function placeBars(bookings, blocks, dates) {
    const rangeStart=state.start, rangeEnd=addDays(state.start,state.days);
    const rows=[...calendar.querySelectorAll('.fdc-room-row')];
    const index=new Map(rows.map(r=>[`${r.dataset.category}|${String(r.dataset.room).toUpperCase()}`,r]));

    const visualBounds=(a,d)=>{
      // WebForms draws arrival from the middle of the arrival date and
      // departure to the middle of the departure date.  When the booking
      // started/ends outside the current view, the clipped edge is full.
      if(!a||!d||d<rangeStart||a>=rangeEnd) return null;

      let leftUnits=a>=rangeStart ? diffDays(rangeStart,a)+0.5 : 0;
      let rightUnits=(d>=rangeStart&&d<rangeEnd) ? diffDays(rangeStart,d)+0.5 : state.days;

      leftUnits=Math.max(0,Math.min(state.days,leftUnits));
      rightUnits=Math.max(0,Math.min(state.days,rightUnits));
      if(rightUnits<=leftUnits) return null;
      return {leftUnits,rightUnits,widthUnits:rightUnits-leftUnits};
    };

    const addBar=(item,isBlock=false)=>{
      const room=(item.roomNo || 'UNASSIGNED').toUpperCase();
      const row=index.get(`${item.categoryId}|${room}`);
      if(!row) return;

      const a=dateOnly(isBlock?item.startDate:item.arrival);
      const d=dateOnly(isBlock?item.endDate:item.departure);
      const bounds=visualBounds(a,d);
      if(!bounds) return;

      const left=`calc(var(--room) + (${bounds.leftUnits} * ((100% - var(--room)) / var(--view-days))) + 2px)`;
      const width=`calc(${bounds.widthUnits} * ((100% - var(--room)) / var(--view-days)) - 4px)`;

      if(isBlock){
        row.insertAdjacentHTML('beforeend',
          `<div class="fdc-bar b ${String(item.kind||'').toLowerCase().includes('maintenance')?'maintenance':''}" `+
          `style="left:${left};width:${width}" data-block-id="${item.blockId}" data-block='${esc(JSON.stringify(item))}'>`+
          `<span class="name">${esc(item.reason||'Blocked')}</span></div>`);
        return;
      }

      const cls=statusClass(item.statusCode||item.status);
      const payClass=paymentDotClass(item.paymentStatus);
      const roomClass=room==='UNASSIGNED'?' unassigned-booking':'';
      row.insertAdjacentHTML('beforeend',
        `<div class="fdc-bar ${cls}${roomClass}" draggable="${item.canDrag?'true':'false'}" `+
        `style="left:${left};width:${width}" data-booking='${esc(JSON.stringify(item))}' title="${esc(item.guestName)}">`+
        `<span class="name">${esc(item.guestName||item.regId)}</span>`+
        `<span class="pay-dot ${payClass}" title="${esc(item.paymentStatus||'')}"></span>`+
        `${item.hasNote?'<span class="fdc-note-indicator" title="Notebook note" aria-label="Notebook note">★</span>':''}`+
        `${item.hasRoomChange?'<span class="fdc-room-change-indicator" title="Room changed" aria-label="Room changed">★</span>':''}`+
        `${item.canResize?'<span class="fdc-handle right" data-resize="right"></span>':''}</div>`);
    };

    bookings.forEach(x=>addBar(x,false));
    blocks.forEach(x=>addBar(x,true));
  }

  function statusClass(status){
    const s=String(status||'').trim().toUpperCase();
    if(s==='CO'||normalizeStatus(status).includes('check out')) return 'co';
    if(s==='O'||normalizeStatus(status).includes('check in')||normalizeStatus(status).includes('in house')) return 'o';
    if(s==='P'||normalizeStatus(status).includes('provisional')) return 'p';
    if(s==='B'||normalizeStatus(status).includes('block')) return 'b';
    if(s==='D'||normalizeStatus(status).includes('dirty')) return 'd';
    return 'r';
  }

  function paymentDotClass(status){
    const s=normalizeStatus(status);
    if(s==='not paid') return 'not-paid';
    if(s==='partially paid') return 'part-paid';
    if(s==='fully paid') return 'fully-paid';
    return 'payment-hidden';
  }

  function updateRangeButton(){
    const end=addDays(state.start,state.days-1);
    $('fdcRange').textContent=`${fmt(state.start,true)} – ${fmt(end,true)}`;
    $('fdcPrev').title=`Previous ${state.days} days`; $('fdcNext').title=`Next ${state.days} days`;
  }

  function closeContext(force=false){
    // Do not allow outside clicks, scroll events, or other document handlers to
    // remove the right-click menu while an action is still processing.  The
    // action itself closes the menu with force=true only after it has finished
    // or after the next modal/page has been opened.
    if(!force && context.classList.contains('fdc-context-processing')) return;
    context.classList.remove('open','booking-menu','fdc-context-processing');
    context.removeAttribute('data-processing');
    context.innerHTML='';
    delete context.dataset.booking;
  }
  function closeTip(){ tip.classList.remove('open'); }
  const modalModeClasses=['fdc-cancel-mode','fdc-category-rate-mode','fdc-professional-mode','fdc-room-action-mode','fdc-block-form-mode','fdc-block-details-mode','fdc-resize-mode'];
  function openModal(title, body, foot='', mode='') {
    overlay.classList.remove(...modalModeClasses);
    String(mode||'').split(/\s+/).filter(Boolean).forEach(x=>overlay.classList.add(x));
    modalTitle.textContent=title; modalBody.innerHTML=body; modalFoot.innerHTML=foot;
    overlay.classList.add('open'); overlay.setAttribute('aria-hidden','false');
    page.classList.add('fdc-modal-open');
  }
  function closeModal(){
    overlay.classList.remove('open',...modalModeClasses);
    overlay.setAttribute('aria-hidden','true');
    page.classList.remove('fdc-modal-open');
  }
  function closeDrawer(){ drawer.classList.remove('open'); drawer.setAttribute('aria-hidden','true'); if(drawerPay)drawerPay.innerHTML=''; drawerFoot.innerHTML=''; state.currentDetails=null; }

  function openRoomAction(cell){
    const date=cell.dataset.date, room=cell.dataset.room, cid=cell.dataset.category, cname=cell.dataset.categoryName;
    const isUnassigned=String(room).toUpperCase()==='UNASSIGNED';
    const body=`<div class="fdc-pro-summary">
      <div class="fdc-pro-summary-icon">⌂</div>
      <div><span>Selected room</span><b>${isUnassigned?'Unassigned lane':`Room ${esc(room)}`}</b><small>${esc(cname)} · ${fmt(date,true)}</small></div>
    </div>
    <div class="fdc-actions-grid fdc-pro-actions">
      <button type="button" class="fdc-action-card fdc-room-action-reserve" data-action="new-reservation"><span class="fdc-action-card-icon">＋</span><span><b>New Reservation</b><small>Create a booking for this room and date.</small></span></button>
      <button type="button" class="fdc-action-card fdc-room-action-checkin" data-action="checkin"><span class="fdc-action-card-icon">→</span><span><b>Check-In</b><small>Open the check-in workspace.</small></span></button>
      ${!isUnassigned?'<button type="button" class="fdc-action-card fdc-room-action-block" data-action="block"><span class="fdc-action-card-icon">▣</span><span><b>Block Room</b><small>Block this room for a selected date range.</small></span></button>':''}
      ${cell.classList.contains('has-dirty')?'<button type="button" class="fdc-action-card" data-action="clean"><span class="fdc-action-card-icon">✓</span><span><b>Mark Clean</b><small>Set this room back to available.</small></span></button>':''}
    </div>`;
    openModal('Room Action',body,'<button type="button" class="fdc-btn fdc-btn-secondary" data-close-modal>Close</button>','fdc-professional-mode fdc-room-action-mode');
    modalBody.querySelector('[data-action="new-reservation"]')?.addEventListener('click',e=>{
      setButtonBusy(e.currentTarget,true,'Opening…');
      const u=new URL(cfg.reservationUrl,location.origin); if(!isUnassigned)u.searchParams.set('RN',room);u.searchParams.set('BT',cname);u.searchParams.set('FD',date);u.searchParams.set('NT','1');location.href=u;
    });
    modalBody.querySelector('[data-action="checkin"]')?.addEventListener('click',e=>{setButtonBusy(e.currentTarget,true,'Opening…');location.href=cfg.checkinUrl;});
    modalBody.querySelector('[data-action="block"]')?.addEventListener('click',()=>openBlockForm({roomNo:room,categoryId:cid,categoryName:cname,startDate:date,endDate:date}));
    modalBody.querySelector('[data-action="clean"]')?.addEventListener('click',e=>markClean(room,e.currentTarget));
  }

  function openBlockForm(x, existing=null){
    const body=`<div class="fdc-pro-summary compact">
      <div class="fdc-pro-summary-icon">▣</div>
      <div><span>${existing?'Editing room block':'Create room block'}</span><b>Room ${esc(x.roomNo)} · ${esc(x.categoryName)}</b><small>${existing?'Update the blocked dates, type or reason.':'Choose the date range and record why the room is unavailable.'}</small></div>
    </div>
    <div class="fdc-form fdc-pro-form">
      <div class="fdc-field"><label>Room</label><input value="${esc(x.roomNo)}" disabled></div>
      <div class="fdc-field"><label>Category</label><input value="${esc(x.categoryName)}" disabled></div>
      <div class="fdc-field"><label>Start date</label><input type="date" id="blockStart" value="${iso(x.startDate)}"></div>
      <div class="fdc-field"><label>End date</label><input type="date" id="blockEnd" value="${iso(existing?addDays(dateOnly(existing.endDate),-1):x.endDate)}"></div>
      <div class="fdc-field full"><label>Type</label><select id="blockKind"><option>Room Block</option><option>Maintenance</option></select></div>
      <div class="fdc-field full"><label>Reason <span class="fdc-required">*</span></label><textarea id="blockReason" placeholder="Enter the reason for blocking this room">${esc(existing?.reason||'')}</textarea></div>
    </div>`;
    openModal(existing?'Edit Room Block':'Block Room',body,`<button class="fdc-btn fdc-btn-secondary" data-close-modal>Cancel</button><button class="fdc-btn primary" id="saveBlock">${existing?'Update block':'Block room'}</button>`,'fdc-professional-mode fdc-block-form-mode');
    if(existing) $('blockKind').value=existing.kind||'Room Block';
    $('saveBlock').onclick=async e=>{
      const btn=e.currentTarget;
      const start=$('blockStart').value,end=$('blockEnd').value,reason=$('blockReason').value.trim();
      if(!reason||!start||!end){showToast('Start date, end date and reason are required.',true);return;}
      const req={roomNo:x.roomNo,categoryId:x.categoryId,categoryName:x.categoryName,startDate:start,endDate:end,reason,kind:$('blockKind').value};
      if(existing)req.blockId=existing.blockId;
      try{
        await mutate(existing?cfg.updateBlockUrl:cfg.blockUrl,req,existing?'Updating block…':'Blocking room…',btn);
        closeModal();
        await loadCalendar(state.start,true);
      }catch{}
    };
  }

  async function markClean(roomNo,button=null){
    try{ await mutate(cfg.cleanUrl,{roomNo},'Updating room…',button); closeModal(); await loadCalendar(state.start,true); }catch{}
  }


  function paymentHoldUrls(){
    const rp=document.getElementById('orpRecordPayment');
    return {
      holds:rp?.dataset.holdsUrl||'/terminalcardpayment/Holds',
      capture:rp?.dataset.captureHoldUrl||'/terminalcardpayment/CaptureHold',
      release:rp?.dataset.releaseHoldUrl||'/terminalcardpayment/ReleaseHold'
    };
  }

  async function fetchReservationHolds(regId){
    if(!regId) return [];
    const urls=paymentHoldUrls();
    const u=new URL(urls.holds,location.origin);
    u.searchParams.set('regId',regId);
    const json=await api(u.toString());
    if(json?.ok===false) throw new Error(json.message||'Unable to load payment holds.');
    return Array.isArray(json?.holds)?json.holds:[];
  }

  async function fetchBookingDetails(item){
    if(!item?.regId) return null;
    const u=new URL(cfg.detailsUrl,location.origin);
    u.searchParams.set('regId',item.regId);
    u.searchParams.set('paymentId',item.paymentId||0);
    const json=await api(u);
    return json.data;
  }

  async function openDetails(item){
    if(!item?.regId) return;
    closeContext();
    drawer.classList.add('open');
    drawer.setAttribute('aria-hidden','false');
    $('fdcAvatar').textContent=initials(item.guestName||item.regId);
    $('fdcDrawerTitle').textContent=item.guestName||'Guest';
    $('fdcDrawerSub').textContent=`Room ${item.roomNo||'Unassigned'} · Loading…`;
    drawerBody.innerHTML='<div class="fdc-drawer-loading"><span></span><b>Loading reservation…</b></div>';
    if(drawerPay) drawerPay.innerHTML='';
    drawerFoot.innerHTML='';
    try{
      const d=await fetchBookingDetails(item);
      if(!d) return;
      state.currentDetails=d;

      // A hold lookup must never stop reservation details from opening.
      // If it fails, keep the normal drawer working and show no hold panel.
      let holds=[];
      try{holds=await fetchReservationHolds(d.regId||item.regId);}catch(_){holds=[];}
      state.currentHolds=holds;

      $('fdcAvatar').textContent=initials(d.guestName);
      $('fdcDrawerTitle').textContent=d.guestName||'Guest';
      const rawStatus=normalizeStatus(d.status);
      const checked=isCheckedInStatus(rawStatus);
      const closed=isCheckedOutStatus(rawStatus);
      $('fdcDrawerSub').textContent=`Room ${d.roomNo||'Unassigned'} · ${checked?'CHECK IN':closed?'CHECKED OUT':'RESERVATION'}`;
      drawerBody.innerHTML=detailsHtml(d,holds);
      if(drawerPay){
        drawerPay.innerHTML=Number(d.balance||0)>0.005
          ? `<button type="button" class="fdc-pay-now" data-drawer-action="payment">Pay Now · ${money(d.balance)}</button>`
          : '';
      }
      drawerFoot.innerHTML=detailsActions(d);
    }catch(e){
      drawerBody.innerHTML=`<div class="fdc-panel"><b>Unable to load reservation.</b><p>${esc(e.message)}</p></div>`;
      showToast(e.message,true);
    }
  }

  function detailsHtml(d,holds=[]){
    const s=normalizeStatus(d.status);
    const checked=isCheckedInStatus(s);
    const closed=isCheckedOutStatus(s);
    const balance=Number(d.balance||0);
    const stateClass=checked?'green':closed?'amber':balance>0.005?'red':'blue';
    const stateText=checked?'Checked In':closed?'Checked Out':(d.status||'Reservation');
    const row=(label,value)=>`<div class="fdc-kv"><span>${label}</span><b>${value}</b></div>`;
    const logs=(d.payments||[]).map(p=>`<div class="fdc-payment-log-entry"><span>${fmt(p.date,true)} · ${esc(p.method||'Payment')}${p.reference?` · ${esc(p.reference)}`:''}</span><strong>${money(p.amount)}</strong></div>`).join('');
    const holdPanel=paymentHoldsHtml(holds);

    return `<div class="fdc-panel"><h4>Booking information</h4>
        ${row('Reference #',esc(d.regId||'—'))}
        ${row('Booking #',esc(d.bookingNo||'—'))}
        ${row('Contact #',esc(d.phone||'—'))}
        ${row('Email',esc(d.email||'—'))}
        ${d.createdAt?row('Created At',fmt(d.createdAt,true)):''}
      </div>
      <div class="fdc-panel"><h4>${checked?'Checked-in stay':'Reservation stay'}</h4>
        ${row('Status',`<i class="fdc-badge ${stateClass}">${esc(stateText)}</i>`)}
        ${row('Arrival',fmt(d.arrival,true))}
        ${row('Departure',fmt(d.departure,true))}
        ${row('Total Nights',`${d.nights||Math.max(1,diffDays(d.arrival,d.departure))} Nights`)}
        ${row('Room',esc(d.roomNo||'Unassigned'))}
        ${row('Room Category',esc(d.categoryName||'—'))}
        ${row('Occupancy',`${Number(d.adults||0)} Adults · ${Number(d.children||0)} Children${Number(d.infants||0)?` · ${Number(d.infants)} Infants`:''}`)}
        ${row('Source',esc(d.source||'—'))}
      </div>
      <div class="fdc-panel"><h4>Charges & Payments</h4>
        ${row('Total',money(d.total))}
        ${row('Payable',money(d.total))}
        ${row('Deposits / Paid',money(d.paid))}
        ${row('Balance',`<span class="${balance>0.005?'fdc-amount-due':'fdc-amount-settled'}">${money(balance)}</span>`)}
        ${balance>0.005?`<div class="fdc-payment-due-note">${money(balance)} remains to be collected.</div>`:'<div class="fdc-payment-settled-note">Payment received in full.</div>'}
        ${logs?`<div class="fdc-payment-log"><strong>Payment history</strong>${logs}</div>`:''}
      </div>
      ${holdPanel}
      ${d.frontDeskNotes?`<div class="fdc-panel"><h4>Notebook</h4><p class="fdc-drawer-notes">${esc(d.frontDeskNotes)}</p></div>`:''}
      ${d.notes?`<div class="fdc-panel"><h4>Notes</h4><p class="fdc-drawer-notes">${esc(d.notes)}</p></div>`:''}`;
  }

  function paymentHoldsHtml(holds){
    if(!Array.isArray(holds)||!holds.length) return '';

    const rows=holds.map(h=>{
      const currency=String(h.currency||cfg.currencyCode||'GBP').toUpperCase();
      const factor=holdMinorFactor(currency);
      const amountMajor=holdMajor(h.amountMinor,currency);
      const card=[h.cardBrand,String(h.last4||'').trim()?`•••• ${h.last4}`:''].filter(Boolean).join(' · ');
      const source=String(h.source||'').toLowerCase();
      const sourceLabel=source==='checkout_hold'?'Online Card':(source==='pdq_terminal'?'PDQ Terminal':'Card Hold');
      const releaseAllowed=h.canRelease!==false;
      const releaseTitle=releaseAllowed?'Release this authorization':'Stripe Checkout authorization cannot be canceled after Checkout completes; capture it or allow it to expire.';

      return `<div class="fdc-hold-card" data-hold-pi="${esc(h.paymentIntentId||'')}" data-hold-minor="${Number(h.amountMinor||0)}" data-hold-currency="${esc(currency)}">
        <div class="fdc-hold-summary">
          <div><b>${money(amountMajor)} authorized</b><small>${esc(sourceLabel)}${card?` · ${esc(card)}`:''}</small></div>
          <span class="fdc-hold-status">HOLD</span>
        </div>
        ${h.description?`<div class="fdc-hold-note">${esc(h.description)}</div>`:''}
        <div class="fdc-hold-capture">
          <label>Capture amount</label>
          <input type="number" class="fdc-hold-capture-input" min="${factor===1?'1':'0.01'}" step="${factor===1?'1':'0.01'}" max="${amountMajor}" value="${amountMajor.toFixed(factor===1?0:2)}" inputmode="decimal" />
        </div>
        <div class="fdc-hold-buttons">
          <button type="button" class="fdc-hold-btn capture" data-drawer-hold-action="capture">Capture</button>
          <button type="button" class="fdc-hold-btn release" data-drawer-hold-action="release" ${releaseAllowed?'':`disabled title="${esc(releaseTitle)}"`}>Release</button>
        </div>
      </div>`;
    }).join('');

    return `<div class="fdc-panel fdc-holds-panel"><h4>Authorized Payment Holds</h4>
      <div class="fdc-holds-help">Capture the full amount or enter a smaller amount for partial capture. A final partial capture releases the unused authorization.</div>
      <div class="fdc-holds-list">${rows}</div>
    </div>`;
  }

  async function drawerHoldAction(button){
    const d=state.currentDetails;
    const row=button?.closest('.fdc-hold-card');
    if(!d||!row||button.disabled) return;

    const action=button.dataset.drawerHoldAction;
    const paymentIntentId=String(row.dataset.holdPi||'').trim();
    const currency=String(row.dataset.holdCurrency||cfg.currencyCode||'GBP').toUpperCase();
    const authorizedMinor=Number(row.dataset.holdMinor||0);
    if(!paymentIntentId||!action) return;

    let amountMinor=null;
    if(action==='capture'){
      const input=row.querySelector('.fdc-hold-capture-input');
      const major=Number(input?.value||0);
      amountMinor=holdMinor(major,currency);
      if(!(amountMinor>0)){showToast('Enter a valid capture amount.',true);input?.focus();return;}
      if(authorizedMinor>0&&amountMinor>authorizedMinor){showToast('Capture amount cannot exceed the authorized hold.',true);input?.focus();return;}
      if(authorizedMinor>0&&amountMinor<authorizedMinor){
        if(!confirm(`Capture ${money(major)} from this hold? The remaining authorization will be released by Stripe.`)) return;
      }
    }else if(action==='release'){
      if(!confirm('Release this card authorization without taking payment?')) return;
    }

    const urls=paymentHoldUrls();
    const endpoint=action==='capture'?urls.capture:urls.release;
    setButtonBusy(button,true,action==='capture'?'Capturing…':'Releasing…');
    try{
      const body={regId:d.regId,paymentIntentId};
      if(action==='capture') body.amountMinor=amountMinor;
      const j=await api(endpoint,{method:'POST',body:JSON.stringify(body)});
      if(j?.ok===false) throw new Error(j.message||`Unable to ${action} hold.`);
      showToast(j?.message||`Hold ${action==='capture'?'captured':'released'}.`);
      await openDetails({regId:d.regId,paymentId:d.paymentId,guestName:d.guestName,roomNo:d.roomNo});
      await loadCalendar(state.start,true);
    }catch(e){
      showToast(e.message||`Unable to ${action} hold.`,true);
      setButtonBusy(button,false);
    }
  }

  function detailsActions(d){
    const s=normalizeStatus(d.status);
    const checked=isCheckedInStatus(s);
    const closed=isCheckedOutStatus(s);
    const provisional=s.includes('provisional');
    const actions=[];
    const trailing=[];
    const action=(icon,label,key,tone='')=>`<button class="fdc-drawer-action ${tone}" type="button" data-drawer-action="${key}"><span class="fdc-action-icon">${icon}</span><span class="fdc-action-text">${label}</span></button>`;
    if(checked){
      actions.push(action('✎','Edit','edit'));
      actions.push(action('↶','Undo Check-in','undo'));
      actions.push(action('▤','Note Book','note'));
      actions.push(action('◷','Guest History','history'));
      actions.push(action('▧','Invoice','invoice'));
    }else if(closed){
      actions.push(action('◷','Guest History','history'));
      actions.push(action('▧','Invoice','invoice'));
    }else if(provisional){
      actions.push(action('✓','Confirm Reservation','confirm'));
      actions.push(action('◷','Guest History','history'));
      actions.push(action('×','Cancel Reservation','cancel','danger-action'));
    }else{
      actions.push(action('➜','Check-In Now','checkin-now','primary-action'));
      actions.push(action('▣','Edit','edit'));
      actions.push(action('▤','Note Book','note'));
      actions.push(action('◷','Guest History','history'));
      actions.push(action('▧','Invoice','invoice'));
      actions.push(action('×','Cancel Reservation','cancel','danger-action'));
    }
    if(Number(d.balance||0)>0.005){
      actions.push(action('£','Record Payment','payment','payment-action'));
    }
    actions.push(action('✉','Send Email','sendpaylink','payment-action'));

    // Room assignment and checkout are terminal actions and stay at the bottom.
    if(d.roomNo && String(d.roomNo).toUpperCase()!=='UNASSIGNED'){
      trailing.push(action('↧','Move to Unassigned','unassign','assignment-action'));
    }
    if(checked){
      trailing.push(action('⇥','Check-Out','checkout','checkout-action'));
    }
    return `<button type="button" class="fdc-drawer-actions-toggle" data-drawer-actions-toggle aria-expanded="false"><span>Actions</span><span class="chevron">⌄</span></button><div class="fdc-drawer-actions" data-drawer-actions-list hidden>${actions.join('')}${trailing.length?`<div class="fdc-drawer-actions-terminal">${trailing.join('')}</div>`:''}</div>`;
  }

  function openCheckInEditor(d,button=null){
    if(!d?.regId){showToast('Reservation reference is missing.',true);return;}
    setButtonBusy(button,true,'Opening…');
    const u=new URL(cfg.checkinUrl,location.origin);
    // RI keeps exact legacy/WebForms navigation compatibility; q is a safe raw fallback.
    u.searchParams.set('RI',base64(d.regId));
    u.searchParams.set('q',d.regId);
    location.href=u.toString();
  }

  async function drawerAction(action,button=null){
    const d=state.currentDetails;if(!d)return;
    if(action==='checkin-now'){ await directCheckIn(d,button); return; }
    if(action==='edit'){
      openCheckInEditor(d,button);
      return;
    }
    if(action==='history'){openHistoryPage(d);return;}
    if(action==='payment'){openPayment(d,button);return;}
    if(action==='cardpayment' || action==='pdqpayment'){openPayment(d);return;}
    if(action==='sendpaylink'){openSendPaymentLink(d,button);return;}
    if(action==='unassign'){await moveToUnassigned(d,button);return;}
    if(action==='invoice'){openInvoice(d);return;}
    if(action==='confirm'){
      openCheckInEditor(d);
      return;
    }
    if(action==='note'){openNote(d);return;}
    if(action==='cancel'){ openCancelReservation(d); return; }
    if(action==='undo'){
      if(!confirm(`Undo check-in for ${d.guestName}?`))return;
      await mutate(cfg.undoUrl,{regId:d.regId},'Undoing check-in…',button);closeDrawer();await loadCalendar(state.start,true);return;
    }
    if(action==='checkout'){openCheckout(d);return;}
  }

  async function moveToUnassigned(d,button=null){
    if(!d?.regId || !d?.paymentId){showToast('Reservation room row is missing.',true);return;}
    if(String(d.roomNo||'').toUpperCase()==='UNASSIGNED'){showToast('This room is already unassigned.');return;}
    if(!confirm(`Move room ${d.roomNo} for ${d.guestName||'this guest'} to Unassigned?`)) return;
    try{
      await mutate(cfg.moveUrl,{
        regId:d.regId,
        paymentId:d.paymentId,
        newRoomNo:'',
        targetCategoryId:d.categoryId||'',
        targetCategoryName:d.categoryName||'',
        newArrival:iso(d.arrival),
        nightlyRateOverride:null
      },'Moving room to Unassigned…',button);
      closeDrawer();
      await loadCalendar(state.start,true);
    }catch{}
  }

  function openCancelReservation(d){
    if(!d?.regId){ showToast('Reservation reference is missing.',true); return; }
    const guest=d.guestName||'this guest';
    const body=`
      <div class="fdc-cancel-subtitle">Please provide a reason. This action will remove the reservation from the system.</div>
      <div class="fdc-cancel-meta"><b>${esc(guest)}</b><span>${esc(d.regId)}</span></div>
      <div class="fdc-cancel-field">
        <label for="fdcCancelReason">Reason <span>(required)</span></label>
        <textarea id="fdcCancelReason" rows="4" maxlength="500" placeholder="e.g., Guest requested cancellation / No payment received / Duplicate booking..."></textarea>
        <small>Keep it short — this reason will be stored in the cancellation record.</small>
        <div id="fdcCancelReasonError" class="fdc-cancel-error" hidden>Please enter a cancellation reason.</div>
      </div>`;
    const foot=`<button type="button" class="fdc-btn" data-close-modal>Close</button><button type="button" class="fdc-btn danger" id="fdcConfirmCancel">Confirm Cancel</button>`;
    openModal('Cancel Reservation',body,foot,'fdc-cancel-mode');
    const ta=$('fdcCancelReason'),err=$('fdcCancelReasonError'),btn=$('fdcConfirmCancel');
    setTimeout(()=>ta?.focus(),40);
    btn.onclick=async()=>{
      const reason=(ta?.value||'').trim();
      if(!reason){ if(err)err.hidden=false; ta?.focus(); return; }
      if(err)err.hidden=true;
      try{
        await mutate(cfg.cancelUrl,{regId:d.regId,paymentId:d.paymentId||0,reason},'Cancelling…',btn);
        closeModal(); closeDrawer(); await loadCalendar(state.start,true);
      }catch{}
    };
    ta?.addEventListener('keydown',e=>{
      if(e.key==='Enter' && !e.shiftKey){ e.preventDefault(); btn.click(); }
    });
  }

  async function directCheckIn(d,button=null){
    if(!confirm(`Check in ${d.guestName||'this guest'} now?`)) return;
    try{
      await mutate(cfg.directCheckinUrl,{regId:d.regId,paymentId:d.paymentId},'Checking in…',button);
      closeDrawer();
      await loadCalendar(state.start,true);
    }catch{}
  }

  function openHistoryPage(d){
    if(!d?.regId){showToast('Reservation reference is missing.',true);return;}
    const regB64=base64(d.regId);
    const u=legacyPageUrl('Reservation_History.aspx');

    // Exact WebForms calendar navigation contract.
    u.searchParams.set('UD',queryOrB64('UD',cfg.userId));
    u.searchParams.set('UN',queryOrB64('UN',cfg.userName));
    u.searchParams.set('cc',regB64);
    u.searchParams.set('vs',regB64);
    u.searchParams.set('hd',queryOrB64('hd',cfg.hotelId));
    u.searchParams.set('rl',queryOrB64('rl',cfg.role));
    u.searchParams.set('RS','');
    u.searchParams.set('RI',regB64);
    u.searchParams.set('hr',queryOrB64('hr',cfg.role));
    u.searchParams.set('hn',queryOrB64('hn',cfg.hotelName));

    const w=window.open(u.toString(),'ReservationHistoryTab','noopener');
    if(!w) showToast('Please allow pop-ups for this website.',true);
  }

  function openInvoice(d){
    if(!d?.regId){showToast('Reservation reference is missing.',true);return;}
    const u=legacyPageUrl('InvoiceRecieving.aspx');

    // WebForms btnViewInvoice uses URL-safe base64 for these two values.
    u.searchParams.set('reg_id',base64Url(d.regId));
    u.searchParams.set('hotel_id',base64Url(cfg.hotelId||''));

    const w=window.open(u.toString(),'_blank','noopener');
    if(!w) showToast('Please allow pop-ups for this website.',true);
  }

  async function openSendPaymentLink(d,button=null){
    if(!d?.regId){showToast('Reservation reference is missing.',true);return;}
    setButtonBusy(button,true,'Preparing…');
    if(button) await waitForPaint();
    try{
      const prepareUrl = calendarActionUrl('PrepareEmail');
      const j=await api(prepareUrl,{method:'POST',body:JSON.stringify({regId:d.regId,paymentId:d.paymentId||0})});
      if(j.ok===false) throw new Error(j.message||'Unable to prepare the email.');
      openModal('Send Email','', '', 'fdc-professional-mode');
      renderEmailComposer(d,j.data||{});
    }catch(e){
      showToast(e.message||'Unable to prepare the email.',true);
    }finally{
      setButtonBusy(button,false);
    }
  }

  function renderEmailComposer(d,data){
    const genericMessage=data.genericMessage||`Hello ${data.guestName||d.guestName||'Guest'},\n\n`;
    const tabs=[
      {key:'GEN',label:'Email',badge:'MSG',url:'',message:genericMessage},
      {key:'PAY',label:'Payment Link',badge:'PAY',url:data.paymentUrl||'',message:data.paymentMessage||'',disabled:!data.paymentUrl},
      {key:'INV',label:'Invoice Link',badge:'INV',url:data.invoiceUrl||'',message:data.invoiceMessage||''},
      {key:'INVPDF',label:'Invoice PDF',badge:'PDF',url:data.invoicePdfUrl||'',message:data.invoicePdfMessage||''}
    ];
    let active='GEN';

    modalTitle.textContent='✉ Send Email';
    modalBody.innerHTML=`<div class="fdc-email-composer">
      <div class="fdc-email-subtitle">Write any guest message, or choose an invoice/payment template.</div>
      <div class="fdc-email-tabs">
        ${tabs.map((t,i)=>`<button type="button" class="fdc-email-tab ${i===0?'active':''}" data-email-tab="${t.key}" ${t.disabled?'disabled aria-disabled="true"':''}>${esc(t.label)} <span>${esc(t.badge)}</span></button>`).join('')}
      </div>
      <div class="fdc-email-field">
        <label>To (Email)</label>
        <input id="fdcEmailTo" type="email" value="${esc(data.email||d.email||'')}" placeholder="guest@example.com">
      </div>
      <div class="fdc-email-field">
        <label>Message</label>
        <textarea id="fdcEmailMessage" rows="10"></textarea>
      </div>
      <div class="fdc-email-tip">Tip: You can edit the email address and message before sending.</div>
    </div>`;

    modalFoot.innerHTML=`<button class="fdc-btn" data-close-modal>Cancel</button>
      <button class="fdc-btn primary fdc-email-send" id="fdcSendEmail"><span>✉</span><span id="fdcSendEmailText">Send Email</span> →</button>`;

    const getTab=()=>tabs.find(x=>x.key===active)||tabs[0];
    const refresh=()=>{
      const t=getTab();
      $('fdcEmailMessage').value=t.message||'';
      $('fdcSendEmailText').textContent='Send Email';
      modalBody.querySelectorAll('[data-email-tab]').forEach(btn=>btn.classList.toggle('active',btn.dataset.emailTab===active));
    };

    modalBody.querySelectorAll('[data-email-tab]').forEach(btn=>{
      btn.onclick=()=>{
        if(btn.disabled) return;
        const current=getTab();
        current.message=$('fdcEmailMessage')?.value||current.message;
        active=btn.dataset.emailTab||'GEN';
        refresh();
      };
    });

    $('fdcSendEmail').onclick=async()=>{
      const tab=getTab();
      const email=$('fdcEmailTo').value.trim();
      const message=$('fdcEmailMessage').value.trim();
      if(!email){showToast('Guest email is required.',true);return;}
      if(!message){showToast('Email message is required.',true);return;}
      if(tab.key!=='GEN' && !tab.url){showToast('The selected email link is not available.',true);return;}

      const btn=$('fdcSendEmail');
      setButtonBusy(btn,true,'Sending…');
      try{
        const sendUrl = calendarActionUrl('SendEmail');
        const j=await api(sendUrl,{
          method:'POST',
          body:JSON.stringify({
            regId:d.regId,
            email,
            emailType:tab.key,
            message,
            url:tab.url||''
          })
        });
        if(j.ok===false) throw new Error(j.message||'Unable to send email.');
        showToast(j.message||'Email sent successfully.');
        closeModal();
      }catch(e){
        showToast(e.message||'Unable to send email.',true);
      }finally{
        setButtonBusy(btn,false);
      }
    };

    refresh();
  }

  function openNote(d){
    const quickTags=['VIP Guest','Payment Required in Advance','Late Arrival','Early Check-In Requested','Late Check-Out Requested','Quiet Room Preferred','Special Assistance','Follow-up Required'];
    openModal('Note Book',`<div class="fdc-note-shell">
      <div class="fdc-note-label">Notes for ${esc(d.guestName||'Guest')}</div>
      <div class="fdc-note-help">Quick tags · click to add to the notes</div>
      <div class="fdc-note-tags">${quickTags.map(x=>`<button type="button" data-note-tag="${esc(x)}">${esc(x)}</button>`).join('')}</div>
      <div class="fdc-field"><textarea id="fdcNote" rows="5">${esc(d.frontDeskNotes||'')}</textarea></div>
    </div>`,`<button class="fdc-btn" data-close-modal>Cancel</button><button class="fdc-btn primary" id="saveNote">Save notes</button>`,'fdc-professional-mode fdc-note-mode');
    modalBody.querySelectorAll('[data-note-tag]').forEach(btn=>btn.addEventListener('click',()=>{
      const ta=$('fdcNote');
      const tag=btn.dataset.noteTag||'';
      const current=(ta.value||'').trim();
      if(!current.toLowerCase().includes(tag.toLowerCase())) ta.value=current?`${current}
${tag}`:tag;
      ta.focus();
    }));
    $('saveNote').onclick=async e=>{const btn=e.currentTarget;await mutate(cfg.noteUrl,{regId:d.regId,notes:$('fdcNote').value},'Saving…',btn);d.frontDeskNotes=$('fdcNote').value;closeModal();await loadCalendar(state.start,true);await openDetails({regId:d.regId,paymentId:d.paymentId});};
  }
  async function openHistory(d){
    busy(true,'Loading guest history…');
    try{const u=new URL(cfg.historyUrl,location.origin);u.searchParams.set('regId',d.regId);const j=await api(u);const h=j.data;const rows=(h.stays||[]).map(x=>`<div class="fdc-kv"><span>${fmt(x.arrival,true)} – ${fmt(x.departure,true)} · ${esc(x.status)}</span><b>${esc(x.roomNo||'—')}</b></div>`).join('')||'<p>No previous stays found.</p>';openModal('Guest Stay History',`<div class="fdc-panel"><h4>${esc(h.guestName||d.guestName)}</h4>${rows}</div>`,'<button class="fdc-btn" data-close-modal>Close</button>');}catch(e){showToast(e.message,true);}finally{busy(false);}
  }
  function openPayment(d,button=null,options={}){
    if(!window.RecordPayment || typeof window.RecordPayment.open!=='function'){
      showToast('Record Payment component is not loaded.',true);
      return;
    }

    window.RecordPayment.open({
      regId:d.regId,
      visitId:d.visitId||'',
      paymentId:d.paymentId||0,
      roomNo:d.roomNo||'',
      guestName:d.guestName||'Guest',
      balance:Number(d.balance||0),
      currencySymbol:state.currency||'£',
      currencyCode:cfg.currencyCode||'GBP',
      hotelId:cfg.hotelId||'',
      userId:cfg.userId||'',
      arrival:d.arrival||'',
      departure:d.departure||'',
      source:d.source||'NR',
      status:d.status||'',
      showAutoPay:d.showAutoPay===true,
      isVirtualCard:d.isVirtualCard===true,
      // Online Card / PDQ are property + permission specific, resolved server-side.
      showOnlineCard:cfg.canOnlineCard==='1',
      showPdqPayment:cfg.canPdqPayment==='1',
      channexBookingId:d.channexBookingId||'',
      focusHolds:options?.focusHolds===true,
      urls:{
        recordPaymentUrl:cfg.paymentUrl||'/CheckIn/RecordPayment',
        pdqUrl:cfg.pdqUrl||'/TerminalCardPayment.aspx',
      },
      onCompleted:async()=>{
        await openDetails({regId:d.regId,paymentId:d.paymentId});
        await loadCalendar(state.start,true);
      }
    });
  }

  function openCheckout(d){
    const balance=Number(d.balance||0);
    openModal(
      'Check-Out',
      `<div class="fdc-info">${balance>0.005
        ? `Outstanding balance: <b>${money(balance)}</b>. Please record/settle the payment before checkout.`
        : `Confirm checkout for <b>${esc(d.guestName||'this guest')}</b>${d.roomNo?` · Room ${esc(d.roomNo)}`:''}.`}</div>`,
      `<button class="fdc-btn" data-close-modal>Cancel</button><button class="fdc-btn danger" id="confirmCheckout">Check-Out</button>`
    );
    $('confirmCheckout').onclick=async e=>{
      const btn=e.currentTarget;
      await mutate(cfg.checkoutUrl,{regId:d.regId,paymentMethod:'',force:false},'Checking out…',btn);
      closeModal();closeDrawer();await loadCalendar(state.start,true);
    };
  }

  async function mutate(url, body, label='Saving…', button=null){
    setButtonBusy(button,true,label);
    if(button) await waitForPaint();
    try{
      const j=await api(url,{method:'POST',body:JSON.stringify(body)});
      if(j.ok===false){ const err=new Error(j.message||'Action failed.');err.data=j.data;throw err; }
      showToast(j.message||'Saved.'); return j;
    }catch(e){
      showToast(e.message,true);
      throw e;
    }finally{
      setButtonBusy(button,false);
    }
  }

  function openBlockDetails(item){
    const endInclusive=addDays(dateOnly(item.endDate),-1);
    const body=`<div class="fdc-pro-summary">
      <div class="fdc-pro-summary-icon">▣</div>
      <div><span>Blocked room</span><b>Room ${esc(item.roomNo)}</b><small>${esc(item.categoryName)} · ${fmt(item.startDate,true)} – ${fmt(endInclusive,true)}</small></div>
    </div>
    <div class="fdc-block-detail-card">
      <div class="fdc-kv"><span>Room</span><b>${esc(item.roomNo)}</b></div>
      <div class="fdc-kv"><span>Category</span><b>${esc(item.categoryName)}</b></div>
      <div class="fdc-kv"><span>Dates</span><b>${fmt(item.startDate,true)} – ${fmt(endInclusive,true)}</b></div>
      <div class="fdc-kv"><span>Type</span><b>${esc(item.kind||'Room Block')}</b></div>
      <div class="fdc-kv"><span>Reason</span><b>${esc(item.reason||'—')}</b></div>
    </div>`;
    openModal('Room Block',body,`<button class="fdc-btn fdc-btn-secondary" data-close-modal>Close</button><button class="fdc-btn" id="editBlock">Edit</button><button class="fdc-btn success" id="activateBlock">Activate from today</button><button class="fdc-btn danger" id="removeBlock">Remove block</button>`,'fdc-professional-mode fdc-block-details-mode');
    $('editBlock').onclick=()=>openBlockForm(item,item);
    $('activateBlock').onclick=async e=>{const btn=e.currentTarget;try{await mutate(cfg.activateRoomUrl,{blockId:item.blockId,roomNo:item.roomNo,categoryId:item.categoryId,categoryName:item.categoryName},'Activating…',btn);closeModal();await loadCalendar(state.start,true);}catch{}};
    $('removeBlock').onclick=async e=>{if(!confirm('Remove this room block?'))return;const btn=e.currentTarget;try{await mutate(cfg.removeBlockUrl,{blockId:item.blockId,roomNo:item.roomNo,categoryId:item.categoryId,categoryName:item.categoryName},'Removing…',btn);closeModal();await loadCalendar(state.start,true);}catch{}};
  }


  function openCellContext(cell,x,y){
    closeContext(true);state.selectedCell?.classList.remove('selected');state.selectedCell=cell;cell.classList.add('selected');
    const room=cell.dataset.room, dirty=cell.classList.contains('has-dirty');
    context.innerHTML=`<button data-ctx="reserve">New Reservation</button>${String(room).toUpperCase()!=='UNASSIGNED'?'<button data-ctx="block">Block Room</button>':''}${dirty?'<button data-ctx="clean">Mark Clean</button>':''}`;
    context.style.left=`${Math.min(x,innerWidth-225)}px`;context.style.top=`${Math.min(y,innerHeight-150)}px`;context.classList.add('open');
  }

  function showContextAt(x,y){
    context.classList.add('open');
    requestAnimationFrame(()=>{
      const pad=8;
      const rect=context.getBoundingClientRect();
      context.style.left=`${Math.max(pad,Math.min(x,innerWidth-rect.width-pad))}px`;
      context.style.top=`${Math.max(pad,Math.min(y,innerHeight-rect.height-pad))}px`;
    });
  }

  function bookingMenuItem(icon,label,key,tone=''){
    return `<button type="button" class="fdc-booking-menu-action ${tone}" data-booking-ctx="${key}"><span class="menu-icon" aria-hidden="true">${icon}</span><span class="menu-label">${label}</span></button>`;
  }

  function openBookingContext(item,x,y){
    if(!item?.regId) return;
    closeContext(true);
    const code=String(item.statusCode||'').toUpperCase();
    const checked=code==='O' || isCheckedInStatus(item.status);
    const closed=code==='CO' || isCheckedOutStatus(item.status);
    const provisional=code==='P' || normalizeStatusKey(item.status)==='provisional';
    const guest=item.guestName||item.regId||'Guest';
    const room=item.roomNo||'Unassigned';
    const statusLabel=checked?'Checked in':closed?'Checked out':provisional?'Provisional':'Reservation';
    const actions=[];
    const terminal=[];

    if(checked){
      actions.push(bookingMenuItem('✎','Edit','edit'));
      actions.push(bookingMenuItem('↶','Undo Check-in','undo'));
      actions.push(bookingMenuItem('▤','Note Book','note'));
      actions.push(bookingMenuItem('◷','Guest History','history'));
      actions.push(bookingMenuItem('▧','Invoice','invoice'));
    }else if(closed){
      actions.push(bookingMenuItem('◷','Guest History','history'));
      actions.push(bookingMenuItem('▧','Invoice','invoice'));
    }else if(provisional){
      actions.push(bookingMenuItem('✓','Confirm Reservation','confirm'));
      actions.push(bookingMenuItem('◷','Guest History','history'));
      actions.push(bookingMenuItem('×','Cancel Reservation','cancel','danger-action'));
    }else{
      actions.push(bookingMenuItem('➜','Check-In Now','checkin','primary-action'));
      actions.push(bookingMenuItem('▣','Edit','edit'));
      actions.push(bookingMenuItem('▤','Note Book','note'));
      actions.push(bookingMenuItem('◷','Guest History','history'));
      actions.push(bookingMenuItem('▧','Invoice','invoice'));
      actions.push(bookingMenuItem('×','Cancel Reservation','cancel','danger-action'));
    }

    if(Number(item.balance||0)>0.005){
      actions.push(bookingMenuItem('£','Record Payment','payment','payment-action'));
    }
    actions.push(bookingMenuItem('✉','Send Email','sendpaylink','payment-action'));

    if(item.roomNo && String(item.roomNo).toUpperCase()!=='UNASSIGNED'){
      terminal.push(bookingMenuItem('↧','Move to Unassigned','unassign','assignment-action'));
    }
    if(checked){
      terminal.push(bookingMenuItem('⇥','Check-Out','checkout','checkout-action'));
    }

    context.classList.add('booking-menu');
    context.dataset.booking=JSON.stringify(item);
    context.innerHTML=`
      <div class="fdc-booking-menu-head">
        <div class="fdc-booking-menu-avatar">${esc(initials(guest))}</div>
        <div class="fdc-booking-menu-guest"><b>${esc(guest)}</b><small>Room ${esc(room)} · ${esc(statusLabel)}</small></div>
      </div>
      <div class="fdc-booking-menu-body">
        <div class="fdc-booking-menu-section-title">Booking actions</div>
        <div class="fdc-booking-menu-actions">${actions.join('')}</div>
        ${terminal.length?`<div class="fdc-booking-menu-divider"></div><div class="fdc-booking-menu-actions fdc-booking-menu-terminal">${terminal.join('')}</div>`:''}
      </div>`;
    showContextAt(x,y);
  }

  async function bookingContextAction(action,item,button=null){
    if(!item?.regId) return;

    // Confirm destructive actions before switching the clicked item into loader mode.
    if(action==='undo' && !confirm(`Undo check-in for ${item.guestName||'this guest'}?`)) return;

    const labels={
      checkin:'Checking in…', edit:'Opening…', cancel:'Opening…', undo:'Undoing…',
      note:'Loading…', history:'Loading…', payment:'Loading…', holds:'Loading holds…', sendpaylink:'Preparing…',
      autopay:'Opening…', invoice:'Opening…', confirm:'Opening…', checkout:'Loading…',
      unassign:'Moving…'
    };
    const label=labels[action]||'Working…';
    const started=performance.now();
    // Keep the loading state on-screen long enough to be visually useful.
    // More importantly, lock the context menu before yielding so none of the
    // document/scroll close handlers can remove it while the request runs.
    const minBusyMs=700;
    let samePageNavigation=false;
    let closeAfter=false;
    let deferredAction=null;

    const waitMin=async()=>{
      const elapsed=performance.now()-started;
      if(elapsed<minBusyMs) await new Promise(resolve=>setTimeout(resolve,minBusyMs-elapsed));
    };

    setButtonBusy(button,true,label);
    context.classList.add('fdc-context-processing');
    context.dataset.processing='1';

    // Force layout + two animation frames.  This guarantees the spinner is
    // painted before fetch/navigation work starts, even on very fast requests.
    if(button){
      void button.offsetWidth;
      await waitForPaint();
      await new Promise(resolve=>setTimeout(resolve,80));
    }

    try{
      if(action==='unassign'){
        await moveToUnassigned(item,null);
        closeAfter=true;
      }
      else if(action==='checkin'){
        await mutate(cfg.directCheckinUrl,{regId:item.regId,paymentId:item.paymentId||0},'Checking in…',null);
        await loadCalendar(state.start,true);
        closeAfter=true;
      }
      else if(action==='edit'){
        const d=await fetchBookingDetails(item);
        if(!d) return;
        state.currentDetails=d;
        samePageNavigation=true;
        await waitMin();
        openCheckInEditor(d,null);
      }
      else if(action==='cancel'){
        const d=await fetchBookingDetails(item);
        if(!d) return;
        state.currentDetails=d;
        closeAfter=true;
        deferredAction=()=>openCancelReservation(d);
      }
      else if(action==='undo'){
        await mutate(cfg.undoUrl,{regId:item.regId},'Undoing…',null);
        await loadCalendar(state.start,true);
        closeAfter=true;
      }
      else{
        const d=await fetchBookingDetails(item);
        if(!d) return;
        state.currentDetails=d;

        if(action==='note'){closeAfter=true;deferredAction=()=>openNote(d);}
        else if(action==='history'){closeAfter=true;deferredAction=()=>openHistoryPage(d);}
        else if(action==='payment'){closeAfter=true;deferredAction=()=>openPayment(d);}
        else if(action==='sendpaylink'){closeAfter=true;deferredAction=()=>openSendPaymentLink(d,null);}
        else if(action==='invoice'){closeAfter=true;deferredAction=()=>openInvoice(d);}
        else if(action==='confirm'){
          samePageNavigation=true;
          await waitMin();
          openCheckInEditor(d,null);
        }
        else if(action==='checkout'){closeAfter=true;deferredAction=()=>openCheckout(d);}
      }

      await waitMin();
      if(closeAfter){
        // Open the destination UI first where applicable, then remove the
        // context menu.  Until this point the user keeps seeing the spinner.
        if(deferredAction) deferredAction();
        closeContext(true);
      }
    }catch(e){
      showToast(e.message,true);
      await waitMin();
      setButtonBusy(button,false);
      context.classList.remove('fdc-context-processing');
      context.removeAttribute('data-processing');
    }finally{
      if(!samePageNavigation && !closeAfter && context.classList.contains('open')){
        await waitMin();
        setButtonBusy(button,false);
        context.classList.remove('fdc-context-processing');
        context.removeAttribute('data-processing');
      }
    }
  }

  function bookingFrom(el){try{return JSON.parse(el.dataset.booking||'{}');}catch{return null;}}
  function blockFrom(el){try{return JSON.parse(el.dataset.block||'{}');}catch{return null;}}

  // ---------------------------------------------------------------------------
  // WebForms-style Move Room spotlight
  // ---------------------------------------------------------------------------
  // The legacy FrontDeskCalender keeps the calendar readable while a booking is
  // dragged: everything outside the calendar is softly blurred, unrelated bars
  // are muted, the dragged booking stays sharp, and only the destination room +
  // affected date headers + one exact landing bar are highlighted.
  function ensureDragFocusLayer(){
    let layer=document.getElementById('fdcDragFocusLayer');
    if(layer) return layer;
    layer=document.createElement('div');
    layer.id='fdcDragFocusLayer';
    layer.setAttribute('aria-hidden','true');
    layer.innerHTML='<div class="fdc-drag-focus-backdrop"></div><div class="fdc-destination-preview"></div>';
    document.body.appendChild(layer);
    return layer;
  }

  function clearDestinationHighlights(){
    calendar.querySelectorAll('.fdc-dnd-room-active').forEach(x=>x.classList.remove('fdc-dnd-room-active'));
    calendar.querySelectorAll('.fdc-dnd-date-active').forEach(x=>x.classList.remove('fdc-dnd-date-active'));
    const preview=document.querySelector('#fdcDragFocusLayer .fdc-destination-preview');
    if(preview){
      preview.classList.remove('active','invalid');
      preview.style.transform='translate3d(-10000px,-10000px,0)';
      preview.style.width='1px';
      preview.style.height='1px';
    }
    if(state.dragFocus){
      state.dragFocus.roomLabel=null;
      state.dragFocus.dateHeaders=[];
      state.dragFocus.lastKey='';
    }
  }

  function beginDragFocus(bar){
    const layer=ensureDragFocusLayer();
    const preview=layer.querySelector('.fdc-destination-preview');
    const bg=window.getComputedStyle(bar).backgroundColor || '#2975db';
    state.dragFocus={layer,preview,roomLabel:null,dateHeaders:[],lastKey:'',previewColor:bg};
    preview.style.backgroundColor=bg;
    preview.style.transform='translate3d(-10000px,-10000px,0)';
    preview.style.width='1px';
    preview.style.height='1px';
    layer.classList.add('active');
    document.body.classList.add('fdc-dnd-focus-active');
    page.classList.add('is-dragging');
    closeTip();
  }

  function endDragFocus(){
    clearDestinationHighlights();
    const layer=document.getElementById('fdcDragFocusLayer');
    layer?.classList.remove('active');
    document.body.classList.remove('fdc-dnd-focus-active');
    page.classList.remove('is-dragging');
    state.dragFocus=null;
    closeTip();
  }

  function renderDragFocus(cell,valid){
    if(!state.drag||!cell) return;
    const item=state.drag.item;
    const row=cell.closest('.fdc-room-row');
    if(!row) return;

    const cells=[...row.querySelectorAll('.fdc-cell')];
    const checkoutMove=String(item.statusCode||'').toUpperCase()==='CO' || isCheckedOutStatus(item.status);
    // Checked-out drag/drop is a room-assignment correction only. Preserve the
    // historical stay dates and preview the original date span in the target row.
    const startIndex=checkoutMove
      ? diffDays(state.start,dateOnly(item.arrival))
      : Number(cell.dataset.day);
    if(!Number.isFinite(startIndex)||startIndex<0||startIndex>=cells.length) return;

    const nights=dragNightCount(item);
    const key=`${row.dataset.category}|${row.dataset.room}|${startIndex}|${nights}|${valid?'1':'0'}`;
    const focus=state.dragFocus||{};
    if(focus.lastKey===key) return;

    clearDestinationHighlights();

    const roomLabel=row.querySelector('.fdc-room-label');
    roomLabel?.classList.add('fdc-dnd-room-active');

    const headers=[...calendar.querySelectorAll('.fdc-date-head .fdc-day')];
    // WebForms highlights arrival through departure, so the departure header is
    // included even though the stay nights end at that boundary.
    const endHeader=Math.min(headers.length-1,startIndex+nights);
    const activeHeaders=[];
    for(let i=startIndex;i<=endHeader;i++){
      if(headers[i]){headers[i].classList.add('fdc-dnd-date-active');activeHeaders.push(headers[i]);}
    }

    const startCell=cells[startIndex];
    const departureCell=cells[startIndex+nights]||null;
    const lastCell=cells[cells.length-1];
    const rowRect=row.getBoundingClientRect();
    const startRect=startCell.getBoundingClientRect();
    const depRect=departureCell?.getBoundingClientRect();
    const lastRect=lastCell?.getBoundingClientRect();
    const scrollRect=scroll.getBoundingClientRect();
    const roomRect=roomLabel?.getBoundingClientRect();

    // Same half-arrival / half-departure geometry used by the WebForms bar.
    let left=startRect.left+(startRect.width/2);
    let right=depRect ? depRect.left+(depRect.width/2) : (lastRect?.right||rowRect.right);
    const dateAreaLeft=Math.max(scrollRect.left,roomRect?.right||scrollRect.left);
    const dateAreaRight=scrollRect.right;
    left=Math.max(left,dateAreaLeft);
    right=Math.min(right,dateAreaRight);
    const top=Math.max(rowRect.top+3,scrollRect.top);
    const bottom=Math.min(rowRect.bottom-3,scrollRect.bottom);

    const preview=(state.dragFocus?.preview)||ensureDragFocusLayer().querySelector('.fdc-destination-preview');
    if(preview&&right>left&&bottom>top){
      preview.style.backgroundColor=state.dragFocus?.previewColor||window.getComputedStyle(state.drag.bar).backgroundColor||'#2975db';
      preview.style.transform=`translate3d(${Math.round(left)}px,${Math.round(top)}px,0)`;
      preview.style.width=`${Math.max(1,Math.round(right-left))}px`;
      preview.style.height=`${Math.max(1,Math.round(bottom-top))}px`;
      preview.classList.toggle('invalid',!valid);
      preview.classList.add('active');
    }

    if(state.dragFocus){
      state.dragFocus.roomLabel=roomLabel;
      state.dragFocus.dateHeaders=activeHeaders;
      state.dragFocus.lastKey=key;
    }
  }

  function dragStart(e,bar){
    const item=bookingFrom(bar); if(!item?.canDrag){e.preventDefault();return;}
    if(e.target.closest('[data-resize]')){e.preventDefault();return;}
    closeTip();
    closeContext();
    state.selectedCell?.classList.remove('selected');
    state.selectedCell=null;
    state.drag={item,bar};
    bar.classList.add('dragging');
    beginDragFocus(bar);
    e.dataTransfer.effectAllowed='move';
    e.dataTransfer.setData('text/plain',item.id||item.regId);
  }
  function dragNightCount(item){return Math.max(1,diffDays(item.arrival,item.departure));}
  function targetRange(item,cell){
    const checkoutMove=String(item.statusCode||'').toUpperCase()==='CO' || isCheckedOutStatus(item.status);
    const start=checkoutMove?dateOnly(item.arrival):dateOnly(cell.dataset.date);
    const nights=dragNightCount(item);
    return {start,end:addDays(start,nights),nights};
  }
  function targetRangeIsFree(item,cell){
    const {start,end}=targetRange(item,cell); const room=String(cell.dataset.room||'').toUpperCase();
    if(room==='UNASSIGNED') return true; const cat=String(cell.dataset.category||'');
    const overlaps=(a,d)=>dateOnly(a)<end && start<dateOnly(d);
    for(const b of (state.payload?.bookings||[])){
      if(String(b.id||'')===String(item.id||'') || (b.paymentId&&b.paymentId===item.paymentId)) continue;
      if(String(b.categoryId)!==cat || String(b.roomNo||'UNASSIGNED').toUpperCase()!==room) continue;
      if(overlaps(b.arrival,b.departure)) return false;
    }
    for(const b of (state.payload?.blocks||[])){
      if(String(b.categoryId)!==cat || String(b.roomNo||'').toUpperCase()!==room) continue;
      if(overlaps(b.startDate,b.endDate)) return false;
    }
    return true;
  }
  function markDropPreview(cell){
    if(!state.drag)return;
    const item=state.drag.item;
    // Do not paint visited cells. The WebForms calendar uses one exact landing
    // preview while separately highlighting the destination room and dates.
    const valid=targetRangeIsFree(item,cell);
    renderDragFocus(cell,valid);
  }

  function setMoveDropLoading(show){
    const preview=document.querySelector('#fdcDragFocusLayer .fdc-destination-preview');
    state.dropPending=!!show;
    document.body.classList.toggle('fdc-move-drop-pending',!!show);
    if(preview){
      preview.classList.toggle('loading',!!show);
      if(show) preview.classList.add('active');
    }
    if(show){
      closeTip();
      closeContext();
    }
  }

  function finishDragUi(){
    setMoveDropLoading(false);
    clearDropMarks();
    state.drag?.bar?.classList.remove('dragging');
    endDragFocus();
    state.drag=null;
    closeTip();
  }

  async function dropBooking(e,cell){
    if(!state.drag)return;
    e.preventDefault();
    const item=state.drag.item;
    if(!targetRangeIsFree(item,cell)){
      showToast('The selected room/date range is not available.',true);
      finishDragUi();
      return;
    }

    const req={
      regId:item.regId,
      paymentId:item.paymentId,
      newRoomNo:String(cell.dataset.room).toUpperCase()==='UNASSIGNED'?'':cell.dataset.room,
      targetCategoryId:cell.dataset.category,
      targetCategoryName:cell.dataset.categoryName,
      newArrival:(String(item.statusCode||'').toUpperCase()==='CO' || isCheckedOutStatus(item.status)) ? iso(item.arrival) : cell.dataset.date,
      nightlyRateOverride:null
    };

    const categoryChanged=String(item.categoryName||'').trim().toLowerCase()!==String(req.targetCategoryName||'').trim().toLowerCase();
    const checkoutMove=String(item.statusCode||'').toUpperCase()==='CO' || isCheckedOutStatus(item.status);
    if(categoryChanged && !checkoutMove){
      const nights=dragNightCount(item);
      const currentRate=nights>0 ? Math.max(0,Number(item.rate||0))/nights : 0;
      finishDragUi();
      askRateAndRetry(req,item,{oldCategory:item.categoryName,newCategory:req.targetCategoryName,suggestedRate:currentRate});
      return;
    }

    // Keep the WebForms-style destination spotlight visible after the native
    // drag ends. The spinner is rendered INSIDE the exact destination bar and
    // remains there until both the move request and calendar reload complete.
    setMoveDropLoading(true);
    state.drag?.bar?.classList.remove('dragging');

    let handOffToRatePopup=false;
    try{
      await mutate(cfg.moveUrl,req,`Moving ${item.guestName||'booking'}…`);
      await loadCalendar(state.start,true);
    }catch(err){
      if(err.data?.requiresRate){
        handOffToRatePopup=true;
        setMoveDropLoading(false);
        finishDragUi();
        askRateAndRetry(req,item,err.data);
      }
    }finally{
      if(!handOffToRatePopup) finishDragUi();
    }
  }

  function askRateAndRetry(req,item,data){
    const oldCategory=data.oldCategory||item.categoryName||'Current category';
    const newCategory=data.newCategory||req.targetCategoryName||'New category';
    const suggested=Math.max(0,Number(data.suggestedRate||0));
    const body=`
      <div class="fdc-crm-note">
        <div class="fdc-crm-note-icon">i</div>
        <div><b>Tip</b><span>Use the same rate if you are only changing category, or adjust it to match the new category pricing.</span></div>
      </div>
      <div class="fdc-crm-form">
        <div class="fdc-crm-label"><span>Rate per night</span><em>Editable</em></div>
        <div class="fdc-crm-inputwrap"><span class="fdc-crm-prefix">${esc(state.currency)}</span><input id="moveRate" type="number" step="0.01" min="0.01" inputmode="decimal" value="${suggested>0?suggested.toFixed(2):''}"></div>
        <div class="fdc-crm-hint">Old: ${esc(oldCategory)} → New: ${esc(newCategory)}</div>
      </div>`;
    const foot=`<button type="button" class="fdc-btn" data-close-modal>Cancel</button><button type="button" class="fdc-btn primary fdc-crm-apply" id="confirmMoveRate">✓&nbsp;&nbsp;Apply &amp; Continue</button>`;
    openModal('Category Change Rate',body,foot,'fdc-category-rate-mode');
    const input=$('moveRate'),confirmBtn=$('confirmMoveRate');
    setTimeout(()=>{input?.focus();input?.select();},40);
    confirmBtn.onclick=async()=>{
      const rate=Number(input?.value||0);
      if(!(rate>0)){showToast('Please enter a valid rate.',true);input?.focus();return;}
      req.nightlyRateOverride=rate;
      try{
        await mutate(cfg.moveUrl,req,'Applying & moving…',confirmBtn);
        closeModal();
        await loadCalendar(state.start,true);
      }catch{}
    };
    input?.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();confirmBtn.click();}});
  }
  function clearDropMarks(){calendar.querySelectorAll('.fdc-drag-ghost').forEach(x=>x.remove());calendar.querySelectorAll('.drop-ok,.drop-no,.drag-preview,.first,.last').forEach(x=>x.classList.remove('drop-ok','drop-no','drag-preview','first','last'));}

  function resizeStart(e,handle){
    e.preventDefault();e.stopPropagation(); const bar=handle.closest('.fdc-bar'),item=bookingFrom(bar);if(!item?.canResize)return;
    closeTip();
    const row=bar.closest('.fdc-room-row'),rect=row.getBoundingClientRect(),dayWidth=(rect.width-112)/state.days;
    state.resize={item,startX:e.clientX,bar,dayWidth:Math.max(1,dayWidth),delta:0}; bar.classList.add('resizing');bar.setPointerCapture?.(e.pointerId);document.body.style.userSelect='none';
  }
  function resizeMove(e){
    const r=state.resize;if(!r)return; const delta=Math.round((e.clientX-r.startX)/r.dayWidth);r.delta=delta;
    const oldNights=Math.max(1,diffDays(r.item.arrival,r.item.departure)),newNights=Math.max(1,oldNights+delta); r.bar.style.width=`calc(${newNights} * ((100% - var(--room)) / var(--view-days)))`;
  }
  function openResizeConfirm(item,a,oldD,newD,delta){
    const isExtend=delta>0;
    const oldNights=Math.max(1,diffDays(a,oldD));
    const newNights=Math.max(1,diffDays(a,newD));
    const changedNights=Math.abs(newNights-oldNights);
    const currentPerNight=oldNights>0?Number(item.rate||0)/oldNights:0;
    const title=isExtend?'Confirm Extend Reservation':'Confirm Shrink Reservation';
    const hint=isExtend?'Review the added nights and rate before confirming.':'Review the reduced stay and new rate before confirming.';
    const rateLabel=isExtend?'New Rate / Night (Extended Nights)':'New Rate / Night';
    const totalLabel=isExtend?'Total Amount of Extended Days':'New Room Amount';
    openModal(title,`<div class="fdc-resize-confirm">
      <div class="fdc-resize-sub">${hint}</div>
      <div class="fdc-resize-grid">
        <div><span>Guest Name</span><b>${esc(item.guestName||'—')}</b></div>
        <div><span>Reference</span><b>${esc(item.regId||'—')}</b></div>
        <div><span>Arrival</span><b>${fmt(a,true)}</b></div>
        <div><span>Old Departure</span><b>${fmt(oldD,true)}</b></div>
        <div><span>New Departure</span><b>${fmt(newD,true)}</b></div>
        <div class="highlight"><span>${isExtend?'Extended Days':'New Nights'}</span><b>${isExtend?changedNights:newNights} night(s)</b></div>
      </div>
      <div class="fdc-resize-ratebox">
        <div class="fdc-resize-plan"><strong>${esc(item.planName||'Room Rate')}</strong><small>${esc(item.categoryName||'')}</small></div>
        <div class="fdc-resize-ratefield">
          <div class="fdc-resize-rate-label">${rateLabel}</div>
          <div class="fdc-crm-inputwrap"><span class="fdc-crm-prefix">${esc(state.currency||'£')}</span><input id="resizeRate" type="number" min="0" step="0.01" value="${currentPerNight.toFixed(2)}"></div>
        </div>
        ${isExtend?'<label class="fdc-tax-check"><input id="resizeTax" type="checkbox"> Tax applicable on extended nights</label>':''}
      </div>
      <div class="fdc-resize-total"><span>${totalLabel}</span><b id="resizeTotal"></b></div>
    </div>`,`<button class="fdc-btn fdc-btn-secondary" data-close-modal>Cancel</button><button class="fdc-btn primary" id="confirmResize">${isExtend?'Confirm Extend':'Confirm Shrink'}</button>`,'fdc-professional-mode fdc-resize-mode');

    const rateInput=$('resizeRate'),taxInput=$('resizeTax'),totalEl=$('resizeTotal');
    const updateTotal=()=>{
      const rate=Math.max(0,Number(rateInput?.value||0));
      let amount=isExtend?rate*changedNights:rate*newNights;
      if(isExtend&&taxInput?.checked&&oldNights>0){
        amount += (Number(item.gst||0)/oldNights)*changedNights;
        amount += (Number(item.bed||0)/oldNights)*changedNights;
      }
      totalEl.textContent=money(amount);
    };
    rateInput?.addEventListener('input',updateTotal);taxInput?.addEventListener('change',updateTotal);updateTotal();
    $('confirmResize').onclick=async e=>{
      const btn=e.currentTarget;
      const rate=Math.max(0,Number(rateInput?.value||0));
      try{
        await mutate(cfg.resizeUrl,{regId:item.regId,paymentId:item.paymentId,newArrival:iso(a),newDeparture:iso(newD),nightlyRateOverride:rate,includeTaxOnExtension:!!taxInput?.checked},isExtend?'Extending…':'Shrinking…',btn);
        closeModal();
        await loadCalendar(state.start,true);
      }catch{ render(); }
    };
  }

  async function resizeEnd(){
    const r=state.resize;if(!r)return;state.resize=null;document.body.style.userSelect='';r.bar.classList.remove('resizing');
    const delta=r.delta||0;if(!delta){render();return;} const a=dateOnly(r.item.arrival),oldD=dateOnly(r.item.departure),d=addDays(oldD,delta);
    if(d<=a){showToast('A stay must be at least one night.',true);render();return;}
    render();
    openResizeConfirm(r.item,a,oldD,d,delta);
  }

  function showBookingTip(bar,e){
    if(state.drag||state.resize||page.classList.contains('is-dragging')) return;
    const b=bookingFrom(bar);if(!b)return;
    const ref=b.regId||'—',book=b.bookId||'—',room=b.roomNo||'Unassigned';
    tip.innerHTML=`<b>${esc(b.guestName||ref)}</b>
      <div><span>Reference:</span> <strong>${esc(ref)}</strong></div>
      <div><span>Booking #:</span> <strong>${esc(book)}</strong></div>
      <div><span>Stay:</span> ${fmt(b.arrival,true)} – ${fmt(b.departure,true)}</div>
      <div><span>Room:</span> ${esc(room)} · ${esc(b.categoryName||'')}</div>
      <div><span>Plan:</span> ${esc(b.planName||'—')}</div>
      <div><span>Source:</span> ${esc(b.source||'—')}</div>
      <div><span>Payment:</span> ${esc(b.paymentStatus||'—')}${Number(b.balance||0)>0?` · ${money(b.balance)} due`:''}</div>`;
    const x=Math.min(e.clientX+14,innerWidth-315),y=Math.min(e.clientY+14,innerHeight-200);tip.style.left=`${x}px`;tip.style.top=`${y}px`;tip.classList.add('open');
  }

  function openRangePicker(){
    let month=new Date(state.start.getFullYear(),state.start.getMonth(),1);
    const draw=()=>{
      const first=new Date(month.getFullYear(),month.getMonth(),1);
      const lead=(first.getDay()+6)%7;
      const gridStart=addDays(first,-lead);
      let days='';
      for(let i=0;i<42;i++){
        const d=addDays(gridStart,i),out=d.getMonth()!==month.getMonth(),sel=sameDate(d,state.start);
        days+=`<button type="button" class="fdc-range-day ${out?'out':''} ${sel?'selected':''}" data-range-date="${iso(d)}">${d.getDate()}</button>`;
      }
      rangePicker.innerHTML=`<div class="fdc-range-head"><button type="button" class="fdc-month-arrow prev" data-month="-1" aria-label="Previous month">‹</button><b>${month.toLocaleDateString('en-GB',{month:'long',year:'numeric'})}</b><button type="button" class="fdc-month-arrow next" data-month="1" aria-label="Next month">›</button></div><div class="fdc-week"><span>Mo</span><span>Tu</span><span>We</span><span>Th</span><span>Fr</span><span>Sa</span><span>Su</span></div><div class="fdc-days">${days}</div><div class="fdc-range-actions"><button type="button" class="fdc-btn" data-range-close>Close</button></div>`;
    };

    rangePicker.onclick=e=>{
      const monthBtn=e.target.closest('[data-month]');
      if(monthBtn){
        e.preventDefault();e.stopPropagation();
        const delta=parseInt(monthBtn.dataset.month||'0',10);
        month=new Date(month.getFullYear(),month.getMonth()+delta,1);
        draw();
        return;
      }
      const dateBtn=e.target.closest('[data-range-date]');
      if(dateBtn){
        e.preventDefault();e.stopPropagation();
        rangePicker.classList.remove('open');
        loadCalendar(dateOnly(dateBtn.dataset.rangeDate));
        return;
      }
      if(e.target.closest('[data-range-close]')){
        e.preventDefault();e.stopPropagation();rangePicker.classList.remove('open');
      }
    };

    draw();
    const r=$('fdcRange').getBoundingClientRect();
    rangePicker.style.left=`${Math.min(r.left,innerWidth-345)}px`;
    rangePicker.style.top=`${r.bottom+6}px`;
    rangePicker.classList.add('open');
  }

  calendar.addEventListener('click',e=>{
    const collapse=e.target.closest('[data-collapse]');if(collapse){const id=collapse.dataset.collapse;state.collapsed.has(id)?state.collapsed.delete(id):state.collapsed.add(id);render();return;}
    const block=e.target.closest('.fdc-bar[data-block-id]');if(block){openBlockDetails(blockFrom(block));return;}
    const cell=e.target.closest('.fdc-cell');if(cell&&!e.target.closest('.fdc-bar')){state.selectedCell?.classList.remove('selected');state.selectedCell=cell;cell.classList.add('selected');}
  });
  calendar.addEventListener('dblclick',e=>{
    const bar=e.target.closest('.fdc-bar[data-booking]');if(bar){openDetails(bookingFrom(bar));return;}
    const cell=e.target.closest('.fdc-cell');if(cell&&!e.target.closest('.fdc-bar'))openRoomAction(cell);
  });
  calendar.addEventListener('contextmenu',e=>{
    const bookingBar=e.target.closest('.fdc-bar[data-booking]');
    if(bookingBar){
      e.preventDefault();
      e.stopPropagation();
      closeTip();
      openBookingContext(bookingFrom(bookingBar),e.clientX,e.clientY);
      return;
    }
    // Match the WebForms behaviour requested for this conversion:
    // right-click is for reservation/check-in bars, not empty grid cells.
    if(e.target.closest('.fdc-cell')){
      e.preventDefault();
      closeContext();
    }
  });
  calendar.addEventListener('mouseover',e=>{const bar=e.target.closest('.fdc-bar[data-booking]');if(bar)showBookingTip(bar,e);});
  calendar.addEventListener('mousemove',e=>{if(tip.classList.contains('open')){tip.style.left=`${Math.min(e.clientX+14,innerWidth-290)}px`;tip.style.top=`${Math.min(e.clientY+14,innerHeight-120)}px`;}});
  calendar.addEventListener('mouseout',e=>{if(e.target.closest('.fdc-bar[data-booking]'))closeTip();});
  calendar.addEventListener('dragstart',e=>{const bar=e.target.closest('.fdc-bar[data-booking]');if(bar)dragStart(e,bar);});
  calendar.addEventListener('dragend',()=>{if(!state.dropPending)finishDragUi();});
  calendar.addEventListener('dragover',e=>{const cell=e.target.closest('.fdc-cell');if(cell&&state.drag){e.preventDefault();markDropPreview(cell);}});
  calendar.addEventListener('drop',e=>{const cell=e.target.closest('.fdc-cell');if(cell)dropBooking(e,cell);});
  calendar.addEventListener('pointerdown',e=>{const h=e.target.closest('[data-resize]');if(h)resizeStart(e,h);});
  document.addEventListener('pointermove',resizeMove);
  document.addEventListener('pointerup',resizeEnd);

  drawerBody.addEventListener('click',e=>{
    const b=e.target.closest('[data-drawer-hold-action]');
    if(b){
      e.preventDefault();
      void drawerHoldAction(b);
    }
  });

  drawerFoot.addEventListener('click',e=>{
    const toggle=e.target.closest('[data-drawer-actions-toggle]');
    if(toggle){
      const list=drawerFoot.querySelector('[data-drawer-actions-list]');
      const expanded=toggle.getAttribute('aria-expanded')==='true';
      toggle.setAttribute('aria-expanded',String(!expanded));
      if(list) list.hidden=expanded;
      return;
    }
    const b=e.target.closest('[data-drawer-action]');
    if(b)drawerAction(b.dataset.drawerAction,b);
  });
  drawerPay?.addEventListener('click',e=>{
    const b=e.target.closest('[data-drawer-action]');
    if(b)drawerAction(b.dataset.drawerAction,b);
  });
  $('fdcDrawerClose').onclick=closeDrawer;$('fdcModalClose').onclick=closeModal;
  document.addEventListener('click',e=>{
    if(e.target.closest('[data-close-modal]')) closeModal();
    if(!e.target.closest('#fdcContext')&&!e.target.closest('.fdc-cell')) closeContext();
    if(!e.target.closest('#fdcRangePicker')&&!e.target.closest('#fdcRange')) rangePicker.classList.remove('open');
  });
  window.addEventListener('scroll',closeContext,true);
  document.addEventListener('keydown',e=>{if(e.key==='Escape'){closeContext();closeTip();}});

  context.addEventListener('click',e=>{
    const bookingButton=e.target.closest('[data-booking-ctx]');
    if(bookingButton){
      e.preventDefault();
      e.stopPropagation();
      if(bookingButton.disabled || context.classList.contains('fdc-context-processing')) return;
      let item=null;
      try{item=JSON.parse(context.dataset.booking||'{}');}catch{}
      void bookingContextAction(bookingButton.dataset.bookingCtx,item,bookingButton);
      return;
    }

    const b=e.target.closest('[data-ctx]'),cell=state.selectedCell;
    if(!b||!cell)return;
    closeContext();
    if(b.dataset.ctx==='reserve')openRoomAction(cell);
    if(b.dataset.ctx==='block')openBlockForm({roomNo:cell.dataset.room,categoryId:cell.dataset.category,categoryName:cell.dataset.categoryName,startDate:cell.dataset.date,endDate:cell.dataset.date});
    if(b.dataset.ctx==='clean')markClean(cell.dataset.room);
  });

  $('fdcPrev').onclick=()=>{rangePicker.classList.remove('open');loadCalendar(addDays(state.start,-state.days));};
  $('fdcNext').onclick=()=>{rangePicker.classList.remove('open');loadCalendar(addDays(state.start,state.days));};
  $('fdcToday').onclick=()=>loadCalendar(state.hotelToday);
  $('fdcRefresh').onclick=()=>loadCalendar(state.start,true);
  $('fdcRange').onclick=e=>{e.stopPropagation();openRangePicker();};
  $('fdcLegendToggle').onclick=()=>{const x=$('fdcLegend'),hidden=!x.hidden;x.hidden=hidden;$('fdcLegendToggle').setAttribute('aria-expanded',String(!hidden));};

  // Calendar page only: start with the master sidebar collapsed without editing the master layout.
  document.body.classList.add('sidebar-collapsed');
  document.body.classList.remove('mobile-sidebar-open');
  document.getElementById('sidebar')?.classList.remove('open');

  loadCalendar(state.start);
})();
