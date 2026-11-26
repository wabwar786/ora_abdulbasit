/* Restaurant room checkout v4: uses existing authenticated folio and payment
 * endpoints. Does NOT mount RecordPayment partial or create new ledger rows for
 * PDQ/Online card; the existing verified gateway/webhook remains responsible.
 */
(() => {
  'use strict';
  const root = document.getElementById('restaurant-pos');
  if (!root) return;
  const $ = id => document.getElementById(id);
  let context = null, initialBalance = null, postedAmount = 0;
  let gatewayWindow = null, gatewayMode = '';
  let pendingAmount = 0, processing = false;
  const token = () => root.querySelector('#r-close-form input[name="__RequestVerificationToken"]')?.value || '';
  const money = num => Number(num || 0);
  const base64Url = value => btoa(unescape(encodeURIComponent(String(value ?? ''))))
    .replace(/=+$/,'').replace(/\+/g,'-').replace(/\//g,'_');
  const request = async (url,options={}) => {
    const headers = new Headers(options.headers||{});
    headers.set('Accept','application/json');
    if (options.body && !(options.body instanceof FormData)) headers.set('Content-Type','application/json');
    if (options.method && options.method!=='GET') headers.set('RequestVerificationToken',token());
    const response = await fetch(url,{...options,headers,credentials:'same-origin',cache:'no-store'});
    let result; try{result=await response.json();}catch{result=null;}
    if(!response.ok || result?.ok===false) throw new Error(result?.message||`Payment request failed (${response.status})`);
    return result;
  };
  const contextUrl = () => root.dataset.roomPaymentContextUrl;
  async function getContext() {return await request(contextUrl());}
  async function getMethods() {
    const rows=await request(root.dataset.paymentMethodsUrl);
    if (!Array.isArray(rows)) throw Error('Hotel payment methods are not available.');
    const unique=new Set();
    return rows.map(row=>({value:String(row.value??row.Value??'').trim(),text:String(row.text??row.Text??row.value??row.Value??'').trim()}))
      .filter(row=>{
        if (!row.value || /^(PDQ Payment|PDQ Terminal|Online Card)$/i.test(row.value) || unique.has(row.value.toLowerCase()))return false;
        unique.add(row.value.toLowerCase());return true;
      });
  }
  function can(tab) {
    return tab==='manual' || !!(context && (tab==='pdq' ? context.showPdqPayment : context.showOnlineCard));
  }
  async function begin(amount) {
    postedAmount=money(amount);
    // Server checks hotel, selected room, checked-in stay, and card permissions.
    const [ctx,methods] = await Promise.all([getContext(),getMethods()]);
    if (!ctx.regId || money(ctx.balance)<=0) throw Error('Guest balance is not payable. Verify the folio before taking payment.');
    context=ctx;initialBalance=money(ctx.balance);
    return {methods};
  }
  function setPending(text,actions) { actions.pending(text); }
  async function collectManual(args,actions) {
    if (!context) throw Error('Room payment context has not been loaded.');
    if (!args.method) throw Error('Select a configured payment method.');
    const note=[args.note];
    if (args.cardType) note.push(`Card type: ${args.cardType}`);
    if (args.reference) note.push(`External terminal ref: ${args.reference}`);
    note.push('Restaurant counter collection');
    const response=await request(root.dataset.checkinRecordPaymentUrl,{
      method:'POST',body:JSON.stringify({regId:context.regId,visitId:context.visitId||'',amount:args.amount,
        method:args.method,roomSecurity:0,note:note.filter(Boolean).join(' | ')})
    });
    if (response?.ok===false) throw Error(response?.message||'Payment not recorded.');
    actions.success(`Recorded ${context.currencySymbol||'£'} ${args.amount.toFixed(2)} to guest ${context.guestName||context.regId}. Restaurant charge was not posted twice.`);
  }
  function pdqUrl(amount) {
    const q=new URLSearchParams({
      hotelId:base64Url(context.hotelId||''),regId:base64Url(context.regId||''),visitId:base64Url(context.visitId||''),
      fullName:base64Url(context.guestName||''),amount:base64Url(String(Math.round(amount*100))),
      currency:base64Url(String(context.currencyCode||'GBP').toUpperCase()),
      arrival:base64Url(''),departure:base64Url(''),grandTotal:base64Url(money(context.balance).toFixed(2)),
      payable:base64Url(money(context.balance).toFixed(2)),advancePaid:base64Url('0'),
      status:base64Url('check in'),src:base64Url(context.source||'GI'),userid:base64Url(context.userId||''),
      description:base64Url('ORA Restaurant POS — folio charge'),mode:base64Url('charge')
    });
    const u=new URL('/TerminalCardPayment.aspx',location.origin);
    u.search=q.toString();
    return u.href;
  }
  async function chargePdq(args,actions) {
    if(!can('pdq')) throw Error('PDQ is not available to this user or hotel.');
    const popup=window.open('about:blank','oraRestaurantRoomPdq','width=530,height=760,resizable=yes,scrollbars=yes');
    if(!popup) throw Error('Allow pop-ups to use the card terminal.');
    gatewayWindow=popup;gatewayMode='pdq';pendingAmount=args.amount;
    popup.location.replace(pdqUrl(args.amount));popup.focus();
    setPending('PDQ payment started. Complete the charge on the terminal. The existing Stripe webhook must save the receipt before reconciliation.',actions);
  }
  async function onlineCard(args,actions) {
    if(!can('online')) throw Error('Online Card is not enabled for this hotel or user.');
    const popup=window.open('about:blank','oraRestaurantRoomOnline','width=560,height=760,resizable=yes,scrollbars=yes');
    if(!popup) throw Error('Allow pop-ups to open secure checkout.');
    gatewayWindow=popup;gatewayMode='online';pendingAmount=args.amount;
    try {
      popup.document.write('<!doctype html><title>ORA PMS Payment</title><p style="font:15px system-ui;padding:28px">Preparing secure card checkout…</p>');
      const res=await request(root.dataset.paynowLinkUrl,{method:'POST',body:JSON.stringify({
        regId:String(context.regId),paymentMode:'charge',source:String(context.source||'GI'),amount:Number(args.amount.toFixed(2))
      })});
      const raw=String(res?.data?.paymentUrl||'').trim();
      if(!raw) throw Error('Payment link could not be generated.');
      const u=new URL(raw,location.origin);
      // Preserve PayNow's protected token and configured HTTPS site only.
      if(!/^https?:$/.test(u.protocol)||!(/^\/PayNow\/p\/?$/i.test(u.pathname)&&u.searchParams.has('t') || /^\/PayNow\/p\/[^/]+\/?$/i.test(u.pathname)))
        throw Error('Unexpected secure payment link.');
      if($('r-pay-link'))$('r-pay-link').hidden=false;
      if($('r-pay-link-value'))$('r-pay-link-value').value=u.href;
      popup.location.replace(u.href);popup.focus();
      setPending('Online card checkout opened. The payment is not recorded until the gateway confirms it.',actions);
    } catch(error) { try{popup.close();}catch{} gatewayWindow=null;gatewayMode=''; throw error; }
  }
  async function pay(tab,args,actions) {
    if(processing) return;
    processing=true;
    try {
      if(!context) throw Error('The selected guest must be verified before payment. The folio charge is already posted.');
      if(!Number.isFinite(args.amount)||args.amount<=0||args.amount>postedAmount+.005||args.amount>money(context.balance)+.005)
        throw Error('Payment exceeds the posted Restaurant amount or the guest balance.');
      if(tab==='manual') await collectManual(args,actions);
      else if(tab==='pdq') await chargePdq(args,actions);
      else if(tab==='online') await onlineCard(args,actions);
      else throw Error('Unknown payment channel.');
    }catch(error){
      if(tab==='manual' && (error instanceof TypeError || /network|failed to fetch|connection/i.test(error?.message||'')))
        actions.pending('Payment response is unclear. Check the guest folio before recording another payment; do not retry blindly.');
      else actions.error(error?.message||'Payment could not be started. The guest charge is still posted.');
    }
    finally{processing=false;}
  }
  async function verify(actions) {
    if(!context || initialBalance===null) {actions.error('Open the room charge before checking payment.');return;}
    try {
      const fresh=await getContext();
      const decrease=initialBalance-money(fresh.balance);
      if(decrease>0.005) {
        // This confirms the SERVER folio balance changed, not a particular
        // Stripe transaction ID. Do not manufacture a payment receipt.
        actions.success(`The server's guest folio balance decreased by ${fresh.currencySymbol||'£'} ${decrease.toFixed(2)}. Confirm payment details in Payments Report before reconciling this bill.`);
      } else {
        actions.pending('No new receipt is reflected in the guest folio yet. Keep this bill pending and check again; do not charge twice.');
      }
    }catch(error){actions.error(error.message||'Unable to check folio payments.');}
  }
  window.addEventListener('message',event=>{
    if(!gatewayWindow || event.source!==gatewayWindow || event.origin!==location.origin)return;
    if(gatewayMode==='pdq' && event.data?.type==='PDQ_PAYMENT_SAVED' &&
       (!event.data.regId || String(event.data.regId)===String(context?.regId))) {
      // A popup message only starts a server check; it is not proof of payment.
      void verify({success:t=>root.dispatchEvent(new CustomEvent('restaurant:room-gateway-verified',{detail:{message:t}})),
        pending:t=>root.dispatchEvent(new CustomEvent('restaurant:room-gateway-pending',{detail:{message:t}})),
        error:t=>root.dispatchEvent(new CustomEvent('restaurant:room-gateway-pending',{detail:{message:t}}))});
    }
  });
  window.RestaurantRoomCheckout={begin,pay,verify,can};
})();
