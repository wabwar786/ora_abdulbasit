/* =========================================================
   PDQ card payment.
   The same flow as the old page: create a PaymentIntent, hand it to the
   reader, poll until Stripe is done, cancel on demand, present a test card
   on a test account. Only the addresses changed: the ASP.NET PageMethods
   are now this controller's POST endpoints, every one of them carrying the
   anti-forgery token. No library, nothing global.
   ========================================================= */
(function () {
    'use strict';

    var root = document.querySelector('.tcp-root');
    if (!root) return;

    function $(id) { return document.getElementById(id); }
    function val(id) { var el = $(id); return el ? String(el.value || '').trim() : ''; }

    // Keep the page working even if an older Razor view renders one of the data-*
    // endpoint attributes empty. This directly fixes the screenshot error:
    // "Create PI error: Payment endpoint is missing from the page."
    var endpointFallbacks = {
        start: '/terminalcardpayment/Start',
        create: '/terminalcardpayment/Create',
        process: '/terminalcardpayment/Process',
        status: '/terminalcardpayment/Status',
        cancelaction: '/terminalcardpayment/CancelAction',
        cancelintent: '/terminalcardpayment/CancelIntent',
        simulate: '/terminalcardpayment/Simulate',
        faillog: '/terminalcardpayment/FailLog'
    };
    function url(name) {
        var fromPage = root.getAttribute('data-' + name + '-url') || '';
        return String(fromPage || endpointFallbacks[name] || '').trim();
    }
    function enable(id, on) { var el = $(id); if (el) el.disabled = !on; }
    function isHold() { return val('q_mode').toLowerCase() === 'hold'; }
    function actionWord() { return isHold() ? 'hold' : 'charge'; }

    var token = (function () {
        var el = root.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : '';
    })();

    /* one place for every call, so the token and response handling are never forgotten.
       IMPORTANT: read as text first. MVC/auth/error middleware can return an HTML page.
       Calling response.json() directly on that HTML caused:
       Unexpected token '<', "<!DOCTYPE "... is not valid JSON */
    function post(address, fields) {
        if (!address) {
            return Promise.reject(new Error('Payment endpoint is missing from the page.'));
        }

        var body = new FormData();
        body.append('__RequestVerificationToken', token);
        Object.keys(fields || {}).forEach(function (k) {
            if (fields[k] !== null && fields[k] !== undefined) body.append(k, fields[k]);
        });

        return fetch(address, {
            method: 'POST',
            body: body,
            credentials: 'same-origin',
            headers: {
                'Accept': 'application/json',
                'RequestVerificationToken': token,
                'X-Requested-With': 'XMLHttpRequest'
            }
        }).then(function (r) {
            return r.text().then(function (raw) {
                var data = null;
                var text = String(raw || '').trim();

                if (text) {
                    try { data = JSON.parse(text); }
                    catch (ignore) { data = null; }
                }

                if (r.status === 401 || r.status === 403) {
                    throw new Error('Your session has ended. Please sign in again.');
                }

                if (!r.ok) {
                    throw new Error(
                        data && data.message
                            ? data.message
                            : 'The server did not answer correctly (' + r.status + ').'
                    );
                }

                if (!data) {
                    var contentType = (r.headers.get('content-type') || '').toLowerCase();
                    if (r.redirected || contentType.indexOf('text/html') >= 0 || text.indexOf('<!DOCTYPE') === 0 || text.indexOf('<html') === 0) {
                        throw new Error(
                            r.redirected
                                ? 'The request was redirected to an HTML page. Your login/session may have expired.'
                                : 'The server returned an HTML error page instead of JSON. Check the application log for the Simulate endpoint.'
                        );
                    }
                    throw new Error('The server returned an invalid response instead of JSON.');
                }

                return data;
            });
        });
    }

    var latestPaymentIntentId = null;
    var countdown = null;
    var poller = null;
    var left = 0;
    var TIMEOUT_SECONDS = 90;

    // ---------------------------------------------------------- activity log
    function logln(msg, cls) {
        var box = $('log');
        if (!box) return;
        var empty = box.querySelector('.tcp-log-empty');
        if (empty) empty.remove();

        var line = document.createElement('div');
        if (cls) line.className = 'is-' + cls;
        line.classList.add('is-new');
        var time = document.createElement('time');
        time.textContent = '[' + new Date().toLocaleTimeString() + ']';
        line.appendChild(time);
        line.appendChild(document.createTextNode(' ' + msg));   /* textContent only: never HTML */
        box.appendChild(line);
        box.scrollTop = box.scrollHeight;

        var pill = $('logCount');
        if (pill) {
            var n = box.querySelectorAll('div:not(.tcp-log-empty)').length;
            pill.textContent = n;
            pill.classList.toggle('is-live', n > 0);
        }
    }

    function wireLogTools() {
        var copy = $('logCopy'), expand = $('logExpand'), box = $('log'), wrap = $('logWrap');
        if (copy) {
            copy.addEventListener('click', function () {
                var text = Array.prototype.map.call(box.querySelectorAll('div:not(.tcp-log-empty)'),
                    function (d) { return d.textContent; }).join(String.fromCharCode(10));
                if (!text) return;
                var said = function () {
                    copy.classList.add('is-said');
                    setTimeout(function () { copy.classList.remove('is-said'); }, 1200);
                };
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(text).then(said, said);
                } else {
                    var ta = document.createElement('textarea');
                    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
                    document.body.appendChild(ta); ta.select();
                    try { document.execCommand('copy'); } catch (e) { }
                    document.body.removeChild(ta); said();
                }
            });
        }
        if (expand) {
            expand.addEventListener('click', function () {
                var open = box.classList.toggle('is-tall');
                expand.classList.toggle('is-open', open);
                expand.setAttribute('aria-expanded', open ? 'true' : 'false');
                expand.title = open ? 'Shrink the log' : 'Expand the log';
                box.scrollTop = box.scrollHeight;
            });
        }
        if (box && wrap) {
            box.addEventListener('scroll', function () {
                wrap.classList.toggle('is-scrolled', box.scrollTop > 4);
            });
        }
    }

    // ---------------------------------------------------------- the states
    var TICK = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg>';

    function markStep(el, cls, mark) {
        if (!el) return;
        el.className = 'tcp-step' + (cls ? ' ' + cls : '');
        var dot = el.querySelector('i');
        if (dot) {
            if (cls === 'is-done') dot.innerHTML = TICK;
            else dot.textContent = mark;
        }
    }

    function steps(name) {
        var hasNote = val('paymentNote').length > 0;
        markStep($('step1'), val('ddlReaders') ? 'is-done' : '', '1');
        markStep($('step2'), hasNote ? 'is-done' : (name === 'idle' ? 'is-now' : ''), '2');
        markStep($('step3'),
            name === 'working' ? 'is-now' : name === 'success' ? 'is-done' : name === 'fail' ? 'is-bad' : '',
            '3');
    }

    function switchAnim(state) {
        ['animIdle', 'animWorking', 'animSuccess', 'animFail'].forEach(function (id) {
            var el = $(id); if (el) el.classList.remove('is-on');
        });
        var map = { idle: 'animIdle', working: 'animWorking', success: 'animSuccess', fail: 'animFail' };
        var el = $(map[state]); if (el) el.classList.add('is-on');

        var box = $('tcpStatus');
        if (box) box.className = 'tcp-status' + (state === 'idle' ? '' : ' is-' + state);

        var pill = $('readyText');
        if (pill) pill.textContent = ({
            idle: 'Ready to ' + actionWord(),
            working: isHold() ? 'Authorizing…' : 'Processing…',
            success: isHold() ? 'Authorized' : 'Completed',
            fail: 'Failed'
        })[state] || '';

        var bar = $('tcpBar');
        if (bar) bar.hidden = (state !== 'working');
        steps(state);
    }

    function startCountdown(seconds) {
        clearInterval(countdown);
        left = seconds;
        var timer = $('timeoutTimer'), fill = $('tcpBarFill');
        if (timer) timer.textContent = left;
        if (fill) fill.style.width = '100%';
        countdown = setInterval(function () {
            left -= 1;
            if (timer) timer.textContent = left;
            if (fill) fill.style.width = Math.max(0, (left / seconds) * 100) + '%';
            if (left <= 0) { clearInterval(countdown); logln('Timeout - attempting cancel', 'warn'); cancelFlow(); }
        }, 1000);
    }

    function resetLocalTimers() { clearInterval(countdown); clearInterval(poller); poller = null; }

    // ---------------------------------------------------------- description
    function getPaymentDescription(showAlert) {
        var el = $('paymentNote');
        var value = el ? String(el.value || '').trim() : '';
        if (!value) {
            if (el) { el.classList.add('is-error'); el.focus(); }
            if (showAlert) window.alert('Please write a description of the payment.');
            logln('Description is required before ' + actionWord() + '.', 'warn');
            return '';
        }
        if (el) el.classList.remove('is-error');
        return value;
    }

    // ---------------------------------------------------------- failure
    function failUI(reason) {
        resetLocalTimers();
        switchAnim('fail');
        if (reason) logln(reason, 'bad');
        logFailToServer(reason);
        enable('btnCancel', false);
        enable('btnCharge', true);
    }

    function logFailToServer(reason) {
        post(url('faillog'), {
            RegId: val('q_regId'),
            VisitId: val('q_visitId'),
            PaymentIntentId: latestPaymentIntentId || '',
            ReaderId: val('hdnReaderId'),
            AmountMinor: val('hdnAmount') || '0',
            Currency: val('hdnCurrency') || 'GBP',
            Reason: reason || 'Failed',
            PiStatus: '',
            ReaderAction: ''
        }).catch(function () { /* logging must never break the screen */ });
    }

    // ---------------------------------------------------------- success
    function showSuccessAndClose(chargeId, authorizedHold) {
        resetLocalTimers();
        switchAnim('success');
        enable('btnCancel', false);

        if (authorizedHold || isHold()) {
            logln('Card authorization held at Stripe. Capture it later from Record Payment.', 'ok');
        } else {
            logln('Payment successful at Stripe. DB save will be handled by Stripe webhook.', 'ok');
        }

        try {
            var amount = Number(val('hdnAmount') || '0') / 100;
            var currency = val('hdnCurrency') || 'GBP';
            var returnUrl = root.getAttribute('data-return-url') || '';

            if (window.opener && !window.opener.closed) {
                window.opener.postMessage({
                    type: (authorizedHold || isHold()) ? 'PDQ_HOLD_AUTHORIZED' : 'PDQ_PAYMENT_SAVED',
                    regId: val('q_regId'),
                    paymentIntentId: latestPaymentIntentId || '',
                    chargeId: chargeId || null,
                    amount: amount,
                    currency: currency,
                    note: val('paymentNote'),
                    savedBy: 'stripe_webhook'
                }, window.location.origin);
                if (returnUrl && !(authorizedHold || isHold())) {
                    setTimeout(function () { try { window.opener.location.href = returnUrl; } catch (e) { } }, 1200);
                }
            }
        } catch (e) { }

        setTimeout(function () { window.close(); }, authorizedHold || isHold() ? 1400 : 1800);
    }

    // ---------------------------------------------------------- the flow
    function refreshReader(quiet) {
        var ddl = $('ddlReaders');
        if (!ddl || !ddl.value) { if (!quiet) logln('Select a device first', 'warn'); return false; }
        $('hdnReaderId').value = ddl.value;
        if (!quiet) logln('Using device: ' + ddl.options[ddl.selectedIndex].text + ' (' + ddl.value + ')');
        return true;
    }

    function startPayment() {
        var description = getPaymentDescription(true);
        if (!description) return;
        if (!refreshReader(false)) { failUI('Missing device selection'); return; }

        var amount = parseInt(val('hdnAmount') || '0', 10);
        if (!amount || amount <= 0) { failUI('Invalid amount'); return; }

        switchAnim('working');
        enable('btnCharge', false);
        enable('btnCancel', true);
        startCountdown(TIMEOUT_SECONDS);

        logln((isHold() ? 'Creating manual-capture hold for ' : 'Creating PaymentIntent for ')
            + (amount / 100).toFixed(2) + ' ' + (val('hdnCurrency') || 'GBP') + '…');

        var began = Date.now();
        post(url('start'), {
            ReaderId: val('hdnReaderId'),
            Description: description,
            RegId: val('q_regId'),
            VisitId: val('q_visitId'),
            FullName: val('q_fullName'),
            ArrivalDate: val('q_arrival'),
            DepartureDate: val('q_departure'),
            GrandTotal: val('q_grandTotal'),
            Payable: val('q_payable'),
            AdvancePaid: val('q_advancePaid'),
            Status: val('q_status'),
            Amount: val('hdnAmount'),
            Currency: val('hdnCurrency'),
            Mode: val('q_mode') || 'charge'
        }).then(function (res) {
            var round = Date.now() - began;
            if (res && res.id) latestPaymentIntentId = res.id;

            if (!res || !res.ok) {
                failUI((res && res.message) || 'The payment could not be started.');
                return;
            }

            /* where the time actually went, so a slow desk or a slow line is visible */
            /* where the time actually went: Stripe's two calls, the whole server request, and
               the browser round trip. A big gap between the last two is the local machine. */
            logln('PaymentIntent created in ' + (res.createMs || 0) + ' ms, reader accepted in '
                + (res.processMs || 0) + ' ms (server ' + (res.totalMs || 0) + ' ms, round trip ' + round + ' ms).');
            logln('Handing off to device…');
            pollUntilDone(res.id);
        }).catch(function (e) {
            failUI('Create PI error: ' + (e && e.message ? e.message : 'unknown'));
        });
    }

    function pollUntilDone(piId) {
        clearInterval(poller); poller = null;
        var busy = false, tick = 0;
        poller = setInterval(function () {
            if (busy) return;                                    /* never stack requests */
            busy = true;
            tick++;
            /* the payment intent is read every time; the reader's own action status only every
               other tick - it is a second Stripe call and the intent is what decides the outcome */
            post(url('status'), { paymentIntentId: piId, readerId: (tick % 2 === 0) ? val('hdnReaderId') : '' })
                .then(function (s) {
                    busy = false;
                    if (!s || !s.ok) { clearInterval(poller); poller = null; failUI('Status error: ' + ((s && s.message) || 'unknown')); return; }
                    if (!s.done) return;
                    clearInterval(poller); poller = null;
                    if (s.success) {
                        if (s.authorizedHold || isHold()) {
                            logln('Authorization successful. Hold is ready to capture.', 'ok');
                            showSuccessAndClose(s.chargeId || null, true);
                        } else {
                            logln('Payment successful at Stripe. Webhook will save this payment.', 'ok');
                            showSuccessAndClose(s.chargeId || null, false);
                        }
                    } else {
                        failUI(s.message || 'Failed');
                    }
                })
                .catch(function (e) {
                    busy = false;
                    clearInterval(poller); poller = null;
                    failUI('Status error: ' + (e && e.message ? e.message : 'unknown'));
                });
        }, 1500);
    }

    function cancelFlow() {
        enable('btnCancel', false);
        clearInterval(poller); poller = null;
        logln('Cancelling device action…', 'warn');

        post(url('cancelaction'), { readerId: val('hdnReaderId') })
            .catch(function () { })
            .then(function () {
                if (!latestPaymentIntentId) { resetAndIdle(); return; }
                logln('Cancelling PaymentIntent…', 'warn');
                return post(url('cancelintent'), { paymentIntentId: latestPaymentIntentId })
                    .catch(function () { })
                    .then(function () { resetAndIdle(); });
            });
    }

    function resetAndIdle() {
        resetLocalTimers();
        switchAnim('idle');
        enable('btnCancel', false);
        enable('btnCharge', true);
        logln('Cancelled.');
    }

    function simulateCard() {
        if (!$('chkSim') || !$('chkSim').checked) { logln('Enable the simulated toggle first.', 'warn'); return; }
        if (!latestPaymentIntentId) {
            logln('No PaymentIntent yet. Write description, click ' + (isHold() ? 'Hold card' : 'Charge now') + ', wait for "Handing off to device…", then click Simulate.', 'warn');
            return;
        }
        if (!val('hdnReaderId')) { logln('Select a device first.', 'warn'); return; }

        logln('Simulating test card on the Stripe test reader…');
        post(url('simulate'), {
            readerId: val('hdnReaderId'),
            paymentIntentId: latestPaymentIntentId,
            scenario: val('selSimCard') || 'success_4242'
        }).then(function (res) {
            if (!res || !res.ok) { failUI('Simulate error: ' + ((res && res.message) || 'unknown')); return; }
            logln('Simulate present: ' + (res.actionStatus || 'in_progress'), 'ok');
        }).catch(function (e) {
            failUI('Simulate error: ' + (e && e.message ? e.message : 'unknown'));
        });
    }

    // ---------------------------------------------------------- wiring
    (function bootstrap() {
        var note = $('paymentNote'), count = $('tcpCount');
        if (note && count) count.textContent = note.value.length + ' / 450';
        if (note && note.value.trim()) logln('Description loaded from Manual Payment screen.', 'ok');

        refreshReader(true);
        switchAnim('idle');
        wireLogTools();

        if (note) {
            note.addEventListener('input', function () {
                if (this.value.trim()) this.classList.remove('is-error');
                if (count) count.textContent = this.value.length + ' / 450';
                steps($('tcpStatus') && $('tcpStatus').classList.contains('is-working') ? 'working' : 'idle');
            });
        }
        var ddl = $('ddlReaders');
        if (ddl) ddl.addEventListener('change', function () { refreshReader(true); steps('idle'); });

        var refresh = $('tcpRefresh');
        if (refresh) {
            refresh.addEventListener('click', function () {
                refresh.classList.remove('is-spin'); void refresh.offsetWidth; refresh.classList.add('is-spin');
                refreshReader(false);
            });
        }
        var charge = $('btnCharge'); if (charge) charge.addEventListener('click', startPayment);
        var cancel = $('btnCancel'); if (cancel) cancel.addEventListener('click', cancelFlow);
        var simulate = $('btnSimulate'); if (simulate) simulate.addEventListener('click', simulateCard);

        /* a charge in flight must not be lost by closing the popup by accident */
        window.addEventListener('beforeunload', function (e) {
            if (poller) { e.preventDefault(); e.returnValue = ''; }
        });
    })();
})();
