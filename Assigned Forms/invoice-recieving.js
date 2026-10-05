(function () {
    'use strict';

    var root = document.querySelector('.inv-root');
    if (!root) return;

    // The logo ships with the module, so this only matters if the file is
    // removed: hide the block rather than show a broken-image icon.
    var logo = root.querySelector('.inv-logo img');
    if (logo) {
        var hideLogo = function () {
            var box = root.querySelector('.inv-logo');
            var rule = root.querySelector('.inv-brand-rule');

            if (box) box.style.display = 'none';
            if (rule) rule.style.display = 'none';
        };

        logo.addEventListener('error', hideLogo);

        if (logo.complete && logo.naturalWidth === 0) hideLogo();
    }

    var button = root.querySelector('[data-copy]');
    var field = root.querySelector('.inv-paylink input');
    var note = root.querySelector('[data-copied]');

    if (!button || !field) return;

    var timer;

    function confirmCopy() {
        if (!note) return;
        note.classList.add('inv-on');
        clearTimeout(timer);
        timer = setTimeout(function () { note.classList.remove('inv-on'); }, 1400);
    }

    button.addEventListener('click', function () {
        var text = field.value || '';
        if (!text) return;

        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(confirmCopy, fallback);
            return;
        }

        fallback();

        function fallback() {
            field.select();
            field.setSelectionRange(0, 999999);

            try { document.execCommand('copy'); } catch (e) { /* nothing else to try */ }

            confirmCopy();
        }
    });
})();
