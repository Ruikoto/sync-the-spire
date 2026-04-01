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
    const lines = ['此分支的 MOD 可能包含 NSFW 内容。', '原因：', ...(reasons || []).map(r => '• ' + r)];
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
        const tag = isCurrent ? ' <span class="text-spire-accent text-[10px] ml-1">(当前)</span>' : '';
        const nsfwBadge = b.isNsfw ? `<span class="nsfw-badge" title="${nsfwTooltip(b.nsfwReasons)}">NSFW</span>` : '';
        return `<tr class="branch-row border-b border-spire-border/50 cursor-pointer transition-colors ${rowHighlight}" data-branch="${escAttr(b.name)}">
            <td class="px-4 py-2.5 font-mono text-xs">${nsfwBadge}${esc(b.name)}${tag}</td>
            <td class="px-4 py-2.5 text-xs text-spire-muted">${esc(b.author)}</td>
            <td class="px-4 py-2.5 text-xs text-spire-muted">${formatRelativeTime(b.lastModified)}</td>
        </tr>`;
    }).join('');

    // bind row click -> open mod preview
    tbody.querySelectorAll('.branch-row').forEach(row => {
        row.addEventListener('click', () => {
            openBranchPreview(row.dataset.branch);
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
            <p class="text-xs text-spire-muted mt-3">正在读取 Mod 列表...</p>
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
                <p class="text-xs text-spire-danger">读取失败，请返回重试</p>
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
                <p class="text-xs text-spire-muted">该分支没有检测到 Mod</p>
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
                <p class="text-xs text-spire-muted">没有匹配的 Mod</p>
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
        ? `${filtered.length} / ${mods.length} 个 Mod`
        : `${mods.length} 个 Mod`;

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
        `确定要强制同步到「${name}」？本地改动将被覆盖。`,
        '同步分支'
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
            ? '<span class="text-[10px] bg-spire-accent/20 text-spire-accent rounded px-1.5 py-0.5">存档</span>'
            : '<span class="text-[10px] bg-spire-warn/20 text-spire-warn rounded px-1.5 py-0.5">Mod</span>';

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
                    ${isSave ? '<span class="text-[10px] text-spire-muted ml-auto shrink-0">点击恢复 →</span>' : ''}
                </div>
                <button class="backup-delete-btn text-spire-muted hover:text-spire-danger text-xs ml-2 opacity-0 group-hover:opacity-100 transition-opacity shrink-0" data-name="${safeAttrName}" title="删除备份">
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
                `确定要恢复备份「${name}」？\n当前存档将被自动备份后替换。`,
                '恢复存档备份'
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
                `确定要删除备份「${name}」？\n此操作不可撤销。`,
                '删除备份'
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

const QUOTES_STS2 = [
    '此事已成',
    '咔咔！',
    '咕嗷。。。',
    '你管这叫武器？',
    '败北？不可能的！！',
    '为什么你还在这里？',
    '永远别放弃！',
    '小的们，给我上！',
    '我们还没完呢！',
    '逃命哇！',
    '啊嗷嗷呜嗷！！',
    '在蓄能啦！',
    '要来咯！！',
    '把钱交出来！',
    '我的烟雾弹在哪儿呢……我找找……',
    '多谢你的钱啦，嘿嘿',
    '给我交出来！',
    '我可溜了！',
    '史莱姆撞击！！',
    '侦测到外来者',
    '你是我的了！',
    '啊……居然有人来了…',
    '愚蠢……何等愚蠢！',
    '一直……都不…喜欢你……',
    '嘎！哑！',
    '重生中……',
    '是时候了',
    '滴 答 滴 答',
    '撤退 撤退啊！',
    '你好啊！买点什么吧！买买买！',
    '你的发型很不错哦',
    '看起来你还挺闲？',
    '你是狗党还是猫党？',
    '你看起来很~危险~啊，嘿嘿……',
    '慢慢看…… 不慢也行',
    '你喜欢这张地毯吗？可惜这个不卖',
    '要支持我这样的小店啊！',
    '面具就是酷，我也是同党啊！',
    '多留会儿，听听音乐啊！',
    '一个人前进太危险了！把你的钱都给我吧！',
    '买点儿啥吧',
    '我喜欢金币。',
    '我最喜欢的颜色是蓝色。你呢？',
    '这可是最后一次买东西的机会咯！ *挤眼* *挤眼*',
    '其实呢，我是个猫党。',
    '不用着急，不用着急。',
    '曾经我也和你一样。',
    '唷，还往上爬呢？',
    '你好…… 我是… 涅奥……',
    '你又… 来啦……',
    '还想 再来……？',
    '见到 Boss... 就能获得 更多… 祝福……',
    '至少… 也要见到 第一个 Boss 吧……',
    '我把你 带回来了……',
    '选择……',
    '实现了……',
    '风险…… 与回报……',
    '来试试 挑战 吧……',
    '哎呀呀，这点金币可不够啊。',
    '嘿兄弟，你没钱啊！',
    '这个你买不起。',
    '我这儿不是做慈善的。',
    '谢 啦~',
    '又一笔买卖…… 嚯嚯嚯！有得赚！',
    '成交！',
    '概不退换。',
    '祝你顺利啊。',
    '面具就是酷，我也是同党啊！',
    '你有没有看见我的送货员？',
    '多留会儿，听听音乐啊！',
    '一个人前进太危险了！把你的钱都给我吧！',
    '这是… 怎么了…？…难道…真的… …做到了吗…？',
    '…高塔沉睡了… 那么… 我也… …该睡了……',
    '启程去屠戮这座高塔。',
];

// per-game quote lists — add new games here
const QUOTES_BY_GAME = {
    sts2: QUOTES_STS2,
    // generic and other game types: no quotes
};

function setRandomQuote(animate) {
    const el = $('#header-quote');
    const wsInfo = AppState.workspaces[AppState.activeWorkspaceId];
    const gameType = wsInfo?.gameType || '';
    const quotes = QUOTES_BY_GAME[gameType] || [];
    if (quotes.length === 0) { el.textContent = ''; return; }
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
$('#about-author').textContent = 'Ruikoto（泡菜）';

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
    const parse = v => v.replace(/^v/i, '').split('.').map(Number);
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
        latestEl.textContent = latestVersionInfo.latest_version + '（已是最新）';
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

    titleEl.textContent = '发现新版本';

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
        storeBtn.textContent = '立即更新';
        storeBtn.classList.remove('hidden');
        downloadBtn.classList.add('hidden');
    } else {
        // direct exe: show both — zip download (secondary) + store link (primary)
        storeBtn.textContent = '从应用商店获取';
        storeBtn.classList.remove('hidden');
        downloadBtn.textContent = '立即下载';
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
            toast('当前已是最新版本', 'success');
        }
    } catch (e) {
        if (!silent) toast('检查更新失败，请检查网络连接', 'error');
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
        if (!silent) toast('当前已是最新版本', 'success');
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
        toast('更新已完成，重启应用后生效', 'success');
        closeUpdateModal();
    } else if (result === 'canceled') {
        toast('更新已取消', 'info');
    } else if (result === 'no_updates') {
        toast('当前已是最新版本', 'success');
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
