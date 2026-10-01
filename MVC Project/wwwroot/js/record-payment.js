(() => {
  'use strict';

  const root = document.getElementById('orpRecordPayment');
  if (!root) return;

  const byId = id => document.getElementById(id);
  const token = document.querySelector('#orpRecordPaymentAntiForgery input[name="__RequestVerificationToken"]')?.value || '';
  const defaults = {
    recordPaymentUrl: root.dataset.recordPaymentUrl || '/CheckIn/RecordPayment',
    prepareEmailUrl: root.dataset.prepareEmailUrl || '/Calendar/PrepareEmail',
    pdqUrl: root.dataset.pdqUrl || '/TerminalCardPayment.aspx',
    autoPaymentUrl: root.dataset.autoPaymentUrl || '/Autopayment.aspx'
  };

  const els = {
    guest: byId('orpPaymentGuest'), balance: byId('orpPaymentBalance'), amount: byId('orpPaymentAmount'),
    method: byId('orpPaymentMethod'), note: byId('orpPaymentNote'), online: byId('orpOnlineCard'),
    pdq: byId('orpPdqTerminal'), auto: byId('orpAutoPayment'), save: byId('orpRecordManualPayment'),
    toast: byId('orpPaymentToast')
  };

  let current = null;
  let toastTimer = 0;

  function escText(v){ return String(v ?? ''); }
  function money(value, symbol){
    const n = Number(value || 0);
    return `${symbol || '£'}${n.toLocaleString('en-GB',{minimumFractionDigits:2,maximumFractionDigits:2})}`;
  }
  function dateOnly(value){
    if(!value) return '';
    const m=String(value).match(/^(\d{4})-(\d{2})-(\d{2})/);
    if(m) return `${m[1]}-${m[2]}-${m[3]}`;
    const d=new Date(value);
    if(Number.isNaN(d.valueOf())) return '';
    return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  }
  function base64Url(v){
    try{return btoa(unescape(encodeURIComponent(String(v ?? '')))).replace(/=+$/,'').replace(/\+/g,'-').replace(/\//g,'_');}
    catch{return String(v ?? '');}
  }
  function showToast(message,error=false){
    if(!els.toast) return;
    els.toast.textContent=message || (error?'Action failed.':'Done.');
    els.toast.classList.toggle('error',!!error);
    els.toast.classList.add('show');
    clearTimeout(toastTimer);toastTimer=setTimeout(()=>els.toast.classList.remove('show'),2800);
  }
  function setBusy(button,on,label='Working…'){
    if(!button) return;
    if(on){
      if(button.dataset.orpHtml===undefined) button.dataset.orpHtml=button.innerHTML;
      button.disabled=true;button.setAttribute('aria-busy','true');
      if(button.classList.contains('orp-payment-option')){
        const copy=button.querySelector('.orp-payment-option-copy b')?.textContent || label;
        button.innerHTML=`<span class="orp-payment-option-icon"><span class="orp-payment-spinner"></span></span><span class="orp-payment-option-copy"><b>${label}</b><small>${copy}</small></span>`;
      }else{
        button.innerHTML=`<span class="orp-payment-spinner"></span>${label}`;
      }
    }else{
      if(button.dataset.orpHtml!==undefined) button.innerHTML=button.dataset.orpHtml;
      delete button.dataset.orpHtml;button.disabled=false;button.removeAttribute('aria-busy');
    }
  }
  function waitForPaint(){return new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));}

  async function api(url,options={}){
    const headers=new Headers(options.headers||{});
    headers.set('Accept','application/json');
    if(options.body && !(options.body instanceof FormData)) headers.set('Content-Type','application/json');
    if(options.method && options.method.toUpperCase()!=='GET' && token) headers.set('RequestVerificationToken',token);
    const res=await fetch(url,{...options,headers,credentials:'same-origin'});
    if(res.status===401){location.href='/LoginHMS';throw new Error('Session expired.');}
    let json=null;try{json=await res.json();}catch{}
    if(!res.ok) throw new Error(json?.message || `Request failed (${res.status}).`);
    return json;
  }

  function optionsFor(ctx){return {...defaults,...(ctx?.urls||{})};}
  function setOptionVisible(el, visible){
    if(!el) return;
    const on = visible === true;
    el.hidden = !on;
    el.style.display = on ? 'flex' : 'none';
    el.setAttribute('aria-hidden', on ? 'false' : 'true');
  }
  function isAutoPayEligible(ctx){
    if(!ctx) return false;
    const bookingId = String(ctx.channexBookingId || '').trim();
    return ctx.showAutoPay === true
      && ctx.isVirtualCard === true
      && bookingId.length > 0
      && bookingId.toUpperCase() !== 'N/A'
      && Number(ctx.balance || 0) > 0.005;
  }

  function open(ctx={}){
    current={...ctx};
    const balance=Number(ctx.balance||0);
    els.guest.textContent=[ctx.guestName,ctx.roomNo?`Room ${ctx.roomNo}`:''].filter(Boolean).join(' · ');
    els.balance.textContent=money(balance,ctx.currencySymbol||'£');
    els.amount.value=balance>0?balance.toFixed(2):'0.00';
    els.method.value=ctx.defaultMethod||'Cash';
    els.note.value='';
    // The shared component never assumes a provider/action is allowed.
    // Use explicit display as well as [hidden], because the option class uses display:flex.
    setOptionVisible(els.online, ctx.showOnlineCard === true);
    setOptionVisible(els.pdq, ctx.showPdqPayment === true);
    // Auto Payment must satisfy the full legacy reservation rule.
    setOptionVisible(els.auto, isAutoPayEligible(ctx));
    root.classList.add('open');root.setAttribute('aria-hidden','false');
    setTimeout(()=>els.amount?.focus(),0);
    root.dispatchEvent(new CustomEvent('recordpayment:opened',{detail:{...current}}));
  }

  function close(){
    root.classList.remove('open');root.setAttribute('aria-hidden','true');
    const previous=current;current=null;
    root.dispatchEvent(new CustomEvent('recordpayment:closed',{detail:previous||{}}));
  }

  async function onlineCard(){
    if(!current) return;
    if(current.showOnlineCard!==true){showToast('Online Card is not enabled for this property.',true);return;}
    if(!(Number(current.balance||0)>0)){showToast('There is no outstanding balance.',true);return;}
    const urls=optionsFor(current);setBusy(els.online,true,'Opening…');await waitForPaint();
    try{
      const j=await api(urls.prepareEmailUrl,{method:'POST',body:JSON.stringify({regId:current.regId,paymentId:current.paymentId||0})});
      if(j?.ok===false) throw new Error(j.message||'Unable to prepare payment checkout.');
      const paymentUrl=String(j?.data?.paymentUrl||'').trim();
      if(!paymentUrl) throw new Error('Payment link is not available for this reservation.');
      const w=window.open(paymentUrl,'_blank','noopener,noreferrer');
      if(!w) showToast('Please allow pop-ups for this website to open Online Card.',true);
    }catch(e){showToast(e.message||'Unable to open payment checkout.',true);}
    finally{setBusy(els.online,false);}
  }

  function pdqPayment(){
    if(!current) return;
    if(current.showPdqPayment!==true){showToast('PDQ Payment is not enabled for this property.',true);return;}
    const amount=Number(current.balance||0);
    if(!(amount>0)){showToast('There is no outstanding balance.',true);return;}
    const urls=optionsFor(current);
    setBusy(els.pdq,true,'Opening…');
    const q=new URLSearchParams({
      hotelId:base64Url(current.hotelId||''),regId:base64Url(current.regId||''),visitId:base64Url(current.visitId||''),
      fullName:base64Url(current.guestName||''),amount:base64Url(String(Math.round(amount*100))),
      currency:base64Url(String(current.currencyCode||'GBP').toUpperCase()),arrival:base64Url(dateOnly(current.arrival)),
      departure:base64Url(dateOnly(current.departure)),grandTotal:base64Url(amount.toFixed(2)),payable:base64Url(amount.toFixed(2)),
      advancePaid:base64Url('0'),status:base64Url(current.status||'check in'),src:base64Url(current.source||'NR'),userid:base64Url(current.userId||'')
    });
    window.open(`${urls.pdqUrl}?${q.toString()}`,'pdq','width=500,height=720,noopener,noreferrer');
    setTimeout(()=>setBusy(els.pdq,false),350);
  }

  function autoPayment(){
    if(!isAutoPayEligible(current)){showToast('Auto Payment is not available for this reservation.',true);return;}
    if(!current.channexBookingId){showToast('Booking ID is missing for Auto Payment.',true);return;}
    const urls=optionsFor(current);setBusy(els.auto,true,'Opening…');
    try{
      const u=new URL(urls.autoPaymentUrl,location.origin);
      u.searchParams.set('autorun','1');u.searchParams.set('hotelId',current.hotelId||'');
      u.searchParams.set('bookingId',current.channexBookingId);u.searchParams.set('regId',current.regId||'');
      u.searchParams.set('src',current.source||'NR');
      if(Number(current.balance||0)>0)u.searchParams.set('amount',Number(current.balance).toFixed(2));
      if(current.currencyCode)u.searchParams.set('currency',String(current.currencyCode).toUpperCase());
      const w=window.open(u.toString(),'_blank','noopener,noreferrer');
      if(!w) showToast('Please allow pop-ups for this website to open Auto Payment.',true);
    }finally{setTimeout(()=>setBusy(els.auto,false),300);}
  }

  async function manualPayment(){
    if(!current) return;
    const amount=Number(els.amount.value);
    if(!(amount>0)){showToast('Enter a valid amount.',true);els.amount.focus();return;}
    const urls=optionsFor(current);setBusy(els.save,true,'Recording…');await waitForPaint();
    try{
      const body={regId:current.regId,visitId:current.visitId||'',amount,method:els.method.value,roomSecurity:0,note:els.note.value||''};
      const j=await api(urls.recordPaymentUrl,{method:'POST',body:JSON.stringify(body)});
      if(j?.ok===false) throw new Error(j.message||'Unable to record payment.');
      showToast(j?.message||'Payment recorded successfully.');
      const detail={context:{...current},response:j,amount,method:els.method.value};
      root.dispatchEvent(new CustomEvent('recordpayment:completed',{detail}));
      const callback=current?.onCompleted;
      close();
      if(typeof callback==='function') await Promise.resolve(callback(detail));
    }catch(e){showToast(e.message||'Unable to record payment.',true);}
    finally{setBusy(els.save,false);}
  }

  root.querySelectorAll('[data-orp-close]').forEach(btn=>btn.addEventListener('click',close));
  els.online?.addEventListener('click',onlineCard);
  els.pdq?.addEventListener('click',pdqPayment);
  els.auto?.addEventListener('click',autoPayment);
  els.save?.addEventListener('click',manualPayment);

  // Deliberately do not close when the dark backdrop is clicked.
  root.addEventListener('click',e=>{if(e.target===root)e.preventDefault();});

  window.RecordPayment={open,close,isOpen:()=>root.classList.contains('open')};
})();
