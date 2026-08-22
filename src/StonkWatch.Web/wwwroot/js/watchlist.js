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

    // ---------- Rendering ----------

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

        if (flash && previous !== null && !isBlank(item.last)) {
            var direction = Number(item.last) >= Number(previous) ? 'flash-up' : 'flash-down';
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
            empty.textContent = 'No symbols yet.';
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
