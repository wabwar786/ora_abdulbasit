/* Local fiscal transport. No SQL credentials, no installer. Pilot: requires fiscal CORS support. */
(() => {
  'use strict';
  const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
  async function send(url, token, data) {
    return fetch(url, { method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: token,
        'X-Requested-With': 'XMLHttpRequest' }, body: JSON.stringify(data) });
  }
  async function execute(job) {
    const result = { id: job.id, connected: false, status: 0, body: '', error: '' };
    try {
      const url = new URL(job.url);
      if (url.hostname !== '127.0.0.1' || !['http:', 'https:'].includes(url.protocol) || url.username || url.password)
        throw new Error('Invalid local fiscal address.');
      if (!['probe', 'post'].includes(job.kind) || Date.parse(job.expires) <= Date.now()) throw new Error('Local job expired or invalid.');
      // HEAD never submits an invoice. A readable HTTP error still proves browser access to the endpoint.
      // POST is attempted at most once. A failed/blocked POST is always uncertain.
      const post = job.kind === 'post';
      const response = await fetch(url.href, { method: post ? 'POST' : 'HEAD', mode: 'cors',
        credentials: 'omit', redirect: 'error', cache: 'no-store',
        ...(post ? { headers: { 'Content-Type': 'application/json' }, body: job.body } : {}),
        signal: AbortSignal.timeout(post ? 40000 : 12000) });
      result.status = response.status;
      if (post) {
        const raw = await response.text();
        if (raw.length > 1000000) throw new Error('Fiscal response too large.');
        result.body = raw;
      }
      result.connected = true;
    } catch (error) {
      result.error = job.kind === 'probe'
        ? 'Local fiscal service is unavailable or browser access is blocked (CORS, local-network permission, TLS or port). No invoice was sent.'
        : 'Local fiscal POST outcome is uncertain. Verify the fiscal portal before retrying.';
    }
    return result;
  }
  window.oraFbrLocal = { async retry(app, token, payload) {
    if (!window.isSecureContext || !crypto.randomUUID) throw new Error('HTTPS is required for local fiscal posting.');
    const attemptId = crypto.randomUUID();
    let finished = false;
    const retry = send(app.dataset.browserRetryUrl, token, { ...payload, attemptId }).finally(() => { finished = true; });
    // Attach a rejection handler immediately so a disconnected request does not become unhandled.
    retry.catch(() => {});
    try {
      while (!finished) {
        const poll = await send(app.dataset.browserJobUrl, token, { attemptId });
        if (poll.redirected || !poll.ok) throw new Error('Local report session or connection failed.');
        if (poll.status !== 204) {
          const job = await poll.json();
          if (finished) break;
          const result = await execute(job);
          const reply = await send(app.dataset.browserReplyUrl, token, { ...result, attemptId });
          if (!reply.ok || reply.redirected) throw new Error('Unable to record local response. Verify fiscal portal before retrying.');
        }
        if (!finished) await delay(350);
      }
      return await retry;
    } catch (error) {
      // Do not send another fiscal POST. Wait for the original server attempt to finish its audit/timeout.
      try { await retry; } catch (_) { }
      throw error;
    }
  } };
})();
