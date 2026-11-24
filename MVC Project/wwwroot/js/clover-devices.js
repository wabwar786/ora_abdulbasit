/* =============================================================
   clover-devices.js   -   CloverDevices module

   Three small jobs, nothing else:
     1. Connect To Stripe opens the OAuth url in a 600x600 window,
        which is what openSmallWindow() did.
     2. Delete asks first, in a themed dialog instead of confirm().
     3. The message strip can be dismissed.

   No library, no framework. Every lookup is guarded, so the empty
   view and the error view cannot throw.
   ============================================================= */
(function () {
    'use strict';

    function ready(fn) {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', fn, { once: true });
        } else {
            fn();
        }
    }

    ready(function () {
        var root = document.querySelector('.clvdev-root');
        if (!root) { return; }

        /* ---- 1. Connect To Stripe --------------------------------- */
        var connect = document.getElementById('clvdevConnectStripe');

        if (connect) {
            connect.addEventListener('click', function () {
                var url = root.getAttribute('data-clvdev-stripe-url');

                if (url) {
                    window.open(
                        url,
                        'StripeIntegration',
                        'width=600,height=600,resizable=yes,scrollbars=yes'
                    );
                }
            });
        }

        /* ---- 2. the message toast: dismiss, and auto-hide ---------- */
        function hideToast(toast) {
            if (!toast || !toast.parentNode) { return; }

            toast.classList.add('clvdev-msg-out');

            window.setTimeout(function () {
                if (toast.parentNode) { toast.parentNode.removeChild(toast); }
            }, 220);
        }

        root.addEventListener('click', function (e) {
            var dismiss = e.target.closest('[data-clvdev-dismiss]');
            if (!dismiss) { return; }

            hideToast(dismiss.closest('.clvdev-msg'));
        });

        var toast = root.querySelector('.clvdev-msg[data-clvdev-autohide]');

        if (toast) {
            var delay = parseInt(toast.getAttribute('data-clvdev-autohide'), 10);
            var timer = null;

            if (delay > 0) {
                timer = window.setTimeout(function () { hideToast(toast); }, delay);

                // Reading it should not be a race: hovering holds it open.
                toast.addEventListener('mouseenter', function () {
                    window.clearTimeout(timer);
                });
            }
        }

        /* ---- 3. confirm before a destructive post ------------------ */
        var pending = null;

        function closeDialog() {
            var host = document.getElementById('clvdevConfirmHost');
            if (host && host.parentNode) { host.parentNode.removeChild(host); }
            pending = null;
        }

        function openDialog(form) {
            closeDialog();
            pending = form;

            var title = form.getAttribute('data-clvdev-confirm-title') || 'Please confirm';
            var text = form.getAttribute('data-clvdev-confirm') || 'Are you sure?';
            var okText = form.getAttribute('data-clvdev-confirm-ok') || 'Confirm';

            var host = document.createElement('div');
            host.id = 'clvdevConfirmHost';

            var overlay = document.createElement('div');
            overlay.className = 'clvdev-overlay';

            var wrap = document.createElement('div');
            wrap.className = 'clvdev-modal';
            wrap.setAttribute('role', 'dialog');
            wrap.setAttribute('aria-modal', 'true');

            var box = document.createElement('div');
            box.className = 'clvdev-modal-box clvdev-confirm';

            var head = document.createElement('div');
            head.className = 'clvdev-modal-head';

            var heading = document.createElement('span');
            heading.textContent = title;

            var close = document.createElement('button');
            close.type = 'button';
            close.className = 'clvdev-modal-close';
            close.setAttribute('aria-label', 'Close');
            close.textContent = '×';

            head.appendChild(heading);
            head.appendChild(close);

            var body = document.createElement('div');
            body.className = 'clvdev-modal-body';

            var message = document.createElement('p');
            message.className = 'clvdev-confirm-text';
            message.textContent = text;
            body.appendChild(message);

            var foot = document.createElement('div');
            foot.className = 'clvdev-modal-foot';

            var cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.className = 'clvdev-btn clvdev-btn-ghost';
            cancel.textContent = 'Cancel';

            var ok = document.createElement('button');
            ok.type = 'button';
            ok.className = 'clvdev-btn clvdev-btn-danger';
            ok.textContent = okText;

            foot.appendChild(cancel);
            foot.appendChild(ok);

            box.appendChild(head);
            box.appendChild(body);
            box.appendChild(foot);
            wrap.appendChild(box);
            host.appendChild(overlay);
            host.appendChild(wrap);

            root.appendChild(host);

            close.addEventListener('click', closeDialog);
            cancel.addEventListener('click', closeDialog);
            overlay.addEventListener('click', closeDialog);

            ok.addEventListener('click', function () {
                var form = pending;
                closeDialog();

                if (form) {
                    // Past the guard: submit without re-entering it.
                    form.setAttribute('data-clvdev-confirmed', '1');
                    form.submit();
                }
            });

            ok.focus();
        }

        document.addEventListener('keydown', function (e) {
            if (e.key !== 'Escape') { return; }

            if (document.getElementById('clvdevConfirmHost')) {
                closeDialog();
            } else {
                hideToast(root.querySelector('.clvdev-msg'));
            }
        });

        root.addEventListener('submit', function (e) {
            var form = e.target;

            if (!form || !form.getAttribute) { return; }
            if (!form.getAttribute('data-clvdev-confirm')) { return; }
            if (form.getAttribute('data-clvdev-confirmed') === '1') { return; }

            e.preventDefault();
            openDialog(form);
        });
    });
})();
