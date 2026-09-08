(function () {
    'use strict';

    function loader() {
        return document.getElementById('pmsGlobalLoader');
    }

    function loaderText() {
        return document.getElementById('pmsGlobalLoaderText');
    }

    window.pmsShowLoader = function (message) {
        var overlay = loader();
        if (!overlay) return;
        var text = loaderText();
        if (text && message) text.textContent = message;
        overlay.classList.add('is-visible');
        overlay.setAttribute('aria-hidden', 'false');
        document.body.classList.add('pms-loading');
    };

    window.pmsHideLoader = function () {
        var overlay = loader();
        if (!overlay) return;
        overlay.classList.remove('is-visible');
        overlay.setAttribute('aria-hidden', 'true');
        document.body.classList.remove('pms-loading');
    };

    document.addEventListener('click', function (event) {
        var toggle = event.target.closest('[data-password-target]');
        if (toggle) {
            var id = toggle.getAttribute('data-password-target');
            var input = id ? document.getElementById(id) : null;
            if (input) {
                var showing = input.type === 'text';
                input.type = showing ? 'password' : 'text';
                toggle.setAttribute('aria-pressed', showing ? 'false' : 'true');
                toggle.setAttribute('aria-label', showing ? 'Show password' : 'Hide password');
                toggle.classList.toggle('is-visible', !showing);
                input.focus({ preventScroll: true });
            }
            return;
        }

        var submit = event.target.closest('[type="submit"][data-pms-loader]');
        if (submit) {
            // Let native form validation run for login. Forgot Password uses formnovalidate.
            var form = submit.form;
            if (form && !submit.formNoValidate && !form.checkValidity()) return;
            var message = submit.getAttribute('data-pms-loader-message') ||
                          (form && form.getAttribute('data-pms-loader-message')) ||
                          'Please wait...';
            window.pmsShowLoader(message);
        }
    });

    window.addEventListener('pageshow', window.pmsHideLoader);
})();
