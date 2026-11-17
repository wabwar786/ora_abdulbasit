(function () {
    'use strict';
    const report = document.getElementById('rwrReport');
    if (!report) return;
    const from = report.querySelector('input[name="from"]');
    const to = report.querySelector('input[name="to"]');
    if (!from || !to) return;
    const form = from.closest('form');
    function validate() {
        to.min = from.value;
        const invalid = !!(from.value && to.value && to.value < from.value);
        to.setCustomValidity(invalid ? 'To date must be on or after From date.' : '');
        return !invalid;
    }
    from.addEventListener('change', validate);
    to.addEventListener('change', validate);
    validate();
    if (form) form.addEventListener('submit', function (event) {
        if (!validate()) { event.preventDefault(); to.reportValidity(); }
    });
})();
