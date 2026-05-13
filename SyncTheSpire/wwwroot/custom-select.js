'use strict';

// ── custom-select ──────────────────────────────────────────────────
// Replaces every <select.settings-select> with a custom dropdown so the
// open panel can be styled (browsers ignore CSS on native option lists).
// The native <select> stays as the source of truth — we just paint over
// it and forward selection back via dispatchEvent('change').
// Active in ALL themes for visual consistency; Island adds its own flair
// via CSS overrides layered on top of the baseline styling.

const CustomSelect = (() => {
    const wrapped = new WeakMap();

    function build(select) {
        const wrapper = document.createElement('div');
        wrapper.className = 'custom-select';

        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'custom-select-trigger';
        trigger.setAttribute('aria-haspopup', 'listbox');
        trigger.setAttribute('aria-expanded', 'false');
        trigger.innerHTML =
            '<span class="custom-select-value"></span>' +
            '<svg class="custom-select-arrow" width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">' +
            '<polyline points="3 4.5 6 7.5 9 4.5"/>' +
            '</svg>';

        const menu = document.createElement('div');
        menu.className = 'custom-select-menu app-dropdown hidden';
        menu.setAttribute('role', 'listbox');

        wrapper.appendChild(trigger);
        wrapper.appendChild(menu);

        function sync() {
            menu.innerHTML = '';
            for (const opt of select.options) {
                const item = document.createElement('button');
                item.type = 'button';
                item.className = 'custom-select-option';
                if (opt.value === select.value) item.classList.add('is-selected');
                item.textContent = opt.textContent;
                item.dataset.value = opt.value;
                item.setAttribute('role', 'option');
                item.addEventListener('click', () => {
                    if (select.value !== opt.value) {
                        select.value = opt.value;
                        select.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    close();
                });
                menu.appendChild(item);
            }
            const cur = select.options[select.selectedIndex];
            trigger.querySelector('.custom-select-value').textContent = cur ? cur.textContent : '';
        }

        function open() {
            menu.classList.remove('hidden');
            trigger.setAttribute('aria-expanded', 'true');
        }
        function close() {
            menu.classList.add('hidden');
            trigger.setAttribute('aria-expanded', 'false');
        }

        trigger.addEventListener('click', (e) => {
            e.stopPropagation();
            if (menu.classList.contains('hidden')) open();
            else close();
        });

        const onDocClick = (e) => { if (!wrapper.contains(e.target)) close(); };
        const onKey = (e) => { if (e.key === 'Escape') close(); };
        document.addEventListener('click', onDocClick);
        document.addEventListener('keydown', onKey);

        // re-sync when underlying select changes externally (or option text changes via i18n)
        select.addEventListener('change', sync);
        const mo = new MutationObserver(sync);
        mo.observe(select, { childList: true, subtree: true, characterData: true });

        // place wrapper next to the (now hidden) native select
        select.parentNode.insertBefore(wrapper, select);
        select.classList.add('custom-select-native-hidden');

        sync();

        return {
            wrapper,
            cleanup() {
                wrapper.remove();
                select.classList.remove('custom-select-native-hidden');
                select.removeEventListener('change', sync);
                document.removeEventListener('click', onDocClick);
                document.removeEventListener('keydown', onKey);
                mo.disconnect();
            }
        };
    }

    function init() {
        for (const sel of document.querySelectorAll('.settings-select')) {
            if (wrapped.has(sel)) continue;
            wrapped.set(sel, build(sel));
        }
    }

    function teardown() {
        for (const sel of document.querySelectorAll('.settings-select')) {
            const w = wrapped.get(sel);
            if (w) { w.cleanup(); wrapped.delete(sel); }
        }
    }

    // run in every theme — once the DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }

    return { init, teardown };
})();
