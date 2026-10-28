(function () {
    'use strict';

    // Flash messages (success / error) are rendered as Bootstrap toasts.
    document.querySelectorAll('.toast').forEach(function (element) {
        new bootstrap.Toast(element).show();
    });

    // Role matrix: a header checkbox toggles every permission of a module in one role column, a row checkbox a whole row.
    document.querySelectorAll('[data-toggle-group]').forEach(function (toggle) {
        toggle.addEventListener('change', function () {
            document.querySelectorAll('input[type=checkbox][data-group="' + toggle.dataset.toggleGroup + '"]').forEach(function (box) {
                box.checked = toggle.checked;
            });
        });
    });

    // Audit trail: show the before / after snapshots of a row in a modal. Values are written as text, never as HTML.
    var modal = document.getElementById('auditModal');
    if (modal) {
        var pretty = function (json) {
            if (!json) { return '(none)'; }
            try { return JSON.stringify(JSON.parse(json), null, 2); } catch (e) { return json; }
        };

        modal.addEventListener('show.bs.modal', function (event) {
            var row = event.relatedTarget;
            modal.querySelector('[data-audit-title]').textContent = row.dataset.title || '';
            modal.querySelector('[data-audit-old]').textContent = pretty(row.dataset.old);
            modal.querySelector('[data-audit-new]').textContent = pretty(row.dataset.new);
        });
    }

    // Confirmation for risky buttons.
    document.querySelectorAll('form[data-confirm]').forEach(function (form) {
        form.addEventListener('submit', function (event) {
            if (!window.confirm(form.dataset.confirm)) { event.preventDefault(); }
        });
    });
})();
