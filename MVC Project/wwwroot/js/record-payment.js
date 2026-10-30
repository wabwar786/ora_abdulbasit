(() => {
  'use strict';

  const root = document.getElementById('orpRecordPayment');
  if (!root) return;

  const byId = id => document.getElementById(id);

  function ensureHoldCaptureStyles(){
    if(document.getElementById('orpHoldCaptureRuntimeStyles')) return;
    const style=document.createElement('style');
    style.id='orpHoldCaptureRuntimeStyles';
    style.textContent=`
      .orp-hold-main{grid-column:1;grid-row:1}
      .orp-hold-actions{grid-column:2;grid-row:1;align-self:center}
      .orp-hold-capture-line{grid-column:1/-1;grid-row:2;display:grid;grid-template-columns:auto minmax(110px,170px);gap:8px;align-items:center;padding-top:8px;border-top:1px dashed #dbe4ed}
      .orp-hold-capture-line label{font-size:9px;font-weight:800;color:#60758b}
      .orp-hold-capture-amount{width:100%;height:32px;padding:0 9px;border:1px solid #cbd8e5;border-radius:8px;background:#fff;color:#172b40;font:inherit;font-size:10px;font-weight:750;outline:none}
      .orp-hold-capture-amount:focus{border-color:#2867e8;box-shadow:0 0 0 3px rgba(40,103,232,.10)}
      @media(max-width:620px){.orp-hold-main,.orp-hold-actions,.orp-hold-capture-line{grid-column:1;grid-row:auto}.orp-hold-capture-line{grid-template-columns:1fr}}
    `;
    document.head.appendChild(style);
  }
  ensureHoldCaptureStyles();
  const token = document.querySelector('#orpRecordPaymentAntiForgery input[name="__RequestVerificationToken"]')?.value || '';
  const defaults = {
    recordPaymentUrl: root.dataset.recordPaymentUrl || '/CheckIn/RecordPayment',
    prepareEmailUrl: root.dataset.prepareEmailUrl || '/Calendar/PrepareEmail',
    payNowLinkUrl: root.dataset.paynowLinkUrl || '/PayNow/GenerateLink',
    pdqUrl: root.dataset.pdqUrl || '/TerminalCardPayment.aspx',
    holdsUrl: root.dataset.holdsUrl || '/terminalcardpayment/Holds',
    captureHoldUrl: root.dataset.captureHoldUrl || '/terminalcardpayment/CaptureHold',
    releaseHoldUrl: root.dataset.releaseHoldUrl || '/terminalcardpayment/ReleaseHold',
    autoPaymentUrl: root.dataset.autoPaymentUrl || '/Autopayment.aspx'
  };

  const els = {
    guest: byId('orpPaymentGuest'), balance: byId('orpPaymentBalance'), amount: byId('orpPaymentAmount'),
    method: byId('orpPaymentMethod'), note: byId('orpPaymentNote'), online: byId('orpOnlineCard'),
    pdq: byId('orpPdqTerminal'), auto: byId('orpAutoPayment'), save: byId('orpRecordManualPayment'),
    toast: byId('orpPaymentToast'),
    onlineAction: byId('orpOnlineAction'), onlineBack: byId('orpOnlineBack'),
    onlineCharge: byId('orpOnlineCharge'), onlineHold: byId('orpOnlineHold'),
    onlineLink: byId('orpOnlineLink'), onlineLinkValue: byId('orpOnlineLinkValue'),
    onlineCopy: byId('orpOnlineCopy'), onlineOpen: byId('orpOnlineOpen'), onlineLinkHint: byId('orpOnlineLinkHint'),
    pdqAction: byId('orpPdqAction'), pdqBack: byId('orpPdqBack'),
    pdqCharge: byId('orpPdqCharge'), pdqHold: byId('orpPdqHold'), holds: byId('orpHolds'),
    holdsList: byId('orpHoldsList'), holdsRefresh: byId('orpHoldsRefresh')
  };

  let current = null;
  let activePdqWindow = null;
  let activePdqContext = null;
  let pdqCompletionInProgress = false;
  let toastTimer = 0;
  let holdsBusy = false;

  function money(value, symbol){
    const n = Number(value || 0);
    return `${symbol || '£'}${n.toLocaleString('en-GB',{minimumFractionDigits:2,maximumFractionDigits:2})}`;
  }
  const zeroDecimalCurrencies=new Set(['BIF','CLP','DJF','GNF','JPY','KMF','KRW','MGA','PYG','RWF','UGX','VND','VUV','XAF','XOF','XPF']);
  function minorFactor(currency){return zeroDecimalCurrencies.has(String(currency||'').toUpperCase())?1:100;}
  function majorFromMinor(minor,currency){return Number(minor||0)/minorFactor(currency);}
  function minorFromMajor(major,currency){return Math.round(Number(major||0)*minorFactor(currency));}
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
  function esc(v){
    return String(v ?? '').replace(/[&<>"']/g, c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
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
        button.innerHTML=`<span class="orp-payment-option-icon"><span class="orp-payment-spinner"></span></span><span class="orp-payment-option-copy"><b>${esc(label)}</b><small>${esc(copy)}</small></span>`;
      }else{
        button.innerHTML=`<span class="orp-payment-spinner"></span>${esc(label)}`;
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
    return ctx.showAutoPay === true && ctx.isVirtualCard === true && bookingId.length > 0
      && bookingId.toUpperCase() !== 'N/A' && Number(ctx.balance || 0) > 0.005;
  }

  function normalizePreferredMethod(value){
    const method=String(value||'').trim().toLowerCase();
    if(['pdq','pdq terminal','pdq payment','terminal'].includes(method)) return 'pdq';
    if(['online','online card','card','card payment','stripe'].includes(method)) return 'online';
    if(['auto','auto payment'].includes(method)) return 'auto';
    return '';
  }

  function markPreferredMethod(method){
    const preferred=normalizePreferredMethod(method);
    [[els.online,'online'],[els.pdq,'pdq'],[els.auto,'auto']].forEach(([button,key])=>{
      if(!button) return;
      const selected=preferred===key;
      button.classList.toggle('is-selected',selected);
      button.setAttribute('aria-pressed',selected?'true':'false');
    });
    return preferred;
  }

  function canManageHolds(ctx){
    return !!ctx && (ctx.showPdqPayment===true || ctx.showOnlineCard===true);
  }

  function hideOnlineChooser(){
    if(els.onlineAction) els.onlineAction.hidden=true;
    if(els.onlineLink) els.onlineLink.hidden=true;
  }
  function showOnlineChooser(){
    if(!current || current.showOnlineCard!==true){showToast('Online Card is not enabled for this property.',true);return;}
    hidePdqChooser();
    if(els.onlineAction) els.onlineAction.hidden=false;
    if(els.onlineLink) els.onlineLink.hidden=true;
    els.onlineAction?.scrollIntoView({behavior:'smooth',block:'nearest'});
  }

  function hidePdqChooser(){ if(els.pdqAction) els.pdqAction.hidden=true; }
  function showPdqChooser(){
    if(!current || current.showPdqPayment!==true){showToast('PDQ Payment is not enabled for this property.',true);return;}
    hideOnlineChooser();
    if(els.pdqAction) els.pdqAction.hidden=false;
    els.pdqAction?.scrollIntoView({behavior:'smooth',block:'nearest'});
  }

  async function open(ctx={}){
    current={...ctx};
    const balance=Number(ctx.balance||0);
    els.guest.textContent=[ctx.guestName,ctx.roomNo?`Room ${ctx.roomNo}`:''].filter(Boolean).join(' · ');
    els.balance.textContent=money(balance,ctx.currencySymbol||'£');
    els.amount.value=balance>0?balance.toFixed(2):'0.00';
    els.method.value=ctx.defaultMethod||'Cash';
    els.note.value='';
    hideOnlineChooser();
    hidePdqChooser();
    if(els.onlineLinkValue) els.onlineLinkValue.value='';
    if(els.onlineLinkHint) els.onlineLinkHint.textContent='';
    if(els.holds){els.holds.hidden=true;}
    if(els.holdsList){els.holdsList.innerHTML='';}
    setOptionVisible(els.online, ctx.showOnlineCard === true);
    setOptionVisible(els.pdq, ctx.showPdqPayment === true);
    setOptionVisible(els.auto, isAutoPayEligible(ctx));
    const preferred=markPreferredMethod(ctx.preferredMethod||ctx.selectedMethod||'');
    root.classList.add('open');root.setAttribute('aria-hidden','false');
    if(preferred==='pdq' && ctx.showPdqPayment===true) showPdqChooser();
    else if(preferred==='online' && ctx.showOnlineCard===true) showOnlineChooser();
    setTimeout(()=>{
      if(preferred==='pdq') els.pdq?.focus();
      else if(preferred==='online') els.online?.focus();
      else els.amount?.focus();
    },0);
    root.dispatchEvent(new CustomEvent('recordpayment:opened',{detail:{...current,preferredMethod:preferred}}));
    if(canManageHolds(ctx)){
      loadHolds(false).then(()=>{
        if(ctx.focusHolds===true && els.holds){
          els.holds.hidden=false;
          els.holds.scrollIntoView({behavior:'smooth',block:'nearest'});
        }
      });
    }
  }

  function close(){
    root.classList.remove('open');root.setAttribute('aria-hidden','true');
    hideOnlineChooser();
    hidePdqChooser();
    const previous=current;current=null;
    root.dispatchEvent(new CustomEvent('recordpayment:closed',{detail:previous||{}}));
  }

  let onlineLinkPending = false;
  async function launchOnline(mode){
    if(!current || onlineLinkPending) return;
    if(current.showOnlineCard!==true){showToast('Online Card is not enabled for this property.',true);return;}

    const amount=Number(els.amount?.value||0);
    if(!(amount>0)){showToast('Enter the amount to charge or hold.',true);els.amount?.focus();return;}

    const cleanMode=mode==='hold'?'hold':'charge';
    const urls=optionsFor(current);
    const button=cleanMode==='hold'?els.onlineHold:els.onlineCharge;
    // Start the network request while the original page is still active.
    // Do NOT await requestAnimationFrame after opening a new tab: Chrome
    // suspends animation frames in background tabs, which prevented this
    // request from starting until the user switched back.
    onlineLinkPending=true;
    if(els.onlineCharge) els.onlineCharge.disabled=true;
    if(els.onlineHold) els.onlineHold.disabled=true;
    if(els.onlineLink) els.onlineLink.hidden=true;
    if(els.onlineLinkValue) els.onlineLinkValue.value='';
    if(els.onlineLinkHint) els.onlineLinkHint.textContent='';
    setBusy(button,true,cleanMode==='hold'?'Generating hold link…':'Generating payment link…');

    // Calling api() here starts fetch synchronously, before window.open()
    // transfers focus to the new tab. No await is allowed before window.open(),
    // otherwise browsers may block the popup (lost user activation).
    const linkRequest=api(urls.payNowLinkUrl,{
      method:'POST',
      body:JSON.stringify({
        regId:String(current.regId||'').trim(),
        paymentMode:cleanMode,
        source:String(current.source||'NR').trim(),
        amount:Number(amount.toFixed(2))
      })
    });

    let checkoutTab=null;
    if(cleanMode==='charge'){
      checkoutTab=window.open('', '_blank');
      if(checkoutTab){
        try{
          checkoutTab.document.open();
          checkoutTab.document.write('<!doctype html><html><head><title>Preparing PayNow checkout</title><meta charset="utf-8"><style>body{font:16px system-ui;background:#f6f8fc;color:#14345a;display:grid;place-items:center;min-height:90vh;text-align:center}.spin{width:32px;height:32px;border:4px solid #d6e2f2;border-top-color:#174579;border-radius:50%;animation:spin 1s linear infinite;margin:auto}@keyframes spin{to{transform:rotate(360deg)}}</style></head><body><main><div class="spin"></div><h2>Preparing secure checkout…</h2><p>Your payment link is being generated.</p></main></body></html>');
          checkoutTab.document.close();
        }catch(_){ /* Keep the new tab available for navigation. */ }
      }
    }

    try{
      const j=await linkRequest;
      if(j?.ok===false) throw new Error(j.message||'Unable to prepare the PayNow link.');
      const rawPaymentUrl=String(j?.data?.paymentUrl||'').trim();
      if(!rawPaymentUrl) throw new Error('Payment link is not available for this reservation.');
      const paymentUrl=new URL(rawPaymentUrl,window.location.origin);
      // GenerateLink produces the secure PayNow route. The PMS may use a
      // separately configured public PayNow domain; do not require same-origin.
      if(!/^https?:$/.test(paymentUrl.protocol) || !/^\/PayNow\/p\//i.test(paymentUrl.pathname))
        throw new Error('The generated payment link is not a valid PayNow URL.');
      if(els.onlineLinkValue) els.onlineLinkValue.value=paymentUrl.href;
      if(els.onlineLinkHint){
        els.onlineLinkHint.textContent=cleanMode==='hold'
          ? 'Hold PayNow link generated. Copy it for the guest or press Open to test it.'
          : 'Payment link generated. Opening secure checkout…';
      }
      if(els.onlineLink) els.onlineLink.hidden=false;

      if(cleanMode==='charge'){
        setBusy(button,true,'Opening secure checkout…');
        if(checkoutTab && !checkoutTab.closed){
          checkoutTab.location.replace(paymentUrl.href);
        }else{
          // Popup blocked/closed: retain the generated link in this modal.
          // The existing Open link lets the user navigate with another click.
          showToast('Payment link ready. Allow pop-ups or use Open to launch PayNow in a new tab.',true);
        }
        return;
      }
      els.onlineLink?.scrollIntoView({behavior:'smooth',block:'nearest'});
      showToast('Hold PayNow link generated.');
    }catch(e){
      if(checkoutTab && !checkoutTab.closed) { try{checkoutTab.close();}catch(_){} }
      showToast(e.message||'Unable to prepare the PayNow link.',true);
    }finally{
      onlineLinkPending=false;
      setBusy(button,false);
      if(els.onlineCharge) els.onlineCharge.disabled=false;
      if(els.onlineHold) els.onlineHold.disabled=false;
    }
  }

  function launchPdq(mode){
    if(!current) return;
    const amount=Number(els.amount?.value||0);
    if(!(amount>0)){showToast('Enter the amount to charge or hold.',true);els.amount?.focus();return;}
    const urls=optionsFor(current);
    const note=(els.note?.value||'').trim() || (mode==='hold'?'Card authorization hold':'PDQ card payment');
    const q=new URLSearchParams({
      hotelId:base64Url(current.hotelId||''),regId:base64Url(current.regId||''),visitId:base64Url(current.visitId||''),
      fullName:base64Url(current.guestName||''),amount:base64Url(String(Math.round(amount*100))),
      currency:base64Url(String(current.currencyCode||'GBP').toUpperCase()),arrival:base64Url(dateOnly(current.arrival)),
      departure:base64Url(dateOnly(current.departure)),grandTotal:base64Url(String(Number(current.balance||amount).toFixed(2))),
      payable:base64Url(String(Number(current.balance||amount).toFixed(2))),advancePaid:base64Url('0'),
      status:base64Url(current.status||'check in'),src:base64Url(current.source||'NR'),userid:base64Url(current.userId||''),
      description:base64Url(note),mode:base64Url(mode)
    });
    const terminalUrl = new URL(urls.pdqUrl, window.location.origin);
    // The payment popup must stay same-origin for a verified completion message.
    if (terminalUrl.origin !== window.location.origin) {
      showToast('PDQ payment must open on the same website.',true);
      return;
    }
    terminalUrl.search = q.toString();
    const pdqTarget = terminalUrl.toString();
    const w=window.open('about:blank','pdq','width=500,height=760,resizable=yes,scrollbars=yes');
    if(!w){
      showToast('Please allow pop-ups for this website to open the PDQ terminal.',true);
      return;
    }
    // Do not clear w.opener: the terminal sends its successful result back via postMessage.
    // The receiving code verifies origin, source window and reservation ID.
    activePdqWindow = w;
    activePdqContext = {...current};
    pdqCompletionInProgress = false;
    try{
      w.location.replace(pdqTarget);
      w.focus();
    }catch(_){
      try{ w.location.href=pdqTarget; }catch(__){}
    }
  }

  async function loadHolds(showErrors=true){
    if(!current || !canManageHolds(current) || holdsBusy) return;
    const urls=optionsFor(current);
    holdsBusy=true;
    if(els.holdsRefresh) els.holdsRefresh.disabled=true;
    if(els.holdsList) els.holdsList.innerHTML='<div class="orp-hold-empty">Checking authorized holds…</div>';
    try{
      const u=new URL(urls.holdsUrl,location.origin);u.searchParams.set('regId',current.regId||'');
      const j=await api(u.toString());
      if(j?.ok===false) throw new Error(j.message||'Unable to load card holds.');
      renderHolds(Array.isArray(j?.holds)?j.holds:[]);
    }catch(e){
      if(els.holds) els.holds.hidden=false;
      if(els.holdsList) els.holdsList.innerHTML=`<div class="orp-hold-empty error">${esc(e.message||'Unable to load card holds.')}</div>`;
      if(showErrors) showToast(e.message||'Unable to load card holds.',true);
    }finally{
      holdsBusy=false;if(els.holdsRefresh) els.holdsRefresh.disabled=false;
    }
  }

  function renderHolds(holds){
    if(!els.holds || !els.holdsList) return;

    const keepVisible=current?.focusHolds===true;
    els.holds.hidden=holds.length===0 && !keepVisible;

    if(!holds.length){
      els.holdsList.innerHTML=keepVisible
        ? '<div class="orp-hold-empty">No active payment holds for this reservation.</div>'
        : '';
      return;
    }

    els.holdsList.innerHTML=holds.map(h=>{
      const currency=String(h.currency||current?.currencyCode||'GBP').toUpperCase();
      const factor=minorFactor(currency);
      const authorizedMajor=majorFromMinor(h.amountMinor,currency);
      const card=[h.cardBrand,String(h.last4||'').trim()?`•••• ${h.last4}`:''].filter(Boolean).join(' · ');
      const amount=h.amountText || authorizedMajor.toLocaleString('en-GB',{minimumFractionDigits:factor===1?0:2,maximumFractionDigits:factor===1?0:2});
      const source=String(h.source||'').toLowerCase();
      const sourceLabel=source==='checkout_hold'?'Online Checkout':(source==='pdq_terminal'?'PDQ Terminal':'Card Hold');
      const releaseAllowed=h.canRelease!==false;
      const releaseButton=releaseAllowed
        ? '<button type="button" class="orp-hold-btn release" data-hold-action="release">Release</button>'
        : '<button type="button" class="orp-hold-btn release" disabled title="Stripe Checkout authorizations cannot be canceled after Checkout completes; capture it or allow it to expire.">Release</button>';
      const holdHint=releaseAllowed
        ? ''
        : ' · Online Checkout hold: capture it or allow the authorization to expire';

      return `<div class="orp-hold-row" data-pi="${esc(h.paymentIntentId)}" data-amount-minor="${Number(h.amountMinor||0)}" data-currency="${esc(currency)}">
        <div class="orp-hold-main">
          <b>${esc((current?.currencySymbol||'£')+amount)} authorized <span class="orp-hold-source">${esc(sourceLabel)}</span></b>
          <small>${esc(card||h.paymentMethod||'Card')} ${h.description?`· ${esc(h.description)}`:''}${holdHint}</small>
        </div>
        <div class="orp-hold-capture-line">
          <label>Capture amount</label>
          <input class="orp-hold-capture-amount" type="number" min="${factor===1?'1':'0.01'}" step="${factor===1?'1':'0.01'}" max="${authorizedMajor}" value="${authorizedMajor.toFixed(factor===1?0:2)}" inputmode="decimal" />
        </div>
        <div class="orp-hold-actions">
          <button type="button" class="orp-hold-btn capture" data-hold-action="capture">Capture</button>
          ${releaseButton}
        </div>
      </div>`;
    }).join('');

    els.holdsList.querySelectorAll('[data-hold-action]').forEach(btn=>btn.addEventListener('click',()=>holdAction(btn)));
  }

  async function holdAction(button){
    if(!current) return;
    const row=button.closest('.orp-hold-row');
    const paymentIntentId=row?.dataset.pi||'';
    const action=button.dataset.holdAction;
    if(!paymentIntentId || !action) return;

    const currency=String(row?.dataset.currency||current.currencyCode||'GBP').toUpperCase();
    const authorizedMinor=Number(row?.dataset.amountMinor||0);
    let amountMinor=null;

    if(action==='capture'){
      const input=row?.querySelector('.orp-hold-capture-amount');
      const captureMajor=Number(input?.value||0);
      amountMinor=minorFromMajor(captureMajor,currency);
      if(!(amountMinor>0)){showToast('Enter a valid capture amount.',true);input?.focus();return;}
      if(authorizedMinor>0 && amountMinor>authorizedMinor){
        showToast('Capture amount cannot exceed the authorized hold.',true);input?.focus();return;
      }
      if(authorizedMinor>0 && amountMinor<authorizedMinor){
        const ok=confirm(`Capture ${money(captureMajor,current.currencySymbol||'£')} from this hold? The remaining authorization will be released by Stripe.`);
        if(!ok) return;
      }
    }

    const urls=optionsFor(current);
    const endpoint=action==='capture'?urls.captureHoldUrl:urls.releaseHoldUrl;
    const label=action==='capture'?'Capturing…':'Releasing…';
    setBusy(button,true,label);await waitForPaint();
    try{
      const payload={regId:current.regId,paymentIntentId};
      if(action==='capture') payload.amountMinor=amountMinor;
      const j=await api(endpoint,{method:'POST',body:JSON.stringify(payload)});
      if(j?.ok===false) throw new Error(j.message||`Unable to ${action} hold.`);
      showToast(j?.message||`Hold ${action==='capture'?'captured':'released'}.`);
      await loadHolds(false);
      if(action==='capture'){
        const detail={context:{...current},response:j,method:'Card Hold Capture'};
        root.dispatchEvent(new CustomEvent('recordpayment:completed',{detail}));
        const callback=current?.onCompleted;
        if(typeof callback==='function') await Promise.resolve(callback(detail));
      }
    }catch(e){showToast(e.message||`Unable to ${action} hold.`,true);}
    finally{setBusy(button,false);}
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
  els.online?.addEventListener('click',()=>{markPreferredMethod('online');showOnlineChooser();});
  els.onlineBack?.addEventListener('click',hideOnlineChooser);
  els.onlineCharge?.addEventListener('click',()=>launchOnline('charge'));
  els.onlineHold?.addEventListener('click',()=>launchOnline('hold'));
  els.onlineCopy?.addEventListener('click',async()=>{
    const value=String(els.onlineLinkValue?.value||'').trim();
    if(!value) return;
    try{
      await navigator.clipboard.writeText(value);
      showToast('PayNow link copied.');
    }catch{
      els.onlineLinkValue?.select();
      showToast('Select and copy the PayNow link.');
    }
  });
  els.onlineOpen?.addEventListener('click',()=>{
    const value=String(els.onlineLinkValue?.value||'').trim();
    if(!value) return;
    const w=window.open(value,'_blank','noopener,noreferrer');
    if(!w) showToast('Please allow pop-ups for this website.',true);
  });
  els.pdq?.addEventListener('click',()=>{markPreferredMethod('pdq');showPdqChooser();});
  els.pdqBack?.addEventListener('click',hidePdqChooser);
  els.pdqCharge?.addEventListener('click',()=>launchPdq('charge'));
  els.pdqHold?.addEventListener('click',()=>launchPdq('hold'));
  els.holdsRefresh?.addEventListener('click',()=>loadHolds(true));
  els.auto?.addEventListener('click',()=>{markPreferredMethod('auto');autoPayment();});
  els.method?.addEventListener('change',()=>markPreferredMethod(''));
  els.save?.addEventListener('click',manualPayment);

  root.addEventListener('click',e=>{if(e.target===root)e.preventDefault();});

  // The terminal can finish before Stripe's webhook has committed PaymentsLogTB.
  // Wait for its actual payment ID to appear before refreshing the originating page.
  async function waitForPdqPosting(regId, response) {
    const ids = [response.paymentIntentId, response.chargeId]
      .map(value => String(value || '').trim().toLowerCase()).filter(Boolean);
    if (!ids.length) return false;
    for (let attempt=0; attempt<15; attempt++) {
      try {
        const url = new URL('/Calendar/BookingDetails', window.location.origin);
        url.searchParams.set('regId', regId);
        url.searchParams.set('paymentId', '0');
        url.searchParams.set('_', String(Date.now()));
        const res = await fetch(url.toString(), {
          credentials: 'same-origin', cache: 'no-store', headers: {'Accept':'application/json'}
        });
        if (res.ok) {
          const result = await res.json();
          const logs = result?.data?.payments || [];
          if (logs.some(log => ids.some(id => [log.paymentId,log.chargeId,log.reference]
            .some(value => String(value || '').toLowerCase() === id)))) return true;
        }
      } catch (_) { /* Transient read; retry while the webhook is processing. */ }
      await new Promise(resolve=>setTimeout(resolve,800));
    }
    return false;
  }

  // The terminal success can arrive before the asynchronous hold webhook commits.
  // Wait briefly for the actual authorization to be readable before returning to
  // Calendar / Check-In / Pending Payments. A HOLD is NOT a received payment.
  async function waitForPdqHold(regId, paymentIntentId){
    if(!paymentIntentId)return false;
    for(let attempt=0;attempt<12;attempt++){
      try{
        const u=new URL(defaults.holdsUrl,location.origin);
        u.searchParams.set('regId',regId);
        u.searchParams.set('_',String(Date.now()));
        const res=await fetch(u.toString(),{
          credentials:'same-origin',cache:'no-store',headers:{'Accept':'application/json'}
        });
        if(res.ok){
          const data=await res.json();
          if(Array.isArray(data?.holds)&&data.holds.some(h=>String(h.paymentIntentId||'')===String(paymentIntentId)))return true;
        }
      }catch(_){ /* Webhook may still be committing; retry. */ }
      await new Promise(resolve=>setTimeout(resolve,500));
    }
    return false;
  }

  window.addEventListener('message',async e=>{
    if(e.origin!==window.location.origin || !e.data) return;
    if(!activePdqWindow || e.source!==activePdqWindow || !activePdqContext) return;
    // Even if Record Payment was closed, retain the originating-page refresh callback.
    const pdqContext = activePdqContext;
    if(e.data.regId && String(e.data.regId)!==String(pdqContext.regId||'')) return;
    if(e.data.type==='PDQ_HOLD_AUTHORIZED'){
      if(pdqCompletionInProgress)return;
      pdqCompletionInProgress=true;
      // The success message comes from the verified PDQ window and Stripe result.
      // No room payment is recorded here: the authorization remains a hold.
      showToast('PDQ hold authorized. Updating the originating page…');
      const context=pdqContext;
      try{
        const posted=await waitForPdqHold(context.regId,e.data.paymentIntentId);
        if(!posted)showToast('Hold approved. The webhook may still be processing.',true);
        const detail={context,response:e.data,method:'PDQ Hold',hold:true,posted};
        root.dispatchEvent(new CustomEvent('recordpayment:completed',{detail}));
        const callback=context.onCompleted;
        // If the current popup is still displayed, refresh its hold list too.
        if(current && String(current.regId)===String(context.regId)) {
          hidePdqChooser();
          await loadHolds(false);
        }
        activePdqWindow=null;
        activePdqContext=null;
        if(typeof callback==='function')await Promise.resolve(callback(detail));
      }finally{pdqCompletionInProgress=false;}
    }else if(e.data.type==='PDQ_PAYMENT_SAVED'){
      if(pdqCompletionInProgress) return;
      pdqCompletionInProgress=true;
      showToast('PDQ payment approved. Updating payment history…');
      const context=pdqContext;
      const posted=await waitForPdqPosting(context.regId,e.data);
      if(!posted) showToast('Payment approved. The Stripe webhook may still be processing.',true);
      const detail={context,response:e.data,method:'PDQ Payment',posted};
      root.dispatchEvent(new CustomEvent('recordpayment:completed',{detail}));
      const callback=context.onCompleted;
      activePdqWindow=null;
      activePdqContext=null;
      try {
        if(typeof callback==='function') await Promise.resolve(callback(detail));
      } finally { pdqCompletionInProgress=false; }
    }
  });

  window.RecordPayment={open,close,isOpen:()=>root.classList.contains('open'),refreshHolds:()=>loadHolds(false)};
})();
