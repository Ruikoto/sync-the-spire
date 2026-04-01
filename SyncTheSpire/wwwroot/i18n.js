'use strict';

// ── i18n: lightweight translation layer ──

const I18n = (() => {
    let _strings = {};
    let _lang = 'zh-CN';
    const _listeners = [];

    async function load(lang) {
        _lang = lang;
        try {
            const resp = await fetch(`i18n/${lang}.json`);
            if (resp.ok) _strings = await resp.json();
        } catch (e) {
            console.warn(`[i18n] failed to load ${lang}:`, e);
        }
    }

    // look up a dotted key, return the key itself if missing
    // supports {param} interpolation and returns arrays as-is (for quotes etc.)
    function t(key, params) {
        const parts = key.split('.');
        let node = _strings;
        for (const p of parts) {
            if (node == null || typeof node !== 'object') return key;
            node = node[p];
        }
        if (node === undefined || node === null) return key;
        // arrays (e.g. quotes) — return as-is
        if (Array.isArray(node)) return node;
        if (typeof node !== 'string') return key;
        // interpolate {name} placeholders
        if (params) {
            return node.replace(/\{(\w+)\}/g, (_, k) => (params[k] !== undefined ? params[k] : `{${k}}`));
        }
        return node;
    }

    function getLang() { return _lang; }

    // walk the DOM and apply data-i18n* attributes
    function applyDom(root) {
        const el = root || document.body;
        // data-i18n → textContent
        el.querySelectorAll('[data-i18n]').forEach(n => {
            const v = t(n.dataset.i18n);
            if (v !== n.dataset.i18n || _lang !== 'zh-CN') n.textContent = v;
        });
        // data-i18n-html → innerHTML (for strings with embedded markup)
        el.querySelectorAll('[data-i18n-html]').forEach(n => {
            const v = t(n.dataset.i18nHtml);
            if (v !== n.dataset.i18nHtml || _lang !== 'zh-CN') n.innerHTML = v;
        });
        // data-i18n-placeholder → placeholder attr
        el.querySelectorAll('[data-i18n-placeholder]').forEach(n => {
            const v = t(n.dataset.i18nPlaceholder);
            if (v !== n.dataset.i18nPlaceholder || _lang !== 'zh-CN') n.placeholder = v;
        });
        // data-i18n-title → title attr
        el.querySelectorAll('[data-i18n-title]').forEach(n => {
            const v = t(n.dataset.i18nTitle);
            if (v !== n.dataset.i18nTitle || _lang !== 'zh-CN') n.title = v;
        });
        // data-i18n-tip → data-tip attr (for help-tip tooltips)
        el.querySelectorAll('[data-i18n-tip]').forEach(n => {
            const v = t(n.dataset.i18nTip);
            if (v !== n.dataset.i18nTip || _lang !== 'zh-CN') n.dataset.tip = v;
        });
        // data-i18n-aria → aria-label attr
        el.querySelectorAll('[data-i18n-aria]').forEach(n => {
            const v = t(n.dataset.i18nAria);
            if (v !== n.dataset.i18nAria || _lang !== 'zh-CN') n.setAttribute('aria-label', v);
        });
    }

    // register a callback to fire after language changes
    function onChange(fn) {
        _listeners.push(fn);
    }

    // switch language: load strings, patch DOM, notify listeners
    async function setLang(lang) {
        await load(lang);
        document.documentElement.lang = lang;
        try { localStorage.setItem('sts-lang', lang); } catch {}
        applyDom();
        _listeners.forEach(fn => { try { fn(lang); } catch {} });
        return lang;
    }

    function getSupportedLangs() {
        return [
            { code: 'zh-CN', label: '中文' },
            { code: 'en', label: 'English' },
        ];
    }

    return { load, t, getLang, applyDom, onChange, setLang, getSupportedLangs };
})();
