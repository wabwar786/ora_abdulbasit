/* ORA PMS Inventory – MVC enhancement for approved HTML design.
 * Server-backed only. Never writes demo data to localStorage.
 * Forms use the existing inventory service and antiforgery protection.
 */
(()=>{'use strict';
if(window.__oraInventoryMvcLoaded)return;window.__oraInventoryMvcLoaded=true;
const root=()=>document.getElementById('ora-inventory-content');
const qs=(s,p=document)=>p?.querySelector(s),qsa=(s,p=document)=>[...p?.querySelectorAll(s)||[]];
const feedback=(message,error=false)=>{const box=qs('#oi-feedback');if(!box)return;box.hidden=false;box.textContent=message;box.classList.toggle('error',error);box.classList.toggle('success',!error);box.scrollIntoView({block:'nearest'});};
const jsonMessage=(response)=>response?.message||'Unable to complete your request.';
const esc=(str)=>String(str??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
async function refreshPage(){const resp=await fetch(location.href,{credentials:'same-origin',headers:{'X-Requested-With':'XMLHttpRequest'}});if(!resp.ok)throw Error('Unable to refresh inventory data.');const html=await resp.text();const dom=new DOMParser().parseFromString(html,'text/html');const newer=qs('#ora-inventory-content',dom);if(!newer)throw Error('Session expired or the inventory page could not be loaded.');root()?.replaceWith(document.importNode(newer,true));initPage();}
async function submit(form,trigger){
 if(!form.reportValidity())return;
 const url=form.getAttribute('action');if(!url){feedback('Form action is not configured.',true);return;}
 const btn=trigger||qs('[type=submit]',form);if(btn?.disabled)return;
 const original=btn?.textContent||'';const saveAnother=btn?.hasAttribute('data-save-another');if(btn){btn.disabled=true;btn.classList.add('loading');}
 try{const response=await fetch(url,{method:(form.getAttribute('method')||'POST').toUpperCase(),credentials:'same-origin',headers:{'X-Requested-With':'XMLHttpRequest','Accept':'application/json'},body:new FormData(form)});
 const kind=response.headers.get('content-type')||'';if(!kind.includes('application/json'))throw Error('The server returned an unexpected response. Confirm your sign-in and route.');const result=await response.json();if(!response.ok||result?.ok!==true)throw Error(jsonMessage(result));
 await refreshPage();feedback(jsonMessage(result));if(saveAnother)openDialog('oi-supplier-dialog');
 }catch(err){feedback(err?.message||'Unable to save changes.',true);}finally{if(btn&&btn.isConnected){btn.disabled=false;btn.classList.remove('loading');btn.textContent=original;}}
}
function openDialog(id,trigger){const d=document.getElementById(id);if(!d)return;const form=qs('form',d);if(form&&!trigger?.hasAttribute('data-edit-record')){form.reset();const hid=qs('[name="Id"]',form);if(hid)hid.value='0';const opening=qs('[data-opening-field]',form);if(opening)opening.hidden=false;}
 const edit=trigger?.hasAttribute('data-edit-record');if(edit&&form){const attrs=trigger.dataset;for(const control of qsa('[name]',form)){const name=control.name;const key=name.charAt(0).toLowerCase()+name.slice(1);if(Object.hasOwn(attrs,key))control.value=attrs[key];}const opening=qs('[data-opening-field]',form);if(opening)opening.hidden=true;}
 if(id==='oi-reject-dialog'){const field=qs('[name="requestId"]',form);if(field)field.value=trigger?.dataset.rejectId||'';}
 if(id==='oi-asset-action'&&trigger?.dataset.assetTag){const field=qs('[name="tag"]',form);if(field)field.value=trigger.dataset.assetTag;}
 const title=qs('[data-dialog-title]',d);if(title)title.textContent=edit?(id==='oi-item-dialog'?'Edit inventory item':'Edit supplier'):(id==='oi-item-dialog'?'Add inventory item':'Add supplier');
 d.showModal();
}
function initRequest(){const typ=qs('#oi-request-type'),dest=qs('#oi-request-dest');if(!typ||!dest)return;
 const sync=()=>{const purchase=typ.value==='Purchase';for(const option of dest.options){const isPurch=option.value==='Main Store'||option.value==='Dept:Kitchen';option.hidden=purchase?!isPurch:option.value==='Main Store';}if(purchase&&(dest.value!=='Main Store'&&dest.value!=='Dept:Kitchen'))dest.value='Main Store';if(!purchase&&dest.value==='Main Store')dest.value='Dept:Housekeeping';};
 typ.addEventListener('change',sync);sync();if(new URLSearchParams(location.search).get('kitchen')==='1'){typ.value='Purchase';sync();dest.value='Dept:Kitchen';}
}
function initMovement(){const action=qs('#oi-movement-action'),item=qs('#oi-movement-item'),source=qs('#oi-movement-source'),dest=qs('#oi-movement-destination'),help=qs('#oi-available-text');if(!action||!item||!source)return;
 let all=[];try{all=JSON.parse(qs('#oi-movement-balances')?.textContent||'[]');}catch{}
 const recalc=()=>{const current=item.value,op=action.value;const allowed=op==='Repair'?['Damaged']:op==='Recovered'?['Repair']:op==='WriteOff'?['Damaged','Repair']:['Good'];const rows=all.filter(x=>x.item===current&&Number(x.qty)>0&&allowed.includes(x.condition)&&!((op==='Transfer'||op==='Use'||op==='LaundryOut')&&x.location==='Main Store')&&!(op==='LaundryOut'&&x.location==='Laundry')&&!(op==='LaundryBack'&&x.location!=='Laundry'));
 const old=source.value;source.replaceChildren(new Option('Choose source',''));for(const row of rows){const option=new Option(`${row.location} — ${row.qty} available${row.condition==='Good'?'':' ('+row.condition+')'}`,row.location);option.dataset.qty=row.qty;source.add(option);}if(rows.some(x=>x.location===old))source.value=old;else if(rows.length===1)source.value=rows[0].location;
 const chosen=rows.find(x=>x.location===source.value);help.textContent=chosen?`${chosen.qty} unit(s) available at the selected location.`:'No eligible stock at this location for this action.';const qty=qs('#oi-movement-qty');if(qty&&chosen)qty.max=chosen.qty;
 if(dest){const automatic=['Return','Use','LaundryOut','Damage','Repair','Recovered','WriteOff'].includes(op);dest.disabled=automatic;if(op==='Return')dest.value='Main Store';else if(op==='LaundryOut')dest.value='Laundry';else if(op==='LaundryBack')dest.disabled=false;}
 };action.addEventListener('change',recalc);item.addEventListener('change',recalc);source.addEventListener('change',recalc);recalc();}
function initReports(){const kind=qs('#oi-report-kind');if(!kind)return;
 let page=1,timeout=0;
 const labels={stock:'Inventory balances',purchases:'Purchases & goods received',demands:'Demand notes & approvals',issues:'Department-wise issuing',department:'Department stock',assets:'Asset register',movements:'Movement history'};
 const draw=async()=>{const params=new URLSearchParams({kind:kind.value,page:String(page),pageSize:qs('#oi-report-size').value});const search=qs('#oi-report-search')?.value||'';if(search)params.set('q',search);const from=qs('#oi-report-from')?.value||'';const to=qs('#oi-report-to')?.value||'';if(from)params.set('from',from);if(to)params.set('to',to);
 qs('#oi-report-title').textContent=labels[kind.value]||'Inventory report';const table=qs('#oi-report-table');const tbody=qs('tbody',table),thead=qs('thead',table);tbody.replaceChildren();const loading=document.createElement('tr');const td=document.createElement('td');td.textContent='Loading report…';td.className='oi-empty';loading.append(td);tbody.append(loading);
 const actionParams=new URLSearchParams(params);actionParams.delete('page');actionParams.delete('pageSize');qs('#oi-report-export').href='/Inventory/export?'+actionParams.toString();qs('#oi-report-print').href='/Inventory/report/print?'+actionParams.toString();
 try{const response=await fetch('/Inventory/report/data?'+params.toString(),{credentials:'same-origin',headers:{Accept:'application/json'}});const obj=await response.json();if(!response.ok||obj.ok!==true)throw Error(jsonMessage(obj));const report=obj.report;
 thead.replaceChildren();const htr=document.createElement('tr');(report.columns||report.Columns||[]).forEach(name=>{const th=document.createElement('th');th.textContent=name;htr.append(th)});thead.append(htr);tbody.replaceChildren();const rows=report.rows||report.Rows||[];for(const row of rows){const tr=document.createElement('tr');for(const value of row){const td=document.createElement('td');td.textContent=value;tr.append(td)}tbody.append(tr);}if(!rows.length){const tr=document.createElement('tr'),cell=document.createElement('td');cell.colSpan=Math.max(1,(report.columns||report.Columns||[]).length);cell.className='oi-empty';cell.textContent='No matching records.';tr.append(cell);tbody.append(tr);}
 const total=report.total??report.Total??0;const pages=report.totalPages??report.TotalPages??1;qs('#oi-report-range').textContent=`${total} matching record(s)`;qs('#oi-report-page').textContent=`Page ${page} of ${pages}`;qs('#oi-report-prev').disabled=page<=1;qs('#oi-report-next').disabled=page>=pages;
 }catch(err){tbody.replaceChildren();const tr=document.createElement('tr'),cell=document.createElement('td');cell.className='oi-empty';cell.textContent=err?.message||'Unable to load report.';tr.append(cell);tbody.append(tr);}
 };
 kind.addEventListener('change',()=>{page=1;draw()});qs('#oi-report-size').addEventListener('change',()=>{page=1;draw()});for(const id of ['oi-report-from','oi-report-to'])qs('#'+id)?.addEventListener('change',()=>{page=1;draw()});qs('#oi-report-search').addEventListener('input',()=>{page=1;clearTimeout(timeout);timeout=setTimeout(draw,250)});
 qs('#oi-report-prev').addEventListener('click',()=>{page=Math.max(1,page-1);draw()});qs('#oi-report-next').addEventListener('click',()=>{page+=1;draw()});draw();
}
function initPage(){initRequest();initMovement();initReports();}
function printTable(tableId,title){const table=document.getElementById(tableId);if(!table)return;const rows=qsa('tr',table).filter(r=>!r.hidden).map(r=>qsa('th,td',r).filter(c=>!c.classList.contains('oi-row-actions')).map(c=>c.innerText));const hotel=qs('.oi-hotel-label strong')?.textContent||'Hotel';const html=`<!doctype html><html><head><meta charset="utf-8"><title>${esc(title)}</title><style>body{font-family:Arial;color:#193756;margin:28px}h1{text-align:center;font-size:28px}h2{text-align:center;font-size:18px}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:8px;border:1px solid #dce4ed;font-size:11px}th{background:#eef2f9}@media print{button{display:none}}</style></head><body><h1>${esc(hotel)}</h1><h2>${esc(title)}</h2><button onclick="print()">Print</button><table>${rows.map((r,i)=>`<tr>${r.map(c=>`<${i===0?'th':'td'}>${esc(c)}</${i===0?'th':'td'}>`).join('')}</tr>`).join('')}</table></body></html>`;
 const win=window.open('','_blank');if(win)win.opener=null;if(!win){feedback('Please allow print popups for this site.',true);return}win.document.open();win.document.write(html);win.document.close();}
function exportTable(tableId){const table=document.getElementById(tableId);if(!table)return;const lines=qsa('tr',table).filter(r=>!r.hidden).map(row=>qsa('th,td',row).filter(c=>!c.classList.contains('oi-row-actions')).map(c=>{let val=c.innerText.trim();if(/^[=+\-@]/.test(val))val="'"+val;return '"'+val.replace(/"/g,'""')+'"'}).join(','));const blob=new Blob(['\ufeff'+lines.join('\r\n')],{type:'text/csv;charset=utf-8'});const link=document.createElement('a');link.href=URL.createObjectURL(blob);link.download='ORA_Suppliers.csv';link.click();setTimeout(()=>URL.revokeObjectURL(link.href),1000);}
document.addEventListener('click',event=>{const btn=event.target.closest('[data-open-dialog],[data-close-dialog],[data-dashboard-tab],[data-remove-line],#oi-add-request-line,[data-print-table],[data-export-table]');if(!btn||!root()?.contains(btn))return;
 if(btn.dataset.openDialog){event.preventDefault();openDialog(btn.dataset.openDialog,btn);return;}
 if(btn.hasAttribute('data-close-dialog')){event.preventDefault();btn.closest('dialog')?.close();return;}
 if(btn.dataset.dashboardTab){const which=btn.dataset.dashboardTab;qsa('[data-dashboard-panel]').forEach(p=>p.hidden=p.dataset.dashboardPanel!==which);qsa('[data-dashboard-tab]').forEach(b=>b.classList.toggle('active',b.dataset.dashboardTab===which));return;}
 if(btn.id==='oi-add-request-line'){const holder=qs('#oi-request-lines'),first=qs('[data-line]',holder);if(!first||qsa('[data-line]',holder).length>=40)return;const clone=first.cloneNode(true);qsa('select',clone).forEach(s=>s.selectedIndex=0);qsa('input',clone).forEach(i=>i.value='1');holder.append(clone);qsa('[data-line]',holder).forEach((line,i)=>qsa('[name]',line).forEach(n=>n.name=n.name.replace(/Lines\[\d+\]/,`Lines[${i}]`)));return;}
 if(btn.hasAttribute('data-remove-line')){const holder=qs('#oi-request-lines');if(qsa('[data-line]',holder).length>1){btn.closest('[data-line]').remove();qsa('[data-line]',holder).forEach((line,i)=>qsa('[name]',line).forEach(n=>n.name=n.name.replace(/Lines\[\d+\]/,`Lines[${i}]`)));}return;}
 if(btn.dataset.printTable){printTable(btn.dataset.printTable,btn.dataset.printTitle||'Inventory register');return;}
 if(btn.dataset.exportTable){event.preventDefault();exportTable(btn.dataset.exportTable);return;}
});
document.addEventListener('submit',event=>{const form=event.target;if(!form.matches('.oi-ajax-form')||!root()?.contains(form))return;event.preventDefault();if(form.dataset.confirm&&!window.confirm(form.dataset.confirm))return;submit(form,event.submitter);});
document.addEventListener('input',event=>{const node=event.target;if(!node.dataset.filterTable||!root()?.contains(node))return;const table=document.getElementById(node.dataset.filterTable),q=node.value.toLowerCase();qsa('tbody tr',table).forEach(tr=>tr.hidden=!tr.textContent.toLowerCase().includes(q));});
initPage();
})();
