(() => {
  'use strict';
  const root = document.getElementById('remainingBalancesPage');
  if (!root) return;
  const get = id => document.getElementById(id);
  const endpoint = root.dataset.dataUrl;
  const state = {
    start: root.dataset.start || null,
    end: root.dataset.end || null,
    search: '', source: '', status: 'all',
    page: 1, pageSize: Number(root.dataset.pageSize) || 50,
    total: 0, loading: false,
    totals: { all: 0, activeOnly: 0 },
    draftStart: null, draftEnd: null,
    month: null,
    abort: null
  };
  const today = new Date();
  const isoToday = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2,'0')}-${String(today.getDate()).padStart(2,'0')}`;
  const currency = root.dataset.currency || '£';
  const money = x => currency + ' ' + Number(x || 0).toLocaleString('en-GB', {minimumFractionDigits: 2, maximumFractionDigits: 2});
  const fmt = s => { if(!s)return '—'; const m = String(s).match(/^(\d{4})-(\d{2})-(\d{2})/); return m ? `${m[3]}-${m[2]}-${m[1]}` : '—'; };
  const shortDate = s => {if(!s)return '';const m=String(s).match(/^(\d{4})-(\d{2})-(\d{2})/);return m ? `${m[3]}/${m[2]}/${m[1]}` : '';};
  const escape = text => String(text == null ? '' : text).replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  const parseDate = s => s ? new Date(`${s}T12:00:00`) : null;
  const iso = date => `${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}`;
  const addDays = (date, days) => { const d = new Date(date); d.setDate(d.getDate()+days); return d; };
  const dateTrigger = get('rbDateTrigger'), picker = get('rbDatePicker');
  let debounce;

  function dateLabel(start = state.start, end = state.end) {
    return start && end ? `${shortDate(start)} - ${shortDate(end)}` : 'All dates';
  }
  function paintDateLabel() { get('rbDateLabel').textContent = dateLabel(); }
  function statusClass(s) {return String(s||'').toLowerCase().replace(/\s+/g,'-');}
  function badge(s) {return `<span class="rb-status ${escape(statusClass(s))}">${escape(s)}</span>`;}
  function guestIcon(name) { return escape(String(name || 'G').trim().charAt(0).toUpperCase() || 'G'); }
  function renderRows(rows) {
    const tbody = get('rbTableBody');
    tbody.innerHTML = rows.map((r, idx) => {
      const name = r.guestName || 'Guest';
      const canPay = r.canReceivePayment === true;
      const pay = canPay ? `<button type="button" class="rb-pay" data-reg="${escape(r.regId)}" data-visit="${escape(r.visitId)}" data-name="${escape(name)}" data-balance="${escape(r.remaining)}" data-source="${escape(r.sourceTable)}" data-arrival="${escape(r.arrival || '')}" data-departure="${escape(r.departure || '')}"><i class="fa-solid fa-wallet"></i> Receive Payment</button>` : '<span class="rb-muted" title="Reservation record unavailable for payment">Unavailable</span>';
      return `<tr>
        <td class="rb-rowno">${((state.page - 1) * state.pageSize) + idx + 1}</td>
        <td><strong>${escape(r.regId)}</strong></td>
        <td><div class="rb-guest"><span class="rb-avatar">${guestIcon(name)}</span><div><strong>${escape(name)}</strong><small>${escape(r.phone || r.email || '')}</small></div></div></td>
        <td><strong>${fmt(r.arrival)} – ${fmt(r.departure)}</strong><small>${escape(r.roomCategory || 'Room not assigned')}</small></td>
        <td>${escape(r.bookingSource || 'Walk In')}</td><td>${badge(r.status)}</td>
        <td class="numeric">${money(r.grandTotal)}</td><td class="numeric">${money(r.paid)}</td>
        <td class="numeric rb-due">${money(r.remaining)}</td><td>${pay}</td>
      </tr>`;
    }).join('');
    get('rbEmpty').hidden = rows.length !== 0;
  }
  function renderPagination(total) {
    const pages = Math.max(1,Math.ceil(total/state.pageSize));
    const box = get('rbPagination');
    const build = (text, index, active, disabled) => `<button type="button" data-page="${index}"${active?' class="active" aria-current="page"':''}${disabled?' disabled':''}>${text}</button>`;
    const first = Math.max(1, Math.min(state.page-2, pages-4));
    const last = Math.min(pages, first+4);
    let html = build('‹',state.page-1,false,state.page<=1);
    for(let i=first;i<=last;i++)html+=build(i,i,i===state.page,false);
    html+=build('›',state.page+1,false,state.page>=pages);
    box.innerHTML=html;
    const beginning = total ? (state.page-1)*state.pageSize+1 : 0;
    const ending = Math.min(total,state.page*state.pageSize);
    get('rbShowingBottom').textContent = `Showing ${beginning}–${ending} of ${total.toLocaleString('en-GB')}`;
  }
  function paintReceivablesTotal() {
    const exclude = get('rbExcludeClosed').checked;
    get('rbReceivablesTotal').textContent = money(exclude ? state.totals.activeOnly : state.totals.all);
    get('rbReceivablesTitle').textContent = exclude ? 'Receivables Total (Active)' : 'Receivables Total';
    get('rbReceivablesDescription').textContent = exclude
      ? 'Excludes cancelled and no-show balances; table rows stay visible'
      : 'Includes cancelled and no-show balances in the selected filters';
  }
  function renderSummary(res) {
    state.total = Number(res.totalRecords) || 0;
    state.totals.all = Number(res.totalReceivables) || 0;
    state.totals.activeOnly = Number(res.totalReceivablesActiveOnly) || 0;
    paintReceivablesTotal();
    get('rbGuestsCount').textContent=state.total.toLocaleString('en-GB');
    get('rbBilledTotal').textContent=money(res.totalBilled);
    get('rbPaidTotal').textContent=money(res.totalPaid);
    get('rbShowingTop').textContent=`Showing ${state.total.toLocaleString('en-GB')}`;
    get('rbAllCount').textContent=state.total.toLocaleString('en-GB');
    renderRows(res.rows || []);
    renderPagination(state.total);
  }
  async function load() {
    if(state.abort)state.abort.abort();
    const controller = new AbortController();state.abort=controller;
    root.classList.add('loading');state.loading=true;get('rbError').hidden=true;
    const u=new URL(endpoint, location.origin);
    for(const [k,v] of Object.entries({start:state.start||'',end:state.end||'',search:state.search,source:state.source,status:state.status,page:state.page,pageSize:state.pageSize}))u.searchParams.set(k,v);
    try {
      const response=await fetch(u,{signal:controller.signal,credentials:'same-origin',headers:{'Accept':'application/json'}});
      const json=await response.json().catch(()=>null);
      if(response.status===401){location.href='/LoginHMS';return;}
      if(!response.ok || !json || json.ok!==true)throw new Error(json?.message || 'Unable to load remaining balances.');
      if(state.page>1 && !json.result?.rows?.length) {state.page=1;return await load();}
      renderSummary(json.result);
    } catch(error) {
      if(error.name!=='AbortError'){get('rbError').textContent=error.message || 'Unable to load balances.';get('rbError').hidden=false;}
    } finally {if(state.abort===controller){root.classList.remove('loading');state.loading=false;state.abort=null;}}
  }
  function runSearch(){state.search=get('rbSearch').value.trim();state.source=get('rbSource').value;state.page=1;load();}
  get('rbSearchButton').addEventListener('click',runSearch);
  get('rbSearch').addEventListener('input',()=>{clearTimeout(debounce);debounce=setTimeout(runSearch,400);});
  get('rbSearch').addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();clearTimeout(debounce);runSearch();}});
  get('rbSource').addEventListener('change',runSearch);
  get('rbRefresh').addEventListener('click',()=>load());
  get('rbExcludeClosed').addEventListener('change', paintReceivablesTotal);
  get('rbStatusTabs').addEventListener('click',event=>{const b=event.target.closest('[data-status]');if(!b)return;state.status=b.dataset.status;state.page=1;get('rbStatusTabs').querySelectorAll('[data-status]').forEach(x=>x.classList.toggle('active',x===b));load();});
  get('rbPageSize').addEventListener('change',event=>{state.pageSize=Number(event.target.value)||50;state.page=1;load();});
  get('rbPagination').addEventListener('click',event=>{const b=event.target.closest('[data-page]');if(!b || b.disabled)return;state.page=Number(b.dataset.page);load();});
  get('rbTableBody').addEventListener('click',event=>{
    const b=event.target.closest('button.rb-pay');if(!b)return;
    if(!window.RecordPayment?.open){get('rbError').hidden=false;get('rbError').textContent='The shared Record Payment component is unavailable.';return;}
    window.RecordPayment.open({
      regId:b.dataset.reg,visitId:b.dataset.visit || '',guestName:b.dataset.name || 'Guest',
      balance:Number(b.dataset.balance)||0,currencySymbol:currency,hotelId:root.dataset.hotelId||'',
      userId:root.dataset.userId||'', currencyCode:root.dataset.currencyCode||'GBP', source:b.dataset.source || 'NR',
      arrival:b.dataset.arrival||'',departure:b.dataset.departure||'',
      // Show only property-configured payment modes granted to this staff member.
      showOnlineCard:root.dataset.canOnlineCard==='1',
      showPdqPayment:root.dataset.canPdqPayment==='1',showAutoPay:false,
      onCompleted:async()=>{await load();}
    });
  });

  function changeMonth(offset){state.month.setMonth(state.month.getMonth()+offset);renderCalendars();}
  function renderCalendars(){
    const first=state.month;
    const months=[new Date(first.getFullYear(),first.getMonth(),1),new Date(first.getFullYear(),first.getMonth()+1,1)];
    get('rbCalendarMonths').innerHTML=months.map(m=>`<span>${escape(m.toLocaleDateString('en-GB',{month:'long',year:'numeric'}))}</span>`).join('');
    get('rbCalendars').innerHTML=months.map(m=>{
      const days=new Date(m.getFullYear(),m.getMonth()+1,0).getDate();
      const offset=(new Date(m.getFullYear(),m.getMonth(),1).getDay()+6)%7;
      let html='<div class="rb-calendar">'+['Mon','Tue','Wed','Thu','Fri','Sat','Sun'].map(w=>`<span class="rb-weekday">${w}</span>`).join('');
      for(let i=0;i<offset;i++)html+='<button type="button" class="rb-day" disabled> </button>';
      for(let day=1;day<=days;day++){
        const d=iso(new Date(m.getFullYear(),m.getMonth(),day));
        const cls=d===state.draftStart?'start':d===state.draftEnd?'end':(state.draftStart && state.draftEnd && d>state.draftStart && d<state.draftEnd?'range':'');
        html+=`<button type="button" class="rb-day ${cls}" data-date="${d}" aria-label="${d}">${day}</button>`;
      }
      return html+'</div>';
    }).join('');
    get('rbDraftLabel').textContent=dateLabel(state.draftStart,state.draftEnd);
  }
  function hidePicker(){picker.hidden=true;dateTrigger.setAttribute('aria-expanded','false');}
  function showPicker(){state.draftStart=state.start;state.draftEnd=state.end;const d=parseDate(state.start)||today;state.month=new Date(d.getFullYear(),d.getMonth(),1);renderCalendars();picker.hidden=false;dateTrigger.setAttribute('aria-expanded','true');}
  dateTrigger.addEventListener('click',()=>picker.hidden?showPicker():hidePicker());
  // Clicking a day redraws the calendar and detaches the clicked button.
  // Without stopping propagation the document outside-click listener sees the
  // detached target as outside and closes the picker after the first date.
  picker.addEventListener('click', event => event.stopPropagation());
  get('rbPreviousMonth').addEventListener('click',()=>changeMonth(-1));
  get('rbNextMonth').addEventListener('click',()=>changeMonth(1));
  get('rbCalendars').addEventListener('click',event=>{
    const b=event.target.closest('[data-date]');if(!b)return;const value=b.dataset.date;
    if (!state.draftStart || state.draftEnd) {
      state.draftStart=value; state.draftEnd=null;
    } else if (value<state.draftStart) {
      state.draftEnd=state.draftStart; state.draftStart=value;
    } else {
      // Selecting the same date twice filters a single arrival day.
      state.draftEnd=value;
    }
    renderCalendars();
  });
  get('rbDatePicker').querySelectorAll('[data-rb-preset]').forEach(button=>button.addEventListener('click',()=>{
    const p=button.dataset.rbPreset;
    state.draftEnd=isoToday;
    if(p==='today')state.draftStart=isoToday;
    else if(p==='7')state.draftStart=iso(addDays(today,-6));
    else if(p==='30')state.draftStart=iso(addDays(today,-29));
    else if(p==='month')state.draftStart=iso(new Date(today.getFullYear(),today.getMonth(),1));
    else {state.draftStart=null;state.draftEnd=null;}
    renderCalendars();
  }));
  get('rbDateCancel').addEventListener('click',hidePicker);
  get('rbDateApply').addEventListener('click',()=>{
    if((state.draftStart && !state.draftEnd) || (!state.draftStart && state.draftEnd)){get('rbDraftLabel').textContent='Select the end date.';return;}
    state.start=state.draftStart;state.end=state.draftEnd;
    paintDateLabel(); hidePicker(); state.page=1;
    // Persist the applied interval even when a guest search is active.
    load();
  });
  document.addEventListener('click',event=>{if(!picker.hidden && !picker.contains(event.target) && !dateTrigger.contains(event.target))hidePicker();});
  document.addEventListener('keydown',event=>{if(event.key==='Escape')hidePicker();});
  paintDateLabel();
  renderPagination(Number(get('rbGuestsCount').textContent.replaceAll(',',''))||0);
})();
