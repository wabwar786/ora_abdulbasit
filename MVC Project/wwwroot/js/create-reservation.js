(() => {
  'use strict';
  const page = document.getElementById('createReservationPage');
  if (!page) return;
  const $ = s => document.querySelector(s);
  const token = $('input[name="__RequestVerificationToken"]')?.value || '';
  const flag = n => page.dataset[n] === '1';
  const isMonthWise = flag('monthwise');
  const isCouncil = flag('council');
  const hasDiscount = flag('discount');
  const hasGst = flag('gst');
  const hasBedTax = flag('bedtax');
  const hasReservationType = flag('reservationType');
  let rooms = [];

  const esc = v => String(v ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  const num = v => { const n = Number(String(v ?? '').replace(/,/g,'')); return Number.isFinite(n) ? n : 0; };
  const money = v => num(v).toFixed(2);
  const dateDiff = (a,b) => Math.max(0, Math.round((new Date(b+'T00:00:00') - new Date(a+'T00:00:00')) / 86400000));
  function monthCount(a,b){ const A=new Date(a+'T00:00:00'),D=new Date(b+'T00:00:00'); if(!(D>A))return 0; let m=(D.getFullYear()-A.getFullYear())*12+(D.getMonth()-A.getMonth()); if(m===0)return 1;if(D.getDate()===A.getDate())return m;if(m===1&&D.getDate()<A.getDate())return 1;if(D.getDate()>A.getDate())return m+1;return m; }
  const getMode = () => $('input[name="reservationMode"]:checked')?.value || 'Individual';
  const sameDates = () => $('#sameDates')?.checked !== false;
  const getShuffle = () => $('input[name="shuffleType"]:checked')?.value || 'Manual';
  const roomArrival = () => getMode()==='Group' && !sameDates() ? $('#roomArrival').value : $('#arrivalDate').value;
  const roomDeparture = () => getMode()==='Group' && !sameDates() ? $('#roomDeparture').value : $('#departureDate').value;
  function setMessage(msg,type=''){ const el=$('#reservationMessage'); el.textContent=msg||''; el.className='resv-message '+type; }
  async function getJson(url){ const r=await fetch(url,{credentials:'same-origin'}); if(!r.ok)throw new Error(await r.text()||('HTTP '+r.status)); return r.json(); }
  async function postJson(url,body){ const r=await fetch(url,{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','RequestVerificationToken':token},body:JSON.stringify(body)}); const text=await r.text(); if(!r.ok)throw new Error(text||('HTTP '+r.status)); try{return JSON.parse(text);}catch{return {success:false,message:text};} }

  function refreshStayCount(){ const a=$('#arrivalDate').value,d=$('#departureDate').value; const n=isMonthWise?monthCount(a,d):dateDiff(a,d); $('#stayCount').value=Math.max(1,n||1); if(getMode()==='Individual'||sameDates()){ $('#roomArrival').value=a; $('#roomDeparture').value=d; } if(isMonthWise) syncMonthlyRate(); }
  function updateDepartureFromCount(){ const a=$('#arrivalDate').value; let n=Math.max(1,parseInt($('#stayCount').value||'1',10)); if(!a)return; const d=new Date(a+'T00:00:00'); if(isMonthWise)d.setMonth(d.getMonth()+n);else d.setDate(d.getDate()+n); $('#departureDate').value=d.toISOString().slice(0,10); if(getMode()==='Individual'||sameDates()){$('#roomArrival').value=a;$('#roomDeparture').value=$('#departureDate').value;} }
  function syncMonthlyRate(){ if(!isMonthWise)return; const monthly=num($('#monthlyRate')?.value),count=Math.max(1,parseInt($('#stayCount').value||'1',10)); $('#roomRate').value=money(monthly*count); }

  async function loadCities(){ const country=$('#country').value; const city=$('#city'); city.innerHTML='<option value="">Loading...</option>'; if(!country){city.innerHTML='<option value="">--Select--</option>';return;} try{const x=await getJson('/CreateReservation/Cities?country='+encodeURIComponent(country));city.innerHTML='<option value="">--Select--</option>'+x.map(o=>`<option value="${esc(o.value)}">${esc(o.text)}</option>`).join('');}catch{city.innerHTML='<option value="">Unable to load cities</option>';} }
  async function loadRatePlans(){ const cat=$('#roomCategory').value, sel=$('#ratePlan'); if(!sel)return; sel.innerHTML='<option value="">Loading...</option>'; if(!cat){sel.innerHTML='<option value="">--select--</option>';return;} try{const x=await getJson('/CreateReservation/RatePlans?categoryId='+encodeURIComponent(cat)); sel.innerHTML='<option value="">--select--</option>'+x.map(o=>`<option value="${esc(o.id)}">${esc(o.name)}</option>`).join(''); const pref=page.dataset.prefillPlan||''; if(pref&&[...sel.options].some(o=>o.value===pref)){sel.value=pref;page.dataset.prefillPlan='';await refreshQuote();}}catch{sel.innerHTML='<option value="">Unable to load rate plans</option>';} }
  async function loadAvailableRooms(){
    const cat=$('#roomCategory').value, a=roomArrival(), d=roomDeparture(), sel=$('#roomNo');
    if(!sel)return;

    if(getShuffle()!=='Manual'){
      sel.innerHTML='<option value="">Auto assignment</option>';
      return;
    }

    if(!cat||!a||!d||d<=a){
      sel.innerHTML='<option value="">--Select--</option>';
      return;
    }

    sel.disabled=true;
    sel.innerHTML='<option value="">Loading available rooms...</option>';

    try{
      const result=await getJson(`/CreateReservation/Rooms?categoryId=${encodeURIComponent(cat)}&arrival=${encodeURIComponent(a)}&departure=${encodeURIComponent(d)}`);

      // Do not offer a room that has already been added to this reservation for an overlapping stay.
      const used=new Set(
        rooms
          .filter(r=>r.roomNo && r.roomNo!=='UNASSIGNED' && r.arrivalDate<d && a<r.departureDate)
          .flatMap(r=>String(r.roomNo).split(',').map(x=>x.trim()).filter(Boolean))
      );
      const available=(Array.isArray(result)?result:[]).filter(o=>!used.has(String(o.value||'').trim()));

      if(!available.length){
        sel.innerHTML='<option value="">--No Available Rooms--</option>';
      }else{
        sel.innerHTML='<option value="">--Select Room--</option>'+
          available.map(o=>`<option value="${esc(o.value)}">${esc(o.text)}</option>`).join('');
      }

      const pref=page.dataset.prefillRoom||'';
      if(pref&&[...sel.options].some(o=>o.value===pref)){
        sel.value=pref;
        page.dataset.prefillRoom='';
      }
    }catch(e){
      console.error('Room availability lookup failed:',e);
      sel.innerHTML='<option value="">Unable to load rooms</option>';
      setMessage('Unable to load available rooms. Please retry or check the selected dates/category.','error');
    }finally{
      sel.disabled=false;
    }
  }

  async function refreshQuote(){
    if(isMonthWise){syncMonthlyRate();return null;}
    const cat=$('#roomCategory').value,plan=$('#ratePlan')?.value||'',a=roomArrival(),d=roomDeparture(); if(!cat||!plan||!a||!d||d<=a)return null;
    const count=getShuffle()==='Manual'?1:Math.max(1,parseInt($('#roomCount').value||'1',10));
    const body={categoryId:cat,planId:plan,rooms:count,arrivalDate:a,departureDate:d,manualRate:isCouncil?0:num($('#roomRate').value),discount:hasDiscount?num($('#roomDiscount')?.value):0,applyGst:!!$('#applyGst')?.checked,applyBedTax:!!$('#applyBedTax')?.checked};
    try{const q=await postJson('/CreateReservation/Quote',body);if(!q.success)throw new Error(q.message||'Unable to calculate rate.');if(isCouncil||num($('#roomRate').value)<=0)$('#roomRate').value=money(q.rate);return q;}catch(e){setMessage(e.message,'error');return null;}
  }

  function renderRooms(){
    const body=$('#roomRows'); if(!rooms.length){body.innerHTML='<tr class="empty-row"><td colspan="13">No rooms added yet.</td></tr>';$('#reservationTotal').textContent='0.00';return;}
    body.innerHTML=rooms.map((r,i)=>`<tr><td>${esc(r.category)}</td><td>${esc(r.ratePlan)}</td><td>${esc(r.guestName)}</td><td>${r.count}</td><td>${esc(r.roomNo||'AUTO')}</td><td>${esc(r.arrivalDate)}</td><td>${esc(r.departureDate)}</td><td>${money(r.rate)}</td>${hasGst?`<td>${money(r.gst)}</td>`:''}${hasBedTax?`<td>${money(r.bedTax)}</td>`:''}${hasDiscount?`<td>${money(r.discount)}</td>`:''}<td><strong>${money(r.total)}</strong></td><td><button type="button" class="resv-remove" data-i="${i}"><i class="fa-solid fa-trash"></i></button></td></tr>`).join('');
    $('#reservationTotal').textContent=money(rooms.reduce((s,r)=>s+num(r.total),0));
  }

  async function addRoom(){
    setMessage(''); const catSel=$('#roomCategory'),cat=catSel.value,category=catSel.options[catSel.selectedIndex]?.text||''; const a=roomArrival(),d=roomDeparture(); if(!cat)return setMessage('Select room category.','error'); if(!a||!d||d<=a)return setMessage('Select valid room arrival/departure dates.','error');
    const shuffle=getShuffle(); let count=shuffle==='Manual'?1:Math.max(1,parseInt($('#roomCount').value||'1',10)); let roomNo=shuffle==='Manual'?$('#roomNo').value:''; if(shuffle==='Manual'&&!roomNo)return setMessage('Select room number.','error');
    let planId='MONTHLY',ratePlan='MONTHLY'; if(!isMonthWise){const p=$('#ratePlan');planId=p.value;ratePlan=p.options[p.selectedIndex]?.text||'';if(!planId)return setMessage('Select rate plan.','error');}
    if(isMonthWise)syncMonthlyRate();
    const q=await postJson('/CreateReservation/Quote',{categoryId:cat,planId:isMonthWise?'':planId,rooms:count,arrivalDate:a,departureDate:d,manualRate:num($('#roomRate').value),discount:hasDiscount?num($('#roomDiscount')?.value):0,applyGst:!!$('#applyGst')?.checked,applyBedTax:!!$('#applyBedTax')?.checked}).catch(e=>{setMessage(e.message,'error');return null;});
    if(!q||!q.success)return setMessage(q?.message||'Unable to calculate room amount.','error');
    rooms.push({categoryId:cat,category,planId,ratePlan,count,roomNo,guestName:$('#roomGuestName').value||'',arrivalDate:a,departureDate:d,rate:q.rate,discount:q.discount,gst:q.gstAmount,bedTax:q.bedTaxAmount,total:q.total,adults:0,children:0});
    renderRooms(); setMessage('Room added.','success'); $('#roomDiscount')&&($('#roomDiscount').value='0'); $('#roomGuestName').value=''; if(shuffle==='Manual'){ $('#roomNo').value=''; await loadAvailableRooms(); }
  }

  async function createReservation(){
    setMessage(''); if(!$('#firstName').value.trim())return setMessage('First name is required.','error'); if(!rooms.length)return setMessage("Please click the 'Add' button to insert room details before proceeding.",'error');
    const advance=isCouncil?0:num($('#advancePaid')?.value),method=isCouncil?'':($('#paymentMethod')?.value||''); if(advance>0&&!method)return setMessage('Please select payment method for advance payment and try again.','error'); if($('#isProvisional')?.checked&&!$('#provisionalExpiry').value)return setMessage('Please select Expiry date.','error');
    const req={arrivalDate:$('#arrivalDate').value,departureDate:$('#departureDate').value,firstName:$('#firstName').value.trim(),lastName:$('#lastName').value.trim(),phone:$('#phone').value.trim(),email:$('#email').value.trim(),country:$('#country').value,city:$('#city').value,address:$('#address').value.trim(),identification:$('#identification').value.trim(),adults:Math.max(0,parseInt($('#adults').value||'0',10)),minors:Math.max(0,parseInt($('#minors').value||'0',10)),company:$('#company').value,source:$('#source').value,advancePaid:advance,paymentMethod:method,isProvisional:!!$('#isProvisional')?.checked,provisionalExpiry:$('#provisionalExpiry')?.value||'',shuffleType:getShuffle(),reservationMode:getMode(),groupSameDates:sameDates(),rooms};
    const loader=$('#reservationLoader');loader.hidden=false;$('#createReservation').disabled=true;
    try{const r=await postJson('/CreateReservation/Create',req);if(!r.success)throw new Error(r.message||'Reservation could not be created.');setMessage(`${r.message} — Reservation #${r.registrationId}`,'success');rooms=[];renderRooms();setTimeout(()=>{const s=req.arrivalDate;const eDate=new Date(s+'T00:00:00');eDate.setDate(eDate.getDate()+14);const e=eDate.toISOString().slice(0,10);window.location.href=`/Calendar?start=${encodeURIComponent(s)}&end=${encodeURIComponent(e)}`;},900);}catch(e){setMessage(e.message,'error');}finally{loader.hidden=true;$('#createReservation').disabled=false;}
  }

  function updateModeUi(){ const group=getMode()==='Group'; document.querySelectorAll('.mode-pill').forEach(x=>x.classList.toggle('active',x.querySelector('input')?.checked)); const wrap=$('#sameDatesWrap');if(wrap)wrap.hidden=!group; const diff=group&&!sameDates();$('#roomDates').hidden=!diff; }
  function updateShuffleUi(){ const manual=getShuffle()==='Manual'; document.querySelectorAll('.manual-room-field').forEach(x=>x.style.display=manual?'block':'none'); document.querySelectorAll('.auto-room-count').forEach(x=>x.style.display=manual?'none':'block'); if(manual)$('#roomCount').value='1'; loadAvailableRooms(); refreshQuote(); }

  let guestTimer;
  function wireGuestLookup(inputSel,boxSel){ const input=$(inputSel),box=$(boxSel); if(!input||!box)return; input.addEventListener('input',()=>{clearTimeout(guestTimer);const term=input.value.trim();if(term.length<2){box.style.display='none';return;}guestTimer=setTimeout(async()=>{try{const x=await getJson('/CreateReservation/GuestSearch?term='+encodeURIComponent(term));if(!x.length){box.style.display='none';return;}box.innerHTML=x.map((g,i)=>`<div class="resv-suggestion" data-i="${i}"><strong>${esc((g.guestName+' '+g.lastName).trim())}</strong><span>${esc(g.phone)} ${esc(g.email)}</span></div>`).join('');box.style.display='block';box.querySelectorAll('.resv-suggestion').forEach(el=>el.onclick=()=>{const g=x[Number(el.dataset.i)];$('#firstName').value=g.guestName||'';$('#lastName').value=g.lastName||'';$('#phone').value=g.phone||'';$('#email').value=g.email||'';$('#address').value=g.address||'';if(g.country){$('#country').value=g.country;loadCities().then(()=>{$('#city').value=g.city||'';});}box.style.display='none';});}catch{box.style.display='none';}},250);}); }

  $('#arrivalDate').addEventListener('change',()=>{refreshStayCount();loadAvailableRooms();refreshQuote();}); $('#departureDate').addEventListener('change',()=>{refreshStayCount();loadAvailableRooms();refreshQuote();}); $('#stayCount').addEventListener('change',()=>{updateDepartureFromCount();loadAvailableRooms();refreshQuote();});
  $('#country').addEventListener('change',loadCities); $('#roomCategory').addEventListener('change',async()=>{await loadRatePlans();await loadAvailableRooms();await refreshQuote();}); $('#ratePlan')?.addEventListener('change',refreshQuote); $('#roomCount').addEventListener('input',refreshQuote); $('#roomDiscount')?.addEventListener('change',refreshQuote); $('#applyGst')?.addEventListener('change',refreshQuote); $('#applyBedTax')?.addEventListener('change',refreshQuote); $('#monthlyRate')?.addEventListener('input',syncMonthlyRate);
  document.querySelectorAll('input[name="shuffleType"]').forEach(x=>x.addEventListener('change',updateShuffleUi)); document.querySelectorAll('input[name="reservationMode"]').forEach(x=>x.addEventListener('change',updateModeUi)); $('#sameDates')?.addEventListener('change',()=>{updateModeUi();loadAvailableRooms();refreshQuote();}); $('#roomArrival').addEventListener('change',()=>{loadAvailableRooms();refreshQuote();});$('#roomDeparture').addEventListener('change',()=>{loadAvailableRooms();refreshQuote();});
  $('#isProvisional')?.addEventListener('change',e=>{$('#provisionalExpiry').disabled=!e.target.checked;}); $('#addRoom').addEventListener('click',addRoom); $('#createReservation').addEventListener('click',createReservation); $('#roomRows').addEventListener('click',e=>{const b=e.target.closest('.resv-remove');if(!b)return;rooms.splice(Number(b.dataset.i),1);renderRooms();loadAvailableRooms();});
  wireGuestLookup('#phone','#phoneSuggestions');wireGuestLookup('#email','#emailSuggestions'); document.addEventListener('click',e=>{if(!e.target.closest('.guest-lookup'))document.querySelectorAll('.resv-suggestions').forEach(x=>x.style.display='none');});

  refreshStayCount();updateModeUi();updateShuffleUi();renderRooms();
  const pref=page.dataset.prefillCategory||''; if(pref&&[...$('#roomCategory').options].some(o=>o.value===pref)){ $('#roomCategory').value=pref; page.dataset.prefillCategory=''; loadRatePlans().then(()=>loadAvailableRooms()).then(refreshQuote); }
})();
