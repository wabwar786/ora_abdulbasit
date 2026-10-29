/* ORA PMS — Invoice Footer Settings editor, intentionally scoped to this page. */
(function () {
    'use strict';
    const root = document.getElementById('invoiceFooterPage');
    if (!root) return;

    const form = document.getElementById('ifsForm');
    const input = document.getElementById('ifsInstructions');
    const preview = document.getElementById('ifsPreviewText');
    const placeholder = document.getElementById('ifsPreviewPlaceholder');
    const count = document.getElementById('ifsCount');
    const state = document.getElementById('ifsSaveState');
    const resetButton = document.getElementById('ifsReset');
    const clearButton = document.getElementById('ifsClear');
    const bulletButton = document.getElementById('ifsInsertBullet');
    const numberedButton = document.getElementById('ifsInsertNumber');
    if (!form || !input || !preview || !placeholder || !count || !state) return;

    const normalized = value => (value || '').replace(/\r\n?/g, '\n');
    const original = normalized(input.value);
    let saving = false;
    let dirty = false;
    let saveError = root.dataset.saveError === '1';

    function refresh() {
        const text = normalized(input.value);
        preview.textContent = text; // Never interpret customer-entered HTML.
        placeholder.hidden = !!text.trim();
        count.textContent = text.length.toLocaleString('en-GB') + ' / 5,000';
        count.classList.toggle('is-max', text.length >= 5000);
        dirty = text !== original;
        state.classList.toggle('is-dirty', dirty);
        if (!saving) {
            state.innerHTML = saveError
                ? '<i class="fa-solid fa-circle-exclamation" aria-hidden="true"></i> Changes not saved'
                : dirty
                ? '<i class="fa-solid fa-circle" aria-hidden="true"></i> Unsaved changes'
                : '<i class="fa-solid fa-circle-check" aria-hidden="true"></i> All changes saved';
        }
        if (resetButton) resetButton.disabled = input.disabled || !dirty;
        if (clearButton) clearButton.disabled = input.disabled || !text.length;
    }

    function insertMarker(type) {
        if (input.disabled || saving) return;
        const start = input.selectionStart;
        const end = input.selectionEnd;
        const value = input.value;
        const atStartOfLine = start === 0 || value.charAt(start - 1) === '\n';
        let marker = '• ';
        if (type === 'number') {
            marker = '1. ';
            const lines = value.slice(0, start).split('\n');
            for (let i = lines.length - 1; i >= 0; i--) {
                const match = /^\s*(\d+)\.\s/.exec(lines[i]);
                if (match) {
                    marker = String(parseInt(match[1], 10) + 1) + '. ';
                    break;
                }
            }
        }
        const insertion = (atStartOfLine ? '' : '\n') + marker;
        if (normalized(value).length - (end - start) + insertion.length > 5000) return;
        input.setRangeText(insertion, start, end, 'end');
        input.focus();
        refresh();
    }

    bulletButton?.addEventListener('click', () => insertMarker('bullet'));
    numberedButton?.addEventListener('click', () => insertMarker('number'));

    input.addEventListener('input', function () { saveError = false; refresh(); });
    input.addEventListener('keydown', function (event) {
        if (event.key !== 'Enter' || event.isComposing || input.selectionStart !== input.selectionEnd) return;
        const position = input.selectionStart;
        const before = input.value.slice(0, position);
        const line = before.slice(before.lastIndexOf('\n') + 1);
        const match = /^(\s*)([•*-]|\d+\.)\s+/.exec(line);
        if (!match) return;
        event.preventDefault();
        if (!line.slice(match[0].length).trim()) {
            input.setRangeText('', position - line.length, position, 'end');
        } else {
            let marker = match[2];
            if (/^\d+\.$/.test(marker)) marker = String(parseInt(marker, 10) + 1) + '.';
            const insertion = '\n' + match[1] + marker + ' ';
            if (input.value.length + insertion.length <= 5000)
                input.setRangeText(insertion, position, position, 'end');
        }
        refresh();
    });

    resetButton?.addEventListener('click', function () {
        if (!dirty || saving) return;
        if (window.confirm('Discard your unsaved edits and restore the last loaded instructions?')) {
            input.value = original;
            refresh();
            input.focus();
        }
    });
    clearButton?.addEventListener('click', function () {
        if (!input.value.length || saving) return;
        if (window.confirm('Clear the instructions? The footer will only be removed after you save.')) {
            input.value = '';
            refresh();
            input.focus();
        }
    });

    form.addEventListener('submit', function (event) {
        if (saving || input.disabled) { event.preventDefault(); return; }
        const text = normalized(input.value);
        if (text.length > 5000) {
            event.preventDefault();
            count.classList.add('is-max');
            input.focus();
            return;
        }
        saving = true;
        dirty = false;
        state.classList.remove('is-dirty');
        state.classList.add('is-busy');
        state.textContent = 'Saving instructions…';
        document.querySelectorAll('.ifs-save-button').forEach(button => {
            button.disabled = true;
            button.classList.add('is-loading');
            const label = button.querySelector('.ifs-save-label');
            if (label) label.textContent = 'Saving…';
        });
    });

    document.addEventListener('keydown', function (event) {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's' && !event.altKey) {
            event.preventDefault();
            if (!saving && !input.disabled) form.requestSubmit();
        }
    });

    window.addEventListener('beforeunload', function (event) {
        if (!dirty || saving) return;
        event.preventDefault();
        event.returnValue = '';
    });

    refresh();
})();
