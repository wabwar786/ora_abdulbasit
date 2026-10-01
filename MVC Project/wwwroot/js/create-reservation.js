(() => {
  'use strict';

  const page = document.getElementById('createReservationPage' );
  if (!page) return;
  const $ = id => document.getElementById(id);
  const $$ = selector => [...page.querySelectorAll(selector)];
  const token = page.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
  const flag = key => page.dataset[key] === '1';
  const isMonthWise = flag('monthwise');
  const hasDiscount = flag('discount');
  const hasGst = flag('gst');
  const hasBedTax = flag('bedtax');
  const hasReservationType = flag('reservationType');
  const hasRoomShuffle = flag('roomShuffle');
  const currencyCode = (page.dataset.currencyCode || 'GBP').trim().toUpperCase();
  const currencySymbol = page.dataset.currency || '£';
  const hotelToday = page.dataset.hotelToday || '';

  let rooms = [];
  let occupancy = { adults: 1, children: 0, infants: 0 };
  let limits = { adults: 1, children: 0, infants: 0 };
  let lastQuote = null;
  let lastCreatedRegId = '';
  let lastCreatedTotal = 0;
  let lastSavedPaymentEmail = '';
  let lastSavedPaymentDeadline = '';
  let lastSavedArrivalDate = '';
  let guestResults = [];

  const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const number = value => { const n = Number(String(value ?? '').replace(/,/g,'')); return Number.isFinite(n) ? n : 0; };
  const money = value => { try { return new Intl.NumberFormat('en-GB',{style:'currency',currency:currencyCode}).format(number(value)); } catch { return `${currencySymbol}${number(value).toFixed(2)}`; } };
  const show = (el,on) => el?.classList.toggle('hidden',!on);
  const error = (el,msg) => { if (!el) return; el.textContent = msg || ''; show(el,!!msg); };
  const toast = msg => { const el=$('toast'); if(!el)return; el.textContent=msg; el.classList.add('show'); clearTimeout(el._t); el._t=setTimeout(()=>el.classList.remove('show'),2600); };
  function showSuccess(message){
    const box=$('reservationSuccess'), text=$('reservationSuccessText');
    if(!box||!text)return;
    text.textContent=message||'Reservation created successfully.';
    show(box,true);
  }
  function clearValidationState(){
    $$('.invalid').forEach(x=>x.classList.remove('invalid'));
    ['dateError','guestError','roomError','linkError','deadlineError','sourceError'].forEach(id=>error($(id),''));
  }
  function resetReservationForm(paymentContext=null){
    const form=$('reservationForm');
    form?.reset();

    rooms=[];
    occupancy={adults:1,children:0,infants:0};
    limits={adults:1,children:0,infants:0};
    lastQuote=null;
    lastCreatedRegId=paymentContext?.registrationId||'';
    lastCreatedTotal=number(paymentContext?.grandTotal||0);
    lastSavedPaymentEmail=paymentContext?.email||'';
    lastSavedPaymentDeadline=paymentContext?.paymentDeadline||'';
    lastSavedArrivalDate=paymentContext?.arrivalDate||'';
    guestResults=[];

    if(hotelToday){
      $('ArrivalDate').value=hotelToday;
      $('DepartureDate').value=addStayUnits(hotelToday,1);
    }
    previousArrival=$('ArrivalDate').value;
    previousDeparture=$('DepartureDate').value;

    $('City').innerHTML='<option value="">Select city</option>';
    $('findGuest').value='';
    $('findGuestSuggestions').innerHTML='';
    $('findGuestSuggestions').style.display='none';

    $('PaymentLinkEmail').value=lastSavedPaymentEmail;
    if(lastCreatedRegId) $('PaymentLinkEmail').dataset.edited='1';
    else delete $('PaymentLinkEmail').dataset.edited;
    $('IdAttachment').value='';
    $('fileName').textContent='';
    show($('fileChip'),false);

    if($('rPlan'))$('rPlan').innerHTML='<option value="">Select rate plan</option>';
    $('rNumber').innerHTML='<option value="">Select room</option>';
    $('rTotal').value='0.00';
    $('occupancyHint').textContent='';
    $('rGuest').value='';

    show($('sentMsg'),false);
    $('sentText').textContent='';
    $('notSent').textContent=lastCreatedRegId
      ? `Status: reservation ${lastCreatedRegId} saved · payment link not sent`
      : 'Status: not sent yet';
    if($('sendLbl'))$('sendLbl').textContent='Send payment link via email';

    clearValidationState();
    modeUi();
    refreshDates();
    renderRooms();
    defaultDeadline();
    if(lastCreatedRegId && lastSavedPaymentDeadline) $('PaymentDeadline').value=lastSavedPaymentDeadline;
    setCounter('adults',1);
    setCounter('children',0);
    setCounter('infants',0);
    $('btnSave').disabled=false;
    $('btnSendLink').disabled=false;
  }
  function setButtonBusy(button,on,text='Please wait…'){
    if(!button)return;
    if(on){
      if(button.dataset.busy==='1')return;
      button.dataset.busy='1';
      button._busyHtml=button.innerHTML;
      button._busyWasDisabled=button.disabled;
      button.disabled=true;
      button.setAttribute('aria-busy','true');
      button.innerHTML=`<span class="btn-spinner" aria-hidden="true"></span><span>${esc(text)}</span>`;
    }else{
      if(button.dataset.busy!=='1')return;
      button.dataset.busy='0';
      if(button._busyHtml!=null)button.innerHTML=button._busyHtml;
      button.disabled=!!button._busyWasDisabled;
      button.removeAttribute('aria-busy');
      delete button._busyHtml;
      delete button._busyWasDisabled;
    }
  }
  function setPrimaryActionBusy(button,on,text){
    const save=$('btnSave'),send=$('btnSendLink');
    if(on){
      setButtonBusy(button,true,text);
      if(save&&save!==button)save.disabled=true;
      if(send&&send!==button)send.disabled=true;
    }else{
      setButtonBusy(button,false);
      if(save)save.disabled=false;
      if(send)send.disabled=false;
    }
  }
  const fmtDate = value => { if(!value)return '—'; const d=new Date(`${value}T00:00:00`); return Number.isNaN(d.getTime())?'—':d.toLocaleDateString('en-GB',{day:'numeric',month:'short',year:'numeric'}); };
  const dateDiff = (a,d) => { if(!a||!d)return 0; const x=new Date(`${a}T00:00:00`), y=new Date(`${d}T00:00:00`); return y>x ? Math.round((y-x)/86400000) : 0; };
  const monthCount = (a,d) => { if(!a||!d)return 0; const x=new Date(`${a}T00:00:00`),y=new Date(`${d}T00:00:00`); if(!(y>x))return 0; let m=(y.getFullYear()-x.getFullYear())*12+y.getMonth()-x.getMonth(); if(y.getDate()>x.getDate())m++; return Math.max(1,m); };
  const getMode = () => hasReservationType ? (page.querySelector('input[name="reservationMode"]:checked')?.value || 'Individual') : 'Individual';
  const getShuffle = () => hasRoomShuffle ? (page.querySelector('input[name="shuffleType"]:checked')?.value || 'Manual') : 'Manual';
  const sameDates = () => !hasReservationType || $('sameDates')?.checked !== false;
  const activeArrival = () => getMode()==='Group' && !sameDates() ? $('RoomArrival').value : $('ArrivalDate').value;
  const activeDeparture = () => getMode()==='Group' && !sameDates() ? $('RoomDeparture').value : $('DepartureDate').value;

  async function getJson(url){ const r=await fetch(url,{credentials:'same-origin'}); const t=await r.text(); if(!r.ok)throw new Error(t||`HTTP ${r.status}`); return t?JSON.parse(t):null; }
  async function postJson(url,body){ const r=await fetch(url,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','RequestVerificationToken':token},body:JSON.stringify(body)}); const t=await r.text(); let data=null; try{data=t?JSON.parse(t):null}catch{} if(!r.ok)throw new Error(data?.message||t||`HTTP ${r.status}`); return data; }

  function chooseOption(select,value){ if(!select||!value)return false; const v=String(value).trim().toLowerCase(); const opt=[...select.options].find(o=>String(o.value).trim().toLowerCase()===v||String(o.text).trim().toLowerCase()===v); if(!opt)return false; select.value=opt.value; return true; }

  function refreshDates(){
    const a=$('ArrivalDate').value,d=$('DepartureDate').value;
    const count=isMonthWise?monthCount(a,d):dateDiff(a,d);
    $('nightsOut').value=count;
    $('nightsLabel').textContent=`${count} ${isMonthWise?'months':'nights'}`;
    error($('dateError'), a&&d&&count<1 ? 'Departure must be after arrival.' : '');
    if(getMode()==='Individual'||sameDates()){ $('RoomArrival').value=a; $('RoomDeparture').value=d; }
    updateSummary();
  }

  const pad2 = n => String(n).padStart(2,'0');
  const toIsoDate = d => `${d.getFullYear()}-${pad2(d.getMonth()+1)}-${pad2(d.getDate())}`;
  function addStayUnits(arrival,count){
    const start=new Date(`${arrival}T00:00:00`);
    if(Number.isNaN(start.getTime()))return '';
    if(!isMonthWise){start.setDate(start.getDate()+count);return toIsoDate(start);}
    const day=start.getDate(), target=new Date(start.getFullYear(),start.getMonth()+count,1);
    const lastDay=new Date(target.getFullYear(),target.getMonth()+1,0).getDate();
    target.setDate(Math.min(day,lastDay));
    return toIsoDate(target);
  }

  async function handleStayCountChange(){
    const input=$('nightsOut');
    const arrival=$('ArrivalDate').value;
    let count=Math.floor(number(input.value));
    if(!arrival){error($('dateError'),'Select an arrival date first.');refreshDates();return;}
    if(count<1){error($('dateError'),`Total ${isMonthWise?'months':'nights'} must be at least 1.`);refreshDates();return;}
    if(!isMonthWise&&count>730){error($('dateError'),'Stay cannot exceed 730 nights.');refreshDates();return;}
    const departure=addStayUnits(arrival,count);
    if(!departure){error($('dateError'),'Unable to calculate the departure date.');refreshDates();return;}
    $('DepartureDate').value=departure;
    await handleStayDateChange('DepartureDate');
  }

  function validateDates(){
    const a=$('ArrivalDate'),d=$('DepartureDate'); a.classList.remove('invalid');d.classList.remove('invalid');
    if(!a.value||!d.value){ a.classList.toggle('invalid',!a.value);d.classList.toggle('invalid',!d.value); error($('dateError'),'Arrival and departure are required.'); return false; }
    const count=isMonthWise?monthCount(a.value,d.value):dateDiff(a.value,d.value);
    if(count<1){d.classList.add('invalid');error($('dateError'),'Departure must be after arrival.');return false;}
    if(hotelToday && a.value<hotelToday){a.classList.add('invalid');error($('dateError'),'Arrival date cannot be in the past.');return false;}
    if(!isMonthWise && count>730){d.classList.add('invalid');error($('dateError'),'Stay cannot exceed 730 nights.');return false;}
    error($('dateError'),'');return true;
  }

  async function loadCities(selected=''){
    const select=$('City'), country=$('Country').value; select.innerHTML='<option value="">Loading...</option>';
    if(!country){select.innerHTML='<option value="">Select city</option>';return;}
    try{const list=await getJson(`/CreateReservation/Cities?country=${encodeURIComponent(country)}`);select.innerHTML='<option value="">Select city</option>'+((Array.isArray(list)?list:[]).map(x=>`<option value="${esc(x.value)}">${esc(x.text)}</option>`).join(''));if(selected)chooseOption(select,selected);}catch{select.innerHTML='<option value="">Unable to load cities</option>';}
  }

  async function searchGuests(){
    const term=$('findGuest').value.trim(),box=$('findGuestSuggestions'),button=$('btnSearch');
    if(term.length<2){box.style.display='none';toast('Enter at least 2 characters to search');return;}
    setButtonBusy(button,true,'Searching…');
    try{guestResults=await getJson(`/CreateReservation/GuestSearch?term=${encodeURIComponent(term)}`)||[]; if(!guestResults.length){box.style.display='none';toast('No matching guest found');return;}
      box.innerHTML=guestResults.map((g,i)=>`<div class="suggestion" data-i="${i}"><strong>${esc(`${g.guestName||''} ${g.lastName||''}`.trim())}</strong><br><small>${esc([g.phone,g.email].filter(Boolean).join(' · '))}</small></div>`).join('');box.style.display='block';
    }catch(e){box.style.display='none';toast(e.message||'Unable to search guests');}
    finally{setButtonBusy(button,false);}
  }
  function fillGuest(g){$('FirstName').value=g.guestName||'';$('LastName').value=g.lastName||'';$('Phone').value=g.phone||'';$('Email').value=g.email||'';$('PaymentLinkEmail').value=g.email||'';$('Address').value=g.address||'';$('IdNumber').value=g.identification||'';if(g.country&&chooseOption($('Country'),g.country))loadCities(g.city||'');updateSummary();$('findGuestSuggestions').style.display='none';}

  async function loadRatePlans(){ if(isMonthWise)return; const cat=$('rCategory').value,s=$('rPlan');s.innerHTML='<option value="">Loading...</option>'; if(!cat){s.innerHTML='<option value="">Select rate plan</option>';return;} try{const list=await getJson(`/CreateReservation/RatePlans?categoryId=${encodeURIComponent(cat)}`);s.innerHTML='<option value="">Select rate plan</option>'+((Array.isArray(list)?list:[]).map(x=>`<option value="${esc(x.id)}">${esc(x.name)}</option>`).join(''));const p=page.dataset.prefillPlan;if(p&&chooseOption(s,p))page.dataset.prefillPlan='';}catch(e){s.innerHTML='<option value="">Unable to load rate plans</option>';error($('roomError'),e.message);}}

  async function loadRooms(){ const s=$('rNumber'); if(getShuffle()!=='Manual'){s.innerHTML='<option value="">Auto assignment</option>';return;} const cat=$('rCategory').value,a=activeArrival(),d=activeDeparture(); if(!cat||!a||!d||d<=a){s.innerHTML='<option value="">Select room</option>';return;} s.disabled=true;s.innerHTML='<option value="">Loading...</option>'; try{const list=await getJson(`/CreateReservation/Rooms?categoryId=${encodeURIComponent(cat)}&arrival=${encodeURIComponent(a)}&departure=${encodeURIComponent(d)}`); const used=new Set(rooms.filter(r=>r.arrivalDate<d&&a<r.departureDate).flatMap(r=>String(r.roomNo||'').split(',').map(x=>x.trim()))); const available=(Array.isArray(list)?list:[]).filter(x=>!used.has(String(x.value)));s.innerHTML=available.length?'<option value="">Select room</option>'+available.map(x=>`<option value="${esc(x.value)}">${esc(x.text)}</option>`).join(''):'<option value="">No available rooms</option>';const p=page.dataset.prefillRoom;if(p&&chooseOption(s,p))page.dataset.prefillRoom='';}catch(e){s.innerHTML='<option value="">Unable to load rooms</option>';error($('roomError'),e.message);}finally{s.disabled=false;}}

  function setCounter(key,value){const min=key==='adults'?1:0,max=Math.max(min,limits[key]||0);occupancy[key]=Math.max(min,Math.min(max,Number(value)||0));const id=key==='adults'?'adultOut':key==='children'?'childOut':'infantOut';$(id).textContent=occupancy[key];const step=page.querySelector(`.stepper[data-key="${key}"]`);step?.querySelector('[data-d="-1"]')?.toggleAttribute('disabled',occupancy[key]<=min);step?.querySelector('[data-d="1"]')?.toggleAttribute('disabled',occupancy[key]>=max);}
  async function loadLimits(){const cat=$('rCategory').value;if(!cat){limits={adults:1,children:0,infants:0};setCounter('adults',1);setCounter('children',0);setCounter('infants',0);return;}try{const x=await getJson(`/CreateReservation/OccupancyLimits?categoryId=${encodeURIComponent(cat)}`);limits={adults:Math.max(1,number(x?.adults)),children:Math.max(0,number(x?.children)),infants:Math.max(0,number(x?.infants))};setCounter('adults',1);setCounter('children',0);setCounter('infants',0);$('occupancyHint').textContent=`Maximum occupancy: ${limits.adults} adult(s), ${limits.children} child(ren), ${limits.infants} infant(s).`;}catch{$('occupancyHint').textContent='';}}

  async function quote(){
    const cat=$('rCategory').value, a=activeArrival(), d=activeDeparture(); if(!cat||!a||!d||d<=a){lastQuote=null;$('rTotal').value='0.00';return null;}
    const plan=isMonthWise?'':$('rPlan').value;if(!isMonthWise&&!plan){lastQuote=null;$('rTotal').value='0.00';return null;}
    const body={categoryId:cat,planId:plan,rooms:getShuffle()==='Auto'?Math.min(20,Math.max(1,number($('rCount').value))):1,arrivalDate:a,departureDate:d,monthlyRate:isMonthWise?number($('rMonthlyRate').value):0,discount:hasDiscount?number($('rDiscount')?.value):0,applyGst:hasGst&&!!$('rVat')?.checked,applyBedTax:hasBedTax&&!!$('rBed')?.checked};
    try{const q=await postJson('/CreateReservation/Quote',body);if(!q?.success)throw new Error(q?.message||'Unable to calculate room total.');lastQuote=q;$('rTotal').value=number(q.total).toFixed(2);error($('roomError'),'');return q;}catch(e){lastQuote=null;$('rTotal').value='0.00';error($('roomError'),e.message);return null;}
  }

  function totals(){return rooms.reduce((a,r)=>{a.sub+=r.rate;a.vat+=r.gst;a.bed+=r.bedTax;a.disc+=r.discount;a.total+=r.total;a.guests+=(r.adults+r.children+r.infants)*r.count;a.count+=r.count;return a;},{sub:0,vat:0,bed:0,disc:0,total:0,guests:0,count:0});}
  function renderRooms(){
    const body=$('roomRows'),columnCount=10+(hasGst?1:0)+(hasBedTax?1:0);
    if(!rooms.length){
      body.innerHTML=`<tr id="emptyRoomRow"><td colspan="${columnCount}" class="empty-cell">No rooms added yet. Add a room above to continue.</td></tr>`;
    }else{
      body.innerHTML=rooms.map((r,i)=>`<tr>
        <td>${i+1}</td><td>${esc(r.categoryName)}</td><td>${esc(r.roomNo || `Auto × ${r.count}`)}</td><td>${esc(r.planName||'Monthly')}</td><td class="wrap">${esc(r.guestName)}</td><td>${r.adults} / ${r.children} / ${r.infants}</td><td class="num">${money(r.rate)}</td>
        ${hasGst?`<td class="num">${money(r.gst)}</td>`:''}
        ${hasBedTax?`<td class="num">${money(r.bedTax)}</td>`:''}
        <td class="num">-${money(r.discount)}</td><td class="num"><strong>${money(r.total)}</strong></td>
        <td class="room-action"><button type="button" class="btn btn-room-delete" data-remove="${i}" aria-label="Delete room ${esc(r.roomNo||String(i+1))}"><span class="trash-icon" aria-hidden="true">⌫</span><span>Delete</span></button></td>
      </tr>`).join('');
    }
    updateSummary();
  }
  function updateSummary(){
    const t=totals(),a=$('ArrivalDate').value,d=$('DepartureDate').value,count=isMonthWise?monthCount(a,d):dateDiff(a,d),source=$('BookingSource')?.selectedOptions?.[0]?.text||'';
    $('sName').textContent=(`${$('FirstName').value} ${$('LastName').value}`.trim()||'New guest');
    $('sMeta').textContent=`${t.count} room${t.count===1?'':'s'} · ${count} ${isMonthWise?'month':'night'}${count===1?'':'s'}${source&&source!=='Select source'?` · ${source}`:''}`;
    $('sArr').textContent=fmtDate(a);$('sDep').textContent=fmtDate(d);$('sGuests').textContent=`${t.guests} guest${t.guests===1?'':'s'}`;$('sRooms').textContent=rooms.length?rooms.map(r=>r.roomNo||`Auto × ${r.count}`).join(', '):'—';
    $('ftRooms').textContent=`${t.count} room${t.count===1?'':'s'}`;$('ftSub').textContent=money(t.sub);
    if($('ftVat'))$('ftVat').textContent=money(t.vat);if($('ftBed'))$('ftBed').textContent=money(t.bed);
    $('ftDisc').textContent=`- ${money(t.disc)}`;$('ftGrand').textContent=money(t.total);$('pSub').textContent=money(t.sub);
    if($('pVat'))$('pVat').textContent=money(t.vat);if($('pBed'))$('pBed').textContent=money(t.bed);if($('pTax'))$('pTax').textContent=money(t.vat+t.bed);
    $('pGrand').textContent=money(t.total);$('pBal').textContent=money(t.total);$('dTotal').textContent=money(t.total);
    const pill=$('sPill')?.querySelector('span');if(pill)pill.textContent=($('IsProvisional')?.type==='checkbox'&&$('IsProvisional').checked)?'Provisional · Awaiting payment':'Reservation · Awaiting payment';
  }

  async function addRoom(){
    error($('roomError'),'');
    if(!validateDates())return;
    const cat=$('rCategory'),guest=$('rGuest').value.trim(),manual=getShuffle()==='Manual',count=manual?1:Math.min(20,Math.max(1,Math.floor(number($('rCount').value))));
    if(!cat.value)return error($('roomError'),'Select a room category.');
    if(manual&&!$('rNumber').value)return error($('roomError'),'Select a room number.');
    if(!isMonthWise&&!$('rPlan').value)return error($('roomError'),'Select a rate plan.');
    if(isMonthWise&&number($('rMonthlyRate').value)<=0)return error($('roomError'),'Enter a valid monthly rate.');
    const button=$('btnAddRoom');setButtonBusy(button,true,'Adding…');
    try{
      const q=await quote();if(!q)return;
      const a=activeArrival(),d=activeDeparture();
      rooms.push({categoryId:cat.value,categoryName:cat.selectedOptions[0]?.text||cat.value,planId:isMonthWise?'':$('rPlan').value,planName:isMonthWise?'Monthly':($('rPlan').selectedOptions[0]?.text||''),count,roomNo:manual?$('rNumber').value:'',guestName:guest,arrivalDate:a,departureDate:d,monthlyRate:isMonthWise?number($('rMonthlyRate').value):0,discount:number(q.discount),applyGst:hasGst&&!!$('rVat')?.checked,applyBedTax:hasBedTax&&!!$('rBed')?.checked,adults:occupancy.adults,children:occupancy.children,infants:occupancy.infants,rate:number(q.rate),gst:number(q.gstAmount),bedTax:number(q.bedTaxAmount),total:number(q.total)});
      renderRooms();$('rGuest').value='';await loadRooms();toast('Room added');
    }finally{setButtonBusy(button,false);}
  }

  function validateGuest(){
    const first=$('FirstName'),last=$('LastName'),phone=$('Phone'),email=$('Email'),source=$('BookingSource');
    const fields=[first,last,phone,email,source];
    fields.forEach(x=>x?.classList.remove('invalid'));
    let msg='',bad=null;
    const phoneValue=phone.value.trim(),digits=(phoneValue.match(/\d/g)||[]).length;
    if(!first.value.trim()){msg='First name is required.';bad=first;}
    else if(first.value.trim().length>60){msg='First name cannot exceed 60 characters.';bad=first;}
    else if(!last.value.trim()){msg='Last name is required.';bad=last;}
    else if(last.value.trim().length>60){msg='Last name cannot exceed 60 characters.';bad=last;}
    else if(!/^[+()\-\s0-9]{7,20}$/.test(phoneValue)||digits<7||digits>15){msg='Enter a valid phone number (7-15 digits).';bad=phone;}
    else if(!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.value.trim())||email.value.trim().length>120){msg='Enter a valid email address.';bad=email;}
    else if(!source.value||source.value==='__add_new__'){msg='Select a booking source.';bad=source;}
    if(msg){bad?.classList.add('invalid');error($('guestError'),msg);return false;}
    error($('guestError'),'');return true;
  }
  function validateDeadline(){const el=$('PaymentDeadline');el.classList.remove('invalid');if(!el.value){el.classList.add('invalid');error($('deadlineError'),'Payment deadline is required.');return false;}const x=new Date(el.value),a=new Date(`${$('ArrivalDate').value}T23:59:59`),now=new Date();if(Number.isNaN(x.getTime())||x<=now){el.classList.add('invalid');error($('deadlineError'),'Payment deadline must be in the future.');return false;}if(x>a){el.classList.add('invalid');error($('deadlineError'),'Payment deadline must be on or before the arrival date.');return false;}error($('deadlineError'),'');return true;}

  function request(){const idType=$('IdType').value,idNo=$('IdNumber').value.trim();return{arrivalDate:$('ArrivalDate').value,departureDate:$('DepartureDate').value,firstName:$('FirstName').value.trim(),lastName:$('LastName').value.trim(),phone:$('Phone').value.trim(),email:$('Email').value.trim(),country:$('Country').value,city:$('City').value,address:$('Address').value.trim(),identification:[idType,idNo].filter(Boolean).join(': '),source:$('BookingSource').value,isProvisional:$('IsProvisional')?.type==='checkbox'?$('IsProvisional').checked:false,paymentDeadline:$('PaymentDeadline').value||null,sendPaymentLink:false,paymentLinkEmail:$('PaymentLinkEmail').value.trim(),shuffleType:getShuffle(),reservationMode:getMode(),groupSameDates:sameDates(),rooms:rooms.map(r=>({categoryId:r.categoryId,planId:r.planId,count:r.count,roomNo:r.roomNo,guestName:r.guestName,arrivalDate:r.arrivalDate,departureDate:r.departureDate,monthlyRate:r.monthlyRate,discount:r.discount,applyGst:r.applyGst,applyBedTax:r.applyBedTax,adults:r.adults,children:r.children,infants:r.infants}))};}

  async function submit(){
    error($('linkError'),'');
    show($('reservationSuccess'),false);
    if(!validateDates()||!validateGuest()||!validateDeadline())return toast('Please fix the highlighted fields');
    if(!rooms.length){error($('roomError'),'Add at least one room before saving the reservation.');$('h-rooms')?.scrollIntoView({behavior:'smooth'});return;}

    const button=$('btnSave');
    const paymentEmail=$('PaymentLinkEmail').value.trim()||$('Email').value.trim();
    const paymentDeadline=$('PaymentDeadline').value||'';
    const paymentArrival=$('ArrivalDate').value||'';
    setPrimaryActionBusy(button,true,'Saving…');

    try{
      const result=await postJson('/CreateReservation/Create',request());
      if(!result?.success)throw new Error(result?.message||'Reservation could not be created.');

      const reg=result.registrationId||'';
      const total=number(result.grandTotal);
      const successMessage=`Reservation ${reg} created successfully.`;
      resetReservationForm({registrationId:reg,grandTotal:total,email:paymentEmail,paymentDeadline,arrivalDate:paymentArrival});
      showSuccess(successMessage);
      toast(successMessage);
      $('reservationSuccess')?.scrollIntoView({behavior:'smooth',block:'start'});
    }catch(e){
      toast(e.message||'Reservation could not be created.');
    }finally{
      setPrimaryActionBusy(button,false);
    }
  }

  async function sendPaymentLinkOnly(){
    error($('linkError'),'');
    if(!lastCreatedRegId){
      error($('linkError'),'Save the reservation first. Send payment link only sends the link and does not save the reservation.');
      return toast('Save the reservation first');
    }
    const email=$('PaymentLinkEmail').value.trim();
    if(!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))return error($('linkError'),'Enter a valid email address for the payment link.');
    const deadlineEl=$('PaymentDeadline');
    deadlineEl.classList.remove('invalid');
    if(!deadlineEl.value){deadlineEl.classList.add('invalid');error($('deadlineError'),'Payment deadline is required.');return;}
    const deadline=new Date(deadlineEl.value),now=new Date();
    if(Number.isNaN(deadline.getTime())||deadline<=now){deadlineEl.classList.add('invalid');error($('deadlineError'),'Payment deadline must be in the future.');return;}
    if(lastSavedArrivalDate){const arrivalEnd=new Date(`${lastSavedArrivalDate}T23:59:59`);if(deadline>arrivalEnd){deadlineEl.classList.add('invalid');error($('deadlineError'),'Payment deadline must be on or before the saved reservation arrival date.');return;}}
    error($('deadlineError'),'');

    const button=$('btnSendLink');
    const reg=lastCreatedRegId;
    let sent=false;
    setPrimaryActionBusy(button,true,'Sending…');
    try{
      const r=await postJson('/CreateReservation/SendPaymentLink',{registrationId:reg,email,paymentDeadline:$('PaymentDeadline').value||null});
      if(!r?.success)throw new Error(r?.message||'Unable to send payment link.');
      sent=true;
    }catch(e){
      error($('linkError'),e.message||'Unable to send payment link.');
      $('notSent').textContent=`Status: send failed for reservation ${reg}`;
    }finally{
      setPrimaryActionBusy(button,false);
    }

    if(sent){
      const msg=`Payment link sent successfully for reservation ${reg}.`;
      resetReservationForm();
      showSuccess(msg);
      toast(msg);
      $('reservationSuccess')?.scrollIntoView({behavior:'smooth',block:'start'});
    }else if(lastCreatedRegId){
      if($('sendLbl'))$('sendLbl').textContent='Retry payment link';
    }
  }

  function clearSavedPaymentContextForNewDraft(){
    if(!lastCreatedRegId)return;
    lastCreatedRegId='';
    lastCreatedTotal=0;
    lastSavedPaymentEmail='';
    lastSavedPaymentDeadline='';
    lastSavedArrivalDate='';
    $('PaymentLinkEmail').value='';
    delete $('PaymentLinkEmail').dataset.edited;
    $('notSent').textContent='Status: not sent yet';
    if($('sendLbl'))$('sendLbl').textContent='Send payment link via email';
    error($('linkError'),'');
    defaultDeadline();
  }

  function openSourceModal(){show($('sourceModal'),true);$('NewSourceName').value='';error($('sourceError'),'');setTimeout(()=>$('NewSourceName').focus(),30);}
  function closeSourceModal(){show($('sourceModal'),false);if($('BookingSource').value==='__add_new__')$('BookingSource').value='';}
  async function addSource(){
    const name=$('NewSourceName').value.trim();if(name.length<2||name.length>80)return error($('sourceError'),'Source name must be 2 to 80 characters.');if(/[<>]/.test(name))return error($('sourceError'),'Source name contains invalid characters.');
    const button=$('saveSource');setButtonBusy(button,true,'Adding…');
    try{const r=await postJson('/CreateReservation/AddSource',{value:name});if(!r?.success)throw new Error(r?.message||'Unable to add source.');const select=$('BookingSource');const add=select.querySelector('option[value="__add_new__"]');const opt=new Option(r.value||name,r.value||name);select.insertBefore(opt,add);select.value=r.value||name;show($('sourceModal'),false);updateSummary();toast('Source added');}
    catch(e){error($('sourceError'),e.message);}
    finally{setButtonBusy(button,false);}
  }

  function defaultDeadline(){const now=new Date();now.setHours(now.getHours()+24);const arrival=new Date(`${$('ArrivalDate').value}T18:00:00`);const d=now<arrival?now:arrival;const pad=n=>String(n).padStart(2,'0');$('PaymentDeadline').value=`${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;}
  function modeUi(){const group=getMode()==='Group',diff=group&&!sameDates();show($('sameDatesWrap'),group);show($('roomDates'),diff);if(!diff){$('RoomArrival').value=$('ArrivalDate').value;$('RoomDeparture').value=$('DepartureDate').value;}const auto=getShuffle()==='Auto';show($('roomNumberWrap'),!auto);show($('roomCountWrap'),auto);}

  let previousArrival=$('ArrivalDate').value,previousDeparture=$('DepartureDate').value;
  async function handleStayDateChange(changed){
    const field=$(changed), previous=changed==='ArrivalDate'?previousArrival:previousDeparture;
    if(rooms.length&&!confirm('Changing stay dates will remove the added room rows. Continue?')){
      field.value=previous;
      refreshDates();
      return;
    }
    if(rooms.length){rooms=[];renderRooms();}
    previousArrival=$('ArrivalDate').value;previousDeparture=$('DepartureDate').value;
    refreshDates();
    if(changed==='ArrivalDate')defaultDeadline();
    await loadRooms();await quote();
  }
  $('ArrivalDate').addEventListener('change',()=>handleStayDateChange('ArrivalDate'));
  $('DepartureDate').addEventListener('change',()=>handleStayDateChange('DepartureDate'));
  $('nightsOut').addEventListener('change',handleStayCountChange);
  $('nightsOut').addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();e.currentTarget.blur();}});
  $('Country').addEventListener('change',()=>loadCities());
  ['FirstName','LastName'].forEach(id=>$(id).addEventListener('input',updateSummary));
  $('Email').addEventListener('input',()=>{if(!$('PaymentLinkEmail').dataset.edited)$('PaymentLinkEmail').value=$('Email').value;});$('PaymentLinkEmail').addEventListener('input',()=>{$('PaymentLinkEmail').dataset.edited='1';});
  $('BookingSource').addEventListener('change',()=>{if($('BookingSource').value==='__add_new__')openSourceModal();else updateSummary();});
  $('IsProvisional')?.addEventListener('change',updateSummary);
  $('btnSearch').addEventListener('click',searchGuests);$('findGuest').addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();searchGuests();}});$('findGuestSuggestions').addEventListener('click',e=>{const item=e.target.closest('.suggestion');if(item)fillGuest(guestResults[Number(item.dataset.i)]);});
  $('rCategory').addEventListener('change',async()=>{await Promise.all([loadRatePlans(),loadLimits()]);await loadRooms();await quote();});
  $('rPlan')?.addEventListener('change',quote);$('rMonthlyRate')?.addEventListener('input',quote);$('rCount')?.addEventListener('input',quote);$('rDiscount')?.addEventListener('input',quote);$('rVat')?.addEventListener('change',quote);$('rBed')?.addEventListener('change',quote);$('btnAddRoom').addEventListener('click',addRoom);
  $$('.stepper button').forEach(b=>b.addEventListener('click',()=>{const key=b.closest('.stepper').dataset.key;setCounter(key,occupancy[key]+Number(b.dataset.d||0));}));
  $$('input[name="reservationMode"]').forEach(x=>x.addEventListener('change',async()=>{modeUi();await loadRooms();await quote();}));$('sameDates')?.addEventListener('change',async()=>{modeUi();await loadRooms();await quote();});$$('input[name="shuffleType"]').forEach(x=>x.addEventListener('change',async()=>{modeUi();await loadRooms();await quote();}));$('RoomArrival').addEventListener('change',async()=>{await loadRooms();await quote();});$('RoomDeparture').addEventListener('change',async()=>{await loadRooms();await quote();});
  $('roomRows').addEventListener('click',async e=>{
    const b=e.target.closest('[data-remove]');if(!b)return;
    const index=Number(b.dataset.remove);if(!Number.isInteger(index)||!rooms[index])return;
    if(!confirm(`Delete room ${rooms[index].roomNo||rooms[index].categoryName} from this reservation?`))return;
    setButtonBusy(b,true,'Deleting…');
    rooms.splice(index,1);
    await loadRooms();
    renderRooms();
  });
  $('btnUpload').addEventListener('click',()=>$('IdAttachment').click());$('IdAttachment').addEventListener('change',()=>{const f=$('IdAttachment').files?.[0];if(!f)return;if(f.size>10*1024*1024){$('IdAttachment').value='';return toast('ID attachment cannot exceed 10 MB.');}if(!/\.(jpe?g|png|pdf)$/i.test(f.name)){$('IdAttachment').value='';return toast('Only JPG, PNG or PDF files are allowed.');}$('fileName').textContent=f.name;show($('fileChip'),true);});$('btnRemoveFile').addEventListener('click',()=>{$('IdAttachment').value='';show($('fileChip'),false);});
  $('reservationForm').addEventListener('input',e=>{if(e.target?.id!=='PaymentLinkEmail'&&e.target?.id!=='PaymentDeadline')clearSavedPaymentContextForNewDraft();},true);
  $('reservationForm').addEventListener('change',e=>{if(e.target?.id!=='PaymentLinkEmail'&&e.target?.id!=='PaymentDeadline')clearSavedPaymentContextForNewDraft();},true);
  $('btnSendLink').addEventListener('click',sendPaymentLinkOnly);
  $('reservationForm').addEventListener('submit',e=>{e.preventDefault();submit();});
  $('btnCancel').addEventListener('click',()=>history.length>1?history.back():window.location.assign('/Availability'));
  $('closeSourceModal').addEventListener('click',closeSourceModal);$('cancelSource').addEventListener('click',closeSourceModal);$('saveSource').addEventListener('click',addSource);$('NewSourceName').addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();addSource();}});$('sourceModal').addEventListener('click',e=>{if(e.target===$('sourceModal'))closeSourceModal();});

  refreshDates();modeUi();renderRooms();defaultDeadline();setCounter('adults',1);setCounter('children',0);setCounter('infants',0);$('rGuest').value='';
  const prefill=page.dataset.prefillCategory;if(prefill&&chooseOption($('rCategory'),prefill)){$('rCategory').dispatchEvent(new Event('change'));}
})();
