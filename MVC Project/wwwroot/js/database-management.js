(() => {
    const root = document.querySelector('.dbm');
    if (!root) return;
    const search = root.querySelector('#dbm-search'), from = root.querySelector('#dbm-from'), to = root.querySelector('#dbm-to');
    const rows = [...root.querySelectorAll('tr[data-backup]')];
    const filter = () => {
        let visible = 0;
        for (const row of rows) {
            const date = row.dataset.date;
            const match = row.dataset.backup.toLowerCase().includes(search.value.trim().toLowerCase()) && (!from.value || date >= from.value) && (!to.value || date <= to.value);
            row.hidden = !match;
            if (match) row.cells[0].textContent = String(++visible);
        }
        root.querySelector('#dbm-empty').hidden = visible !== 0;
        root.querySelector('#dbm-count').textContent = `${visible} of ${rows.length} records`;
    };
    [search, from, to].forEach(el => el.addEventListener('input', filter));
    root.querySelector('#dbm-clear').addEventListener('click', () => { search.value = from.value = to.value = ''; filter(); });
    const form = root.querySelector('.dbm-backup');
    const button = form.querySelector('button');
    const dialog = root.querySelector('#dbm-progress');
    const find = id => root.querySelector('#dbm-' + id);
    const bar = find('progress-bar'), pct = find('progress-pct'), stage = find('progress-stage');
    let running = false;
    let lastPct = null;
    dialog.addEventListener('cancel', event => event.preventDefault());
    const warnUnload = event => { if (running) { event.preventDefault(); event.returnValue = ''; } };
    const updateProgress = data => {
        if (!running || !data.active) return;
        if (data.finalizing) {
            stage.textContent = 'Finalizing backup…';
            bar.removeAttribute('value');
            pct.textContent = '—';
            return;
        }
        stage.textContent = data.started ? 'Writing backup file…' : 'Preparing backup…';
        if (Number.isInteger(data.percent) && data.percent >= 0 && data.percent < 100) {
            lastPct = Math.max(lastPct ?? 0, data.percent);
            bar.value = lastPct;
            pct.textContent = lastPct + '%';
        }
    };
    form.addEventListener('submit', async event => {
        if (running || button.disabled || !window.confirm('Create a full database backup? This includes every hotel.')) {
            event.preventDefault(); return;
        }
        // Preserve the original normal POST if enhanced browser features are unavailable.
        if (!window.fetch || !window.crypto?.randomUUID || !dialog.showModal || !root.dataset.progressUrl) {
            button.disabled = true; button.textContent = 'Creating backup…'; return;
        }
        event.preventDefault();
        const operationId = crypto.randomUUID();
        const body = new FormData(form); // includes Razor antiforgery token
        body.set('operationId', operationId);
        const progressUrl = new URL(root.dataset.progressUrl, window.location.href);
        progressUrl.searchParams.set('operationId', operationId);
        running = true;
        button.disabled = true;
        dialog.dataset.state = 'running';
        dialog.showModal();
        window.addEventListener('beforeunload', warnUnload);
        const started = Date.now();
        const elapsed = () => {
            const sec = Math.floor((Date.now() - started) / 1000);
            find('elapsed').textContent = String(Math.floor(sec / 60)).padStart(2, '0') + ':' + String(sec % 60).padStart(2, '0');
        };
        const timer = setInterval(elapsed, 1000);
        let pollTimer, pollController;
        const poll = async () => {
            if (!running) return;
            pollController = new AbortController();
            const timeout = setTimeout(() => pollController.abort(), 8000);
            try {
                const response = await fetch(progressUrl, { credentials: 'same-origin', cache: 'no-store', signal: pollController.signal });
                if (response.ok && !response.redirected && (response.headers.get('content-type') || '').includes('application/json')) {
                    updateProgress(await response.json());
                }
            } catch { /* The backup POST remains authoritative, never retry it automatically. */ }
            finally {
                clearTimeout(timeout);
                if (running) pollTimer = setTimeout(poll, 1000);
            }
        };
        pollTimer = setTimeout(poll, 350);
        const finish = (success, message) => {
            running = false;
            elapsed(); clearInterval(timer); clearTimeout(pollTimer); pollController?.abort();
            window.removeEventListener('beforeunload', warnUnload);
            dialog.dataset.state = success ? 'success' : 'error';
            find('progress-title').textContent = success ? 'Backup completed' : 'Backup not confirmed';
            find('progress-icon').className = success ? 'fa-solid fa-circle-check' : 'fa-solid fa-triangle-exclamation';
            stage.textContent = success ? 'Backup file created' : 'Check backup status';
            if (success) { bar.value = 100; pct.textContent = '100%'; }
            else { bar.value = lastPct ?? 0; pct.textContent = lastPct === null ? '—' : lastPct + '%'; }
            find('progress-detail').textContent = message;
            find('progress-wait').hidden = true;
            find('progress-done').hidden = false;
            find('progress-done').focus();
        };
        try {
            const response = await fetch(form.action, { method: 'POST', body, credentials: 'same-origin', headers: { 'X-ORA-Backup-Progress': '1', 'Accept': 'application/json' } });
            if (response.redirected || !(response.headers.get('content-type') || '').includes('application/json'))
                throw new Error('Backup response could not be confirmed.');
            const data = await response.json();
            finish(response.ok && data.success === true, typeof data.message === 'string' ? data.message : 'Check backup history and SQL Server logs before retrying.');
        } catch {
            finish(false, 'The server response was interrupted or unavailable. The backup may still be running or may have completed. Refresh history and check SQL Server before retrying.');
        }
    });
})();
