using System.Drawing;
using System.Drawing.Imaging;

namespace LiteMonitor.src.WebServer;

internal static class WebPageContent
{
    private static readonly Lazy<string> AppIcon = new(() =>
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon == null) return "";
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return Convert.ToBase64String(stream.ToArray());
        }
        catch { return ""; }
    });

    public static string GetAppIconBase64() => AppIcon.Value;

    public const string IndexHtml = """
<!doctype html>
<html lang="{{LANG}}">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width,initial-scale=1">
    {{FAVICON}}
    <title>LiteMonitor</title>
    <style>
        :root {
            color-scheme: dark;
            --bg: #141414;
            --panel: #1c1c1c;
            --line: #303030;
            --ink: #f4f4f4;
            --muted: #b2b2b2;
            --live: #76d6b2;
            --warning: #ffc571;
            --danger: #ff918d;
            font: 15px/1.45 system-ui, sans-serif;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--ink); }
        [hidden] { display: none !important; }
        .shell { width: min(100% - 40px, 1120px); margin: auto; }
        header { border-bottom: 1px solid var(--line); }
        .header { min-height: 62px; display: flex; gap: 12px 24px; align-items: center; justify-content: space-between; flex-wrap: wrap; }
        h1, h2, h3, p { margin: 0; }
        h1 { font-size: 20px; font-weight: 650; letter-spacing: -.02em; }
        main { padding-block: 30px 44px; }
        .title { display: flex; justify-content: space-between; align-items: baseline; gap: 12px; margin-bottom: 12px; flex-wrap: wrap; }
        h2 { font-size: 22px; font-weight: 620; }
        h3 { font-size: 16px; font-weight: 630; }
        #freshness, #status, .unit, .label { color: var(--muted); }
        #status { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; }
        #status::before { content: ''; width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
        #status.live::before { color: var(--live); }
        #status.offline::before { color: var(--danger); }
        .surface { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; }
        .summary { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); margin-bottom: 24px; }
        .summary .metric { display: grid; min-width: 0; align-content: start; justify-content: stretch; gap: 6px; padding: 17px 20px; }
        .summary .metric + .metric { border-left: 1px solid var(--line); }
        .summary .reading { text-align: left; }
        .summary .value { font-size: clamp(23px, 3vw, 36px); line-height: 1.15; letter-spacing: -.035em; }
        .summary .unit { font-size: 13px; }
        .meter { height: 4px; background: #3b3b3b; border-radius: 4px; margin-top: 4px; overflow: hidden; }
        .meter > span { display: block; height: 100%; background: #888; border-radius: inherit; }
        .groups { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
        section { min-width: 0; }
        section.board { grid-column: 1 / -1; }
        section > h3 { padding: 14px 16px 10px; }
        .metric { min-height: 43px; display: flex; align-items: baseline; justify-content: space-between; gap: 10px; padding: 9px 16px; }
        section .metric + .metric { border-top: 1px solid var(--line); }
        .label { overflow-wrap: anywhere; }
        .reading { flex: 0 1 auto; text-align: right; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
        .value { font-size: 16px; color: var(--ink); font-weight: 640; }
        .unit { margin-inline-start: 4px; font-size: 12px; }
        [data-state="1"] .value { color: var(--warning); }
        [data-state="2"] .value { color: var(--danger); }
        [data-state="1"] .meter > span { background: var(--warning); }
        [data-state="2"] .meter > span { background: var(--danger); }
        .notice { color: var(--muted); padding: 16px; }
        .sign-in { max-width: 390px; padding: 20px; }
        label { display: block; margin: 12px 0 5px; }
        .form-row { display: flex; align-items: center; gap: 8px; }
        input, button { border-radius: 5px; font: inherit; min-height: 40px; }
        input { border: 1px solid #696969; flex: 1; min-width: 0; color: var(--ink); background: var(--bg); padding: 6px 9px; }
        button { border: 1px solid transparent; background: var(--ink); color: #161616; padding: 6px 12px; cursor: pointer; white-space: nowrap; }
        button:disabled { opacity: .58; cursor: wait; }
        #retry { background: transparent; color: var(--ink); border-color: #555; min-height: 34px; }
        :focus-visible { outline: 2px solid var(--live); outline-offset: 3px; }
        #auth-error { color: var(--danger); margin-top: 8px; }
        @media (max-width: 620px) {
            .shell { width: calc(100% - 32px); }
            main { padding-block: 22px 30px; }
            .summary .metric { padding: 11px 10px; gap: 4px; }
            .summary .unit { margin-inline-start: 2px; }
            .groups { grid-template-columns: minmax(0, 1fr); }
            input, button { min-height: 44px; }
        }
    </style>
</head>
<body>
    <header>
        <div class="header shell">
            <h1>LiteMonitor</h1>
            <div class="form-row">
                <span id="status" role="status"></span>
                <button id="retry" type="button" hidden></button>
            </div>
        </div>
    </header>
    <main class="shell">
        <div class="title">
            <h2 id="title"></h2>
            <time id="freshness"></time>
        </div>
        <form id="login" class="surface sign-in" hidden>
            <h3 id="login-title"></h3>
            <label id="password-label" for="password"></label>
            <div class="form-row">
                <input id="password" type="password" maxlength="256" autocomplete="current-password" required>
                <button id="submit" type="submit"></button>
            </div>
            <p id="auth-error" role="alert" hidden></p>
        </form>
        <p id="notice" class="surface notice"></p>
        <div id="summary" class="surface summary" hidden></div>
        <div id="groups" class="groups"></div>
    </main>
    <script nonce="{{NONCE}}">
        const zh = document.documentElement.lang.startsWith('zh');
        const t = (cn, en) => zh ? cn : en;
        const $ = id => document.getElementById(id);
        const login = $('login');
        const retry = $('retry');
        const notice = $('notice');
        const summary = $('summary');
        const groups = $('groups');
        const rows = new Map();
        const mainMetrics = [
            { keys: ['CPU.Load'], label: 'CPU' },
            { keys: ['GPU.Load', 'GPU.Core.Load'], label: 'GPU' },
            { keys: ['MEM.Load'], label: t('内存', 'Memory') }
        ];
        let shape = '';
        let websocket = null;
        let retryTimer = null;
        let lastFrame = 0;
        let connecting = false;
        let locked = false;

        $('title').textContent = t('实时监控', 'Live metrics');
        $('login-title').textContent = t('连接 LiteMonitor', 'Connect to LiteMonitor');
        $('password-label').textContent = t('访问密码', 'Password');
        $('submit').textContent = t('连接', 'Connect');
        retry.textContent = t('重试', 'Retry');
        retry.addEventListener('click', connect);
        setStatus(t('正在连接', 'Connecting'));

        function setStatus(text, state = '') {
            $('status').textContent = text;
            $('status').className = state;
            retry.hidden = state !== 'offline';
            if (!lastFrame && !locked) {
                notice.hidden = false;
                notice.textContent = state === 'offline' ? t('暂未收到监控数据。', 'No monitoring data received.') : t('正在等待监控数据…', 'Waiting for metrics…');
            }
        }

        function showLogin() {
            locked = true;
            lastFrame = 0;
            login.hidden = false;
            notice.hidden = true;
            summary.hidden = true;
            groups.hidden = true;
            $('freshness').textContent = '';
            setStatus(t('需要登录', 'Sign in required'));
            $('password').focus();
        }

        login.addEventListener('submit', async event => {
            event.preventDefault();
            $('submit').disabled = true;
            $('auth-error').hidden = true;
            try {
                const response = await fetch('/api/session', {
                    method: 'POST',
                    body: $('password').value,
                    signal: AbortSignal.timeout(6000)
                });
                if (!response.ok) throw new Error(response.status === 401
                    ? t('密码不正确', 'Incorrect password')
                    : response.status === 429 ? t('尝试过多，请稍后再试', 'Too many attempts. Try again later.')
                    : t('暂时无法连接', 'Could not connect'));
                $('password').value = '';
                locked = false;
                login.hidden = true;
                await connect();
            } catch (error) {
                $('auth-error').textContent = error.message;
                $('auth-error').hidden = false;
            } finally {
                $('submit').disabled = false;
            }
        });

        async function connect() {
            if (connecting || locked || websocket) return;
            clearTimeout(retryTimer);
            connecting = true;
            setStatus(t('正在连接', 'Connecting'));
            try {
                const session = await fetch('/api/session', { signal: AbortSignal.timeout(6000) });
                if (session.status === 401) { showLogin(); return; }
                if (!session.ok) throw new Error();
                const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
                const connection = new WebSocket(protocol + '//' + location.host + '/ws');
                websocket = connection;
                let frameTime = Date.now();
                const watchdog = setInterval(() => {
                    if (Date.now() - frameTime > 6000) connection.close();
                }, 2000);
                connection.onmessage = event => {
                    try {
                        const snapshot = JSON.parse(event.data);
                        if (!Array.isArray(snapshot.items)) return;
                        render(snapshot.items);
                        frameTime = lastFrame = Date.now();
                        const stamp = new Date(lastFrame);
                        $('freshness').dateTime = stamp.toISOString();
                        $('freshness').textContent = t('更新于 ', 'Updated ') + stamp.toLocaleTimeString();
                        setStatus(t('实时', 'Live'), 'live');
                    } catch { /* Keep the last complete snapshot. */ }
                };
                connection.onclose = () => {
                    clearInterval(watchdog);
                    websocket = null;
                    reconnect();
                };
                connection.onerror = () => connection.close();
            } catch { reconnect(); }
            finally { connecting = false; }
        }

        function reconnect() {
            setStatus(t('已断开', 'Disconnected'), 'offline');
            if (lastFrame) $('freshness').textContent = t('上次更新 ', 'Last updated ') + new Date(lastFrame).toLocaleTimeString();
            retryTimer = setTimeout(connect, 2500);
        }

        function element(tag, className, text) {
            const node = document.createElement(tag);
            if (className) node.className = className;
            if (text !== undefined) node.textContent = text;
            return node;
        }

        function createRow(label, primary = false) {
            const row = element('div', 'metric');
            const value = element('span', 'value', '—');
            const unit = element('span', 'unit');
            const reading = element('div', 'reading');
            reading.append(value, unit);
            row.append(element('span', 'label', label), reading);
            let fill;
            if (primary) {
                const meter = element('div', 'meter');
                meter.setAttribute('aria-hidden', 'true');
                fill = element('span');
                meter.append(fill);
                row.append(meter);
            }
            return { row, value, unit, fill };
        }

        function render(items) {
            const byKey = new Map(items.map(item => [item.k, item]));
            const selected = mainMetrics.map(metric => ({
                label: metric.label,
                item: metric.keys.map(key => byKey.get(key)).find(Boolean)
            }));
            const promoted = new Set(selected.map(metric => metric.item?.k));
            const remaining = items.filter(item => !promoted.has(item.k));
            const currentShape = JSON.stringify([
                selected.map(metric => metric.item?.k),
                remaining.map(item => [item.k, item.gid, item.gn, item.n])
            ]);

            if (currentShape !== shape) {
                shape = currentShape;
                summary.replaceChildren();
                groups.replaceChildren();
                rows.clear();
                selected.forEach(({ label, item }) => {
                    const row = createRow(label, true);
                    summary.append(row.row);
                    if (item) rows.set(item.k, row);
                });

                const sections = new Map();
                remaining.forEach(item => {
                    if (!sections.has(item.gid)) {
                        const section = element('section', item.gid === 'DASH' ? 'surface board' : 'surface');
                        section.style.order = item.gid === 'DASH' ? 1000 : Number(item.gidx) || 0;
                        section.append(element('h3', '', item.gn));
                        sections.set(item.gid, section);
                        groups.append(section);
                    }
                    const row = createRow(item.n);
                    rows.set(item.k, row);
                    sections.get(item.gid).append(row.row);
                });
            }

            summary.hidden = items.length === 0;
            groups.hidden = items.length === 0;
            notice.hidden = items.length > 0;
            if (!items.length) notice.textContent = t('暂无可用指标。', 'No metrics are available.');
            items.forEach(item => {
                const row = rows.get(item.k);
                if (!row) return;
                if (row.value.textContent !== item.v) row.value.textContent = item.v;
                if (row.unit.textContent !== item.u) row.unit.textContent = item.u;
                row.row.dataset.state = item.sts === 1 ? '1' : item.sts === 2 ? '2' : '0';
                if (row.fill) row.fill.style.width = Math.max(0, Math.min(100, Number(item.pct) || 0)) + '%';
            });
        }

        connect();
    </script>
</body>
</html>
""";
}
