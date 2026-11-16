(()=>{
  'use strict';
  const root=document.getElementById('hotel-transfer');if(!root)return;
  const $=id=>document.getElementById(id);
  const token=document.querySelector('#ht-antiforgery input[name="__RequestVerificationToken"]')?.value||'';
  const status=$('ht-status'),previewBtn=$('ht-preview'),startBtn=$('ht-start');
  let preview=null, running=false;
  const resumeBtn=$('ht-resume');
  const demoId='638935275363396747';
  const demoPhrase='DELETE DEMO HOTEL '+demoId;
  let resetPreview=null, demoBusy=false;
  const storageKey='ora.pms.hoteltransfer.active';
  const saved=()=>{try{return JSON.parse(localStorage.getItem(storageKey)||'null');}catch{return null;}};
  const last=saved();
  if(last?.batchId && last?.destinationHotelId)resumeBtn.hidden=false;
  const selected=()=>Array.from(document.querySelectorAll('input[name="ht-area"]:checked')).map(x=>Number(x.value));
  const model=()=>({sourceHotelId:$('ht-source').value,destinationHotelId:$('ht-dest').value,
    fromDate:$('ht-from').value,toDate:$('ht-to').value,areas:selected(),
    replaceMatching:$('ht-replace').checked,confirmDestinationId:$('ht-confirm-id').value.trim()});
  const validate=r=>{
    if(!r.sourceHotelId||r.destinationHotelId!==demoId||r.sourceHotelId===demoId)
      throw Error('Select a valid source. Transfers may only be copied to the demo hotel.');
    if(!r.fromDate||!r.toDate||r.fromDate>r.toDate)throw Error('Choose a valid From and To date.');
    if(!r.areas.length)throw Error('Select at least one module.');
    if(r.replaceMatching&&!([3,4,5,6,7,8].every(x=>r.areas.includes(x))))throw Error('Replacement requires all six dated modules.');
  };
  const invalidate=()=>{preview=null;startBtn.disabled=true;$('ht-preview-panel').hidden=true;};
  for(const el of root.querySelectorAll('select,input'))el.addEventListener('change',()=>{
    if(running)return;
    invalidate();if(el.id==='ht-replace')$('ht-delete-confirm').hidden=!el.checked;
  });
  const request=async(url,data)=>{
    const resp=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':token},
      credentials:'same-origin',body:JSON.stringify(data)});
    const json=await resp.json().catch(()=>({}));
    if(!resp.ok)throw Error(json.error||'Server request failed ('+resp.status+').');
    return json;
  };
  function showProgress(p){
    $('ht-progress-panel').hidden=false;
    $('ht-percent').textContent=p.percent+'%';$('ht-bar').style.width=p.percent+'%';
    for(const id of ['processed','copied','skipped','failed'])$('ht-'+id).textContent=p[id];
    status.textContent=p.status+' — '+p.processed+' / '+p.total+' processed';
  }
  function addLogs(messages){
    const out=$('ht-log');
    for(const msg of messages){const line=document.createElement('div');line.textContent=msg;out.appendChild(line);}
    out.scrollTop=out.scrollHeight;
  }
  previewBtn.addEventListener('click',async()=>{
    try{
      const r=model();validate(r);status.textContent='Checking selected records...';
      previewBtn.disabled=true;
      const p=await request(root.dataset.preview,r);
      preview=p;startBtn.disabled=p.total===0;
      const tbody=$('ht-counts');tbody.replaceChildren();
      for(const c of p.counts){const tr=document.createElement('tr');
        for(const v of [c.area,c.sourceRows,c.existingDestinationRows]){
          const td=document.createElement('td');td.textContent=String(v);tr.appendChild(td);
        }tbody.appendChild(tr);
      }
      const warning=$('ht-warnings');warning.replaceChildren();
      for(const w of p.warnings){const pEl=document.createElement('p');pEl.textContent=w;warning.appendChild(pEl);}
      $('ht-preview-panel').hidden=false;
      status.textContent='Preview ready — '+p.total+' transfer units';
    }catch(ex){status.textContent=ex.message;startBtn.disabled=true;}
    finally{previewBtn.disabled=false;}
  });
  async function consumeBatch(p,destination){
    showProgress(p);
    $('ht-log').replaceChildren();
    while(p.status==='Running'){
      const step=await request(root.dataset.step,{batchId:p.batchId,destinationHotelId:destination});
      Object.assign(p,step.progress);showProgress(p);addLogs(step.messages||[]);
      await new Promise(resolve=>setTimeout(resolve,25));
    }
    if(p.status!=='Running'){
      localStorage.removeItem(storageKey);
      resumeBtn.hidden=true;
    }
  }
  startBtn.addEventListener('click',async()=>{
    if(!preview||running)return;
    const r=model();
    try{
      validate(r);
      if(r.replaceMatching){
        if(r.confirmDestinationId!==r.destinationHotelId)throw Error('Type the exact destination Hotel ID before deleting records.');
        if(!window.confirm('Delete and replace only MATCHING destination reservation numbers? This cannot be undone. Continue?'))return;
      }else if(!window.confirm('Start copying the previewed records to the selected destination hotel?'))return;
      running=true;startBtn.disabled=true;previewBtn.disabled=true;
      for(const el of root.querySelectorAll('select,input'))el.disabled=true;
      const p=await request(root.dataset.start,r);
      localStorage.setItem(storageKey,JSON.stringify({batchId:p.batchId,destinationHotelId:r.destinationHotelId}));
      resumeBtn.hidden=false;
      await consumeBatch(p,r.destinationHotelId);
    }catch(ex){status.textContent='Stopped: '+ex.message+' — check progress or server logs before retrying.';}
    finally{
      running=false;previewBtn.disabled=false;
      for(const el of root.querySelectorAll('select,input'))el.disabled=false;
      invalidate();
    }
  });
  // A full-demo reset is a separate, explicitly confirmed operation. It does not
  // trigger a transfer or delete any source-hotel records.
  const demoPreviewBtn=$('ht-demo-preview');
  const demoEnable=$('ht-demo-enable');
  const demoExecuteBtn=$('ht-demo-execute');
  demoEnable.addEventListener('change',()=>{
    demoPreviewBtn.disabled=!demoEnable.checked || demoBusy || running;
    resetPreview=null;
    $('ht-demo-confirm-panel').hidden=true;
    demoExecuteBtn.disabled=true;
    demoStatus.textContent='';
  });
  const demoStatus=$('ht-demo-status');
  const demoPhraseInput=$('ht-demo-phrase');
  const demoBackup=$('ht-demo-backup');
  const demoResetValid=()=>resetPreview?.destinationHotelId===demoId &&
    demoEnable.checked && demoBackup.checked && demoPhraseInput.value.trim()===demoPhrase && !running && !demoBusy;
  for(const el of [demoPhraseInput,demoBackup])el.addEventListener('input',()=>{
    demoExecuteBtn.disabled=!demoResetValid();
  });
  demoBackup.addEventListener('change',()=>{demoExecuteBtn.disabled=!demoResetValid();});
  demoPreviewBtn.addEventListener('click',async()=>{
    if(running||demoBusy||!demoEnable.checked)return;
    demoBusy=true;demoPreviewBtn.disabled=true;demoExecuteBtn.disabled=true;
    resetPreview=null;$('ht-demo-confirm-panel').hidden=true;
    try{
      demoStatus.textContent='Reading demo hotel record counts...';
      const result=await request(root.dataset.demoPreview,{});
      if(result.destinationHotelId!==demoId)throw Error('Unexpected reset destination.');
      resetPreview=result;demoPhraseInput.value='';demoBackup.checked=false;
      $('ht-demo-total').textContent=Number(result.totalRows).toLocaleString()+' rows';
      const container=$('ht-demo-counts');container.replaceChildren();
      for(const entry of result.tables){
        const line=document.createElement('div');
        const label=document.createElement('span');label.textContent=entry.table+(entry.exists?'':' (not present)');
        const number=document.createElement('strong');number.textContent=Number(entry.rows).toLocaleString();
        line.append(label,number);container.appendChild(line);
      }
      $('ht-demo-confirm-panel').hidden=false;
      demoStatus.textContent='Preview ready. Confirm only after making a backup. This preview expires in 15 minutes.';
    }catch(ex){demoStatus.textContent=ex.message;}
    finally{demoBusy=false;demoPreviewBtn.disabled=!demoEnable.checked;}
  });
  demoExecuteBtn.addEventListener('click',async()=>{
    if(!demoResetValid())return;
    if(!window.confirm('Permanently delete ALL records in the 15 listed tables for DEMO hotel '+demoId+'? This is NOT limited to the selected dates and cannot be undone.'))return;
    demoBusy=true;demoExecuteBtn.disabled=true;demoPreviewBtn.disabled=true;
    try{
      demoStatus.textContent='Deleting demo hotel records in one SQL transaction...';
      const result=await request(root.dataset.demoReset,{
        token:resetPreview.token,destinationHotelId:demoId,
        confirmation:demoPhraseInput.value.trim(),backupConfirmed:demoBackup.checked
      });
      demoStatus.textContent=result.message+' Deleted rows: '+Number(result.deletedRows).toLocaleString()+'.';
      resetPreview=null;$('ht-demo-confirm-panel').hidden=true;
      localStorage.removeItem(storageKey);
      resumeBtn.hidden=true;
      invalidate();status.textContent='Demo reset completed. Run a new transfer preview to continue.';
    }catch(ex){
      resetPreview=null;$('ht-demo-confirm-panel').hidden=true;
      demoStatus.textContent='Reset NOT confirmed: '+ex.message+' Check database and logs before retrying.';
    }finally{demoBusy=false;demoPreviewBtn.disabled=!demoEnable.checked;}
  });
  resumeBtn.addEventListener('click',async()=>{
    const last=saved();if(!last||running)return;
    try{
      running=true;resumeBtn.disabled=true;
      const url=root.dataset.progress+'?batchId='+encodeURIComponent(last.batchId)+
          '&destinationHotelId='+encodeURIComponent(last.destinationHotelId);
      const resp=await fetch(url,{credentials:'same-origin'});
      if(!resp.ok)throw Error('Cannot recover this batch.');
      const progress=await resp.json();
      await consumeBatch(progress,last.destinationHotelId);
    }catch(ex){status.textContent=ex.message;}
    finally{running=false;resumeBtn.disabled=false;}
  });
})();
