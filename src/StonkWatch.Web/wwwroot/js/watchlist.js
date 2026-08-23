/* Live watchlist sidebar.
   Renders from /api/watchlist, then patches rows from an SSE stream. Deliberately plain:
   one table refreshing is not a framework problem. */
(function () {
    'use strict';

    var sidebar = document.getElementById('watchlist-sidebar');
    if (!sidebar) { return; }

    var body = document.getElementById('watchlist-body');
    var toggle = document.getElementById('watchlist-toggle');
    var status = document.getElementById('watchlist-status');
    var statusText = status.querySelector('.watchlist-status-text');

    var COLLAPSE_KEY = 'stonkwatch.watchlist.collapsed';
    var GROUPS_KEY = 'stonkwatch.watchlist.collapsedGroups';

    // en-CA to match the culture the server pins, so the sidebar and the Razor pages
    // never disagree about what a decimal point looks like.
    var priceFmt = new Intl.NumberFormat('en-CA', {
        minimumFractionDigits: 2, maximumFractionDigits: 2
    });
    var pctFmt = new Intl.NumberFormat('en-CA', {
        minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: 'exceptZero'
    });

    function isBlank(v) { return v === null || v === undefined; }

    function formatVolume(v) {
        if (isBlank(v)) { return '—'; }
        if (v >= 1e9) { return (v / 1e9).toFixed(2) + 'B'; }
        if (v >= 1e6) { return (v / 1e6).toFixed(2) + 'M'; }
        if (v >= 1e3) { return (v / 1e3).toFixed(2) + 'K'; }
        return String(v);
    }

    function readCollapsedGroups() {
        try { return JSON.parse(localStorage.getItem(GROUPS_KEY)) || {}; }
        catch (e) { return {}; }
    }

    function writeCollapsedGroups(map) {
        try { localStorage.setItem(GROUPS_KEY, JSON.stringify(map)); } catch (e) { /* private mode */ }
    }

    // ---------- Collapse ----------

    function applyCollapsed(collapsed) {
        sidebar.classList.toggle('is-collapsed', collapsed);
        document.body.classList.toggle('watchlist-collapsed', collapsed);
        toggle.setAttribute('aria-expanded', String(!collapsed));
    }

    document.body.classList.add('has-watchlist');
    applyCollapsed(localStorage.getItem(COLLAPSE_KEY) === '1');

    toggle.addEventListener('click', function () {
        var collapsed = !sidebar.classList.contains('is-collapsed');
        applyCollapsed(collapsed);
        try { localStorage.setItem(COLLAPSE_KEY, collapsed ? '1' : '0'); } catch (e) { /* private mode */ }
    });

    // ---------- Add & remove ----------

    var addToggle = document.getElementById('watchlist-add');
    var searchPanel = document.getElementById('watchlist-search');
    var searchInput = document.getElementById('watchlist-search-input');
    var resultsList = document.getElementById('watchlist-results');
    var searchNote = document.getElementById('watchlist-search-note');

    var SEARCH_DEBOUNCE_MS = 250;
    var searchTimer = null;

    // Bumped on every keystroke that starts or abandons a search, so a slow answer for "NV"
    // can never paint itself over the results already showing for "NVDA".
    var searchSeq = 0;
    var results = [];
    var activeIndex = -1;

    function note(text) {
        searchNote.textContent = text || '';
        searchNote.hidden = !text;
    }

    function clearResults() {
        results = [];
        activeIndex = -1;
        resultsList.textContent = '';
        searchInput.setAttribute('aria-expanded', 'false');
    }

    function openSearch(open) {
        searchPanel.hidden = !open;
        addToggle.setAttribute('aria-expanded', String(open));

        if (open) {
            searchInput.focus();
        } else {
            searchInput.value = '';
            clearResults();
            note('');
        }
    }

    addToggle.addEventListener('click', function () { openSearch(searchPanel.hidden); });

    function renderResults(items) {
        results = items;
        activeIndex = -1;
        resultsList.textContent = '';

        items.forEach(function (item, index) {
            var li = document.createElement('li');
            li.className = 'watchlist-result';
            li.setAttribute('role', 'option');
            li.setAttribute('aria-selected', 'false');
            li.setAttribute('data-index', String(index));

            var sym = document.createElement('span');
            sym.className = 'watchlist-result-sym';
            sym.textContent = item.symbol;

            var desc = document.createElement('span');
            desc.className = 'watchlist-result-desc';
            desc.textContent = item.description || item.exchange;
            desc.title = item.description ? item.description + ' · ' + item.exchange : item.exchange;

            li.appendChild(sym);
            li.appendChild(desc);
            li.addEventListener('click', function () { addSymbol(item.symbol); });
            resultsList.appendChild(li);
        });

        searchInput.setAttribute('aria-expanded', items.length ? 'true' : 'false');
    }

    function highlight(index) {
        var nodes = resultsList.querySelectorAll('.watchlist-result');
        if (!nodes.length) { return; }

        // Wraps at both ends, so holding Down never dead-ends on the last row.
        if (index < 0) { index = nodes.length - 1; }
        if (index >= nodes.length) { index = 0; }
        activeIndex = index;

        for (var i = 0; i < nodes.length; i++) {
            var on = i === index;
            nodes[i].classList.toggle('is-active', on);
            nodes[i].setAttribute('aria-selected', String(on));
        }
        nodes[index].scrollIntoView({ block: 'nearest' });
    }

    function runSearch(query) {
        var seq = ++searchSeq;

        fetch('/api/watchlist/search?q=' + encodeURIComponent(query),
              { headers: { 'Accept': 'application/json' } })
            .then(function (response) {
                if (response.ok) { return response.json(); }

                // 503 is the server saying Questrade is off or unreachable, and it carries a
                // ProblemDetails explaining which. Typing a ticker still adds it, so this
                // ends up on the note line rather than looking like a broken search.
                return response.json().catch(function () { return {}; })
                    .then(function (problem) {
                        throw new Error(problem.detail || 'Symbol search is unavailable.');
                    });
            })
            .then(function (items) {
                if (seq !== searchSeq) { return; }
                renderResults(items);
                note(items.length ? '' : 'No matching US listing. Enter adds it anyway.');
            })
            .catch(function (error) {
                if (seq !== searchSeq) { return; }
                clearResults();
                note(error.message);
            });
    }

    searchInput.addEventListener('input', function () {
        var query = searchInput.value.trim();
        window.clearTimeout(searchTimer);

        if (!query) {
            searchSeq++;            // abandons whatever is still in flight
            clearResults();
            note('');
            return;
        }

        searchTimer = window.setTimeout(function () { runSearch(query); }, SEARCH_DEBOUNCE_MS);
    });

    searchInput.addEventListener('keydown', function (event) {
        if (event.key === 'ArrowDown') {
            event.preventDefault();
            highlight(activeIndex + 1);
        } else if (event.key === 'ArrowUp') {
            event.preventDefault();
            highlight(activeIndex - 1);
        } else if (event.key === 'Escape') {
            event.preventDefault();
            openSearch(false);
            addToggle.focus();
        } else if (event.key === 'Enter') {
            event.preventDefault();
            // A highlighted suggestion wins; otherwise whatever was typed. That fallback is
            // what keeps the box working with Questrade switched off, when there are no
            // suggestions to pick from and the server still accepts a bare ticker.
            var chosen = activeIndex >= 0 && results[activeIndex]
                ? results[activeIndex].symbol
                : searchInput.value.trim();
            if (chosen) { addSymbol(chosen); }
        }
    });

    function addSymbol(symbol) {
        window.clearTimeout(searchTimer);
        searchSeq++;
        note('Adding ' + symbol.toUpperCase() + '…');

        fetch('/api/watchlist/items', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
            body: JSON.stringify({ symbol: symbol })
        })
            .then(function (response) {
                if (response.ok) { return null; }

                // 400 and 409 both arrive as { error: "..." } — an empty symbol, a watchlist
                // at its cap, a duplicate. Every one of those is worth showing verbatim
                // instead of flattening into "add failed".
                return response.json().catch(function () { return {}; })
                    .then(function (body) {
                        throw new Error(body.error || ('Add failed (HTTP ' + response.status + ').'));
                    });
            })
            .then(function () {
                openSearch(false);
                return load();
            })
            .catch(function (error) { note(error.message); });
    }

    function removeItem(item) {
        if (!window.confirm('Remove ' + item.symbol + ' from the watchlist?')) { return; }

        // 404 counts as done: the row is gone either way. The reload is what reconciles this
        // page with what the server actually holds — including on a real failure, where it
        // puts the row back rather than leaving a lie on screen.
        fetch('/api/watchlist/items/' + encodeURIComponent(item.id), { method: 'DELETE' })
            .then(function () { return load(); })
            .catch(function () { /* the stream is still live; the next resync corrects it */ });
    }

    // ---------- Rendering ----------

    // What the status line says for each phase the server reports. Only "Regular" gets the
    // green dot: pre- and post-market do move prices, but calling them "live" would claim a
    // regular session, and a green dot at 02:00 reads as a fault to anyone checking.
    var PHASE_STATUS = {
        Regular: ['live', 'live'],
        PreMarket: ['quiet', 'pre-market'],
        AfterHours: ['quiet', 'after hours'],
        Closed: ['quiet', 'market closed']
    };

    function setStatus(state, text) {
        status.setAttribute('data-state', state);
        statusText.textContent = text;
    }

    function numCell(row, field) {
        return row.querySelector('[data-field="' + field + '"]');
    }

    function buildRow(item) {
        var el = document.createElement('div');
        el.className = 'watchlist-row';
        el.setAttribute('role', 'listitem');
        el.setAttribute('data-row-id', item.id);
        // Focusable and semantic now so making rows clickable later is a behaviour
        // change, not a rebuild.
        el.setAttribute('tabindex', '0');

        var sym = document.createElement('div');
        sym.className = 'watchlist-sym';

        var chip = document.createElement('span');
        chip.className = 'watchlist-chip';
        chip.textContent = item.symbol.charAt(0);

        var label = document.createElement('span');
        label.className = 'watchlist-label';
        label.textContent = item.label;
        label.title = item.symbol;

        sym.appendChild(chip);
        sym.appendChild(label);
        el.appendChild(sym);

        ['last', 'change', 'volume', 'ext'].forEach(function (field) {
            var cell = document.createElement('span');
            cell.className = 'num empty';
            cell.setAttribute('data-field', field);
            cell.textContent = '—';
            el.appendChild(cell);
        });

        // Absolutely positioned (see watchlist.css) so it stays out of the row's grid and
        // cannot add a sixth column the header does not have.
        var remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'watchlist-remove';
        remove.title = 'Remove ' + item.symbol;
        remove.textContent = '\u00d7';
        remove.addEventListener('click', function (event) {
            event.stopPropagation();
            removeItem(item);
        });
        el.appendChild(remove);

        updateRow(el, item, false);
        return el;
    }

    function updateRow(el, item, flash) {
        var last = numCell(el, 'last');
        var previous = last.getAttribute('data-value');

        if (isBlank(item.last)) {
            last.textContent = '—';
            last.className = 'num empty';
            last.removeAttribute('data-value');
        } else {
            last.textContent = priceFmt.format(item.last);
            last.className = 'num';
            last.setAttribute('data-value', item.last);
        }

        var change = numCell(el, 'change');
        if (isBlank(item.changePercent)) {
            // Never render 0.00% for "no baseline yet" — that reads as "flat today",
            // which is a different claim. Tinted neither green nor red, for the same reason.
            change.textContent = '—';
            change.className = 'num empty';
        } else {
            change.textContent = pctFmt.format(item.changePercent) + '%';
            change.className = 'num ' + (item.changePercent >= 0 ? 'up' : 'down');
        }

        var volume = numCell(el, 'volume');
        volume.textContent = formatVolume(item.volume);
        volume.className = isBlank(item.volume) ? 'num empty' : 'num';

        // Extended-hours price. Blank outside pre/post market rather than repeating
        // Last — showing the regular-session price under an "Ext" heading would assert
        // after-hours trading that did not happen.
        var ext = numCell(el, 'ext');
        if (isBlank(item.extendedPrice)) {
            ext.textContent = '—';
            ext.className = 'num empty';
        } else {
            ext.textContent = priceFmt.format(item.extendedPrice);
            ext.className = 'num';
        }

        // An unchanged price does not flash. The old `>=` sent every equal value down the
        // up branch, so any symbol that had not traded since the last poll pulsed green on
        // every tick — all night with the market shut, and during the session too for
        // anything thinly traded. Green has to mean the price rose.
        if (flash && previous !== null && !isBlank(item.last)
            && Number(item.last) !== Number(previous)) {
            var direction = Number(item.last) > Number(previous) ? 'flash-up' : 'flash-down';
            el.classList.remove('flash-up', 'flash-down');
            void el.offsetWidth;   // restart the animation
            el.classList.add(direction);
        }
    }

    function render(view) {
        var collapsedGroups = readCollapsedGroups();
        body.textContent = '';

        if (!view.rows.length) {
            var empty = document.createElement('p');
            empty.className = 'watchlist-empty';
            empty.textContent = 'No symbols yet \u2014 press + to add one.';
            body.appendChild(empty);
            return;
        }

        var byGroup = new Map();
        view.rows.forEach(function (row) {
            var key = row.groupId || '';
            if (!byGroup.has(key)) { byGroup.set(key, []); }
            byGroup.get(key).push(row);
        });

        // Ungrouped first, then named groups in their stored order.
        var order = [{ id: '', name: null }].concat(view.groups.map(function (g) {
            return { id: g.id, name: g.name };
        }));

        order.forEach(function (group) {
            var rows = byGroup.get(group.id);
            if (!rows || !rows.length) { return; }

            var section = document.createElement('div');
            section.className = 'watchlist-group';
            section.setAttribute('data-group-id', group.id);

            if (group.name) {
                if (collapsedGroups[group.id]) { section.classList.add('is-collapsed'); }

                var head = document.createElement('button');
                head.type = 'button';
                head.className = 'watchlist-group-head';
                head.textContent = group.name;
                head.addEventListener('click', function () {
                    var nowCollapsed = section.classList.toggle('is-collapsed');
                    var map = readCollapsedGroups();
                    map[group.id] = nowCollapsed;
                    writeCollapsedGroups(map);
                });
                section.appendChild(head);
            }

            rows.forEach(function (row) { section.appendChild(buildRow(row)); });
            body.appendChild(section);
        });
    }

    // ---------- Data ----------

    function load() {
        return fetch('/api/watchlist', { headers: { 'Accept': 'application/json' } })
            .then(function (response) {
                if (!response.ok) { throw new Error('HTTP ' + response.status); }
                return response.json();
            })
            .then(render);
    }

    // A quote for a symbol this page has no row for means the watchlist changed since we
    // painted — the server re-reads its own symbol list mid-stream, so the stream itself
    // needs no reconnect, only our DOM does. Throttled: an unknown symbol ticking every
    // few seconds must not become a fetch per tick.
    var RESYNC_MS = 5000;
    var lastResync = 0;
    var resyncing = false;

    function resync() {
        var now = Date.now();
        if (resyncing || now - lastResync < RESYNC_MS) { return; }
        resyncing = true;
        lastResync = now;
        load().catch(function () { /* the stream is still live; leave the stale rows up */ })
              .then(function () { resyncing = false; });
    }

    function connect() {
        var source = new EventSource('/api/watchlist/stream');

        source.addEventListener('open', function () { setStatus('live', 'live'); });

        source.addEventListener('quote', function (event) {
            var row;
            try { row = JSON.parse(event.data); } catch (e) { return; }
            if (!row) { return; }

            var el = body.querySelector('[data-row-id="' + row.id + '"]');
            if (el) { updateRow(el, row, true); } else { resync(); }
        });

        // Phase changes arrive on their own event, first on connect and then at each bell.
        // Without it a closed market is indistinguishable from a broken poller: the rows
        // simply stop moving under a status line still saying "live".
        source.addEventListener('phase', function (event) {
            var payload;
            try { payload = JSON.parse(event.data); } catch (e) { return; }
            // An unknown phase name is left alone rather than blanked: a server newer than
            // this script should not be able to empty the status line.
            var entry = payload && PHASE_STATUS[payload.phase];
            if (entry) { setStatus(entry[0], entry[1]); }
        });

        // The keepalive, ignored on purpose. It carries `null`, not a row, so letting it
        // reach the renderer would throw on the first property access — and it must not
        // reset any state either: a ping means "the connection is alive", nothing more.
        source.addEventListener('ping', function () { /* intentionally empty */ });

        source.addEventListener('error', function () {
            // A non-2xx response (the 503 the stream returns when live prices are switched
            // off) closes the EventSource for good — it does not retry. Anything else is a
            // dropped connection, which it retries on its own.
            if (source.readyState === EventSource.CLOSED) {
                setStatus('off', 'live prices off');
            } else {
                setStatus('error', 'reconnecting…');
            }
        });
    }

    load()
        .then(function () {
            setStatus('connecting', 'connecting…');
            connect();
        })
        .catch(function () {
            body.textContent = '';
            var failed = document.createElement('p');
            failed.className = 'watchlist-empty';
            failed.textContent = 'Watchlist unavailable.';
            body.appendChild(failed);
            setStatus('error', 'offline');
        });
})();
