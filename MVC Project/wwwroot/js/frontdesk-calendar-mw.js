(() => {
  'use strict';

  const page = document.getElementById('frontDeskCalendarMWPage');
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
      .fdc-security-summary{display:flex;align-items:center;justify-content:space-between;gap:10px;margin:0 0 9px;padding:10px 11px;border:1px solid #bcd7cb;border-radius:8px;background:#f0faf5}
      .fdc-security-summary-copy{min-width:0}.fdc-security-summary-copy span{display:block;color:#547365;font-size:8px;font-weight:850;letter-spacing:.06em;text-transform:uppercase}.fdc-security-summary-copy strong{display:block;margin-top:1px;color:#0f6c4c;font-size:18px;line-height:1.1}.fdc-security-summary-copy small{display:block;margin-top:3px;color:#6d8178;font-size:8px;line-height:1.25}
      .fdc-security-summary button{flex:0 0 auto;min-height:30px;padding:5px 9px;border:1px solid #176d50;border-radius:6px;background:#176d50;color:#fff;font:inherit;font-size:8.5px;font-weight:850;cursor:pointer}
      @media(max-width:520px){.fdc-hold-capture{grid-template-columns:1fr}.fdc-hold-buttons{grid-template-columns:1fr 1fr}.fdc-security-summary{align-items:flex-start;flex-direction:column}.fdc-security-summary button{width:100%}}
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
  const fallbackToday = dateOnly(cfg.hotelToday) || todayLocal();
  const fallbackStart = new Date(fallbackToday.getFullYear(), fallbackToday.getMonth(), 1);
  const fallbackEnd = addDays(addMonths(fallbackStart, 14), -1);
  const state = {
    start: dateOnly(cfg.start) || fallbackStart,
    end: dateOnly(cfg.end) || fallbackEnd,
    hotelToday: dateOnly(cfg.hotelToday) || todayLocal(),
    currency: cfg.currency || '£',
    payload: null,
    months: [],
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
  function addMonths(d, n) { const x = new Date(d); const day=x.getDate(); x.setDate(1); x.setMonth(x.getMonth()+n); x.setDate(Math.min(day,new Date(x.getFullYear(),x.getMonth()+1,0).getDate())); return x; }
  function monthDiffCeil(start,end){
    const a=dateOnly(start),b=dateOnly(end);if(!a||!b||b<=a)return 0;
    let months=((b.getFullYear()-a.getFullYear())*12)+(b.getMonth()-a.getMonth());
    if(addMonths(a,months)<b)months++;
    return Math.max(months,1);
  }
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

  function rangeOverlaps(aStart,aEndExclusive,bStart,bEndExclusive){
    const as=dateOnly(aStart),ae=dateOnly(aEndExclusive),bs=dateOnly(bStart),be=dateOnly(bEndExclusive);
    return !!(as&&ae&&bs&&be&&as<be&&bs<ae);
  }
  function monthDays(month){return Math.max(1,diffDays(month.startDate,addDays(month.endDate,1)));}
  function monthIndexForDate(value,allowEnd=false){
    const d=dateOnly(value); if(!d)return -1;
    for(let i=0;i<state.months.length;i++){
      const m=state.months[i],s=dateOnly(m.startDate),e=addDays(m.endDate,1);
      if(d>=s && (allowEnd?d<=e:d<e)) return i;
    }
    return -1;
  }
  function dateToMonthUnit(value,asEnd=false){
    const d=dateOnly(value); if(!d||!state.months.length)return null;
    const first=dateOnly(state.months[0].startDate),lastEnd=addDays(state.months[state.months.length-1].endDate,1);
    if(d<=first)return 0;
    if(d>=lastEnd)return state.months.length;
    for(let i=0;i<state.months.length;i++){
      const m=state.months[i],s=dateOnly(m.startDate),e=addDays(m.endDate,1);
      if(d>=s && d<=e){
        const total=Math.max(1,diffDays(s,e));
        const offset=Math.max(0,Math.min(total,diffDays(s,d)));
        if(d<e || asEnd) return i+(offset/total);
      }
    }
    return null;
  }
  function geometryForRange(start,end){
    const left=dateToMonthUnit(start,false),right=dateToMonthUnit(end,true);
    if(left==null||right==null||right<=left)return null;
    return {leftUnits:left,rightUnits:right,widthUnits:right-left};
  }
  function inferMonthCellDate(e,cell,allowEndBoundary=false){
    if(!cell)return null;
    const idx=Number(cell.dataset.monthIndex||0),m=state.months[idx];
    if(!m)return dateOnly(cell.dataset.date);
    const rect=cell.getBoundingClientRect(),days=monthDays(m);
    const raw=rect.width>0?(e.clientX-rect.left)/rect.width:0;
    const frac=Math.max(0,Math.min(allowEndBoundary?1:0.999999,raw));
    const offset=Math.max(0,Math.min(days-(allowEndBoundary?0:1),Math.floor(frac*days)));
    return addDays(m.startDate,offset);
  }
  function dateFromRowX(row,clientX,allowEndBoundary=true){
    if(!row||!state.months.length)return null;
    const label=row.querySelector('.fdc-room-label');
    const firstCell=row.querySelector('.fdc-cell');
    if(!firstCell)return null;
    const firstRect=firstCell.getBoundingClientRect();
    const cellWidth=firstRect.width||112;
    let unit=(clientX-firstRect.left)/cellWidth;
    unit=Math.max(0,Math.min(state.months.length,unit));
    let idx=Math.floor(Math.min(state.months.length-1,unit));
    let frac=unit-idx;
    if(unit>=state.months.length){idx=state.months.length-1;frac=1;}
    const m=state.months[idx],days=monthDays(m);
    const offset=Math.round(frac*days);
    const d=addDays(m.startDate,Math.max(0,Math.min(days,offset)));
    const rangeEnd=addDays(state.months[state.months.length-1].endDate,1);
    if(d>rangeEnd)return rangeEnd;
    return d;
  }
  function monthlyPaymentStatus(item,month){
    const stayArrival=dateOnly(item.arrival),stayDeparture=dateOnly(item.departure);
    const monthStart=dateOnly(month.startDate),monthEnd=dateOnly(month.endDate);
    const grandTotal=Number(item.total||0),paidAmount=Number(item.paid||0);
    if(!stayArrival||!stayDeparture||stayDeparture<=stayArrival||grandTotal<=0)return '';
    if(!rangeOverlaps(stayArrival,stayDeparture,monthStart,addDays(monthEnd,1)))return '';
    const arrivalMonth=new Date(stayArrival.getFullYear(),stayArrival.getMonth(),1);
    const thisMonth=new Date(monthStart.getFullYear(),monthStart.getMonth(),1);
    let totalMonths=((stayDeparture.getFullYear()-stayArrival.getFullYear())*12)+(stayDeparture.getMonth()-stayArrival.getMonth());
    if(addMonths(stayArrival,totalMonths)<stayDeparture)totalMonths++;
    totalMonths=Math.max(1,totalMonths);
    const monthlyDue=Math.round((grandTotal/totalMonths)*100)/100;
    const monthIndex=((thisMonth.getFullYear()-arrivalMonth.getFullYear())*12)+(thisMonth.getMonth()-arrivalMonth.getMonth());
    if(monthIndex<0)return '';
    const available=paidAmount-(monthlyDue*monthIndex);
    if(available<=0)return 'Not Paid';
    if(available>=monthlyDue)return 'Fully Paid';
    return 'Partially Paid';
  }
  function buildMonthPaymentStrip(item){
    const stayArrival=dateOnly(item.arrival),stayDeparture=dateOnly(item.departure);
    const grandTotal=Number(item.total||0),paidAmount=Number(item.paid||0);
    if(!stayArrival||!stayDeparture||stayDeparture<=stayArrival||grandTotal<=0||!state.months.length)return '';

    const viewStart=dateOnly(state.months[0].startDate);
    const viewEndExclusive=addDays(dateOnly(state.months[state.months.length-1].endDate),1);
    const visibleStart=stayArrival>viewStart?stayArrival:viewStart;
    const visibleEnd=stayDeparture<viewEndExclusive?stayDeparture:viewEndExclusive;
    if(visibleEnd<=visibleStart)return '';

    const totalMonths=Math.max(1,monthDiffCeil(stayArrival,stayDeparture));
    const monthlyDue=Math.round((grandTotal/totalMonths)*100)/100;
    const visibleDays=Math.max(1,diffDays(visibleStart,visibleEnd));
    let html='<span class="fdc-mw-pay-strip" aria-hidden="true">';

    for(let n=1;n<=totalMonths;n++){
      const segmentStart=n===1?stayArrival:addMonths(stayArrival,n-1);
      let segmentEnd=addMonths(stayArrival,n);
      if(segmentEnd>stayDeparture)segmentEnd=stayDeparture;
      if(segmentEnd<=segmentStart)continue;

      const drawStart=segmentStart<visibleStart?visibleStart:segmentStart;
      const drawEnd=segmentEnd>visibleEnd?visibleEnd:segmentEnd;
      if(drawEnd<=drawStart)continue;

      const available=paidAmount-(monthlyDue*(n-1));
      const status=available>=monthlyDue?'Fully Paid':available>0?'Partially Paid':'Not Paid';
      const left=(diffDays(visibleStart,drawStart)/visibleDays)*100;
      const width=Math.max(.5,(diffDays(drawStart,drawEnd)/visibleDays)*100);
      html+=`<span class="fdc-mw-pay-segment ${paymentDotClass(status)}" style="left:${left.toFixed(3)}%;width:${width.toFixed(3)}%" title="${esc(status)}"></span>`;
    }
    return html+'</span>';
  }

  function monthCount(){return Math.max(1,state.months.length||1);}

  function fitMonthColumnsToViewport(){
    const count=Math.max(1,state.months.length||14);
    const pageStyle=getComputedStyle(page);
    const roomWidth=Math.max(76,parseFloat(pageStyle.getPropertyValue('--room'))||112);
    const viewportWidth=Math.max(320,scroll.clientWidth||page.clientWidth||window.innerWidth||1200);
    // Leave a tiny allowance for the scroll container border/vertical scrollbar.
    const usable=Math.max(1,viewportWidth-roomWidth-2);
    // Desktop screens should fit all default months. Very narrow/mobile screens
    // keep a small readable floor and can scroll horizontally when necessary.
    const fitted=Math.max(44,Math.min(150,usable/count));
    calendar.style.setProperty('--view-months',count);
    calendar.style.setProperty('--month-col',`${fitted.toFixed(2)}px`);
  }

  function shiftRangeByMonths(delta){
    const newStart=addMonths(state.start,delta),newEnd=addMonths(state.end,delta);
    loadCalendar(newStart,newEnd);
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
      const base = new URL(cfg.actionsBaseUrl || '/Calendar', location.origin);
      const path = (base.pathname || '/Calendar').replace(/\/$/, '');
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
      const markerDaily = lower.lastIndexOf('/calendar/');
      const markerMw = lower.lastIndexOf('/calendarmw/');
      const marker = Math.max(markerDaily, markerMw);
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

  async function loadCalendar(start=state.start, endOrKeepScroll=state.end, maybeKeepScroll=false) {
    let end=state.end,keepScroll=false;
    if(typeof endOrKeepScroll==='boolean'){
      keepScroll=endOrKeepScroll;
    }else{
      end=dateOnly(endOrKeepScroll)||state.end;
      keepScroll=!!maybeKeepScroll;
    }
    start=dateOnly(start)||state.start;
    if(!end||end<start){showToast('End date must be on or after start date.',true);return;}
    if(state.request) state.request.abort();
    state.request = new AbortController();
    state.loading = true;
    const oldLeft=scroll.scrollLeft, oldTop=scroll.scrollTop;
    calendar.innerHTML='<div class="fdc-loading"><span></span><b>Loading month-wise calendar…</b></div>';
    try {
      const url = new URL(cfg.dataUrl, location.origin);
      url.searchParams.set('start', iso(start));
      url.searchParams.set('end', iso(end));
      const json = await api(url.toString(), {signal:state.request.signal});
      state.payload = json.data;
      state.start = dateOnly(json.data.startDate) || start;
      state.end = dateOnly(json.data.endDate) || end;
      state.hotelToday = dateOnly(json.data.hotelToday) || state.hotelToday;
      state.currency = json.data.currencySymbol || state.currency;
      state.months = (json.data.months||[]).map(m=>({...m,startDate:dateOnly(m.startDate),endDate:dateOnly(m.endDate)}));
      render();
      if (keepScroll) { scroll.scrollLeft=oldLeft; scroll.scrollTop=oldTop; }
    } catch (e) {
      if (e.name !== 'AbortError') calendar.innerHTML=`<div class="fdc-empty"><b>Month-wise calendar could not be loaded.</b><br>${esc(e.message)}</div>`;
    } finally { state.loading=false; }
  }

  function render() {
    const p = state.payload;
    if (!p) return;
    const months=state.months;
    calendar.style.setProperty('--view-months',Math.max(1,months.length));
    fitMonthColumnsToViewport();
    const cats = p.categories || [];
    const rooms = p.rooms || [];
    const bookings = p.bookings || [];
    const blocks = p.blocks || [];
    const assignments=p.councilAssignments||[];
    const roomGroups = new Map();
    for (const c of cats) roomGroups.set(String(c.id), []);
    for (const r of rooms) {
      if (!roomGroups.has(String(r.categoryId))) roomGroups.set(String(r.categoryId), []);
      roomGroups.get(String(r.categoryId)).push(r);
    }

    let html = `<div class="fdc-date-head"><div class="fdc-rooms-head">Rooms</div>`;
    for (const [monthIndex,m] of months.entries()) {
      const ms=dateOnly(m.startDate),me=dateOnly(m.endDate),todayIn=state.hotelToday>=ms&&state.hotelToday<=me;
      html += `<div class="fdc-day fdc-month ${todayIn?'today':''}" data-month-index="${monthIndex}" data-date="${iso(ms)}"><span>${esc(m.month||ms.toLocaleDateString('en-GB',{month:'long'}))}</span><b>${esc(m.label||ms.toLocaleDateString('en-GB',{month:'short',year:'numeric'}))}</b><small>${fmt(ms)} – ${fmt(me)}</small></div>`;
    }
    html += '</div>';

    for (const cat of cats) {
      const cid=String(cat.id), collapsed=state.collapsed.has(cid);
      const cr=roomGroups.get(cid) || [];
      html += `<div class="fdc-category" data-category="${esc(cid)}"><div class="fdc-category-label" data-collapse="${esc(cid)}"><span class="arrow">${collapsed?'▸':'▾'}</span><span title="${esc(cat.name)}">${esc(cat.name)}</span></div><div class="fdc-category-grid"></div></div>`;
      if (collapsed) continue;
      for (const r of cr) html += roomRow(r, cat, months, assignments, false);
      if(normalizeStatus(cfg.role)!=='council')
        html += roomRow({roomNo:'UNASSIGNED',categoryId:cid,categoryName:cat.name,condition:'Unassigned'}, cat, months, assignments, true);
    }
    calendar.innerHTML=html;
    // Recalculate once more after rows are in the DOM because the vertical
    // scrollbar can slightly change the available viewport width. Bars use the
    // same CSS variable, so their geometry stays aligned with the month cells.
    fitMonthColumnsToViewport();
    placeBars(bookings, blocks, months);
    updateRangeButton();
  }

  function roomRow(room, cat, months, assignments, unassigned) {
    const dirty=normalizeStatus(room.condition).includes('dirty');
    const roomNo=String(room.roomNo||'');
    const roomAssignments=unassigned?[]:assignments.filter(a=>String(a.roomNo||'').trim().toUpperCase()===roomNo.trim().toUpperCase());
    const assignmentInRange=roomAssignments.some(a=>{
      const af=dateOnly(a.fromDate)||new Date(1900,0,1),at=addDays(dateOnly(a.toDate)||new Date(9998,11,31),1);
      return rangeOverlaps(af,at,state.start,addDays(state.end,1));
    });
    let html=`<div class="fdc-room-row ${unassigned?'unassigned-row':''}" data-room="${esc(roomNo)}" data-category="${esc(room.categoryId)}" data-category-name="${esc(room.categoryName||cat.name)}">`;
    html += `<div class="fdc-room-label ${dirty?'dirty':''} ${unassigned?'unassigned':''} ${assignmentInRange?'council-assigned':''}" data-room-label><b>${unassigned?'Unassigned':esc(roomNo)}</b></div>`;
    months.forEach((m,i)=>{
      const ms=dateOnly(m.startDate),me=dateOnly(m.endDate),meExclusive=addDays(me,1);
      const dirtyDate=dateOnly(room.dirtyDate);
      const dirtyInMonth=dirty && (dirtyDate ? (dirtyDate>=ms&&dirtyDate<=me) : (state.hotelToday>=ms&&state.hotelToday<=me));
      const councilCell=roomAssignments.some(a=>{
        const af=dateOnly(a.fromDate)||new Date(1900,0,1),at=addDays(dateOnly(a.toDate)||new Date(9998,11,31),1);
        return rangeOverlaps(af,at,ms,meExclusive);
      });
      const currentMonth=state.hotelToday>=ms&&state.hotelToday<=me;
      html += `<div class="fdc-cell ${dirtyInMonth?'has-dirty':''} ${councilCell?'council-assigned':''} ${currentMonth?'current-month':''}" data-month-index="${i}" data-date="${iso(ms)}" data-month-end="${iso(me)}" data-room="${esc(roomNo)}" data-category="${esc(room.categoryId)}" data-category-name="${esc(room.categoryName||cat.name)}">${dirtyInMonth?'<span class="fdc-dirty-tag">Dirty</span>':''}</div>`;
    });
    return html+'</div>';
  }

  function placeBars(bookings, blocks, months) {
    const rows=[...calendar.querySelectorAll('.fdc-room-row')];
    const index=new Map(rows.map(r=>[`${r.dataset.category}|${String(r.dataset.room).toUpperCase()}`,r]));
    const monthWidth='var(--month-col)';

    const addBar=(item,isBlock=false)=>{
      const room=(item.roomNo || 'UNASSIGNED').toUpperCase();
      const row=index.get(`${item.categoryId}|${room}`);
      if(!row) return;
      const a=dateOnly(isBlock?item.startDate:item.arrival);
      const d=dateOnly(isBlock?item.endDate:item.departure);
      if(!a||!d||d<=state.start||a>state.end)return;
      const clippedStart=a<state.start?state.start:a;
      const clippedEnd=d>addDays(state.end,1)?addDays(state.end,1):d;
      const bounds=geometryForRange(clippedStart,clippedEnd);
      if(!bounds)return;
      const left=`calc(var(--room) + (${bounds.leftUnits} * ${monthWidth}) + 2px)`;
      const width=`calc(${bounds.widthUnits} * ${monthWidth} - 4px)`;

      if(isBlock){
        row.insertAdjacentHTML('beforeend',
          `<div class="fdc-bar b ${String(item.kind||'').toLowerCase().includes('maintenance')?'maintenance':''}" `+
          `style="left:${left};width:${width}" data-block-id="${item.blockId}" data-block='${esc(JSON.stringify(item))}'>`+
          `<span class="name">${esc(item.reason||'Blocked')}</span></div>`);
        return;
      }

      const cls=statusClass(item.statusCode||item.status);
      const roomClass=room==='UNASSIGNED'?' unassigned-booking':'';
      const paymentStrip=buildMonthPaymentStrip(item);
      row.insertAdjacentHTML('beforeend',
        `<div class="fdc-bar ${cls}${roomClass}" draggable="${item.canDrag?'true':'false'}" `+
        `style="left:${left};width:${width}" data-booking='${esc(JSON.stringify(item))}' title="${esc(item.guestName)}">`+
        `${paymentStrip}<span class="name">${esc(item.guestName||item.regId)}</span>`+
        `${item.hasNote?'<span class="fdc-note-indicator" title="Notebook note" aria-label="Notebook note">★</span>':''}`+
        `${item.hasRoomChange?'<span class="fdc-room-change-indicator" title="Room changed" aria-label="Room changed">★</span>':''}`+
        `${item.canResize?'<span class="fdc-handle right" data-resize="right" title="Drag to extend / shrink"></span>':''}</div>`);
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
    $('fdcRange').textContent=`${fmt(state.start,true)} – ${fmt(state.end,true)}`;
    $('fdcPrev').title=`Previous ${monthCount()} month${monthCount()===1?'':'s'}`;
    $('fdcNext').title=`Next ${monthCount()} month${monthCount()===1?'':'s'}`;
    const startInput=$('fdcMwStart'),endInput=$('fdcMwEnd');
    if(startInput)startInput.value=iso(state.start);
    if(endInput)endInput.value=iso(state.end);
    document.querySelectorAll('[data-mw-months]').forEach(b=>{
      const count=Math.max(1,Number(b.dataset.mwMonths||0));
      const expectedEnd=new Date(state.start.getFullYear(),state.start.getMonth()+count,0);
      b.classList.toggle('active',sameDate(state.start,state.hotelToday)&&sameDate(state.end,expectedEnd));
    });
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
    const roomSecurity=Math.max(0,Number(d.roomSecurity||0));
    const stateClass=checked?'green':closed?'amber':balance>0.005?'red':'blue';
    const stateText=checked?'Checked In':closed?'Checked Out':(d.status||'Reservation');
    const row=(label,value)=>`<div class="fdc-kv"><span>${label}</span><b>${value}</b></div>`;
    const logs=(d.payments||[]).map(p=>{
      const refundable=Math.max(0,Number(p.remainingRefundable||0));
      const canRefund=cfg.canRefund==='1' && p.canRefund===true && refundable>0.005;
      const receipt=String(p.receiptUrl||'').trim();
      return `<div class="fdc-payment-log-entry" data-payment-log-id="${Number(p.id||0)}">
        <div class="fdc-payment-log-meta">
          <span>${fmt(p.date,true)} · ${esc(p.method||'Payment')}${p.reference?` · ${esc(p.reference)}`:''}</span>
          <strong class="${Number(p.amount||0)<0?'fdc-payment-refund-amount':''}">${money(p.amount)}</strong>
        </div>
        ${(receipt||canRefund)?`<div class="fdc-payment-log-tools">
          ${receipt?`<a class="fdc-payment-receipt" href="${esc(receipt)}" target="_blank" rel="noopener">Receipt ↗</a>`:''}
          ${canRefund?`<button type="button" class="fdc-payment-refund" data-payment-refund="${Number(p.id||0)}" data-refundable="${refundable}">Refund</button>`:''}
        </div>`:''}
      </div>`;
    }).join('');
    const holdPanel=paymentHoldsHtml(holds);
    const securityPanel=roomSecurity>0.005?`<div class="fdc-security-summary">
      <div class="fdc-security-summary-copy"><span>Refundable Security</span><strong>${money(roomSecurity)}</strong><small>Room security is held separately from the stay balance.</small></div>
      <button type="button" data-drawer-action="security-settle">Manage Security</button>
    </div>`:'';

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
      ${securityPanel}
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

  function openReceivedPaymentRefund(button){
    const d=state.currentDetails;
    if(!d?.regId) return showToast('Reservation reference is missing.',true);
    if(!window.RefundPayment || typeof window.RefundPayment.open!=='function')
      return showToast('Refund component is not loaded.',true);

    const logId=Number(button?.dataset.paymentRefund||0);
    const refundable=Math.max(0,Number(button?.dataset.refundable||0));
    if(!logId || refundable<=0.005)
      return showToast('This payment has no refundable balance.',true);

    window.RefundPayment.open({
      regId:d.regId,
      logId,
      refundableAmount:refundable,
      currencySymbol:state.currency||'£',
      notify:(text,isError)=>showToast(text,isError),
      onCompleted:async()=>{
        await openDetails({
          regId:d.regId,
          paymentId:d.paymentId,
          guestName:d.guestName,
          roomNo:d.roomNo
        });
        await loadCalendar(state.start,true);
      }
    });
  }

  function openRoomGuestNameEditor(d,button=null){
    if(!d?.regId || !d?.paymentId){showToast('Selected room row is missing.',true);return;}
    if(isCheckedOutStatus(d.status)){showToast('Checked-out room guest details are read-only.',true);return;}

    const current=String(d.guestName||'').trim();
    const body=`<div class="fdc-pro-summary compact">
      <div class="fdc-pro-summary-icon">✎</div>
      <div><span>Room guest</span><b>Room ${esc(d.roomNo||'Unassigned')}</b><small>${esc(d.regId||'')}</small></div>
    </div>
    <div class="fdc-form">
      <div class="fdc-field full">
        <label for="fdcRoomGuestName">Guest Name</label>
        <input id="fdcRoomGuestName" type="text" maxlength="100" value="${esc(current)}" autocomplete="off" placeholder="Enter the guest name for this room" />
        <small>This changes only this room. The main reservation guest remains unchanged.</small>
      </div>
    </div>`;
    const foot=`<button type="button" class="fdc-btn fdc-btn-secondary" data-close-modal>Cancel</button><button type="button" class="fdc-btn primary" id="fdcSaveRoomGuestName">Save Guest Name</button>`;
    openModal('Update Room Guest Name',body,foot,'fdc-professional-mode');

    const input=$('fdcRoomGuestName');
    const save=$('fdcSaveRoomGuestName');
    requestAnimationFrame(()=>{input?.focus();input?.select();});

    const submit=async()=>{
      const guestName=String(input?.value||'').trim();
      if(!guestName){showToast('Guest name cannot be empty.',true);input?.focus();return;}
      setButtonBusy(save,true,'Saving…');
      try{
        const endpoint=cfg.updateRoomGuestUrl||'/CheckIn/UpdateChargeGuestName';
        const result=await api(endpoint,{method:'POST',body:JSON.stringify({regId:d.regId,paymentId:Number(d.paymentId||0),guestName})});
        if(result?.ok===false) throw new Error(result.message||'Unable to update room guest name.');
        showToast(result?.message||'Room guest name updated.');
        closeModal();
        await loadCalendar(state.start,true);
        await openDetails({regId:d.regId,paymentId:d.paymentId,guestName,roomNo:d.roomNo});
      }catch(e){
        showToast(e.message||'Unable to update room guest name.',true);
        setButtonBusy(save,false);
      }
    };

    save?.addEventListener('click',submit);
    input?.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();void submit();}});
  }

  function detailsActions(d){
    const s=normalizeStatus(d.status);
    const checked=isCheckedInStatus(s);
    const closed=isCheckedOutStatus(s);
    const provisional=s.includes('provisional') || s.includes('tentative');
    const arrival=dateOnly(d.arrival);
    const canNoShow=!checked && !closed && !provisional && arrival && arrival<state.hotelToday;
    const actions=[];
    const trailing=[];
    const action=(icon,label,key,tone='')=>`<button class="fdc-drawer-action ${tone}" type="button" data-drawer-action="${key}"><span class="fdc-action-icon">${icon}</span><span class="fdc-action-text">${label}</span></button>`;
    if(checked){
      actions.push(action('✎','Edit','edit'));
      actions.push(action('Aa','Edit Guest Name','guest-name'));
      actions.push(action('↶','Undo Check-in','undo'));
      actions.push(action('▤','Note Book','note'));
      actions.push(action('◆','Room Security','security'));
      actions.push(action('◷','Guest History','history'));
      actions.push(action('▧','Invoice','invoice'));
      actions.push(action('▤','Detail Invoice','detail-invoice'));
    }else if(closed){
      actions.push(action('◷','Guest History','history'));
      actions.push(action('▧','Invoice','invoice'));
      actions.push(action('▤','Detail Invoice','detail-invoice'));
    }else if(provisional){
      actions.push(action('✓','Confirm Reservation','confirm'));
      actions.push(action('Aa','Edit Guest Name','guest-name'));
      actions.push(action('◆','Room Security','security'));
      actions.push(action('◷','Guest History','history'));
      actions.push(action('▤','Detail Invoice','detail-invoice'));
      actions.push(action('×','Cancel Reservation','cancel','danger-action'));
    }else{
      actions.push(action('➜','Check-In Now','checkin-now','primary-action'));
      actions.push(action('▣','Edit','edit'));
      actions.push(action('Aa','Edit Guest Name','guest-name'));
      actions.push(action('▤','Note Book','note'));
      actions.push(action('◆','Room Security','security'));
      actions.push(action('◷','Guest History','history'));
      actions.push(action('▧','Invoice','invoice'));
      actions.push(action('▤','Detail Invoice','detail-invoice'));
      if(canNoShow) actions.push(action('⊘','No Show','no-show','danger-action'));
      else actions.push(action('×','Cancel Reservation','cancel','danger-action'));
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
    if(action==='detail-invoice'){await openDetailInvoice(d,button);return;}
    if(action==='guest-name'){openRoomGuestNameEditor(d,button);return;}
    if(action==='confirm'){
      openCheckInEditor(d);
      return;
    }
    if(action==='note'){openNote(d);return;}
    if(action==='security'){openRoomSecurity(d);return;}
    if(action==='security-settle'){openRoomSecurity(d,{settleLatestCardHold:true});return;}
    if(action==='no-show'){await noShowReservation(d,button);return;}
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

  async function noShowReservation(d,button=null){
    if(!d?.regId){showToast('Reservation reference is missing.',true);return;}
    const arrival=dateOnly(d.arrival);
    if(!arrival || arrival>=state.hotelToday){
      showToast('No Show is available only after the arrival date has passed.',true);
      return;
    }
    if(!confirm(`Mark ${d.guestName||'this guest'} as No Show? This will remove the active reservation.`)) return;
    try{
      await mutate(cfg.noShowUrl,{regId:d.regId,paymentId:d.paymentId||0},'Marking No Show…',button);
      closeDrawer();
      await loadCalendar(state.start,true);
    }catch{}
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
    const u=legacyPageUrl('Reservation_History');

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

  function openDetailInvoiceWindow(){
    const w=window.open('','_blank');
    if(!w){
      showToast('Please allow pop-ups for this website.',true);
      return null;
    }
    try{
      w.opener=null;
      w.document.title='Detail Invoice';
      w.document.body.innerHTML='<div style="font-family:Arial,sans-serif;padding:24px;color:#24364b">Opening Detail Invoice…</div>';
    }catch{}
    return w;
  }

  async function openDetailInvoice(d,button=null,targetWindow=null){
    if(!d?.regId){showToast('Reservation reference is missing.',true);return;}
    const invoiceWindow=targetWindow||openDetailInvoiceWindow();
    if(!invoiceWindow) return;

    setButtonBusy(button,true,'Opening…');
    try{
      // Use the Check-In reservation state so the query-string values are the
      // same ones used by the Check-In page Print button.
      const stateUrl=new URL(cfg.checkinUrl||'/CheckIn',location.origin);
      stateUrl.pathname=stateUrl.pathname.replace(/\/+$/,'').replace(/\/Index$/i,'')+'/ReservationState';
      stateUrl.search='';
      stateUrl.searchParams.set('regId',d.regId);

      const response=await fetch(stateUrl.toString(),{
        method:'GET',
        credentials:'same-origin',
        headers:{'X-Requested-With':'XMLHttpRequest'}
      });
      const html=await response.text();
      if(!response.ok) throw new Error('Unable to load the reservation invoice details.');

      const doc=new DOMParser().parseFromString(html,'text/html');
      const stateScript=doc.getElementById('reservationStateJson');
      if(!stateScript) throw new Error('Unable to load the reservation invoice details.');
      const invoiceState=JSON.parse(stateScript.textContent||'{}');
      const totals=invoiceState.totals||{};

      const current=new URL(window.location.href);
      const qs=new URLSearchParams();
      qs.set('reg_id',base64(invoiceState.reservationId||d.regId));
      qs.set('roomAmount',base64(String(Number(totals.roomSecurity??d.roomSecurity??0))));
      qs.set('visit',base64(String(invoiceState.visitId||d.visitId||'')));
      qs.set('paidAmount',base64(String(Number(totals.paidAmount??d.paid??0))));
      qs.set('payable',base64(String(Number(totals.payable??totals.grandTotal??d.total??0))));
      qs.set('paymethod',base64(String(totals.paymentMethod||'')));
      qs.set('hd',current.searchParams.get('hd')||base64(invoiceState.hotelId||cfg.hotelId||''));
      qs.set('UN',current.searchParams.get('UN')||base64(invoiceState.userName||cfg.userName||''));
      qs.set('UD',current.searchParams.get('UD')||base64(invoiceState.userId||cfg.userId||''));
      qs.set('PG',base64('CHECK-IN'));
      qs.set('FBR',base64(''));

      invoiceWindow.location.replace(`/BookingConfirmationInvoice.aspx?${qs.toString()}`);
    }catch(e){
      try{invoiceWindow.close();}catch{}
      showToast(e?.message||'Unable to open Detail Invoice.',true);
    }finally{
      setButtonBusy(button,false);
    }
  }

  function openInvoice(d){
    if(!d?.regId){showToast('Reservation reference is missing.',true);return;}

    // POST the reservation reference so it never appears in the browser URL.
    // The server creates a stateless protected token and redirects the new tab
    // to /InvoiceRecieving/i/{token}, which is safe to copy/send to a customer.
    const u=legacyPageUrl('InvoiceRecieving/OpenShare');
    const form=document.createElement('form');
    form.method='POST';
    form.action=u.toString();
    form.target='_blank';
    form.style.display='none';

    const addField=(name,value)=>{
      const input=document.createElement('input');
      input.type='hidden';
      input.name=name;
      input.value=String(value??'');
      form.appendChild(input);
    };

    addField('regId',d.regId);
    if(token) addField('__RequestVerificationToken',token);

    document.body.appendChild(form);
    form.submit();
    form.remove();
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
        ${tabs.map((t,i)=>`<button type="button" class="fdc-email-tab ${i===0?'fdc-is-active':''}" data-email-tab="${t.key}" ${t.disabled?'disabled aria-disabled="true"':''}>${esc(t.label)} <span>${esc(t.badge)}</span></button>`).join('')}
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
      modalBody.querySelectorAll('[data-email-tab]').forEach(btn=>btn.classList.toggle('fdc-is-active',btn.dataset.emailTab===active));
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
  function openRoomSecurity(d,options={}){
    if(!d?.regId){showToast('Reservation reference is missing.',true);return;}
    if(!window.RoomSecurity || typeof window.RoomSecurity.open!=='function'){
      showToast('Room Security component is not loaded.',true);
      return;
    }

    window.RoomSecurity.open({
      regId:d.regId,
      visitId:d.visitId||'',
      guestName:d.guestName||'Guest',
      securityBalance:Number(d.roomSecurity||0),
      currencySymbol:state.currency||'£',
      settleLatestCardHold:options.settleLatestCardHold===true,
      onChanged:async()=>{
        await openDetails({regId:d.regId,paymentId:d.paymentId,guestName:d.guestName,roomNo:d.roomNo});
        await loadCalendar(state.start,true);
      }
    }).catch(e=>showToast(e.message||'Unable to open Room Security.',true));
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
    const provisional=code==='P' || ['provisional','tentative'].includes(normalizeStatusKey(item.status));
    const arrival=dateOnly(item.arrival);
    const canNoShow=!checked && !closed && !provisional && arrival && arrival<state.hotelToday;
    const guest=item.guestName||item.regId||'Guest';
    const room=item.roomNo||'Unassigned';
    const statusLabel=checked?'Checked in':closed?'Checked out':provisional?'Provisional':'Reservation';
    const actions=[];
    const terminal=[];

    if(checked){
      actions.push(bookingMenuItem('✎','Edit','edit'));
      actions.push(bookingMenuItem('Aa','Edit Guest Name','guest-name'));
      actions.push(bookingMenuItem('↶','Undo Check-in','undo'));
      actions.push(bookingMenuItem('▤','Note Book','note'));
      actions.push(bookingMenuItem('◆','Room Security','security'));
      actions.push(bookingMenuItem('◷','Guest History','history'));
      actions.push(bookingMenuItem('▧','Invoice','invoice'));
      actions.push(bookingMenuItem('▤','Detail Invoice','detail-invoice'));
    }else if(closed){
      actions.push(bookingMenuItem('◷','Guest History','history'));
      actions.push(bookingMenuItem('▧','Invoice','invoice'));
      actions.push(bookingMenuItem('▤','Detail Invoice','detail-invoice'));
    }else if(provisional){
      actions.push(bookingMenuItem('✓','Confirm Reservation','confirm'));
      actions.push(bookingMenuItem('Aa','Edit Guest Name','guest-name'));
      actions.push(bookingMenuItem('◆','Room Security','security'));
      actions.push(bookingMenuItem('◷','Guest History','history'));
      actions.push(bookingMenuItem('▤','Detail Invoice','detail-invoice'));
      actions.push(bookingMenuItem('×','Cancel Reservation','cancel','danger-action'));
    }else{
      actions.push(bookingMenuItem('➜','Check-In Now','checkin','primary-action'));
      actions.push(bookingMenuItem('▣','Edit','edit'));
      actions.push(bookingMenuItem('Aa','Edit Guest Name','guest-name'));
      actions.push(bookingMenuItem('▤','Note Book','note'));
      actions.push(bookingMenuItem('◆','Room Security','security'));
      actions.push(bookingMenuItem('◷','Guest History','history'));
      actions.push(bookingMenuItem('▧','Invoice','invoice'));
      actions.push(bookingMenuItem('▤','Detail Invoice','detail-invoice'));
      if(canNoShow) actions.push(bookingMenuItem('⊘','No Show','no-show','danger-action'));
      else actions.push(bookingMenuItem('×','Cancel Reservation','cancel','danger-action'));
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

    // Detail Invoice must open a tab immediately from the user's click so popup
    // blockers do not stop it while the full Check-In reservation state loads.
    if(action==='detail-invoice'){
      const invoiceWindow=openDetailInvoiceWindow();
      if(!invoiceWindow) return;
      setButtonBusy(button,true,'Opening…');
      context.classList.add('fdc-context-processing');
      context.dataset.processing='1';
      try{
        const d=await fetchBookingDetails(item);
        if(!d) throw new Error('Unable to load reservation details.');
        state.currentDetails=d;
        await openDetailInvoice(d,null,invoiceWindow);
        closeContext(true);
      }catch(e){
        try{invoiceWindow.close();}catch{}
        showToast(e?.message||'Unable to open Detail Invoice.',true);
        setButtonBusy(button,false);
        context.classList.remove('fdc-context-processing');
        context.removeAttribute('data-processing');
      }
      return;
    }

    // Confirm destructive actions before switching the clicked item into loader mode.
    if(action==='undo' && !confirm(`Undo check-in for ${item.guestName||'this guest'}?`)) return;
    if(action==='no-show' && !confirm(`Mark ${item.guestName||'this guest'} as No Show? This will remove the active reservation.`)) return;

    const labels={
      checkin:'Checking in…', edit:'Opening…', cancel:'Opening…', 'no-show':'Marking No Show…', undo:'Undoing…',
      note:'Loading…', history:'Loading…', payment:'Loading…', security:'Loading security…', holds:'Loading holds…', sendpaylink:'Preparing…',
      autopay:'Opening…', invoice:'Opening…', 'detail-invoice':'Opening…', 'guest-name':'Loading…', confirm:'Opening…', checkout:'Loading…',
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
      else if(action==='no-show'){
        await mutate(cfg.noShowUrl,{regId:item.regId,paymentId:item.paymentId||0},'Marking No Show…',null);
        await loadCalendar(state.start,true);
        closeAfter=true;
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
        else if(action==='security'){closeAfter=true;deferredAction=()=>openRoomSecurity(d);}
        else if(action==='history'){closeAfter=true;deferredAction=()=>openHistoryPage(d);}
        else if(action==='payment'){closeAfter=true;deferredAction=()=>openPayment(d);}
        else if(action==='sendpaylink'){closeAfter=true;deferredAction=()=>openSendPaymentLink(d,null);}
        else if(action==='invoice'){closeAfter=true;deferredAction=()=>openInvoice(d);}
        else if(action==='detail-invoice'){closeAfter=true;deferredAction=()=>openDetailInvoice(d,null);}
        else if(action==='guest-name'){closeAfter=true;deferredAction=()=>openRoomGuestNameEditor(d,null);}
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
      preview.classList.remove('fdc-is-active','invalid');
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
    layer.classList.add('fdc-is-active');
    document.body.classList.add('fdc-dnd-focus-active');
    page.classList.add('is-dragging');
    closeTip();
  }

  function endDragFocus(){
    clearDestinationHighlights();
    const layer=document.getElementById('fdcDragFocusLayer');
    layer?.classList.remove('fdc-is-active');
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
    const target=targetRange(item,cell);
    const bounds=geometryForRange(target.start,target.end);
    if(!bounds)return;
    const key=`${row.dataset.category}|${row.dataset.room}|${iso(target.start)}|${iso(target.end)}|${valid?'1':'0'}`;
    const focus=state.dragFocus||{};
    if(focus.lastKey===key) return;
    clearDestinationHighlights();
    const roomLabel=row.querySelector('.fdc-room-label');
    roomLabel?.classList.add('fdc-dnd-room-active');
    const headers=[...calendar.querySelectorAll('.fdc-date-head .fdc-day')];
    const activeHeaders=[];
    state.months.forEach((m,i)=>{
      if(rangeOverlaps(target.start,target.end,m.startDate,addDays(m.endDate,1))&&headers[i]){
        headers[i].classList.add('fdc-dnd-date-active');activeHeaders.push(headers[i]);
      }
    });
    const firstCell=row.querySelector('.fdc-cell');
    if(!firstCell)return;
    const firstRect=firstCell.getBoundingClientRect(),rowRect=row.getBoundingClientRect(),scrollRect=scroll.getBoundingClientRect();
    let left=firstRect.left+(bounds.leftUnits*firstRect.width);
    let right=firstRect.left+(bounds.rightUnits*firstRect.width);
    left=Math.max(left,scrollRect.left);right=Math.min(right,scrollRect.right);
    const top=Math.max(rowRect.top+3,scrollRect.top),bottom=Math.min(rowRect.bottom-3,scrollRect.bottom);
    const preview=(state.dragFocus?.preview)||ensureDragFocusLayer().querySelector('.fdc-destination-preview');
    if(preview&&right>left&&bottom>top){
      preview.style.backgroundColor=state.dragFocus?.previewColor||window.getComputedStyle(state.drag.bar).backgroundColor||'#2975db';
      preview.style.transform=`translate3d(${Math.round(left)}px,${Math.round(top)}px,0)`;
      preview.style.width=`${Math.max(1,Math.round(right-left))}px`;
      preview.style.height=`${Math.max(1,Math.round(bottom-top))}px`;
      preview.classList.toggle('invalid',!valid);preview.classList.add('fdc-is-active');
    }
    if(state.dragFocus){state.dragFocus.roomLabel=roomLabel;state.dragFocus.dateHeaders=activeHeaders;state.dragFocus.lastKey=key;}
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

  // Month-wise drag/drop must not snap a stay to the first of the month.
  // The grid cell represents a MONTH, not a specific day. Preserve the
  // reservation's original arrival day inside the destination month.
  // Example: 07 Oct -> 07 Jan moved to another October room stays
  // 07 Oct -> 07 Jan; moved to November starts on 07 Nov.
  function arrivalInTargetMonth(item,cell){
    const original=dateOnly(item.arrival);
    const monthStart=dateOnly(cell?.dataset?.date);
    if(!original)return monthStart;
    if(!monthStart)return original;

    const lastDay=new Date(monthStart.getFullYear(),monthStart.getMonth()+1,0).getDate();
    const day=Math.min(original.getDate(),lastDay);
    return new Date(monthStart.getFullYear(),monthStart.getMonth(),day);
  }

  function targetRange(item,cell){
    const checkoutMove=String(item.statusCode||'').toUpperCase()==='CO' || isCheckedOutStatus(item.status);
    const start=checkoutMove?dateOnly(item.arrival):arrivalInTargetMonth(item,cell);
    const nights=dragNightCount(item);
    return {start,end:addDays(start,nights),nights};
  }
  function sameBooking(a,b){
    if(!a||!b)return false;
    if(a.paymentId&&b.paymentId&&Number(a.paymentId)===Number(b.paymentId))return true;
    return String(a.id||'')!=='' && String(a.id||'')===String(b.id||'');
  }
  function cellMonthRange(cell){
    const start=dateOnly(cell?.dataset?.date);
    if(!start)return null;
    const end=addDays(dateOnly(cell.dataset.monthEnd)||new Date(start.getFullYear(),start.getMonth()+1,0),1);
    return {start,end};
  }
  function targetBookingAtCell(item,cell,directBar=null){
    if(directBar){
      const direct=bookingFrom(directBar);
      if(direct&&!sameBooking(item,direct))return direct;
    }
    const r=cellMonthRange(cell);if(!r)return null;
    const room=String(cell.dataset.room||'').toUpperCase(),cat=String(cell.dataset.category||'');
    return (state.payload?.bookings||[]).find(b=>{
      if(sameBooking(item,b))return false;
      if(String(b.categoryId)!==cat||String(b.roomNo||'UNASSIGNED').toUpperCase()!==room)return false;
      return dateOnly(b.arrival)<r.end && r.start<dateOnly(b.departure);
    })||null;
  }
  function targetBlockAtCell(cell,directBar=null){
    if(directBar){
      try{return JSON.parse(directBar.dataset.block||'{}');}catch{}
    }
    const r=cellMonthRange(cell);if(!r)return null;
    const room=String(cell.dataset.room||'').toUpperCase(),cat=String(cell.dataset.category||'');
    return (state.payload?.blocks||[]).find(b=>
      String(b.categoryId)===cat && String(b.roomNo||'').toUpperCase()===room &&
      dateOnly(b.startDate)<r.end && r.start<addDays(dateOnly(b.endDate),1)
    )||null;
  }
  function sameStayDates(a,b){
    return !!a&&!!b&&sameDate(a.arrival,b.arrival)&&sameDate(a.departure,b.departure);
  }
  function targetRangeIsFree(item,cell){
    const {start,end}=targetRange(item,cell); const room=String(cell.dataset.room||'').toUpperCase();
    if(room==='UNASSIGNED') return true; const cat=String(cell.dataset.category||'');
    const overlaps=(a,d)=>dateOnly(a)<end && start<dateOnly(d);
    for(const b of (state.payload?.bookings||[])){
      if(sameBooking(item,b)) continue;
      if(String(b.categoryId)!==cat || String(b.roomNo||'UNASSIGNED').toUpperCase()!==room) continue;
      if(overlaps(b.arrival,b.departure)) return false;
    }
    for(const b of (state.payload?.blocks||[])){
      if(String(b.categoryId)!==cat || String(b.roomNo||'').toUpperCase()!==room) continue;
      if(overlaps(b.startDate,addDays(dateOnly(b.endDate),1))) return false;
    }
    return true;
  }
  function monthCellFromEvent(e){
    const direct=e.target.closest('.fdc-cell');
    if(direct)return direct;
    const row=e.target.closest('.fdc-room-row');
    if(!row)return null;
    const cells=[...row.querySelectorAll('.fdc-cell')];
    if(!cells.length)return null;
    const x=e.clientX;
    return cells.find(c=>{const r=c.getBoundingClientRect();return x>=r.left&&x<r.right;})||
      (x<cells[0].getBoundingClientRect().left?cells[0]:cells[cells.length-1]);
  }
  function markDropPreview(cell,directBookingBar=null,directBlockBar=null){
    if(!state.drag)return;
    const item=state.drag.item;
    const targetBooking=targetBookingAtCell(item,cell,directBookingBar);
    const targetBlock=targetBlockAtCell(cell,directBlockBar);
    // Occupied cells are a valid WebForms-style SWAP target only when the two
    // stays have identical dates. A blocked target is never valid.
    const valid=targetBlock?false:(targetBooking?sameStayDates(item,targetBooking):targetRangeIsFree(item,cell));
    renderDragFocus(cell,valid);
  }

  function setMoveDropLoading(show){
    const preview=document.querySelector('#fdcDragFocusLayer .fdc-destination-preview');
    state.dropPending=!!show;
    document.body.classList.toggle('fdc-move-drop-pending',!!show);
    if(preview){
      preview.classList.toggle('loading',!!show);
      if(show) preview.classList.add('fdc-is-active');
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
    const directBookingBar=e.target.closest('.fdc-bar[data-booking]');
    const directBlockBar=e.target.closest('.fdc-bar[data-block-id]');
    const targetBlock=targetBlockAtCell(cell,directBlockBar);
    if(targetBlock){
      showToast('Cannot change room: the target room is blocked.',true);
      finishDragUi();
      return;
    }

    const targetBooking=targetBookingAtCell(item,cell,directBookingBar);
    if(targetBooking){
      if(!sameStayDates(item,targetBooking)){
        showToast('Cannot swap these rooms because both reservations must have the same check-in and check-out dates.',true);
        finishDragUi();
        return;
      }
      const swapReq={
        sourceRegId:item.regId,sourcePaymentId:Number(item.paymentId||0),sourceRoomNo:item.roomNo||'',
        sourceCategoryId:item.categoryId||'',sourceCategoryName:item.categoryName||'',
        targetRegId:targetBooking.regId,targetPaymentId:Number(targetBooking.paymentId||0),targetRoomNo:targetBooking.roomNo||'',
        targetCategoryId:targetBooking.categoryId||'',targetCategoryName:targetBooking.categoryName||''
      };
      setMoveDropLoading(true);
      state.drag?.bar?.classList.remove('dragging');
      try{
        await mutate(cfg.mwSwapUrl||'/CalendarMW/SwapBooking',swapReq,`Swapping ${item.guestName||'booking'}…`);
        await loadCalendar(state.start,true);
      }finally{finishDragUi();}
      return;
    }

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
      // Use the same date calculation as the availability preview. A month
      // cell's data-date is always the 1st, so sending it directly would
      // incorrectly change 07 Oct -> 07 Jan into 01 Oct -> 01 Jan.
      newArrival:iso(targetRange(item,cell).start),
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
    e.preventDefault();e.stopPropagation();
    const bar=handle.closest('.fdc-bar'),item=bookingFrom(bar);if(!item?.canResize)return;
    closeTip();
    const row=bar.closest('.fdc-room-row');
    const firstCell=row?.querySelector('.fdc-cell');
    if(!row||!firstCell)return;
    const monthPx=firstCell.getBoundingClientRect().width||112;
    const oldMonths=Math.max(1,monthDiffCeil(item.arrival,item.departure));
    state.resize={item,startX:e.clientX,bar,row,monthPx,monthDelta:0,oldMonths,candidate:dateOnly(item.departure)};
    bar.classList.add('resizing');bar.setPointerCapture?.(e.pointerId);document.body.style.userSelect='none';
  }

  function resizeMove(e){
    const r=state.resize;if(!r)return;
    let delta=Math.round((e.clientX-r.startX)/Math.max(1,r.monthPx));
    if(r.oldMonths+delta<1)delta=1-r.oldMonths;
    const candidate=addMonths(dateOnly(r.item.departure),delta);
    const arrival=dateOnly(r.item.arrival);
    if(!candidate||candidate<=arrival)return;
    r.monthDelta=delta;r.candidate=candidate;
    const bounds=geometryForRange(arrival,candidate);if(!bounds)return;
    r.bar.style.width=`calc(${bounds.widthUnits} * var(--month-col) - 4px)`;
  }

  async function checkMwResizeAvailability(item,newDeparture){
    const url=cfg.mwResizeCheckUrl||'/CalendarMW/CheckResizeAvailability';
    return api(url,{method:'POST',body:JSON.stringify({
      regId:item.regId,
      arrival:iso(item.arrival),
      newDeparture:iso(newDeparture)
    })});
  }

  async function loadMwResizePlans(item){
    const url=new URL(cfg.mwResizePlansUrl||'/CalendarMW/ResizePlans',location.origin);
    url.searchParams.set('regId',item.regId||'');
    url.searchParams.set('arrival',iso(item.arrival));
    url.searchParams.set('oldDeparture',iso(item.departure));
    const result=await api(url.toString());
    return result?.data?.plans||[];
  }

  function renderMwPlanRows(plans){
    return plans.map((p,i)=>`<div class="fdc-mw-plan-row">
      <div class="fdc-mw-plan-name"><b>${esc(p.roomType||p.planName||p.planId||'Room')}</b><small>${esc(p.planName||p.planId||'')}</small></div>
      <div class="fdc-mw-plan-old">${money(p.oldRate||0)}</div>
      <div class="fdc-mw-plan-new"><span>${esc(state.currency||'£')}</span><input class="fdc-mw-plan-rate" data-plan-index="${i}" type="number" min="0" step="0.01" value="${Number(p.newRate??p.oldRate??0).toFixed(2)}"></div>
    </div>`).join('');
  }

  function openMwResizeConfirm(item,newDeparture,monthDelta,plans){
    const arrival=dateOnly(item.arrival),oldDeparture=dateOnly(item.departure);
    const isExtend=monthDelta>0;
    const oldMonths=Math.max(1,monthDiffCeil(arrival,oldDeparture));
    const newMonths=Math.max(1,monthDiffCeil(arrival,newDeparture));
    const changedMonths=Math.abs(monthDelta);
    const workingPlans=(plans||[]).map(p=>({...p,newRate:Number(p.newRate??p.oldRate??0)}));
    const title=isExtend?'Confirm Extend Reservation':'Confirm Shrink Reservation';
    const subtitle=isExtend?'Review extended months and monthly rates before confirming.':'Review the reduced stay and monthly rates before confirming.';

    const body=`<div class="fdc-resize-confirm fdc-mw-resize-confirm">
      <div class="fdc-resize-sub">${subtitle}</div>
      <div class="fdc-resize-grid">
        <div><span>Guest Name</span><b>${esc(item.guestName||'—')}</b></div>
        <div><span>Reference</span><b>${esc(item.regId||'—')}</b></div>
        <div><span>Arrival</span><b>${fmt(arrival,true)}</b></div>
        <div><span>Old Departure</span><b>${fmt(oldDeparture,true)}</b></div>
        <div><span>New Departure</span><b>${fmt(newDeparture,true)}</b></div>
        <div class="highlight"><span>${isExtend?'Extended Months':'New Total Months'}</span><b>${isExtend?changedMonths:newMonths} month(s)</b></div>
      </div>
      <div class="fdc-mw-plan-box">
        <div class="fdc-mw-plan-head"><span>Room Type / Rate Plan</span><span>Old Rate / Month</span><span>New Rate / Month</span></div>
        <div id="fdcMwPlans">${renderMwPlanRows(workingPlans)}</div>
      </div>
      <div class="fdc-resize-total"><span>${isExtend?'Total Amount of Extended Months':'New Room Amount'}</span><b id="resizeTotal"></b></div>
    </div>`;

    openModal(title,body,`<button class="fdc-btn fdc-btn-secondary" data-close-modal>Cancel</button><button class="fdc-btn primary" id="confirmResize">${isExtend?'Confirm Extend':'Confirm Shrink'}</button>`,'fdc-professional-mode fdc-resize-mode');

    const totalEl=$('resizeTotal');
    const recalc=()=>{
      document.querySelectorAll('.fdc-mw-plan-rate').forEach(input=>{
        const i=Number(input.dataset.planIndex||0);
        if(workingPlans[i])workingPlans[i].newRate=Math.max(0,Number(input.value||0));
      });
      const multiplier=isExtend?changedMonths:newMonths;
      const total=workingPlans.reduce((sum,p)=>sum+(Math.max(0,Number(p.newRate||0))*multiplier),0);
      totalEl.textContent=money(total);
    };
    document.querySelectorAll('.fdc-mw-plan-rate').forEach(input=>input.addEventListener('input',recalc));
    recalc();

    $('confirmResize').onclick=async e=>{
      const btn=e.currentTarget;recalc();
      if(!workingPlans.length){showToast('No monthly rate plans were found for this reservation.',true);return;}
      try{
        await mutate(cfg.mwResizeUrl||'/CalendarMW/ResizeBooking',{
          regId:item.regId,
          arrival:iso(arrival),
          oldDeparture:iso(oldDeparture),
          newDeparture:iso(newDeparture),
          plans:workingPlans.map(p=>({
            planId:p.planId||'',planName:p.planName||'',roomType:p.roomType||'',categoryLocalId:p.categoryLocalId||'',oldRate:Number(p.oldRate||0),newRate:Number(p.newRate||0)
          }))
        },isExtend?'Extending…':'Shrinking…',btn);
        closeModal();
        await loadCalendar(state.start,true);
      }catch{}
    };
  }

  async function resizeEnd(){
    const r=state.resize;if(!r)return;
    state.resize=null;document.body.style.userSelect='';r.bar.classList.remove('resizing');
    const delta=r.monthDelta||0;
    render();
    if(!delta)return;

    const a=dateOnly(r.item.arrival),oldD=dateOnly(r.item.departure),newD=addMonths(oldD,delta);
    if(!a||!newD||newD<=a){showToast('New departure must be after arrival.',true);return;}

    try{
      showToast('Checking room availability…');
      await checkMwResizeAvailability(r.item,newD);
      const plans=await loadMwResizePlans(r.item);
      if(!plans.length){showToast('No monthly rate plans were found for this reservation.',true);return;}
      openMwResizeConfirm(r.item,newD,delta,plans);
    }catch(e){
      showToast(e.message||'Room is not available for the selected month range.',true);
    }
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
        const newStart=dateOnly(dateBtn.dataset.rangeDate);
        const spanDays=Math.max(0,diffDays(state.start,state.end));
        loadCalendar(newStart,addDays(newStart,spanDays));
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
    const cell=e.target.closest('.fdc-cell');if(cell&&!e.target.closest('.fdc-bar')){openRoomAction(cell);}
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
  calendar.addEventListener('dragover',e=>{
    const cell=monthCellFromEvent(e);
    if(cell&&state.drag){
      e.preventDefault();
      markDropPreview(cell,e.target.closest('.fdc-bar[data-booking]'),e.target.closest('.fdc-bar[data-block-id]'));
    }
  });
  calendar.addEventListener('drop',e=>{const cell=monthCellFromEvent(e);if(cell)dropBooking(e,cell);});
  calendar.addEventListener('pointerdown',e=>{const h=e.target.closest('[data-resize]');if(h)resizeStart(e,h);});
  document.addEventListener('pointermove',resizeMove);
  document.addEventListener('pointerup',resizeEnd);

  drawerBody.addEventListener('click',e=>{
    const refundButton=e.target.closest('[data-payment-refund]');
    if(refundButton){
      e.preventDefault();
      openReceivedPaymentRefund(refundButton);
      return;
    }
    const holdButton=e.target.closest('[data-drawer-hold-action]');
    if(holdButton){
      e.preventDefault();
      void drawerHoldAction(holdButton);
      return;
    }
    const actionButton=e.target.closest('[data-drawer-action]');
    if(actionButton){
      e.preventDefault();
      drawerAction(actionButton.dataset.drawerAction,actionButton);
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

  $('fdcPrev').onclick=()=>{rangePicker.classList.remove('open');shiftRangeByMonths(-monthCount());};
  $('fdcNext').onclick=()=>{rangePicker.classList.remove('open');shiftRangeByMonths(monthCount());};
  $('fdcToday').onclick=()=>{const spanDays=Math.max(0,diffDays(state.start,state.end));loadCalendar(state.hotelToday,addDays(state.hotelToday,spanDays));};

  let monthFitTimer=0;
  window.addEventListener('resize',()=>{
    clearTimeout(monthFitTimer);
    monthFitTimer=setTimeout(fitMonthColumnsToViewport,80);
  });
  if(window.ResizeObserver){
    const monthWidthObserver=new ResizeObserver(()=>fitMonthColumnsToViewport());
    monthWidthObserver.observe(scroll);
  }
  $('fdcRefresh').onclick=()=>loadCalendar(state.start,state.end,true);
  $('fdcRange').onclick=e=>{e.stopPropagation();openRangePicker();};
  $('fdcLegendToggle').onclick=()=>{const x=$('fdcLegend'),hidden=!x.hidden;x.hidden=hidden;$('fdcLegendToggle').setAttribute('aria-expanded',String(!hidden));};



  // Calendar page only: start with the master sidebar collapsed without editing the master layout.
  document.body.classList.add('sidebar-collapsed');
  document.body.classList.remove('mobile-sidebar-open');
  document.getElementById('sidebar')?.classList.remove('open');

  loadCalendar(state.start,state.end);
})();
