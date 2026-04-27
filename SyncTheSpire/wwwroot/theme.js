'use strict';

// ── theme: light / dark / system ──
//
// The initial dark class is set by the inline boot script in index.html so the
// first paint already matches the saved theme. This module owns runtime mode
// changes from the settings UI, and keeps the page in sync when the OS theme
// changes while mode === "system".

const Theme = (() => {
    const STORAGE_KEY = 'sts-theme';
    const VALID = new Set(['system', 'light', 'dark']);
    const _listeners = [];
    const _mql = window.matchMedia('(prefers-color-scheme: dark)');

    let _mode = readSaved();

    function readSaved() {
        try {
            const v = localStorage.getItem(STORAGE_KEY);
            return VALID.has(v) ? v : 'system';
        } catch {
            return 'system';
        }
    }

    function resolved() {
        return _mode === 'system'
            ? (_mql.matches ? 'dark' : 'light')
            : _mode;
    }

    function apply() {
        const r = resolved();
        document.documentElement.classList.toggle('dark', r === 'dark');
        _listeners.forEach(fn => { try { fn(r, _mode); } catch {} });
    }

    function setMode(mode) {
        if (!VALID.has(mode)) mode = 'system';
        _mode = mode;
        try { localStorage.setItem(STORAGE_KEY, mode); } catch {}
        apply();
    }

    function getMode() { return _mode; }
    function getResolved() { return resolved(); }

    function onChange(fn) { _listeners.push(fn); }

    // when following the system, react to OS theme switches in real time
    _mql.addEventListener('change', () => {
        if (_mode === 'system') apply();
    });

    return { setMode, getMode, getResolved, onChange };
})();
