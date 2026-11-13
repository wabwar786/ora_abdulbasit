(() => {
  'use strict';
  const root = document.getElementById('importdata-app');
  if (!root) return;
  const $ = id => document.getElementById(id);
  const token = document.querySelector('#imp-antiforgery input[name="__RequestVerificationToken"]')?.value;
  const hotelPicker = $('imp-hotel');
  const selectedHotel = () => hotelPicker?.value?.trim() || '';
  const withHotel = (url, extra = {}) => {
    const query = new URLSearchParams({ hotelId: selectedHotel(), ...extra });
    return url + (url.includes('?') ? '&' : '?') + query.toString();
  };
  hotelPicker?.addEventListener('change', () => {
    if (busy) return;
    // Reload to show the newly selected hotel's own import history.
    window.location.assign(withHotel(window.location.pathname));
  });
  let kind = 'Rooms';
  let activeBatch = null;
  let ready = 0;
  let busy = false;
  const labels = {Rooms:'Rooms + Categories',Reservations:'Reservations',CheckIns:'Check-ins',CheckOuts:'Check-outs',Payments:'Payments',Cancellations:'Cancellations',NoShows:'No-shows'};
  let progressTimer = null;
  let progressPending = false;
  const updateProgress = data => {
    const percent = Math.max(0, Math.min(100, Number(data.percent) || 0));
    $('imp-live-progress').hidden = false;
    $('imp-progress-bar').value = percent;
    $('imp-progress-percent').textContent = percent + '%';
    $('imp-progress-processed').textContent = Number(data.processed) || 0;
    $('imp-progress-total').textContent = Number(data.eligibleRows) || 0;
    $('imp-progress-imported').textContent = Number(data.imported) || 0;
    $('imp-progress-failed').textContent = Number(data.failed) || 0;
    $('imp-progress-remaining').textContent = Number(data.remaining) || 0;
    $('imp-progress-skipped').textContent = Number(data.skipped) || 0;
    if (data.status === 'Completed' || (data.remaining === 0 && data.status !== 'Processing'))
      $('imp-progress-note').textContent = 'Finished. See the import preview for row-level outcomes.';
    else
      $('imp-progress-note').textContent = 'Processing validated rows. Do not close this tab.';
  };
  const checkProgress = async () => {
    if (progressPending || !activeBatch || !selectedHotel()) return;
    progressPending = true;
    try {
      const response = await fetch(withHotel(root.dataset.progressUrl, { id: activeBatch }), { credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) return;
      const body = await response.json();
      if (body.ok && body.progress) updateProgress(body.progress);
    } catch { /* transient network failure; the next poll or final refresh retries */ }
    finally { progressPending = false; }
  };
  const stopProgress = () => {
    if (progressTimer !== null) { clearInterval(progressTimer); progressTimer = null; }
  };
  const message = (text, error = false) => {
    const target = $('imp-msg'); target.hidden = !text;
    target.textContent = text || ''; target.classList.toggle('is-error', error);
  };
  function resetPreview() {
    stopProgress();
    $('imp-live-progress').hidden = true;
    activeBatch = null; ready = 0;
    $('imp-preview-area').hidden = true;
    $('imp-confirm-text').value = '';
    $('imp-commit-btn').disabled = true;
    $('imp-issues').hidden = true;
  }
  function choose(next) {
    if (busy || !labels[next]) return;
    kind = next;
    document.querySelectorAll('.imp-type').forEach(b => {
      const selected = b.dataset.kind === kind;
      b.classList.toggle('is-selected', selected);
      b.setAttribute('aria-pressed', String(selected));
    });
    $('imp-selected-kind').value = labels[next];
    $('imp-checkout-hint').hidden = next !== 'CheckOuts';
    $('imp-template').href = withHotel(root.dataset.templateUrl, { kind: next });
    $('imp-file').value = '';
    $('imp-filename').textContent = 'Choose an Excel or CSV file';
    message(''); resetPreview();
  }
  document.querySelectorAll('.imp-type').forEach(button => button.addEventListener('click', () => choose(button.dataset.kind)));
  $('imp-file').addEventListener('change', () => {
    $('imp-filename').textContent = $('imp-file').files[0]?.name || 'Choose an Excel or CSV file';
    resetPreview(); message('');
  });
  $('imp-clear').addEventListener('click', () => choose(kind));
  $('imp-confirm-text').addEventListener('input', () => {
    $('imp-commit-btn').disabled = busy || !selectedHotel() || !activeBatch || ready <= 0 || $('imp-confirm-text').value !== 'IMPORT';
  });
  const api = async (url, data, isFile = false) => {
    const headers = { RequestVerificationToken: token || '' };
    const response = await fetch(url, { method: 'POST', credentials: 'same-origin', headers, body: data });
    const text = await response.text();
    let result;
    try { result = JSON.parse(text); } catch { throw new Error('Server returned an unexpected response. Please sign in again if the session expired.'); }
    if (!response.ok || !result.ok) throw new Error(result.message || 'Request failed.');
    return result;
  };
  const cell = (row, text) => {
    const td = document.createElement('td'); td.textContent = text ?? ''; row.append(td); return td;
  };
  function displayBatch(batch) {
    activeBatch = batch.batchId;
    ready = batch.ready;
    $('imp-preview-area').hidden = false;
    $('imp-preview-kind').textContent = labels[batch.kind] || batch.kind;
    $('imp-ready').textContent = batch.ready;
    $('imp-invalid').textContent = batch.invalid;
    $('imp-duplicates').textContent = batch.duplicates;
    const tbody = $('imp-preview-rows'); tbody.replaceChildren();
    for (const record of (batch.rows || []).slice(0, 100)) {
      const tr = document.createElement('tr');
      cell(tr, record.number); cell(tr, record.key); cell(tr, record.summary);
      const badge = cell(tr, '');
      const span = document.createElement('span'); span.className = 'imp-status';
      span.dataset.state = record.status; span.textContent = record.status; badge.append(span);
      cell(tr, record.message || (record.targetRegId ? `ORA #${record.targetRegId}` : '—'));
      tbody.append(tr);
    }
    $('imp-confirm-text').value = '';
    $('imp-commit-btn').disabled = true;
    $('imp-issues').href = withHotel(root.dataset.issuesUrl, { id: activeBatch });
    $('imp-issues').hidden = !(batch.invalid || batch.duplicates || (batch.rows || []).some(x => x.status === 'Failed'));
  }
  const setBusy = state => {
    busy = state;
    $('imp-preview-btn').disabled = state || !selectedHotel();
    if (hotelPicker) hotelPicker.disabled = state;
    $('imp-commit-btn').disabled = state || !selectedHotel() || !activeBatch || ready === 0 || $('imp-confirm-text').value !== 'IMPORT';
    document.querySelectorAll('.imp-type').forEach(b => b.disabled = state);
  };
  $('imp-preview-form').addEventListener('submit', async e => {
    e.preventDefault(); if (busy) return;
    if (!selectedHotel()) return message('Select a hotel first.', true);
    const file = $('imp-file').files[0];
    if (!file) return message('Select a CSV or Excel file.', true);
    if (file.size > 5 * 1024 * 1024) return message('Maximum file size is 5 MB.', true);
    setBusy(true); resetPreview(); message('Validating rows…');
    try {
      const form = new FormData();
      form.append('file', file); form.append('kind', kind);
      form.append('source', $('imp-source').value.trim());
      form.append('hotelId', selectedHotel());
      const result = await api(root.dataset.previewUrl, form, true);
      displayBatch(result.batch);
      message(result.batch.ready ? `${result.batch.ready} row(s) ready. Review before importing.` : 'No importable rows. Correct the highlighted errors.', !result.batch.ready);
    } catch (err) { message(err.message, true); }
    finally { setBusy(false); }
  });
  $('imp-commit-btn').addEventListener('click', async () => {
    if (busy || !activeBatch || ready <= 0 || $('imp-confirm-text').value !== 'IMPORT') return;
    setBusy(true); message('Import started. Progress updates as records are saved…');
    const batch = activeBatch;
    const form = new FormData();
    form.append('id', batch); form.append('confirmation', 'IMPORT'); form.append('hotelId', selectedHotel());
    updateProgress({ processed: 0, eligibleRows: ready, remaining: ready, imported: 0, failed: 0, skipped: 0, percent: 0, status: 'Processing' });
    progressTimer = setInterval(checkProgress, 700);
    let completedNormally = false;
    try {
      let remaining = ready;
      do {
        const result = await api(root.dataset.commitUrl, form);
        remaining = result.result.remaining;
        await checkProgress();
      } while (remaining > 0);
      completedNormally = true;
      await checkProgress();
      const url = withHotel(root.dataset.batchUrl, { id: batch });
      const response = await fetch(url, { credentials:'same-origin', cache:'no-store' });
      const detail = await response.json();
      if (response.ok && detail.ok) displayBatch(detail.batch);
      $('imp-commit-btn').disabled = true;
      const imported = Number($('imp-progress-imported').textContent) || 0;
      const failed = Number($('imp-progress-failed').textContent) || 0;
      message(`${imported} imported; ${failed} failed. Review row details and import history.`, failed > 0);
    } catch (err) {
      await checkProgress();
      message(`${err.message} Any already committed rows are preserved. Check import history before retrying.`, true);
    } finally {
      stopProgress();
      if (!completedNormally) $('imp-progress-note').textContent = 'Interrupted. Review the import history before resuming.';
      setBusy(false);
    }
  });
  document.querySelectorAll('[data-view-batch]').forEach(button => button.addEventListener('click', async () => {
    if (busy) return;
    try {
      const res = await fetch(withHotel(root.dataset.batchUrl, { id: button.dataset.viewBatch }), {credentials:'same-origin'});
      const data = await res.json();
      if (!res.ok || !data.ok) throw new Error('Unable to open import history.');
      displayBatch(data.batch);
      $('imp-preview-area').scrollIntoView({behavior:'smooth',block:'nearest'});
      message('Loaded saved import batch.');
    } catch (err) { message(err.message, true); }
  }));
  $('imp-refresh-history').addEventListener('click', () => window.location.reload());
  choose(kind);
  $('imp-preview-btn').disabled = !selectedHotel();
})();
