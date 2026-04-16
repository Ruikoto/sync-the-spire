'use strict';

// ── modals: welcome, branch, backup list, about, update, announcements ──────

// ── welcome/guide modal ──────────────────────────────────────────────────────

let welcomeAutoOpened = false;

function showWelcomeModal() {
    $('#welcome-modal').classList.remove('hidden');
}

function closeWelcomeModal() {
    $('#welcome-modal').classList.add('hidden');
    if (welcomeAutoOpened) {
        welcomeAutoOpened = false;
        promptAutoFind();
    }
}

$('#btn-welcome-continue').addEventListener('click', closeWelcomeModal);
$('#welcome-modal').addEventListener('click', e => {
    if (e.target === $('#welcome-modal')) closeWelcomeModal();
});


// ── branch modal ─────────────────────────────────────────────────────────────

let branchData = [];
let branchCurrentName = '';
let branchSortKey = 'lastModified';
let branchSortAsc = false; // newest first by default
let branchPreviewName = '';
const branchModsCache = {};

// called on workspace switch to prevent stale data from the previous workspace
function clearBranchCache() {
    branchData = [];
    branchCurrentName = '';
    for (const k in branchModsCache) delete branchModsCache[k];
}

function showBranchListView() {
    $('#branch-list-view').classList.remove('hidden');
    $('#branch-preview-view').classList.add('hidden');
}

function showBranchPreviewView(branchName) {
    branchPreviewName = branchName;
    $('#branch-preview-name').textContent = branchName;
    $('#branch-list-view').classList.add('hidden');
    $('#branch-preview-view').classList.remove('hidden');
}

function openBranchModal() {
    // clear cache -- GET_BRANCHES does a fresh fetch, cached mod data may be stale
    for (const k in branchModsCache) delete branchModsCache[k];
    showBranchListView();
    sendMessage('GET_BRANCHES');
    renderBranchTable();
    $('#branch-modal').classList.remove('hidden');
}

function closeBranchModal() {
    $('#branch-modal').classList.add('hidden');
    showBranchListView();
}

function nsfwTooltip(reasons) {
    const lines = [I18n.t('branch.nsfwWarning'), I18n.t('branch.nsfwReason'), ...(reasons || []).map(r => '• ' + r)];
    return lines.map(l => escAttr(l)).join('&#10;');
}

function renderBranchTable() {
    const tbody = $('#branch-table-body');
    const empty = $('#branch-empty');

    if (branchData.length === 0) {
        tbody.innerHTML = '';
        empty.classList.remove('hidden');
        return;
    }
    empty.classList.add('hidden');

    // sort a copy
    const sorted = [...branchData].sort((a, b) => {
        let va = a[branchSortKey], vb = b[branchSortKey];
        if (typeof va === 'string') { va = va.toLowerCase(); vb = vb.toLowerCase(); }
        if (va < vb) return branchSortAsc ? -1 : 1;
        if (va > vb) return branchSortAsc ? 1 : -1;
        return 0;
    });

    tbody.innerHTML = sorted.map(b => {
        const isCurrent = b.name === branchCurrentName;
        const rowHighlight = isCurrent ? 'bg-spire-accent/10' : 'hover:bg-spire-bg/50';
        const tag = isCurrent ? ` <span class="text-spire-accent text-[10px] ml-1">${esc(I18n.t('branch.current'))}</span>` : '';
        const nsfwBadge = b.isNsfw ? `<span class="nsfw-badge" title="${nsfwTooltip(b.nsfwReasons)}">NSFW</span>` : '';
        return `<tr class="branch-row border-b border-spire-border/50 cursor-pointer transition-colors ${rowHighlight}" data-branch="${escAttr(b.name)}">
            <td class="px-4 py-2.5 font-mono text-xs">${nsfwBadge}${esc(b.name)}${tag}</td>
            <td class="px-4 py-2.5 text-xs text-spire-muted">${esc(b.author)}</td>
            <td class="px-4 py-2.5 text-xs text-spire-muted">${formatRelativeTime(b.lastModified)}</td>
        </tr>`;
    }).join('');

    // bind row click -> open mod preview (if supported) or sync directly
    tbody.querySelectorAll('.branch-row').forEach(row => {
        row.addEventListener('click', () => {
            const branch = row.dataset.branch;
            const ws = getWsState();
            if (ws.capabilities?.supportsModScanning) {
                openBranchPreview(branch);
            } else {
                // no mod scanning — skip preview, go straight to sync confirm
                syncBranchDirect(branch);
            }
        });
    });
}

// sort headers
document.querySelectorAll('.branch-sort-th').forEach(th => {
    th.addEventListener('click', () => {
        const key = th.dataset.key;
        if (branchSortKey === key) {
            branchSortAsc = !branchSortAsc;
        } else {
            branchSortKey = key;
            branchSortAsc = key === 'name'; // name defaults to A-Z, others default to desc
        }
        // update arrow indicators
        document.querySelectorAll('.branch-sort-th').forEach(h => {
            h.classList.remove('active-sort');
            const arrow = h.querySelector('.sort-arrow');
            if (arrow) arrow.remove();
        });
        th.classList.add('active-sort');
        th.insertAdjacentHTML('beforeend', ` <span class="sort-arrow">${branchSortAsc ? '▲' : '▼'}</span>`);
        renderBranchTable();
    });
});

$('#btn-browse-branches').addEventListener('click', openBranchModal);
$('#branch-modal-close').addEventListener('click', closeBranchModal);
// close modal when clicking backdrop
$('#branch-modal').addEventListener('click', e => {
    if (e.target === $('#branch-modal')) closeBranchModal();
});

// ── branch mod preview ───────────────────────────────────────────────────────

let modSearchCaseSensitive = false;
let modSearchWholeWord = false;
let currentPreviewMods = [];

// directly confirm & sync a branch without mod preview
async function syncBranchDirect(branchName) {
    const ok = await showConfirm(
        I18n.t('branch.syncConfirmMessage', { name: branchName }),
        I18n.t('branch.syncConfirmTitle')
    );
    if (ok) {
        closeBranchModal();
        sendMessage('SYNC_OTHER_BRANCH', { branchName });
    }
}

function openBranchPreview(branchName) {
    showBranchPreviewView(branchName);
    // reset search state
    $('#mod-search-input').value = '';
    currentPreviewMods = [];

    if (branchModsCache[branchName]) {
        currentPreviewMods = branchModsCache[branchName];
        renderModCards();
        return;
    }

    // inline loading state
    $('#branch-preview-body').innerHTML = `
        <div class="flex flex-col items-center justify-center py-8">
            <div class="spinner"></div>
            <p class="text-xs text-spire-muted mt-3">${esc(I18n.t('branch.loadingMods'))}</p>
        </div>`;

    sendMessage('GET_BRANCH_MODS', { branchName });
}

on('GET_BRANCH_MODS', data => {
    if (data.status === 'success') {
        const branchName = data.payload?.branchName;
        const mods = data.payload?.mods || [];
        if (branchName) branchModsCache[branchName] = mods;
        // only render if still viewing this branch
        if (branchPreviewName === branchName) {
            currentPreviewMods = mods;
            renderModCards();
        }
    } else if (data.status === 'error') {
        $('#branch-preview-body').innerHTML = `
            <div class="text-center py-8">
                <p class="text-xs text-spire-danger">${esc(I18n.t('branch.loadFailed'))}</p>
            </div>`;
    }
});

// highlight matching parts in text, returns HTML-safe string
function highlightMatch(text, regex) {
    if (!text || !regex) return esc(text || '');
    const escaped = esc(text);
    // rebuild regex against the escaped string (match positions may shift due to &amp; etc.)
    // instead, find spans in original text, map to escaped output
    const parts = [];
    let lastIdx = 0;
    for (const m of text.matchAll(regex)) {
        if (m.index > lastIdx) parts.push(esc(text.slice(lastIdx, m.index)));
        parts.push(`<mark class="mod-highlight">${esc(m[0])}</mark>`);
        lastIdx = m.index + m[0].length;
    }
    if (lastIdx < text.length) parts.push(esc(text.slice(lastIdx)));
    return parts.length ? parts.join('') : escaped;
}

function buildSearchRegex(query) {
    if (!query) return null;
    const escaped = query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const pattern = modSearchWholeWord ? `\\b${escaped}\\b` : escaped;
    const flags = modSearchCaseSensitive ? 'g' : 'gi';
    try { return new RegExp(pattern, flags); }
    catch { return null; }
}

function renderModCards() {
    const body = $('#branch-preview-body');
    const mods = currentPreviewMods;

    if (mods.length === 0) {
        body.innerHTML = `
            <div class="text-center py-8">
                <p class="text-xs text-spire-muted">${esc(I18n.t('branch.noMods'))}</p>
            </div>`;
        return;
    }

    const query = $('#mod-search-input').value.trim();
    const regex = buildSearchRegex(query);

    // filter: keep mods where any visible field matches
    const filtered = regex
        ? mods.filter(m =>
            [m.name, m.id, m.author, m.version, m.description]
                .some(f => f && regex.test(f)))
        : mods;

    if (filtered.length === 0) {
        body.innerHTML = `
            <div class="text-center py-8">
                <p class="text-xs text-spire-muted">${esc(I18n.t('branch.noMatch'))}</p>
            </div>`;
        return;
    }

    const hl = (text) => regex ? highlightMatch(text, new RegExp(regex.source, regex.flags)) : esc(text || '');

    const card = m => `
        <div class="mod-card bg-spire-bg rounded-lg border border-spire-border/50 p-3 break-inside-avoid mb-2">
            <div class="flex items-center justify-between mb-1">
                <span class="text-xs font-medium text-spire-text">${hl(m.name || m.id)}</span>
                <span class="text-[10px] text-spire-muted font-mono">${hl(m.version || '')}</span>
            </div>
            ${m.author ? `<div class="text-[11px] text-spire-muted mb-1">${hl(m.author)}</div>` : ''}
            ${m.description ? `<div class="text-[11px] text-spire-muted/70 leading-relaxed">${hl(m.description)}</div>` : ''}
        </div>`;

    const countText = regex
        ? I18n.t('branch.modCountFiltered', { filtered: filtered.length, total: mods.length })
        : I18n.t('branch.modCount', { count: mods.length });

    body.innerHTML = `
        <div class="text-xs text-spire-muted mb-3">${countText}</div>
        <div class="columns-2 gap-2">
            ${filtered.map(card).join('')}
        </div>`;
}

// search input — live filter
$('#mod-search-input').addEventListener('input', renderModCards);

// toggle buttons
$('#mod-search-case').addEventListener('click', () => {
    modSearchCaseSensitive = !modSearchCaseSensitive;
    $('#mod-search-case').classList.toggle('mod-search-active', modSearchCaseSensitive);
    renderModCards();
});
$('#mod-search-word').addEventListener('click', () => {
    modSearchWholeWord = !modSearchWholeWord;
    $('#mod-search-word').classList.toggle('mod-search-active', modSearchWholeWord);
    renderModCards();
});

$('#branch-preview-back').addEventListener('click', showBranchListView);
$('#branch-preview-close').addEventListener('click', closeBranchModal);

$('#branch-preview-sync').addEventListener('click', async () => {
    const name = branchPreviewName;
    const ok = await showConfirm(
        I18n.t('branch.syncConfirmMessage', { name }),
        I18n.t('branch.syncConfirmTitle')
    );
    if (ok) {
        closeBranchModal();
        sendMessage('SYNC_OTHER_BRANCH', { branchName: name });
    }
});

// basic git branch name validation
function isValidBranchName(name) {
    if (/\s/.test(name)) return false;
    if (/\.\./.test(name)) return false;
    if (/[~^:\\\[\]{}]/.test(name)) return false;
    if (name.startsWith('.') || name.startsWith('-')) return false;
    if (name.endsWith('.') || name.endsWith('/') || name.endsWith('.lock')) return false;
    if (/\/\//.test(name)) return false;
    return true;
}


// ── backup list modal ────────────────────────────────────────────────────────

function renderBackupList(backups) {
    const container = $('#backup-list-body');
    const empty = $('#backup-list-empty');

    if (backups.length === 0) {
        container.innerHTML = '';
        empty.classList.remove('hidden');
        return;
    }
    empty.classList.add('hidden');

    container.innerHTML = backups.map(b => {
        const typeBadge = b.type === 'save'
            ? `<span class="text-[10px] bg-spire-accent/20 text-spire-accent rounded px-1.5 py-0.5">${esc(I18n.t('backup.typeSave'))}</span>`
            : `<span class="text-[10px] bg-spire-warn/20 text-spire-warn rounded px-1.5 py-0.5">${esc(I18n.t('backup.typeMod'))}</span>`;

        const isSave = b.type === 'save';
        const safeName = esc(b.name);
        const safeAttrName = escAttr(b.name);

        return `
            <div class="flex items-center justify-between py-2.5 group">
                <div class="flex items-center gap-2 flex-1 min-w-0 ${isSave ? 'backup-row cursor-pointer' : ''}" data-backup="${safeAttrName}">
                    ${typeBadge}
                    <div class="min-w-0">
                        <div class="text-xs font-mono truncate">${safeName}</div>
                        <div class="text-[10px] text-spire-muted">${formatSize(b.sizeBytes)} · ${formatRelativeTime(b.createdAt)}</div>
                    </div>
                    ${isSave ? `<span class="text-[10px] text-spire-muted ml-auto shrink-0">${esc(I18n.t('backup.clickToRestore'))}</span>` : ''}
                </div>
                <button class="backup-delete-btn text-spire-muted hover:text-spire-danger text-xs ml-2 opacity-0 group-hover:opacity-100 transition-opacity shrink-0" data-name="${safeAttrName}" title="${escAttr(I18n.t('backup.deleteTooltip'))}">
                    <svg width="14" height="14" viewBox="0 0 20 20" fill="currentColor"><path fill-rule="evenodd" d="M9 2a1 1 0 00-.894.553L7.382 4H4a1 1 0 000 2v10a2 2 0 002 2h8a2 2 0 002-2V6a1 1 0 100-2h-3.382l-.724-1.447A1 1 0 0011 2H9zM7 8a1 1 0 012 0v6a1 1 0 11-2 0V8zm5-1a1 1 0 00-1 1v6a1 1 0 102 0V8a1 1 0 00-1-1z" clip-rule="evenodd"/></svg>
                </button>
            </div>
        `;
    }).join('');

    // bind click on save backup rows (for restore)
    container.querySelectorAll('.backup-row').forEach(row => {
        row.addEventListener('click', async () => {
            const name = row.dataset.backup;
            const ok = await showConfirm(
                I18n.t('backup.restoreConfirm', { name }),
                I18n.t('backup.restoreTitle')
            );
            if (ok) {
                sendMessage('RESTORE_BACKUP', { backupName: name });
            }
        });
    });

    // bind delete buttons
    container.querySelectorAll('.backup-delete-btn').forEach(btn => {
        btn.addEventListener('click', async (e) => {
            e.stopPropagation();
            const name = btn.dataset.name;
            const ok = await showConfirm(
                I18n.t('backup.deleteConfirm', { name }),
                I18n.t('backup.deleteTitle')
            );
            if (ok) {
                sendMessage('DELETE_BACKUP', { backupName: name });
            }
        });
    });
}

function closeBackupListModal() {
    $('#backup-list-modal').classList.add('hidden');
}

$('#backup-list-close').addEventListener('click', closeBackupListModal);
$('#backup-list-modal').addEventListener('click', e => {
    if (e.target === $('#backup-list-modal')) closeBackupListModal();
});


// ── settings modal (replaces standalone about modal) ────────────────────────

const REPO_URL = 'https://github.com/Ruikoto/sync-the-spire';
const AUTHOR_URL = 'https://github.com/Ruikoto';

// per-game quote lists — dynamically read from I18n
const QUOTES_BY_GAME = {
    sts2: 'quotes.sts2',
    // generic and other game types: no quotes
};

function setRandomQuote(animate) {
    const el = $('#header-quote');
    const wsInfo = AppState.workspaces[AppState.activeWorkspaceId];
    const gameType = wsInfo?.gameType || '';
    const quotesKey = QUOTES_BY_GAME[gameType];
    const quotes = quotesKey ? I18n.t(quotesKey) : [];
    if (!Array.isArray(quotes) || quotes.length === 0) { el.textContent = ''; return; }
    const pick = () => {
        let q;
        do { q = quotes[Math.floor(Math.random() * quotes.length)]; } while (q === el.textContent && quotes.length > 1);
        return q;
    };
    if (!animate) { el.textContent = pick(); return; }
    el.style.transition = 'opacity 0.15s';
    el.style.opacity = '0';
    setTimeout(() => {
        el.textContent = pick();
        el.style.opacity = '1';
    }, 150);
}

$('#header-quote').addEventListener('click', () => setRandomQuote(true));

$('#about-repo').textContent = 'GitHub';
$('#about-author').textContent = I18n.t('settings.authorName');

$('#about-repo').addEventListener('click', e => { e.preventDefault(); openExternal(REPO_URL); });
$('#about-author').addEventListener('click', e => { e.preventDefault(); openExternal(AUTHOR_URL); });

function openSettingsModal() {
    hideUpdateBadge();
    $('#settings-modal').classList.remove('hidden');
}

function closeSettingsModal() {
    $('#settings-modal').classList.add('hidden');
}

$('#settings-modal-close').addEventListener('click', closeSettingsModal);
$('#settings-modal').addEventListener('click', e => {
    if (e.target === $('#settings-modal')) closeSettingsModal();
});


// ── update check ─────────────────────────────────────────────────────────────

const VERSION_CHECK_URL = 'https://sts.rkto.cc/version.json';
const ANNOUNCEMENTS_URL = 'https://sts.rkto.cc/announcements.json';
let latestVersionInfo = null;

function compareVersions(current, latest) {
    // L9 fix: strip non-numeric suffixes like "-beta" before parsing
    const parse = v => v.replace(/^v/i, '').split('.').map(s => parseInt(s, 10) || 0);
    const c = parse(current), l = parse(latest);
    for (let i = 0; i < 3; i++) {
        if ((l[i] || 0) !== (c[i] || 0)) return (l[i] || 0) - (c[i] || 0);
    }
    return 0;
}

// decide which update tier the current client falls into
function getUpdateBehavior() {
    const info = latestVersionInfo;
    const hasThresholds = info.force_update_below || info.popup_update_below;

    if (hasThresholds) {
        if (info.force_update_below && compareVersions(AppState.appVersion, info.force_update_below) >= 0)
            return 'forced';
        if (info.popup_update_below && compareVersions(AppState.appVersion, info.popup_update_below) >= 0)
            return 'popup';
        return 'silent';
    }

    // legacy fallback — old server without threshold fields
    return info.force_update ? 'forced' : 'popup';
}

function showUpdateBadge() {
    const dot = $('#settings-update-dot');
    if (dot) dot.classList.remove('hidden');
}

function hideUpdateBadge() {
    const dot = $('#settings-update-dot');
    if (dot) dot.classList.add('hidden');
}

function updateAboutVersionStatus() {
    const row = $('#about-update-row');
    const latestEl = $('#about-latest');
    const dlBtn = $('#about-download');

    if (!latestVersionInfo) {
        row.classList.add('hidden');
        return;
    }

    row.classList.remove('hidden');
    const isNightly = AppState.appVersion.startsWith('nightly-');
    const hasUpdate = !isNightly && AppState.appVersion !== 'unknown' &&
                      compareVersions(AppState.appVersion, latestVersionInfo.latest_version) > 0;

    if (isNightly || hasUpdate) {
        latestEl.textContent = latestVersionInfo.latest_version;
        latestEl.className = 'font-mono text-xs text-spire-success';
        dlBtn.classList.remove('hidden');
    } else {
        latestEl.textContent = latestVersionInfo.latest_version + I18n.t('settings.upToDate');
        latestEl.className = 'font-mono text-xs text-spire-muted';
        dlBtn.classList.add('hidden');
    }
}

function getDownloadUrl() {
    if (!latestVersionInfo) return null;
    return AppState.appArch === 'arm64'
        ? latestVersionInfo.download_url_arm
        : latestVersionInfo.download_url_x64;
}

// dedicated update modal — supports changelog display and forced updates
function showUpdateModal(isForced) {
    const modal = $('#update-modal');
    const titleEl = $('#update-title');
    const changelogEl = $('#update-changelog');
    const closeBtn = $('#update-modal-close');
    const cancelBtn = $('#update-cancel');
    const storeBtn = $('#update-store');
    const downloadBtn = $('#update-download');

    titleEl.textContent = I18n.t('modals.update.title');

    // show version comparison
    $('#update-version-info').textContent = `${AppState.appVersion} → ${latestVersionInfo.latest_version}`;

    // render changelog as markdown
    const changelog = latestVersionInfo.changelog;
    if (changelog) {
        changelogEl.innerHTML = marked.parse(changelog);
        changelogEl.classList.remove('hidden');
    } else {
        changelogEl.classList.add('hidden');
    }

    // forced update: hide close/cancel, block escape and backdrop click
    if (isForced) {
        closeBtn.classList.add('hidden');
        cancelBtn.classList.add('hidden');
    } else {
        closeBtn.classList.remove('hidden');
        cancelBtn.classList.remove('hidden');
    }

    // adjust buttons based on distribution channel
    if (AppState.appDistribution === 'store') {
        // store: use the primary store button for in-app update, hide zip download
        storeBtn.textContent = I18n.t('modals.update.storeUpdate');
        storeBtn.classList.remove('hidden');
        downloadBtn.classList.add('hidden');
    } else {
        // direct exe: show both — zip download (secondary) + store link (primary)
        storeBtn.textContent = I18n.t('modals.update.storeGet');
        storeBtn.classList.remove('hidden');
        downloadBtn.textContent = I18n.t('modals.update.download');
        downloadBtn.classList.remove('hidden');
    }

    modal.classList.remove('hidden');
}

function closeUpdateModal() {
    $('#update-modal').classList.add('hidden');
}

// update modal event bindings
$('#update-download').addEventListener('click', () => {
    // zip download — only visible for EXE users
    const url = getDownloadUrl();
    if (url) openExternal(url);
});
$('#update-store').addEventListener('click', () => {
    closeUpdateModal();
    if (AppState.appDistribution === 'store') {
        // trigger Store in-app update
        sendMessage('INSTALL_STORE_UPDATE');
    } else {
        // EXE users: open the Store page
        openExternal('ms-windows-store://pdp/?ProductId=9PC112T0C074');
    }
});
$('#update-cancel').addEventListener('click', closeUpdateModal);
$('#update-modal-close').addEventListener('click', closeUpdateModal);
$('#update-modal').addEventListener('click', e => {
    // only close on backdrop if not forced
    if (e.target === $('#update-modal') && !$('#update-cancel').classList.contains('hidden')) {
        closeUpdateModal();
    }
});

async function checkForUpdates(silent = true) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 10000);
    try {
        const res = await fetch(VERSION_CHECK_URL, { cache: 'no-cache', signal: controller.signal });
        if (!res.ok) return;
        latestVersionInfo = await res.json();
        updateAboutVersionStatus();

        // nightly / dev / unknown builds: skip update prompts entirely
        if (!/^v?\d+\.\d+/.test(AppState.appVersion)) return;

        // store version: delegate update detection to Store API, use version.json only for changelog/display
        if (AppState.appDistribution === 'store') {
            await checkForStoreUpdates(silent);
            return;
        }

        const hasUpdate = compareVersions(AppState.appVersion, latestVersionInfo.latest_version) > 0;

        if (hasUpdate) {
            const behavior = getUpdateBehavior();

            if (behavior === 'forced') {
                showUpdateModal(true);
            } else if (behavior === 'popup' || !silent) {
                // popup tier: show dismissible modal on startup
                // silent tier: only show when user manually checks
                showUpdateModal(false);
            }

            // badge hint for silent-tier updates
            if (behavior === 'silent') {
                showUpdateBadge();
            }
        } else if (!silent) {
            toast(I18n.t('toast.upToDate'), 'success');
        }
    } catch (e) {
        if (!silent) toast(I18n.t('toast.updateCheckFailed'), 'error');
    } finally {
        clearTimeout(timeout);
    }
}

// store-specific update check via IPC — version.json already fetched at this point
let storeUpdateResolve = null;
async function checkForStoreUpdates(silent) {
    sendMessage('CHECK_STORE_UPDATE');
    // wait for the IPC response with a timeout
    const result = await Promise.race([
        new Promise(resolve => { storeUpdateResolve = resolve; }),
        new Promise(resolve => setTimeout(() => resolve(null), 15000))
    ]);
    storeUpdateResolve = null;

    // store has no update available (or API failed / timed out) — do nothing.
    // we intentionally skip any version.json fallback here to avoid confusing users
    // who'd see an update prompt but find nothing in the Store.
    if (!result || !result.available) {
        if (!silent) toast(I18n.t('toast.upToDate'), 'success');
        return;
    }

    // Store confirms update available — use version.json thresholds to decide behavior
    const behavior = getUpdateBehavior();
    if (behavior === 'forced') {
        showUpdateModal(true);
    } else if (behavior === 'popup' || !silent) {
        showUpdateModal(false);
    }
    if (behavior === 'silent') {
        showUpdateBadge();
    }
}

on('CHECK_STORE_UPDATE', data => {
    if (storeUpdateResolve) {
        storeUpdateResolve(data.status === 'success' ? data.payload : null);
    }
});

on('INSTALL_STORE_UPDATE', data => {
    if (data.status !== 'success') {
        // Store install failed — fallback to opening store page
        openExternal('ms-windows-store://pdp/?ProductId=9PC112T0C074');
        return;
    }
    const result = data.payload?.result;
    if (result === 'completed') {
        toast(I18n.t('toast.updateDone'), 'success');
        closeUpdateModal();
    } else if (result === 'canceled') {
        toast(I18n.t('toast.updateCanceled'), 'info');
    } else if (result === 'no_updates') {
        toast(I18n.t('toast.upToDate'), 'success');
        closeUpdateModal();
    } else {
        // error — fallback to store page
        openExternal('ms-windows-store://pdp/?ProductId=9PC112T0C074');
    }
});

$('#about-download').addEventListener('click', e => {
    e.preventDefault();
    // close settings first, then show full update modal with changelog
    closeSettingsModal();
    showUpdateModal(false);
});

$('#btn-check-update').addEventListener('click', () => checkForUpdates(false));
$('#btn-open-docs').addEventListener('click', () => openExternal('https://sts.rkto.cc/guide/getting-started'));


// ── announcements ────────────────────────────────────────────────────────────

async function checkAnnouncements() {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 10000);
    try {
        // fetch remote announcements and dismissed IDs from C# in parallel
        const [res, dismissedResult] = await Promise.all([
            fetch(ANNOUNCEMENTS_URL, { cache: 'no-cache', signal: controller.signal }),
            ipcCall('GET_DISMISSED_ANNOUNCEMENTS'),
        ]);
        if (!res.ok) return;
        const data = await res.json();
        const announcements = data.announcements || [];
        if (announcements.length === 0) return;

        const dismissed = dismissedResult.payload?.ids || [];
        const now = Date.now();
        const container = $('#announcement-container');
        container.innerHTML = '';

        const colorMap = {
            info: { border: 'border-spire-accent', bg: 'bg-spire-accent/10', text: 'text-spire-accent' },
            warning: { border: 'border-spire-warn', bg: 'bg-spire-warn/20', text: 'text-spire-warn' },
            error: { border: 'border-spire-danger', bg: 'bg-spire-danger/20', text: 'text-spire-danger' },
        };

        announcements.forEach(a => {
            // skip disabled, dismissed, or expired
            if (a.enabled === false) return;
            if (dismissed.includes(a.id)) return;
            if (a.expires_at && new Date(a.expires_at).getTime() < now) return;

            const colors = colorMap[a.type] || colorMap.info;
            const banner = document.createElement('div');
            banner.className = `announcement-banner rounded-lg border ${colors.border} ${colors.bg} p-2.5 flex items-start gap-2`;

            const content = document.createElement('div');
            content.className = 'flex-1 min-w-0';
            if (a.title) {
                const title = document.createElement('div');
                title.className = `text-sm font-bold ${colors.text}`;
                title.textContent = a.title;
                content.appendChild(title);
            }
            const body = document.createElement('div');
            body.className = 'text-xs text-spire-muted leading-relaxed announcement-body';
            body.innerHTML = marked.parse(a.content);
            content.appendChild(body);
            banner.appendChild(content);

            if (a.dismissible !== false) {
                const btn = document.createElement('button');
                btn.className = 'dismiss-btn text-spire-muted hover:text-spire-text text-lg leading-none transition-colors shrink-0 px-1';
                btn.innerHTML = '&times;';
                btn.addEventListener('click', () => {
                    sendMessage('DISMISS_ANNOUNCEMENT', { id: a.id });
                    banner.style.opacity = '0';
                    banner.style.transition = 'opacity 0.3s';
                    setTimeout(() => banner.remove(), 300);
                });
                banner.appendChild(btn);
            }

            container.appendChild(banner);
        });
    } catch {
        // fail silently — announcements are non-critical
    } finally {
        clearTimeout(timeout);
    }
}

// ── mod diff preview modal ──────────────────────────────────────────────────

function showModDiffModal(direction) {
    return new Promise(async (resolve) => {
        const modal = $('#mod-diff-modal');
        const body = $('#mod-diff-body');
        const icon = $('#mod-diff-icon');
        const title = $('#mod-diff-title');
        const confirmBtn = $('#mod-diff-confirm');
        const unchangedEl = $('#mod-diff-unchanged');

        const isPush = direction === 'push';
        title.textContent = I18n.t(isPush ? 'modDiff.titlePush' : 'modDiff.titlePull');
        confirmBtn.textContent = I18n.t(isPush ? 'modDiff.confirmPush' : 'modDiff.confirmPull');
        // swap lucide icon
        icon.setAttribute('data-lucide', isPush ? 'upload' : 'download');
        lucide.createIcons({ nodes: [icon] });

        // loading state
        body.innerHTML = `<div class="flex items-center justify-center py-10">
            <div class="spinner"></div>
            <span class="text-xs text-spire-muted ml-3">${esc(I18n.t('modDiff.loading'))}</span>
        </div>`;
        unchangedEl.textContent = '';
        modal.classList.remove('hidden');

        // fetch mod data
        const res = await ipcCall('GET_MOD_DIFF');
        if (res.status !== 'success') {
            modal.classList.add('hidden');
            toast(res.message || 'Failed to get mod diff', 'error');
            resolve(false);
            return;
        }

        const localMods = res.payload.local || [];
        const remoteMods = res.payload.remote || [];

        // for push: local is "new state", remote is "old state"
        // for pull: remote is "new state", local is "old state"
        const newSide = isPush ? localMods : remoteMods;
        const oldSide = isPush ? remoteMods : localMods;
        const diff = computeModDiff(newSide, oldSide);

        renderModDiff(body, diff, unchangedEl);

        let settled = false;
        function cleanup() {
            modal.classList.add('hidden');
            confirmBtn.removeEventListener('click', onConfirm);
            $('#mod-diff-cancel').removeEventListener('click', onCancel);
            $('#mod-diff-close').removeEventListener('click', onCancel);
            modal.removeEventListener('click', onBackdrop);
            document.removeEventListener('keydown', onKey);
        }
        function onConfirm() { if (!settled) { settled = true; cleanup(); resolve(true); } }
        function onCancel()  { if (!settled) { settled = true; cleanup(); resolve(false); } }
        function onBackdrop(e) { if (e.target === modal) onCancel(); }
        function onKey(e) { if (e.key === 'Escape') onCancel(); }

        confirmBtn.addEventListener('click', onConfirm);
        $('#mod-diff-cancel').addEventListener('click', onCancel);
        $('#mod-diff-close').addEventListener('click', onCancel);
        modal.addEventListener('click', onBackdrop);
        document.addEventListener('keydown', onKey);
    });
}

// match mods by name (case-insensitive), fallback to id
function computeModDiff(newMods, oldMods) {
    const key = m => (m.name || m.id || '').toLowerCase();

    const oldMap = new Map();
    for (const m of oldMods) oldMap.set(key(m), m);

    const added = [];
    const updated = [];
    const matched = new Set();

    for (const m of newMods) {
        const k = key(m);
        const old = oldMap.get(k);
        if (!old) {
            added.push(m);
        } else {
            matched.add(k);
            if ((m.version || '') !== (old.version || '')) {
                updated.push({ name: m.name || m.id, author: m.author, oldVersion: old.version || '\u2014', newVersion: m.version || '\u2014' });
            }
        }
    }

    const removed = [];
    for (const m of oldMods) {
        if (!matched.has(key(m))) {
            removed.push(m);
        }
    }

    const unchanged = oldMods.length - updated.length - removed.length;
    return { added, updated, removed, unchanged: Math.max(0, unchanged) };
}

function renderModDiff(container, diff, unchangedEl) {
    const { added, updated, removed, unchanged } = diff;
    const hasChanges = added.length > 0 || updated.length > 0 || removed.length > 0;

    if (!hasChanges) {
        container.innerHTML = `
            <div class="flex flex-col items-center justify-center py-10 gap-2">
                <i data-lucide="check-circle-2" class="w-8 h-8 text-spire-success" style="width:32px;height:32px"></i>
                <p class="text-xs text-spire-muted">${esc(I18n.t('modDiff.noDiff'))}</p>
            </div>`;
        lucide.createIcons({ nodes: container.querySelectorAll('[data-lucide]') });
        unchangedEl.textContent = I18n.t('modDiff.unchangedCount', { count: unchanged });
        return;
    }

    let html = '';

    if (added.length > 0) {
        html += modDiffSection(I18n.t('modDiff.added'), added.length, 'added', added.map(m => `
            <div class="mod-diff-card mod-diff-added">
                <div class="flex items-center justify-between">
                    <span class="text-xs font-medium text-spire-text">${esc(m.name || m.id)}</span>
                    <span class="text-[10px] text-spire-muted font-mono">${esc(m.version || '')}</span>
                </div>
                ${m.author ? `<div class="text-[11px] text-spire-muted">${esc(m.author)}</div>` : ''}
            </div>`).join(''));
    }

    if (updated.length > 0) {
        html += modDiffSection(I18n.t('modDiff.updated'), updated.length, 'updated', updated.map(u => `
            <div class="mod-diff-card mod-diff-updated">
                <div class="flex items-center justify-between">
                    <span class="text-xs font-medium text-spire-text">${esc(u.name)}</span>
                    <span class="text-[10px] font-mono">
                        <span class="text-spire-muted">${esc(u.oldVersion)}</span>
                        <span class="text-spire-muted/50 mx-1">\u2192</span>
                        <span class="text-spire-warn">${esc(u.newVersion)}</span>
                    </span>
                </div>
                ${u.author ? `<div class="text-[11px] text-spire-muted">${esc(u.author)}</div>` : ''}
            </div>`).join(''));
    }

    if (removed.length > 0) {
        html += modDiffSection(I18n.t('modDiff.removed'), removed.length, 'removed', removed.map(m => `
            <div class="mod-diff-card mod-diff-removed">
                <div class="flex items-center justify-between">
                    <span class="text-xs font-medium text-spire-text/60 line-through">${esc(m.name || m.id)}</span>
                    <span class="text-[10px] text-spire-muted/50 font-mono">${esc(m.version || '')}</span>
                </div>
                ${m.author ? `<div class="text-[11px] text-spire-muted/50">${esc(m.author)}</div>` : ''}
            </div>`).join(''));
    }

    container.innerHTML = html;
    unchangedEl.textContent = unchanged > 0 ? I18n.t('modDiff.unchangedCount', { count: unchanged }) : '';
}

function modDiffSection(label, count, type, cardsHtml) {
    const colors = { added: '#22c55e', updated: '#f59e0b', removed: '#ef4444' };
    const color = colors[type] || '#94a3b8';
    return `
        <div class="mb-4">
            <div class="flex items-center gap-2 mb-2">
                <div class="w-1.5 h-1.5 rounded-full" style="background:${color}"></div>
                <span class="text-[11px] font-medium" style="color:${color}">${esc(label)}</span>
                <span class="text-[10px] text-spire-muted">${count}</span>
            </div>
            <div class="flex flex-col gap-1.5">${cardsHtml}</div>
        </div>`;
}


// ── mod load order modal ────────────────────────────────────────────────────

let modOrderMods = [];
let modOrderConsistent = true;

async function openModOrderModal() {
    const modal = $('#mod-order-modal');
    const list = $('#mod-order-list');
    const warning = $('#mod-order-warning');
    const saveBtn = $('#mod-order-save');

    // reset state
    list.innerHTML = `<div class="flex items-center justify-center py-8 text-xs text-spire-muted">${esc(I18n.t('common.processing'))}</div>`;
    warning.classList.add('hidden');
    saveBtn.disabled = false;
    saveBtn.classList.remove('opacity-50', 'cursor-not-allowed');
    modal.classList.remove('hidden');

    try {
        const result = await ipcCall('GET_MOD_ORDER');
        const payload = result.payload;
        modOrderMods = payload.mods || [];
        modOrderConsistent = payload.consistent;

        if (!payload.modsEnabled) {
            list.innerHTML = `<div class="flex flex-col items-center justify-center py-8 gap-2 text-xs text-spire-muted">
                <span>${esc(I18n.t('modOrder.modsDisabled'))}</span>
            </div>`;
            saveBtn.disabled = true;
            saveBtn.classList.add('opacity-50', 'cursor-not-allowed');
            return;
        }

        if (modOrderMods.length === 0) {
            list.innerHTML = `<div class="flex items-center justify-center py-8 text-xs text-spire-muted">${esc(I18n.t('modOrder.empty'))}</div>`;
            saveBtn.disabled = true;
            saveBtn.classList.add('opacity-50', 'cursor-not-allowed');
            return;
        }

        if (!modOrderConsistent) {
            warning.classList.remove('hidden');
            saveBtn.disabled = true;
            saveBtn.classList.add('opacity-50', 'cursor-not-allowed');
        }

        renderModOrderList();
    } catch {
        list.innerHTML = `<div class="flex items-center justify-center py-8 text-xs text-spire-danger">${esc(I18n.t('modOrder.loadFailed'))}</div>`;
    }
}

function moveModOrder(fromIdx, toIdx) {
    if (fromIdx === toIdx || toIdx < 0 || toIdx >= modOrderMods.length) return;
    const list = $('#mod-order-list');
    const scrollTop = list.scrollTop;
    const [moved] = modOrderMods.splice(fromIdx, 1);
    modOrderMods.splice(toIdx, 0, moved);
    renderModOrderList();
    // restore scroll and keep the moved item visible
    list.scrollTop = scrollTop;
    const movedItem = list.querySelector(`[data-index="${toIdx}"]`);
    if (movedItem) movedItem.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

function renderModOrderList() {
    const list = $('#mod-order-list');
    const scrollTop = list.scrollTop;
    list.innerHTML = '';
    const canDrag = modOrderConsistent;
    const lastIdx = modOrderMods.length - 1;

    modOrderMods.forEach((mod, idx) => {
        const item = document.createElement('div');
        item.className = 'mod-order-item flex items-center gap-2 px-3 py-2 rounded-lg border border-spire-border/50 bg-spire-bg/50 select-none transition-all';
        if (canDrag) item.className += ' cursor-grab';
        item.draggable = canDrag;
        item.dataset.index = idx;

        // grip handle (6-dot pattern)
        const grip = document.createElement('div');
        grip.className = 'flex flex-col gap-[2px] shrink-0 text-spire-muted/40';
        grip.innerHTML = `<svg width="8" height="14" viewBox="0 0 8 14" fill="currentColor" style="width:8px;height:14px">
            <circle cx="2" cy="2" r="1.2"/><circle cx="6" cy="2" r="1.2"/>
            <circle cx="2" cy="7" r="1.2"/><circle cx="6" cy="7" r="1.2"/>
            <circle cx="2" cy="12" r="1.2"/><circle cx="6" cy="12" r="1.2"/>
        </svg>`;

        // position number
        const num = document.createElement('span');
        num.className = 'text-[10px] text-spire-muted/50 font-mono w-4 text-right shrink-0';
        num.textContent = idx + 1;

        // mod info
        const info = document.createElement('div');
        info.className = 'flex-1 min-w-0';
        const nameRow = document.createElement('div');
        nameRow.className = 'flex items-center gap-2';
        const name = document.createElement('span');
        name.className = 'text-xs font-medium text-spire-text truncate';
        name.textContent = mod.name || mod.id;
        nameRow.appendChild(name);

        if (mod.version) {
            const ver = document.createElement('span');
            ver.className = 'text-[10px] text-spire-muted/60 font-mono shrink-0';
            ver.textContent = mod.version;
            nameRow.appendChild(ver);
        }

        if (!mod.isEnabled) {
            const badge = document.createElement('span');
            badge.className = 'text-[10px] px-1.5 py-0.5 rounded bg-spire-muted/20 text-spire-muted/60 shrink-0';
            badge.textContent = I18n.t('modOrder.disabled');
            nameRow.appendChild(badge);
        }

        info.appendChild(nameRow);

        if (mod.author) {
            const author = document.createElement('div');
            author.className = 'text-[10px] text-spire-muted/50 truncate';
            author.textContent = mod.author;
            info.appendChild(author);
        }

        // move buttons (top / up / down / bottom)
        const btns = document.createElement('div');
        btns.className = 'flex items-center gap-0.5 shrink-0';
        if (canDrag) {
            const mkBtn = (icon, title, disabled, onClick) => {
                const b = document.createElement('button');
                b.className = 'mod-order-btn p-1.5 rounded text-spire-muted hover:text-spire-accent hover:bg-spire-accent/10 transition-colors';
                b.innerHTML = icon;
                b.title = title;
                if (disabled) {
                    b.classList.add('opacity-0', 'pointer-events-none');
                    b.disabled = true;
                }
                b.addEventListener('click', e => { e.stopPropagation(); onClick(); });
                return b;
            };
            // svg icons — inline with fixed size to avoid tailwind rebuild dependency
            const iconTop = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" style="width:15px;height:15px"><line x1="4" y1="3" x2="12" y2="3"/><polyline points="5,9 8,6 11,9"/><line x1="8" y1="6" x2="8" y2="13"/></svg>';
            const iconUp = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" style="width:15px;height:15px"><polyline points="5,9 8,6 11,9"/><line x1="8" y1="6" x2="8" y2="13"/></svg>';
            const iconDown = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" style="width:15px;height:15px"><polyline points="5,7 8,10 11,7"/><line x1="8" y1="3" x2="8" y2="10"/></svg>';
            const iconBottom = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" style="width:15px;height:15px"><polyline points="5,7 8,10 11,7"/><line x1="8" y1="3" x2="8" y2="10"/><line x1="4" y1="13" x2="12" y2="13"/></svg>';

            btns.appendChild(mkBtn(iconTop, I18n.t('modOrder.moveTop'), idx === 0, () => moveModOrder(idx, 0)));
            btns.appendChild(mkBtn(iconUp, I18n.t('modOrder.moveUp'), idx === 0, () => moveModOrder(idx, idx - 1)));
            btns.appendChild(mkBtn(iconDown, I18n.t('modOrder.moveDown'), idx === lastIdx, () => moveModOrder(idx, idx + 1)));
            btns.appendChild(mkBtn(iconBottom, I18n.t('modOrder.moveBottom'), idx === lastIdx, () => moveModOrder(idx, lastIdx)));
        }

        item.appendChild(grip);
        item.appendChild(num);
        item.appendChild(info);
        item.appendChild(btns);
        list.appendChild(item);
    });

    // restore scroll position after re-render
    list.scrollTop = scrollTop;
    initModOrderDragAndDrop();
}

// ── HTML5 drag-and-drop for mod order list ──

let modDragIndex = null;
let modDropIndicator = null;

function initModOrderDragAndDrop() {
    const list = $('#mod-order-list');

    // clean up old listeners by cloning the node
    const newList = list.cloneNode(true);
    list.parentNode.replaceChild(newList, list);

    newList.addEventListener('dragstart', e => {
        const item = e.target.closest('.mod-order-item');
        if (!item) return;
        modDragIndex = parseInt(item.dataset.index);
        item.classList.add('opacity-30');
        e.dataTransfer.effectAllowed = 'move';
        requestAnimationFrame(() => item.classList.add('dragging'));
    });

    newList.addEventListener('dragend', e => {
        const item = e.target.closest('.mod-order-item');
        if (item) {
            item.classList.remove('opacity-30', 'dragging');
        }
        modDragIndex = null;
        removeDropIndicator();
    });

    newList.addEventListener('dragover', e => {
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        const item = e.target.closest('.mod-order-item');
        if (!item || modDragIndex === null) return;

        const rect = item.getBoundingClientRect();
        const midY = rect.top + rect.height / 2;
        const insertBefore = e.clientY < midY;
        showDropIndicator(item, insertBefore);
    });

    newList.addEventListener('dragleave', e => {
        if (!e.relatedTarget || !newList.contains(e.relatedTarget)) {
            removeDropIndicator();
        }
    });

    newList.addEventListener('drop', e => {
        e.preventDefault();
        const item = e.target.closest('.mod-order-item');
        if (!item || modDragIndex === null) return;

        const targetIndex = parseInt(item.dataset.index);
        const rect = item.getBoundingClientRect();
        const midY = rect.top + rect.height / 2;
        let dropIndex = e.clientY < midY ? targetIndex : targetIndex + 1;

        if (modDragIndex < dropIndex) dropIndex--;
        if (modDragIndex !== dropIndex && dropIndex >= 0 && dropIndex <= modOrderMods.length) {
            moveModOrder(modDragIndex, dropIndex);
        }

        modDragIndex = null;
        removeDropIndicator();
    });
}

function showDropIndicator(targetItem, before) {
    removeDropIndicator();
    modDropIndicator = document.createElement('div');
    modDropIndicator.className = 'mod-drop-indicator h-0.5 bg-spire-accent rounded-full mx-2 transition-all';
    if (before) {
        targetItem.parentNode.insertBefore(modDropIndicator, targetItem);
    } else {
        targetItem.parentNode.insertBefore(modDropIndicator, targetItem.nextSibling);
    }
}

function removeDropIndicator() {
    if (modDropIndicator) {
        modDropIndicator.remove();
        modDropIndicator = null;
    }
}

async function saveModOrder() {
    const orderedIds = modOrderMods.map(m => m.id);
    try {
        await ipcCall('SAVE_MOD_ORDER', { orderedIds });
        toast(I18n.t('modOrder.saved'), 'success');
        closeModOrderModal();
    } catch {
        toast(I18n.t('modOrder.saveFailed'), 'error');
    }
}

function closeModOrderModal() {
    $('#mod-order-modal').classList.add('hidden');
    modOrderMods = [];
}

$('#mod-order-close').addEventListener('click', closeModOrderModal);
$('#mod-order-cancel').addEventListener('click', closeModOrderModal);
$('#mod-order-save').addEventListener('click', saveModOrder);
$('#mod-order-modal').addEventListener('click', e => {
    if (e.target === $('#mod-order-modal')) closeModOrderModal();
});
