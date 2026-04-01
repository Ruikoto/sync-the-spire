'use strict';

// ── i18n: lightweight translation layer ──

const I18n = (() => {
    let _strings = {};
    let _lang = 'zh-CN';

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
    function t(key) {
        const parts = key.split('.');
        let node = _strings;
        for (const p of parts) {
            if (node == null || typeof node !== 'object') return key;
            node = node[p];
        }
        return (typeof node === 'string') ? node : key;
    }

    function getLang() { return _lang; }

    return { load, t, getLang };
})();
