using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.WebServer
{
    public sealed class LiteWebServer : IDisposable
    {
        private static readonly long StartedAt = Stopwatch.GetTimestamp();
        private readonly Settings _cfg;
        private WebSessionManager? _sessions;
        private (int Port, bool Network, string Thumbprint, string Password, string Language)? _runningOptions;
        private volatile int _currentRunningPort = -1;

        public static LiteWebServer? Instance { get; private set; }
        public bool IsRunning => _sessions != null;
        public int CurrentRunningPort => _currentRunningPort;
        public Uri? Address { get; private set; }
        public string? LastError { get; private set; }

        public LiteWebServer(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;
        }

        public bool Start(out string error)
        {
            error = "";
            var options = (_cfg.WebServerPort, _cfg.WebServerNetworkAccess,
                _cfg.WebServerCertificateThumbprint ?? "", _cfg.WebServerPassword ?? "", _cfg.Language);
            if (!_cfg.WebServerEnabled)
            {
                Stop();
                return true;
            }
            if (_runningOptions == options)
            {
                LastError = null;
                return true;
            }

            TcpListener? listener = null;
            X509Certificate2? certificate = null;
            try
            {
                ValidateConfiguration(_cfg, out certificate, out string host);
                Stop();

                if (options.WebServerNetworkAccess && Socket.OSSupportsIPv6)
                {
                    listener = new TcpListener(IPAddress.IPv6Any, options.WebServerPort);
                    listener.Server.DualMode = true;
                }
                else
                {
                    listener = new TcpListener(options.WebServerNetworkAccess ? IPAddress.Any : IPAddress.Loopback,
                        options.WebServerPort);
                }
                listener.Server.ExclusiveAddressUse = true;
                listener.Start(backlog: 32);

                _currentRunningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                Address = new UriBuilder(certificate == null ? "http" : "https", host, _currentRunningPort).Uri;
                _sessions = new WebSessionManager(WriteSnapshotJson, certificate,
                    certificate == null ? null : host, certificate == null ? null : options.Item4, _cfg.Language);
                _sessions.Start(listener);
                _runningOptions = options;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                listener?.Stop();
                certificate?.Dispose();
                error = LastError = ex.Message;
                Debug.WriteLine($"[Web server] {ex}");
                return false;
            }
        }

        internal static string? Validate(Settings settings)
        {
            if (!settings.WebServerEnabled) return null;
            try
            {
                ValidateConfiguration(settings, out var certificate, out _);
                certificate?.Dispose();
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static void ValidateConfiguration(Settings settings, out X509Certificate2? certificate, out string host)
        {
            certificate = null;
            host = "127.0.0.1";
            bool chinese = settings.Language is "zh" or "zh-tw";
            if (settings.WebServerPort is < 1 or > 65535)
                throw new InvalidOperationException(chinese ? "端口应为 1–65535。" : "Enter a port between 1 and 65535.");
            if (!settings.WebServerNetworkAccess) return;

            string password = settings.WebServerPassword ?? "";
            if (password.Length < 12 || string.IsNullOrWhiteSpace(password) || Encoding.UTF8.GetByteCount(password) > 1024)
                throw new InvalidOperationException(chinese ? "网络访问密码至少需要 12 个字符。" : "Network access requires a password of at least 12 characters.");

            string thumbprint = string.Concat((settings.WebServerCertificateThumbprint ?? "")
                .Where(c => !char.IsWhiteSpace(c) && c != '\u200e')).ToUpperInvariant();
            if (thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit))
                throw new InvalidOperationException(chinese ? "请输入 HTTPS 证书的指纹。" : "Enter the HTTPS certificate thumbprint.");

            foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                foreach (var match in matches)
                {
                    string name = match.GetNameInfo(X509NameType.DnsName, forIssuer: false);
                    if (certificate == null && match.HasPrivateKey && match.NotBefore <= DateTime.Now &&
                        match.NotAfter > DateTime.Now && Uri.CheckHostName(name) is UriHostNameType.Dns or UriHostNameType.IPv4)
                    {
                        certificate = match;
                        host = name;
                    }
                    else match.Dispose();
                }
                if (certificate != null) return;
            }

            throw new InvalidOperationException(chinese ? "找不到有效的 HTTPS 证书。请检查证书名称、有效期和私钥。" :
                "No usable HTTPS certificate found. Check its host name, validity and private key.");
        }

        public void Stop()
        {
            var sessions = _sessions;
            _sessions = null;
            _runningOptions = null;
            _currentRunningPort = -1;
            Address = null;
            LastError = null;
            sessions?.Dispose();
        }

        public void Dispose() => Stop();

        private void WriteSnapshotJson(Utf8JsonWriter writer)
        {
            var hw = HardwareMonitor.Instance;
            if (hw == null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }

            // ★★★ 修复：创建列表副本并加锁，防止遍历时 UI 线程修改集合导致崩溃 ★★★
            List<MonitorItemConfig> itemsCopy;
            Dictionary<string, int> groupIndices = new Dictionary<string, int>();
            
            lock (_cfg.MonitorItemsLock)
            {
                // ★★★ 修复：复用主界面 (Panel模式) 的排序逻辑 ★★★
                itemsCopy = _cfg.MonitorItems
                    .GroupBy(x => x.UIGroup)
                    .OrderBy(g => g.Min(x => x.SortIndex))
                    .SelectMany(g => g.OrderBy(x => x.SortIndex))
                    .ToList();

                // 预计算组顺序索引 (基于排序后的列表)
                int gIdx = 0;
                foreach(var item in itemsCopy)
                {
                    if (!groupIndices.ContainsKey(item.UIGroup))
                    {
                        groupIndices[item.UIGroup] = gIdx++;
                    }
                }
            }

            string localIp = hw.GetNetworkIP() ?? "127.0.0.1";

            writer.WriteStartObject();

            // "sys" object
            writer.WriteStartObject("sys");
            writer.WriteString("ip", localIp);
            writer.WriteNumber("port", _currentRunningPort);
            writer.WriteString("uptime", Stopwatch.GetElapsedTime(StartedAt).ToString(@"d\.hh\:mm\:ss"));
            writer.WriteEndObject();

            // "items" array
            writer.WriteStartArray("items");

            foreach (var item in itemsCopy)
            {
                string displayName;
                // [Fix] WebUI should also use MetricLabelResolver to show dynamic names
                string resolved = MetricLabelResolver.ResolveLabel(item);
                if (!string.IsNullOrEmpty(resolved))
                {
                    displayName = resolved;
                }
                else
                {
                    // [Optimization] Use cached keys from MonitorItemConfig
                    string itemsKey = !string.IsNullOrEmpty(item.CachedItemsKey) ? item.CachedItemsKey : UIUtils.Intern("Items." + item.Key);
                    displayName = LanguageManager.T(itemsKey);
                    // 如果翻译失败 (fallback to key suffix)
                    if (displayName.StartsWith("Items.")) displayName = displayName.Split('.')[1];
                }
                
                string groupId = item.UIGroup.ToUpper();
                // [Optimization] Use cached keys
                string groupsKey = !string.IsNullOrEmpty(item.CachedGroupsKey) ? item.CachedGroupsKey : UIUtils.Intern("Groups." + item.UIGroup);
                string groupDisplay = LanguageManager.T(groupsKey);
                if (groupDisplay == LanguageManager.GetOriginal(groupsKey) && groupDisplay.Length != 0)
                {
                    string prefix = System.Globalization.StringInfo.GetNextTextElement(groupDisplay);
                    if (Rune.GetUnicodeCategory(Rune.GetRuneAt(prefix, 0)) == System.Globalization.UnicodeCategory.OtherSymbol)
                        groupDisplay = groupDisplay[prefix.Length..].TrimStart();
                }

                string valStr = "";
                string unit = "";
                double pct = 0;
                int status = 0;
                bool isPrimary = false;

                if (item.Key.StartsWith("DASH."))
                {
                    // 特殊处理 DASH.HOST 等纯信息项 (以及插件项)
                    string dashKey = item.Key.Substring(5);
                    valStr = InfoService.Instance.GetValue(dashKey);
                    
                    // ★★★ 修复：读取插件写入的颜色状态 (.Color) ★★★
                    // [Optimization] Use cached Dash keys
                    string colorKey = !string.IsNullOrEmpty(item.CachedDashColorKey) ? item.CachedDashColorKey : dashKey + ".Color";
                    string colorStr = InfoService.Instance.GetValue(colorKey);
                    if (int.TryParse(colorStr, out int cVal)) status = cVal;

                    // 读取插件写入的单位 (.Unit)
                    string unitKey = !string.IsNullOrEmpty(item.CachedDashUnitKey) ? item.CachedDashUnitKey : dashKey + ".Unit";
                    string unitStr = InfoService.Instance.GetValue(unitKey);
                    if (!string.IsNullOrEmpty(unitStr)) unit = unitStr;
                }
                else
                {
                    // 硬件监控项
                    float? val = hw.Get(item.Key);

                    // [Fix] 恢复过滤逻辑：跳过没有数据的监控项（如台式机的电池），以符合业务屏蔽需求
                    // 布局顺序问题已由 CSS order (gidx) 机制解决，即使数据晚到也会插入正确位置
                    if (!val.HasValue) continue;

                    // [Refactor] 使用 MetricUtils 统一格式化逻辑，确保与桌面端完全一致 (包括单位换算、颜色阈值、内存显示模式)
                    valStr = MetricUtils.GetValueStr(item.Key, val.Value, false, item.UnitPanel); // false = Panel Context (Standard)
                    string calculatedUnit = MetricUtils.GetUnitStr(item.Key, val.Value, MetricUtils.UnitContext.Panel);
                    unit = MetricUtils.GetDisplayUnit(item.Key, calculatedUnit, item.UnitPanel);

                    // GetProgressValue 返回 0.0-1.0，Web端通常需要 0-100
                    pct = MetricUtils.GetProgressValue(item.Key, val.Value) * 100.0;

                    // 状态判断 (0=Safe, 1=Warn, 2=Crit)
                    status = MetricUtils.GetState(item.Key, val.Value);

                    // Primary Logic
                    if (item.Key == "BAT.Percent") isPrimary = true;
                    else if (item.Key == "CPU.Load") isPrimary = true;
                    else if (item.Key == "MEM.Load") isPrimary = true;
                    else if (item.Key == "GPU.Core.Load" || item.Key == "GPU.Load") isPrimary = true;
                    else isPrimary = false;
                }

                if (string.IsNullOrEmpty(valStr)) valStr = "--";
                
                writer.WriteStartObject();
                writer.WriteString("k", item.Key);
                writer.WriteString("n", displayName);
                writer.WriteString("gid", groupId);
                writer.WriteString("gn", groupDisplay);
                // ★★★ 新增：发送组顺序索引，供前端 CSS order 使用 ★★★
                writer.WriteNumber("gidx", groupIndices.TryGetValue(item.UIGroup, out int idx) ? idx : 999);
                writer.WriteString("v", valStr);
                writer.WriteString("u", unit);
                writer.WriteNumber("pct", double.IsFinite(pct) ? Math.Clamp(pct, 0, 100) : 0);
                writer.WriteNumber("sts", Math.Clamp(status, 0, 2));
                writer.WriteBoolean("primary", isPrimary);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}
