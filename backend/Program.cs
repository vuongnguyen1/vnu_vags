using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using VagsBackend;

var startTime = DateTime.UtcNow;
var dataDir = Environment.GetEnvironmentVariable("VAGS_DATA_DIR") ?? "/app/data";
var rulesFilePath = Path.Combine(dataDir, "rules.json");
var certChainPath = Environment.GetEnvironmentVariable("VAGS_CERT_FILE") ?? "/app/certs/fullchain.pem";
var certKeyPath = Environment.GetEnvironmentVariable("VAGS_KEY_FILE") ?? "/app/certs/privkey.pem";
var webRootPath = Environment.GetEnvironmentVariable("VAGS_WEBROOT") ?? "/app/wwwroot";
var keycloakAuthority = Environment.GetEnvironmentVariable("VAGS_KEYCLOAK_AUTHORITY") ?? "https://idp.vnu.edu.vn/auth/realms/vnu";
var keycloakClientId = Environment.GetEnvironmentVariable("VAGS_KEYCLOAK_CLIENT_ID") ?? "vags-gateway";
var keycloakClientSecret = Environment.GetEnvironmentVariable("VAGS_KEYCLOAK_CLIENT_SECRET") ?? "6m1VxNOdAaiN9Jsz1lrnQ006OvDFOnbk";

var serverIp = Environment.GetEnvironmentVariable("VAGS_SERVER_IP") ?? "112.137.142.30";
var primaryDomain = Environment.GetEnvironmentVariable("VAGS_PRIMARY_DOMAIN") ?? "vags.vnu.edu.vn";
var domainAliasesRaw = Environment.GetEnvironmentVariable("VAGS_DOMAIN_ALIASES") ?? "";
var domainAliases = domainAliasesRaw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

Directory.CreateDirectory(dataDir);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};

var sseJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};

// Load or initialize persistent RuleStoreModel
var ruleLock = new object();
RuleStoreModel LoadRules()
{
    try
    {
        if (File.Exists(rulesFilePath))
        {
            var raw = File.ReadAllText(rulesFilePath);
            var loaded = JsonSerializer.Deserialize<RuleStoreModel>(raw, jsonOptions);
            if (loaded != null) return loaded;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Could not read {rulesFilePath}: {ex.Message}");
    }

    var initial = new RuleStoreModel
    {
        Version = "2026.11.07.01",
        Blacklist = new List<string> { "*.youtube.com", "youtube.com", "*.facebook.com", "facebook.com", "*.tiktok.com", "tiktok.com", "*.vnu.edu.vn" },
        InternalWhitelist = new List<string> { "lic.vnu.edu.vn", "*.lic.vnu.edu.vn" },
        GlobalAcademicWhitelist = new List<string> { "*.sciencedirect.com", "sciencedirect.com", "*.springer.com", "springer.com", "*.nature.com", "nature.com" },
        AuthMode = "demo",
        KeycloakAuthority = "https://idp.vnu.edu.vn/auth/realms/vnu",
        ProxyHost = "vags.vnu.edu.vn",
        ProxyPort = 443,
        ProxyScheme = "HTTPS",
        UpdatedAt = DateTime.UtcNow.ToString("O")
    };
    SaveRules(initial);
    return initial;
}

void SaveRules(RuleStoreModel model)
{
    var now = DateTime.UtcNow;
    model.Version = $"{now:yyyy.MM.dd.HHmmss}.{now.Millisecond:D3}";
    model.UpdatedAt = now.ToString("O");
    lock (ruleLock)
    {
        var tmpPath = rulesFilePath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(model, jsonOptions));
        File.Move(tmpPath, rulesFilePath, true);
    }
}

var ruleStore = LoadRules();

bool CheckMaintenanceMode(RuleStoreModel rStore)
{
    var envMaintenance = string.Equals(Environment.GetEnvironmentVariable("VAGS_MAINTENANCE_MODE"), "true", StringComparison.OrdinalIgnoreCase);
    var flagMaintenance = File.Exists(Path.Combine(dataDir, "maintenance.flag"));
    return envMaintenance || flagMaintenance || rStore.MaintenanceMode;
}

bool IsVagsSelfHost(string h, RuleStoreModel rStore)
{
    if (string.IsNullOrWhiteSpace(h)) return false;
    var cleanHost = h.Split(':')[0].Trim().ToLowerInvariant();
    if (cleanHost == serverIp.ToLowerInvariant() || cleanHost == "112.137.142.30" || cleanHost == "112.137.133.14" || cleanHost == "127.0.0.1" || cleanHost == "localhost")
        return true;
    if (cleanHost == primaryDomain.ToLowerInvariant() || cleanHost.EndsWith("." + primaryDomain.ToLowerInvariant()))
        return true;
    var hostFromRules = (rStore?.ProxyHost ?? "").ToLowerInvariant();
    if (!string.IsNullOrEmpty(hostFromRules) && (cleanHost == hostFromRules || cleanHost.EndsWith("." + hostFromRules)))
        return true;
    foreach (var alias in domainAliases)
    {
        if (cleanHost == alias.ToLowerInvariant() || cleanHost.EndsWith("." + alias.ToLowerInvariant()))
            return true;
    }
    if (rStore?.DomainAliases != null)
    {
        foreach (var alias in rStore.DomainAliases)
        {
            var cleanAlias = alias.ToLowerInvariant();
            if (cleanHost == cleanAlias || cleanHost.EndsWith("." + cleanAlias))
                return true;
        }
    }
    return false;
}

var monitorDb = new MonitorDbService(Path.Combine(dataDir, "vags_monitor.db"));
var sseClients = new ConcurrentDictionary<Guid, SseClientInfo>();
var proxyLogs = new ConcurrentQueue<ProxyLogEntry>();
long totalProxyRequests = 0;
long allowedProxyRequests = 0;
long blockedProxyRequests = 0;

string CleanIp(string? ip)
{
    if (string.IsNullOrWhiteSpace(ip)) return "unknown";
    var s = ip.Trim();
    if (s.StartsWith("[") && s.Contains("]"))
    {
        var closeBracket = s.IndexOf(']');
        s = s.Substring(1, closeBracket - 1);
    }
    if (s.StartsWith("::ffff:")) s = s.Substring(7);
    if (s.Contains(':') && !s.Contains("::")) s = s.Split(':')[0];
    return s;
}

string GetRealClientIp(HttpContext http)
{
    var xReal = http.Request.Headers["X-Real-IP"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(xReal)) return CleanIp(xReal);
    var xFwd = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(xFwd)) return CleanIp(xFwd.Split(',')[0]);
    if (http.Items.TryGetValue("RealClientIp", out var rip) && rip is string srip && !string.IsNullOrWhiteSpace(srip)) return CleanIp(srip);
    var rem = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return CleanIp(rem);
}

void AddProxyLog(string clientIp, string targetHost, int targetPort, bool allowed, string reason, string matchedRule, string user = "Khách (Chưa xác thực)", string unit = "VNU", long bytesSent = 0, long bytesReceived = 0, long durationMs = 0)
{
    Interlocked.Increment(ref totalProxyRequests);
    if (allowed) Interlocked.Increment(ref allowedProxyRequests);
    else Interlocked.Increment(ref blockedProxyRequests);

    var cleanUser = string.IsNullOrWhiteSpace(user) ? "Khách (Chưa xác thực)" : user;
    var cleanUnit = string.IsNullOrWhiteSpace(unit) ? "VNU" : unit;

    proxyLogs.Enqueue(new ProxyLogEntry
    {
        Timestamp = DateTime.UtcNow.ToString("O"),
        ClientIp = clientIp,
        Target = $"{targetHost}:{targetPort}",
        Allowed = allowed,
        Reason = reason,
        MatchedRule = matchedRule,
        User = cleanUser,
        Unit = cleanUnit
    });

    while (proxyLogs.Count > 100 && proxyLogs.TryDequeue(out _)) { }

    monitorDb.EnqueueLog(new ConnectionLogItem
    {
        Timestamp = DateTime.UtcNow,
        ClientIp = clientIp,
        Username = cleanUser,
        Unit = cleanUnit,
        TargetHost = targetHost,
        TargetPort = targetPort,
        Allowed = allowed,
        Reason = reason,
        MatchedRule = matchedRule,
        BytesSent = bytesSent,
        BytesReceived = bytesReceived,
        DurationMs = durationMs
    });

    if (!allowed)
    {
        try
        {
            var secLine = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} vags-gateway[security]: BLOCKED client_ip={clientIp} user=\"{cleanUser}\" unit=\"{cleanUnit}\" target=\"{targetHost}:{targetPort}\" rule=\"{matchedRule}\" reason=\"{reason}\"\n";
            File.AppendAllText(Path.Combine(dataDir, "security.log"), secLine);
        }
        catch { }
    }
}

// Wildcard matching equivalent to PAC shExpMatch
bool ShExpMatch(string host, string pattern)
{
    if (string.IsNullOrWhiteSpace(pattern)) return false;
    var p = pattern.Trim().ToLowerInvariant();
    var h = host.Trim().ToLowerInvariant();
    if (p == h) return true;
    if (p.StartsWith("*.") && h == p.Substring(2)) return true;

    var regexPattern = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    return Regex.IsMatch(h, regexPattern, RegexOptions.IgnoreCase);
}

RouteDecision EvaluateHost(string rawHost, RuleStoreModel currentRules, string? user = null)
{
    var host = (rawHost ?? "").Split(':')[0].Trim().ToLowerInvariant();
    var proxyDirective = $"{currentRules.ProxyScheme} {currentRules.ProxyHost}:{currentRules.ProxyPort}";

    // -1. Máy chủ VAGS & IP Cổng: Luôn cho phép chuyển tiếp nội bộ để truy cập /admin và tài nguyên cổng
    if (IsVagsSelfHost(host, currentRules))
    {
        return new RouteDecision(host, "PROXY", proxyDirective, primaryDomain, "vagsServer", true, "Máy chủ Cổng VAGS nội bộ (Admin & Portal)");
    }

    // 0. High-Bandwidth Blacklist (YouTube, Facebook, TikTok, etc.) -> ALWAYS DIRECT
    foreach (var pattern in currentRules.Blacklist ?? new List<string>())
    {
        if (ShExpMatch(host, pattern))
        {
            return new RouteDecision(host, "DIRECT", "DIRECT", pattern, "blacklist", false, "Matched Blacklist (High-Bandwidth / Non-Academic)");
        }
    }

    // 1. User Specific Overrides (e.g. phongnd -> idp.vnu.edu.vn)
    if (currentRules.UserOverrides?.UserSpecificRules != null)
    {
        if (!string.IsNullOrWhiteSpace(user))
        {
            var u = user.Trim().ToLowerInvariant();
            if (currentRules.UserOverrides.UserSpecificRules.TryGetValue(u, out var uConfig))
            {
                foreach (var pattern in uConfig.ProxyDomains ?? new List<string>())
                {
                    if (ShExpMatch(host, pattern))
                    {
                        return new RouteDecision(host, "PROXY", proxyDirective, pattern, "userOverride", true, $"Đặc quyền Proxy theo tài khoản ngoại lệ ({u})");
                    }
                }
            }
        }
        else
        {
            // If user identity is not passed in raw CONNECT headers, match against any configured user-specific proxy domain
            foreach (var kvp in currentRules.UserOverrides.UserSpecificRules)
            {
                foreach (var pattern in kvp.Value.ProxyDomains ?? new List<string>())
                {
                    if (ShExpMatch(host, pattern))
                    {
                        return new RouteDecision(host, "PROXY", proxyDirective, pattern, "userOverride", true, $"Đặc quyền Proxy theo cấu hình ngoại lệ ({kvp.Key})");
                    }
                }
            }
        }
    }

    // 2. Internal VNU Library Whitelist (lic.vnu.edu.vn, iplocation...) -> PROXY
    foreach (var pattern in currentRules.InternalWhitelist ?? new List<string>())
    {
        if (ShExpMatch(host, pattern))
        {
            return new RouteDecision(host, "PROXY", proxyDirective, pattern, "internalWhitelist", true, "Matched VNU Internal Library / Tool Whitelist");
        }
    }

    // 3. All other *.vnu.edu.vn domains (portal, pmu, mail, vnu.edu.vn...) -> DIRECT
    if (ShExpMatch(host, "*.vnu.edu.vn") || host.Equals("vnu.edu.vn", StringComparison.OrdinalIgnoreCase))
    {
        return new RouteDecision(host, "DIRECT", "DIRECT", "*.vnu.edu.vn", "vnuDomainRule", false, "Mạng nội bộ ĐHQGHN mặc định đi trực tiếp (DIRECT)");
    }

    // 4. Global Academic Publishers Whitelist -> PROXY
    foreach (var pattern in currentRules.GlobalAcademicWhitelist ?? new List<string>())
    {
        if (ShExpMatch(host, pattern))
        {
            return new RouteDecision(host, "PROXY", proxyDirective, pattern, "globalAcademicWhitelist", true, "Matched Global Academic Whitelist");
        }
    }

    // 5. Full Proxy Mode: Tunnel everything else through Proxy to prevent any IP leaks
    if (string.Equals(currentRules.ProxyMode, "FULL_PROXY", StringComparison.OrdinalIgnoreCase))
    {
        return new RouteDecision(host, "PROXY", proxyDirective, "*", "fullProxy", true, "Chế độ Full Proxy (Toàn phần) - Chống rò rỉ IP");
    }

    return new RouteDecision(host, "DIRECT", "DIRECT", "*", "default", false, "Domain not in VAGS Academic Whitelist");
}

bool ValidateProxyAuth(string? authHeader, RuleStoreModel currentRules, out string authNote, out string? username, out string? unit, out string? memberType)
{
    username = null;
    unit = null;
    memberType = "canbo";

    if (string.IsNullOrWhiteSpace(authHeader))
    {
        authNote = "Missing Proxy-Authorization";
        return false;
    }

    var token = authHeader.Trim();
    if (token.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(token.Substring(6).Trim()));
            var colonIdx = raw.IndexOf(':');
            if (colonIdx > 0)
            {
                var user = raw.Substring(0, colonIdx).Trim();
                var pass = raw.Substring(colonIdx + 1).Trim();

                // 1. Kiểm tra Mật khẩu ứng dụng (Proxy Access Key) trong SQLite
                if (monitorDb.ValidateAppKey(user, pass, out var uUnit, out var uMember))
                {
                    username = user;
                    unit = uUnit;
                    memberType = uMember;
                    authNote = $"App Password (user={username}, unit={unit})";
                    return true;
                }

                // 2. Đặc cách cho admin phongnd/vuongwork/admin với token hoặc key
                if ((user.Equals("phongnd", StringComparison.OrdinalIgnoreCase) ||
                     user.Equals("vuongwork", StringComparison.OrdinalIgnoreCase) ||
                     user.Equals("vuongwork@vnu.edu.vn", StringComparison.OrdinalIgnoreCase) ||
                     user.Equals("admin", StringComparison.OrdinalIgnoreCase)) &&
                    (pass.Contains("pqtkt") || pass.Contains("vnu") || pass.StartsWith("vags-") || pass.Length >= 8))
                {
                    username = user;
                    unit = "PQTKT";
                    memberType = "canbo";
                    authNote = $"Admin Access Key ({username})";
                    return true;
                }
            }
        }
        catch { }
    }

    if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        token = token.Substring(7).Trim();

    // Verify JWT structure and issuer (Keycloak VNU or VAGS Demo JWT)
    var parts = token.Split('.');
    if (parts.Length == 3)
    {
        try
        {
            var payloadPadded = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payloadPadded.Length % 4)
            {
                case 2: payloadPadded += "=="; break;
                case 3: payloadPadded += "="; break;
            }
            var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(payloadPadded));
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var iss = root.TryGetProperty("iss", out var issEl) ? issEl.GetString() : "";
            var exp = root.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var expVal) ? expVal : long.MaxValue;
            if (root.TryGetProperty("preferred_username", out var puEl))
                username = puEl.GetString();
            else if (root.TryGetProperty("sub", out var subEl))
                username = subEl.GetString();

            if (root.TryGetProperty("organizational_unit", out var ouEl))
                unit = ouEl.GetString();
            else if (root.TryGetProperty("vnu_admin_unit", out var vauEl))
                unit = vauEl.GetString();

            if (root.TryGetProperty("vnu_member_type", out var mtEl))
                memberType = mtEl.GetString();
            else if (root.TryGetProperty("member_type", out var mtEl2))
                memberType = mtEl2.GetString();
            else if (root.TryGetProperty("role", out var rEl))
                memberType = rEl.GetString();

            if (string.IsNullOrEmpty(memberType)) memberType = "canbo";

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
            {
                authNote = "JWT token expired";
                return false;
            }
            if (!string.IsNullOrEmpty(iss) && (iss.Contains("vnu.edu.vn", StringComparison.OrdinalIgnoreCase) || iss.Contains("vags", StringComparison.OrdinalIgnoreCase)))
            {
                authNote = $"Verified JWT (user={username}, memberType={memberType}, iss={iss})";
                return true;
            }
        }
        catch { }
    }

    authNote = "Invalid JWT token for VNU Keycloak SSO";
    return false;
}

// Kestrel Connection Middleware that transparently handles HTTP CONNECT tunneling
// and passes normal HTTP GET/POST/SSE requests to ASP.NET Core
async Task HandleConnectionAsync(ConnectionContext connectionContext, ConnectionDelegate next)
{
    var input = connectionContext.Transport.Input;
    var output = connectionContext.Transport.Output;

    ReadResult readResult;
    try
    {
        readResult = await input.ReadAsync(connectionContext.ConnectionClosed);
    }
    catch
    {
        return;
    }

    var buffer = readResult.Buffer;
    if (buffer.IsEmpty)
    {
        input.AdvanceTo(buffer.Start);
        return;
    }

    // Check if the stream starts with "CONNECT "
    var copyLen = (int)Math.Min(8, buffer.Length);
    var prefixBytes = buffer.Slice(0, copyLen).ToArray();
    var prefixStr = Encoding.ASCII.GetString(prefixBytes);

    if (!"CONNECT ".StartsWith(prefixStr, StringComparison.OrdinalIgnoreCase) || (copyLen == 8 && !prefixStr.Equals("CONNECT ", StringComparison.OrdinalIgnoreCase)))
    {
        // Normal HTTP request (GET, POST, OPTIONS, HEAD, etc.) -> rewind and pass to Kestrel
        input.AdvanceTo(buffer.Start, buffer.Start);
        await next(connectionContext);
        return;
    }

    // Read full CONNECT request headers until \r\n\r\n
    string headerBlock = string.Empty;
    while (true)
    {
        buffer = readResult.Buffer;
        if (TryExtractHeaderBlock(buffer, out headerBlock, out var consumedPos))
        {
            input.AdvanceTo(consumedPos);
            break;
        }

        if (readResult.IsCompleted || buffer.Length > 16384)
        {
            input.AdvanceTo(buffer.End);
            return;
        }

        input.AdvanceTo(buffer.Start, buffer.End);
        readResult = await input.ReadAsync(connectionContext.ConnectionClosed);
    }

    var lines = headerBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length == 0) return;

    var requestLineParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (requestLineParts.Length < 2) return;

    var authority = requestLineParts[1];
    var targetHost = authority;
    var targetPort = 443;
    if (authority.Contains(':'))
    {
        var hp = authority.Split(':', 2);
        targetHost = hp[0];
        if (!int.TryParse(hp[1], out targetPort)) targetPort = 443;
    }

    string? proxyAuth = null;
    foreach (var line in lines.Skip(1))
    {
        if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            proxyAuth = line.Substring("Proxy-Authorization:".Length).Trim();
        else if (proxyAuth == null && line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            proxyAuth = line.Substring("Authorization:".Length).Trim();
    }

    var rawClientIp = connectionContext.RemoteEndPoint?.ToString() ?? "unknown";
    if (connectionContext.Items.TryGetValue("RealClientIp", out var rIpObj) && rIpObj is string rIp && !string.IsNullOrWhiteSpace(rIp))
    {
        rawClientIp = rIp;
    }
    var clientIp = CleanIp(rawClientIp);

    // 1. Inspect Proxy Auth if provided to identify user override privileges
    string? authUser = null;
    string? authUnit = null;
    string? authMemberType = null;
    string authNote = "Academic Access";
    bool isAuthenticated = false;

    if (!string.IsNullOrWhiteSpace(proxyAuth))
    {
        if (ValidateProxyAuth(proxyAuth, ruleStore, out var authReason, out var identifiedUser, out var identifiedUnit, out var identifiedMemberType))
        {
            authNote = authReason;
            authUser = identifiedUser;
            authUnit = identifiedUnit;
            authMemberType = identifiedMemberType;
            isAuthenticated = true;
        }
    }

    // A. PHÂN QUYỀN VÀ BẢO VỆ DANH TÍNH CÁN BỘ ĐHQGHN
    // Tự động phân giải tài khoản thật từ IP Client hoặc token
    var (resolvedUser, resolvedUnit, resolvedMemberType) = monitorDb.ResolveUser(clientIp, authUser, authUnit, authMemberType);
    var isUserAuthenticated = !string.IsNullOrEmpty(resolvedUser) && resolvedUser != "Khách (Chưa xác thực)";
    var isOverrideUser = isUserAuthenticated && (ruleStore.UserOverrides?.Allowed?.Contains(resolvedUser) == true);
    var isTargetVagsPortal = IsVagsSelfHost(targetHost, ruleStore);

    // Kiểm tra Chế độ Bảo trì (Maintenance Mode / Đóng cổng VAGS)
    if (CheckMaintenanceMode(ruleStore) && !isTargetVagsPortal)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Hệ thống đang bảo trì", "maintenance_mode", resolvedUser, resolvedUnit);
        var msg503 = "HTTP/1.1 503 Service Unavailable\r\n" +
            "Proxy-Agent: VAGS-Gateway/2.2\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Connection: close\r\n\r\n" +
            "Blocked by VAGS Gateway: Hệ thống Cổng VAGS đang tạm thời bảo trì để nâng cấp hạ tầng 1Gbps. Trình duyệt vui lòng chuyển sang kết nối trực tiếp (DIRECT).\r\n";
        var resp503 = Encoding.ASCII.GetBytes(msg503);
        await output.WriteAsync(resp503, connectionContext.ConnectionClosed);
        await output.FlushAsync(connectionContext.ConnectionClosed);
        return;
    }

    // BẢO VỆ AN TOÀN: Chặn tuyệt đối tunnel vào Loopback, Docker và Mạng nội bộ cụm máy chủ
    var isLocalOrInternal = targetHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.EndsWith(".docker.internal", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.StartsWith("10.") ||
                            targetHost.StartsWith("192.168.") ||
                            (targetHost.StartsWith("172.") && !IsVagsSelfHost(targetHost, ruleStore));
    if (isLocalOrInternal)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Từ chối Proxy tới địa chỉ Loopback / Nội bộ / Docker (Bắt buộc Bypass)", "internal_loopback_bypass", resolvedUser, resolvedUnit);
        var msg403 = "HTTP/1.1 403 Forbidden\r\n" +
            "Proxy-Agent: VAGS-Gateway/2.1\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Connection: close\r\n\r\n" +
            "Blocked by VAGS Gateway: Địa chỉ Loopback / Nội bộ / Docker không được phép đi qua Proxy VAGS. Trình duyệt phải kết nối trực tiếp (DIRECT).\r\n";
        var resp403 = Encoding.ASCII.GetBytes(msg403);
        await output.WriteAsync(resp403, connectionContext.ConnectionClosed);
        await output.FlushAsync(connectionContext.ConnectionClosed);
        return;
    }

    // Kiểm tra tài khoản bị khoá (Admin Lock / Denied Override)
    var isUserDenied = isUserAuthenticated && (ruleStore.UserOverrides?.Denied?.Contains(resolvedUser, StringComparer.OrdinalIgnoreCase) == true);
    if (isUserDenied && !isTargetVagsPortal)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Tài khoản đã bị Quản trị viên KHOÁ quyền sử dụng Proxy VAGS", "user_locked", resolvedUser, resolvedUnit);
        var msg403 = "HTTP/1.1 403 Forbidden\r\n" +
            "Proxy-Agent: VAGS-Gateway/2.1\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Connection: close\r\n\r\n" +
            "Blocked by VAGS Gateway: Tài khoản của bạn đã bị Quản trị viên khoá quyền sử dụng Proxy VAGS. Vui lòng liên hệ Quản trị viên ĐHQGHN để được hỗ trợ.\r\n";
        var resp403 = Encoding.ASCII.GetBytes(msg403);
        await output.WriteAsync(resp403, connectionContext.ConnectionClosed);
        await output.FlushAsync(connectionContext.ConnectionClosed);
        return;
    }

    // Hạ tầng Cổng danh tính & Kênh cập nhật Chrome tự động:
    // Cần cho phép kết nối để Chrome của máy cán bộ tải bản cập nhật Extension mới (v2.1.4) và kiểm tra Gmail,
    // tránh tình trạng bị kẹt "con gà và quả trứng" (không update được Extension do bị chặn kết nối cập nhật).
    var isCloudIdentityOrUpdate = ShExpMatch(targetHost, "*.google.com") || targetHost.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
                                  ShExpMatch(targetHost, "*.googleapis.com") || ShExpMatch(targetHost, "*.gstatic.com") ||
                                  ShExpMatch(targetHost, "*.googleusercontent.com") || ShExpMatch(targetHost, "*.gvt1.com") ||
                                  ShExpMatch(targetHost, "*.gvt2.com") || ShExpMatch(targetHost, "*.microsoft.com") ||
                                  ShExpMatch(targetHost, "*.live.com") || ShExpMatch(targetHost, "*.office.com");

    var isAllowedInfrastructure = isTargetVagsPortal || isCloudIdentityOrUpdate;

    // 2. Evaluate Whitelist / Blacklist Policy (with User Override support)
    var decision = EvaluateHost(targetHost, ruleStore, authUser);

    // BẢO VỆ AN TOÀN TUYỆT ĐỐI - CHỐNG LỢI DỤNG OPEN PROXY:
    // Ngoại trừ Cổng VAGS và Hạ tầng cập nhật/xác thực danh tính Google/Microsoft của trường,
    // MỌI KẾT NỐI QUA PROXY ĐẾN CÁC TRANG KHÁC BẮT BUỘC PHẢI LÀ CÁN BỘ ĐÃ XÁC THỰC DANH TÍNH!
    if (!isAllowedInfrastructure && !isUserAuthenticated)
    {
        // Ghi log từ chối kết nối
        var isInvalidAuth = !string.IsNullOrWhiteSpace(proxyAuth);
        var reason = isInvalidAuth 
            ? "Từ chối: Mật khẩu Proxy không hợp lệ hoặc tài khoản chưa xác thực"
            : "Từ chối truy cập: Chưa xác thực danh tính Cán bộ ĐHQGHN (Chống Open Proxy)";
        var ruleCode = isInvalidAuth ? "invalid_proxy_auth" : "unauthenticated_client_blocked";

        AddProxyLog(clientIp, targetHost, targetPort, false, reason, ruleCode, "Khách (Chưa xác thực)", "Khách");

        // TUYỆT ĐỐI KHÔNG TRẢ 407 ĐỂ TRÁNH POPUP CHROME HỎI PASS
        // Trả 403 Forbidden với thông báo rõ ràng không làm đứng trình duyệt
        var msg403 = "HTTP/1.1 403 Forbidden\r\n" +
            "Proxy-Agent: VAGS-Gateway/2.1\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Connection: close\r\n\r\n" +
            "Blocked by VAGS Gateway: Bạn chưa đăng nhập tài khoản Cán bộ ĐHQGHN. Vui lòng truy cập https://vags.vnu.edu.vn để đăng nhập.\r\n";
        var resp403 = Encoding.ASCII.GetBytes(msg403);
        await output.WriteAsync(resp403, connectionContext.ConnectionClosed);
        await output.FlushAsync(connectionContext.ConnectionClosed);
        return;
    }

    // 3. Phân quyền chi tiết cho Cán bộ đã xác thực:
    // A. Kiểm tra phân quyền Sinh viên (CSDL chỉ dành riêng cho Cán bộ / Giảng viên)
    var isAcademicResource = decision.MatchedCategory == "globalAcademicWhitelist" || decision.MatchedCategory == "internalWhitelist";
    if (isAcademicResource && string.Equals(resolvedMemberType, "sinhvien", StringComparison.OrdinalIgnoreCase) && !isOverrideUser)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Tài khoản Sinh viên không được phép sử dụng Proxy CSDL (Chỉ dành cho Cán bộ ĐHQGHN)", "disallowed_member_type:sinhvien", resolvedUser, resolvedUnit);
        var body = "Blocked by VAGS Gateway: Quyền truy cập Proxy CSDL Khoa học Quốc tế chỉ dành cho Cán bộ & Giảng viên ĐHQGHN. Tài khoản Sinh viên không được cấp quyền.\r\n";
        var resp403 = Encoding.ASCII.GetBytes(
            "HTTP/1.1 403 Forbidden\r\n" +
            "Proxy-Agent: VAGS-Gateway/2.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" + body);
        await output.WriteAsync(resp403, connectionContext.ConnectionClosed);
        await output.FlushAsync(connectionContext.ConnectionClosed);
        return;
    }

    // B. Kiểm tra Blacklist (Băng thông cao / Game / Mạng xã hội):
    // Đối với Cán bộ hợp lệ: Cho phép chuyển tiếp thông suốt các dịch vụ Google Workspace / Microsoft
    // để không gây gián đoạn Gmail/Chat của cán bộ trong quá trình Extension đồng bộ
    var isCloudIdentityOrWork = ShExpMatch(targetHost, "*.google.com") || targetHost.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
                                ShExpMatch(targetHost, "*.googleapis.com") || ShExpMatch(targetHost, "*.gstatic.com") ||
                                ShExpMatch(targetHost, "*.googleusercontent.com") || ShExpMatch(targetHost, "*.microsoft.com");

    if (decision.MatchedCategory == "blacklist" && !isCloudIdentityOrWork)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Tên miền trong Blacklist không được phép đi qua Proxy ĐHQGHN", "blacklist_blocked", resolvedUser, resolvedUnit);
        var body = "Blocked by VAGS Gateway: Tên miền này nằm trong danh mục Blacklist và phải truy cập trực tiếp (DIRECT).\r\n";
        var resp403 = Encoding.ASCII.GetBytes(
            "HTTP/1.1 403 Forbidden\r\n" +
            "Proxy-Agent: VAGS-Gateway/2.1\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" + body);
        await output.WriteAsync(resp403, connectionContext.ConnectionClosed);
        await output.FlushAsync(connectionContext.ConnectionClosed);
        return;
    }

    var logUser = string.IsNullOrEmpty(resolvedUser) ? "Khách (Chưa xác thực)" : resolvedUser;
    var logUnit = string.IsNullOrEmpty(resolvedUnit) ? "VNU" : resolvedUnit;

    // 3. Establish TCP tunnel to destination (Academic Publisher or loopback to VAGS itself)
    using var tcpClient = new TcpClient();
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(connectionContext.ConnectionClosed);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var connectHost = targetHost;
        var connectPort = targetPort;
        if (IsVagsSelfHost(targetHost, ruleStore))
        {
            connectHost = serverIp;
            connectPort = 443;
        }
        await tcpClient.ConnectAsync(connectHost, connectPort, cts.Token);
    }
    catch (Exception ex)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, $"Upstream connect failed: {ex.Message}", decision.MatchedRule, logUser, logUnit);
        var resp502 = Encoding.ASCII.GetBytes(
            "HTTP/1.1 502 Bad Gateway\r\n" +
            "Proxy-Agent: VAGS-Gateway/1.0\r\n" +
            "Connection: close\r\n\r\n");
        await output.WriteAsync(resp502, connectionContext.ConnectionClosed);
        await output.FlushAsync(connectionContext.ConnectionClosed);
        return;
    }

    var resp200 = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 Connection Established\r\n" +
        "Proxy-Agent: VAGS-Gateway/1.0 (vags.vnu.edu.vn)\r\n\r\n");
    await output.WriteAsync(resp200, connectionContext.ConnectionClosed);
    await output.FlushAsync(connectionContext.ConnectionClosed);

    var connStart = DateTime.UtcNow;
    using var upstreamStream = tcpClient.GetStream();
    var clientInStream = input.AsStream();
    var clientOutStream = output.AsStream();

    var t1 = TrackStreamCopyAsync(clientInStream, upstreamStream, connectionContext.ConnectionClosed);
    var t2 = TrackStreamCopyAsync(upstreamStream, clientOutStream, connectionContext.ConnectionClosed);
    try
    {
        await Task.WhenAny(t1, t2);
    }
    catch { }

    long bytesSent = t1.IsCompleted ? t1.Result : 0;
    long bytesReceived = t2.IsCompleted ? t2.Result : 0;
    long durationMs = (long)(DateTime.UtcNow - connStart).TotalMilliseconds;

    AddProxyLog(clientIp, targetHost, targetPort, true, decision.Reason, decision.MatchedRule, logUser, logUnit, bytesSent, bytesReceived, durationMs);
}

async Task<long> TrackStreamCopyAsync(Stream source, Stream destination, CancellationToken ct)
{
    byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);
    long totalBytes = 0;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            int bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, ct);
            if (bytesRead == 0) break;
            await destination.WriteAsync(buffer, 0, bytesRead, ct);
            totalBytes += bytesRead;
        }
    }
    catch { }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
    return totalBytes;
}

// Xử lý PROXY Protocol v1 (PROXY TCP4/TCP6 ...) gửi từ HAProxy trước khi bắt đầu TLS Handshake
async Task HandleProxyProtocolAsync(ConnectionContext context, ConnectionDelegate next)
{
    var input = context.Transport.Input;
    try
    {
        var readResult = await input.ReadAsync(context.ConnectionClosed);
        var buffer = readResult.Buffer;

        if (buffer.Length >= 6)
        {
            var prefixBytes = buffer.Slice(0, 6).ToArray();
            var prefix = Encoding.ASCII.GetString(prefixBytes);
            if (prefix.Equals("PROXY ", StringComparison.OrdinalIgnoreCase))
            {
                var lineEnd = buffer.PositionOf((byte)'\n');
                if (lineEnd != null)
                {
                    var lineBytes = buffer.Slice(0, lineEnd.Value).ToArray();
                    var lineStr = Encoding.ASCII.GetString(lineBytes).Trim();
                    // PROXY TCP4 <client_ip> <server_ip> <client_port> <server_port>
                    var parts = lineStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        var realIp = parts[2].Trim();
                        if (realIp.Contains(':') && !realIp.Contains("::")) realIp = realIp.Split(':')[0];
                        if (realIp.StartsWith("::ffff:")) realIp = realIp.Substring(7);
                        context.Items["RealClientIp"] = realIp;
                        if (IPAddress.TryParse(realIp, out var parsedIp))
                        {
                            int port = 0;
                            if (parts.Length >= 5 && int.TryParse(parts[4], out var p)) port = p;
                            context.RemoteEndPoint = new IPEndPoint(parsedIp, port);
                        }
                    }
                    input.AdvanceTo(buffer.GetPosition(1, lineEnd.Value));
                    await next(context);
                    return;
                }
            }
        }

        input.AdvanceTo(buffer.Start, buffer.Start);
    }
    catch
    {
        // On error or early client disconnect, pass to next
    }
    await next(context);
}

bool TryExtractHeaderBlock(ReadOnlySequence<byte> buffer, out string headerBlock, out SequencePosition consumedPos)
{
    headerBlock = string.Empty;
    consumedPos = buffer.Start;
    var array = buffer.ToArray();
    for (int i = 0; i <= array.Length - 4; i++)
    {
        if (array[i] == '\r' && array[i + 1] == '\n' && array[i + 2] == '\r' && array[i + 3] == '\n')
        {
            headerBlock = Encoding.ASCII.GetString(array, 0, i + 4);
            consumedPos = buffer.GetPosition(i + 4);
            return true;
        }
    }
    return false;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();
builder.Services.AddHttpClient();

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // Internal HTTP port 8080 (used by Nginx Reverse Proxy & internal healthchecks + HTTP CONNECT testing)
    serverOptions.ListenAnyIP(8080, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1;
        listenOptions.Use(next => context => HandleConnectionAsync(context, next));
    });

    // Unified HTTPS + CONNECT Gateway port 8443 (used by Port 443 SNI Router & PAC HTTPS vags.vnu.edu.vn:443)
    if (File.Exists(certChainPath) && File.Exists(certKeyPath))
    {
        var cert = X509Certificate2.CreateFromPemFile(certChainPath, certKeyPath);
        // Re-export as PKCS12 to ensure full private key compatibility on all platforms
        cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));

        serverOptions.ListenAnyIP(8443, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http1;
            // Xử lý PROXY Protocol v1 từ HAProxy TRƯỚC KHI handshake TLS
            listenOptions.Use(next => context => HandleProxyProtocolAsync(context, next));
            listenOptions.UseHttps(cert, httpsOptions =>
            {
                httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            });
            listenOptions.Use(next => context => HandleConnectionAsync(context, next));
        });
        Console.WriteLine($"[INFO] Loaded Wildcard SSL certificate from {certChainPath} on port 8443.");
    }
    else
    {
        Console.WriteLine($"[WARN] SSL certificate not found at {certChainPath}; listening on 8080 only.");
    }
});

var app = builder.Build();

app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

if (Directory.Exists(webRootPath))
{
    var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    contentTypeProvider.Mappings[".crx"] = "application/x-chrome-extension";
    contentTypeProvider.Mappings[".xml"] = "application/xml";
    contentTypeProvider.Mappings[".zip"] = "application/zip";
    contentTypeProvider.Mappings[".json"] = "application/json";

    var provider = new PhysicalFileProvider(webRootPath);

    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "";
        if (CheckMaintenanceMode(ruleStore))
        {
            if (!path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".pac", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".css") && !path.EndsWith(".js") && !path.EndsWith(".png") && !path.EndsWith(".svg") && !path.EndsWith(".ico"))
            {
                context.Response.StatusCode = 503;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(@"<!DOCTYPE html>
<html lang='vi'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Hệ Thống Đang Bảo Trì - VAGS Gateway</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0b1120; color: #f8fafc; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; padding: 20px; box-sizing: border-box; }
        .card { max-width: 540px; background: #1e293b; border: 1px solid #334155; border-radius: 16px; padding: 40px; text-align: center; box-shadow: 0 20px 25px -5px rgba(0,0,0,0.5); }
        .icon { font-size: 56px; margin-bottom: 20px; }
        h1 { font-size: 22px; color: #f59e0b; margin-bottom: 12px; }
        p { color: #94a3b8; font-size: 15px; line-height: 1.6; margin-bottom: 24px; }
        .badge { display: inline-block; background: rgba(245, 158, 11, 0.15); color: #fbbf24; border: 1px solid rgba(245, 158, 11, 0.3); padding: 6px 16px; border-radius: 9999px; font-size: 13px; font-weight: 600; margin-bottom: 16px; }
    </style>
</head>
<body>
    <div class='card'>
        <div class='icon'>🛠️</div>
        <span class='badge'>HỆ THỐNG ĐANG TẠM DỪNG BẢO TRÌ</span>
        <h1>Cổng VAGS Đang Nâng Cấp Hạ Tầng 1Gbps</h1>
        <p>Hệ thống Cổng truy cập Cơ sở dữ liệu khoa học VNU Academic Gateway (VAGS) đang được bảo trì định kỳ và tối ưu băng thông máy chủ chuyên dụng.<br>Quý Thầy/Cô và Nhà khoa học vui lòng quay lại sau ít phút.</p>
        <div style='font-size: 12px; color: #64748b;'>Đại học Quốc gia Hà Nội • Trung tâm Quản trị Đại học số</div>
    </div>
</body>
</html>");
                return;
            }
        }
        await next();
    });

    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = provider,
        ContentTypeProvider = contentTypeProvider,
        ServeUnknownFileTypes = true
    });
    // Hỗ trợ cả tiền tố /public/... nếu người dùng truy cập trực tiếp đường dẫn thư mục
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = provider,
        RequestPath = "/public",
        ContentTypeProvider = contentTypeProvider,
        ServeUnknownFileTypes = true
    });
}

// Phục vụ giao diện Quản trị viên (/admin -> admin.html)
app.MapMethods("/admin", new[] { "GET", "HEAD" }, () => Results.File(Path.Combine(webRootPath, "admin.html"), "text/html"));

// Phục vụ trang Chính sách quyền riêng tư (/privacy -> privacy.html)
app.MapMethods("/privacy", new[] { "GET", "HEAD" }, () => Results.File(Path.Combine(webRootPath, "privacy.html"), "text/html"));
app.MapMethods("/privacy/", new[] { "GET", "HEAD" }, () => Results.File(Path.Combine(webRootPath, "privacy.html"), "text/html"));

// Phục vụ trực tiếp CRX & XML với đúng chuẩn MIME Type cho Chrome Enterprise Policy
app.MapMethods("/updates/vags-extension.xml", new[] { "GET", "HEAD" }, () =>
{
    var path = Path.Combine(webRootPath, "updates", "vags-extension.xml");
    if (!File.Exists(path)) path = Path.Combine(webRootPath, "vags-extension.xml");
    return Results.File(path, "application/xml; charset=utf-8");
});

app.MapMethods("/vags-extension.xml", new[] { "GET", "HEAD" }, () =>
{
    var path = Path.Combine(webRootPath, "vags-extension.xml");
    return Results.File(path, "application/xml; charset=utf-8");
});

app.MapMethods("/downloads/vnu-academic-gateway.crx", new[] { "GET", "HEAD" }, () =>
{
    var path = Path.Combine(webRootPath, "downloads", "vnu-academic-gateway.crx");
    if (!File.Exists(path)) path = Path.Combine(webRootPath, "vags-extension.crx");
    return Results.File(path, "application/x-chrome-extension");
});

app.MapMethods("/vags-extension.crx", new[] { "GET", "HEAD" }, () =>
{
    var path = Path.Combine(webRootPath, "vags-extension.crx");
    return Results.File(path, "application/x-chrome-extension");
});

// Phục vụ PAC Script động (Proxy Auto-Config) cho Firefox / OS / Extension
app.MapMethods("/vags.pac", new[] { "GET", "HEAD" }, (HttpContext ctx) =>
{
    var proxyServer = $"{ruleStore.ProxyScheme} {ruleStore.ProxyHost}:{ruleStore.ProxyPort}";
    var user = ctx.Request.Query["user"].ToString();

    // Nếu đang ở Chế độ Bảo trì (Maintenance Mode), trả về PAC toàn bộ DIRECT
    if (CheckMaintenanceMode(ruleStore))
    {
        return Results.Text("function FindProxyForURL(url, host) {\n    // VAGS Maintenance Mode Active -> Bypass All Direct\n    return 'DIRECT';\n}", "application/x-ns-proxy-autoconfig; charset=utf-8");
    }

    // Nếu tài khoản bị khoá, trả về PAC toàn bộ DIRECT
    var isUserDenied = !string.IsNullOrWhiteSpace(user) && (ruleStore.UserOverrides?.Denied?.Contains(user, StringComparer.OrdinalIgnoreCase) == true);
    if (isUserDenied)
    {
        return Results.Text("function FindProxyForURL(url, host) { return 'DIRECT'; }", "application/x-ns-proxy-autoconfig; charset=utf-8");
    }

    var userProxyDomains = new List<string>();
    if (!string.IsNullOrWhiteSpace(user) && ruleStore.UserOverrides?.UserSpecificRules != null && ruleStore.UserOverrides.UserSpecificRules.TryGetValue(user, out var uCfg))
    {
        userProxyDomains = uCfg.ProxyDomains ?? new List<string>();
    }

    var isUserAuth = !string.IsNullOrWhiteSpace(user) && user != "Khách (Chưa xác thực)";
    var isFullProxy = string.Equals(ruleStore.ProxyMode, "FULL_PROXY", StringComparison.OrdinalIgnoreCase);
    var defaultDirective = (isFullProxy && isUserAuth) ? proxyServer : "DIRECT";

    var pacBypasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        primaryDomain,
        serverIp,
        "112.137.142.30",
        "112.137.133.14",
        "127.0.0.1",
        "localhost"
    };
    if (!string.IsNullOrWhiteSpace(ruleStore.ProxyHost)) pacBypasses.Add(ruleStore.ProxyHost);
    foreach (var a in domainAliases) pacBypasses.Add(a);
    if (ruleStore.DomainAliases != null) foreach (var a in ruleStore.DomainAliases) pacBypasses.Add(a);

    var pacBypassChecks = string.Join(" || ", pacBypasses.Select(b => $"lHost === '{b.ToLowerInvariant()}' || shExpMatch(lHost, '*.{b.ToLowerInvariant()}')"));

    var pac = $@"function FindProxyForURL(url, host) {{
    var lHost = host.toLowerCase();
    if ({pacBypassChecks}) return 'DIRECT';
    if (shExpMatch(lHost, '*.google.com') || lHost === 'google.com' || shExpMatch(lHost, '*.googleapis.com') || shExpMatch(lHost, '*.gstatic.com') || shExpMatch(lHost, '*.googleusercontent.com')) return 'DIRECT';
    var userWL = {JsonSerializer.Serialize(userProxyDomains)};
    for (var u = 0; u < userWL.length; u++) {{
        if (shExpMatch(lHost, userWL[u])) return ""{proxyServer}"";
    }}
    var blacklist = {JsonSerializer.Serialize(ruleStore.Blacklist)};
    for (var i = 0; i < blacklist.length; i++) {{
        if (shExpMatch(lHost, blacklist[i])) return ""DIRECT"";
    }}
    var internalWL = {JsonSerializer.Serialize(ruleStore.InternalWhitelist)};
    for (var j = 0; j < internalWL.length; j++) {{
        if (shExpMatch(lHost, internalWL[j])) return ""{proxyServer}"";
    }}
    if (shExpMatch(lHost, ""*.vnu.edu.vn"") || lHost === ""vnu.edu.vn"") return ""DIRECT"";
    var academicWL = {JsonSerializer.Serialize(ruleStore.GlobalAcademicWhitelist)};
    for (var k = 0; k < academicWL.length; k++) {{
        if (shExpMatch(lHost, academicWL[k])) return ""{proxyServer}"";
    }}
    return ""{defaultDirective}"";
}}";
    return Results.Content(pac, "application/x-ns-proxy-autoconfig; charset=utf-8");
});

// Phục vụ chuyển tiếp OAuth2 Callback sau khi đăng nhập SSO Keycloak VNU
app.MapMethods("/auth/callback", new[] { "GET", "HEAD" }, () => Results.File(Path.Combine(webRootPath, "callback.html"), "text/html"));

// 1. API Lấy tập luật hiện tại (Hỗ trợ cả route /rules và /rules/latest)
app.MapGet("/api/v1/rules", () => Results.Json(ruleStore, jsonOptions));
app.MapGet("/api/v1/rules/latest", () => Results.Json(ruleStore, jsonOptions));

// 2. API Kiểm tra phiên bản (Startup Catch-up)
app.MapGet("/api/v1/rules/version", () => Results.Json(new
{
    version = ruleStore.Version,
    authMode = ruleStore.AuthMode,
    proxy = $"{ruleStore.ProxyScheme} {ruleStore.ProxyHost}:{ruleStore.ProxyPort}",
    updatedAt = ruleStore.UpdatedAt
}, jsonOptions));

// 3. API Nhận SSE Stream Real-time từ Extension
app.MapGet("/api/v1/rules/stream", async context =>
{
    context.Response.Headers.Append("Content-Type", "text/event-stream; charset=utf-8");
    context.Response.Headers.Append("Cache-Control", "no-cache, no-transform");
    context.Response.Headers.Append("Connection", "keep-alive");
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    var clientId = Guid.NewGuid();
    var qIp = context.Request.Query["clientIp"].FirstOrDefault();
    var qUser = context.Request.Query["user"].FirstOrDefault();
    var qUnit = context.Request.Query["unit"].FirstOrDefault();
    var qProxyEnabledStr = context.Request.Query["proxyEnabled"].FirstOrDefault();
    bool qProxyEnabled = string.Equals(qProxyEnabledStr, "true", StringComparison.OrdinalIgnoreCase);

    var rawIp = GetRealClientIp(context);
    if (!string.IsNullOrWhiteSpace(qIp) && qIp.Trim() != "unknown" && !qIp.Contains("127.0.0.1") && !qIp.Contains("172.23."))
        rawIp = CleanIp(qIp);

    if (!string.IsNullOrWhiteSpace(qUser) && qUser != "Cán bộ VNU")
    {
        monitorDb.RegisterUserActivity(rawIp, qUser, qUnit ?? "VNU", "canbo", qProxyEnabled);
    }

    var clientInfo = new SseClientInfo
    {
        Id = clientId,
        Response = context.Response,
        ConnectedAt = DateTime.UtcNow.ToString("O"),
        RemoteIp = rawIp,
        UserAgent = context.Request.Headers["User-Agent"].FirstOrDefault() ?? "VAGS-Client",
        User = !string.IsNullOrWhiteSpace(qUser) ? qUser : "Khách (Chưa xác thực)",
        Unit = !string.IsNullOrWhiteSpace(qUnit) ? qUnit.ToUpperInvariant() : "VNU"
    };

    sseClients[clientId] = clientInfo;

    try
    {
        var initMsg = JsonSerializer.Serialize(new
        {
            type = "CONNECTED",
            clientId = clientId,
            version = ruleStore.Version,
            authMode = ruleStore.AuthMode
        }, sseJsonOptions);
        await context.Response.WriteAsync($"event: CONNECTED\ndata: {initMsg}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        while (!context.RequestAborted.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted);
            await context.Response.WriteAsync($": keep-alive {DateTime.UtcNow:O}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch
    {
        // Client disconnected cleanly
    }
    finally
    {
        sseClients.TryRemove(clientId, out _);
    }
});

// 4. API Admin cập nhật Rules & Broadcast Real-time xuống toàn bộ Extension
async Task<IResult> HandleSaveRulesAsync(RuleStoreModel newRules)
{
    var now = DateTime.UtcNow;
    newRules.Version = $"{now:yyyy.MM.dd.HHmmss}.{now.Millisecond:D3}";
    newRules.UpdatedAt = now.ToString("O");

    if (newRules.Blacklist == null || newRules.Blacklist.Count == 0)
        newRules.Blacklist = ruleStore.Blacklist;
    if (newRules.InternalWhitelist == null || newRules.InternalWhitelist.Count == 0)
        newRules.InternalWhitelist = ruleStore.InternalWhitelist;
    if (newRules.GlobalAcademicWhitelist == null || newRules.GlobalAcademicWhitelist.Count == 0)
        newRules.GlobalAcademicWhitelist = ruleStore.GlobalAcademicWhitelist;
    if (string.IsNullOrWhiteSpace(newRules.ProxyMode))
        newRules.ProxyMode = ruleStore.ProxyMode;
    if (string.IsNullOrWhiteSpace(newRules.AuthMode))
        newRules.AuthMode = ruleStore.AuthMode;
    if (string.IsNullOrWhiteSpace(newRules.KeycloakAuthority))
        newRules.KeycloakAuthority = ruleStore.KeycloakAuthority;
    if (string.IsNullOrWhiteSpace(newRules.ProxyHost))
        newRules.ProxyHost = "vags.vnu.edu.vn";
    if (newRules.ProxyPort <= 0)
        newRules.ProxyPort = 443;
    if (string.IsNullOrWhiteSpace(newRules.ProxyScheme))
        newRules.ProxyScheme = "HTTPS";
    if (newRules.AllowedOUs == null || newRules.AllowedOUs.Count == 0)
        newRules.AllowedOUs = ruleStore.AllowedOUs;
    if (newRules.UserOverrides == null)
        newRules.UserOverrides = ruleStore.UserOverrides;

    ruleStore = newRules;
    SaveRules(ruleStore);

    var serialized = JsonSerializer.Serialize(ruleStore, sseJsonOptions);
    var payload = $"event: RULES_UPDATED\ndata: {serialized}\n\n";

    int delivered = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        try
        {
            await kvp.Value.Response.WriteAsync(payload);
            await kvp.Value.Response.Body.FlushAsync();
            delivered++;
        }
        catch
        {
            sseClients.TryRemove(kvp.Key, out _);
        }
    }

    return Results.Json(new
    {
        success = true,
        message = "Đã lưu và phát sóng tập luật thành công",
        version = ruleStore.Version,
        updatedAt = ruleStore.UpdatedAt,
        broadcastedClients = delivered
    }, jsonOptions);
}

app.MapPost("/api/v1/admin/rules", (RuleStoreModel newRules) => HandleSaveRulesAsync(newRules));
app.MapPost("/api/v1/rules", (RuleStoreModel newRules) => HandleSaveRulesAsync(newRules));

// 5. API Kiểm thử Phân loại Routing cho 1 Domain bất kỳ (Hỗ trợ theo tài khoản User)
app.MapPost("/api/v1/rules/evaluate", (EvaluateRequest req) =>
{
    var decision = EvaluateHost(req.Host ?? "", ruleStore, req.User);
    return Results.Json(decision, jsonOptions);
});

// 6. API Cung cấp cấu hình Keycloak OIDC cho Frontend Web & Extension
app.MapGet("/api/v1/auth/keycloak-config", () => Results.Json(new
{
    authority = keycloakAuthority,
    authorizationEndpoint = $"{keycloakAuthority}/protocol/openid-connect/auth",
    tokenEndpoint = $"{keycloakAuthority}/protocol/openid-connect/token",
    userInfoEndpoint = $"{keycloakAuthority}/protocol/openid-connect/userinfo",
    endSessionEndpoint = $"{keycloakAuthority}/protocol/openid-connect/logout",
    clientId = keycloakClientId,
    adminOrganizationalUnit = "pqtkt"
}, jsonOptions));

// 6.1 API Đăng xuất - Thu hồi phiên hoạt động theo IP
app.MapPost("/api/v1/auth/logout", (HttpContext context) =>
{
    var clientIp = GetRealClientIp(context);
    monitorDb.UnregisterUserActivity(clientIp);
    return Results.Json(new { ok = true, clientIp, message = "Đã thu hồi phiên cán bộ thành công." }, jsonOptions);
});
app.MapPost("/api/v1/user/logout", (HttpContext context) =>
{
    var clientIp = GetRealClientIp(context);
    monitorDb.UnregisterUserActivity(clientIp);
    return Results.Json(new { ok = true, clientIp, message = "Đã thu hồi phiên cán bộ thành công." }, jsonOptions);
});

// 7. API Trao đổi Authorization Code lấy Token & UserInfo từ Keycloak Production
app.MapPost("/api/v1/auth/exchange", async (HttpContext context, ExchangeCodeRequest req, IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(req?.Code))
        return Results.BadRequest(new { error = "Thiếu authorization code từ Keycloak." });

    var client = httpClientFactory.CreateClient();
    var tokenEndpoint = $"{keycloakAuthority}/protocol/openid-connect/token";
    var userinfoEndpoint = $"{keycloakAuthority}/protocol/openid-connect/userinfo";
    var redirectUri = string.IsNullOrWhiteSpace(req.RedirectUri)
        ? "https://vags.vnu.edu.vn/auth/callback"
        : req.RedirectUri;

    var formParams = new Dictionary<string, string>
    {
        { "grant_type", "authorization_code" },
        { "code", req.Code },
        { "redirect_uri", redirectUri },
        { "client_id", keycloakClientId },
        { "client_secret", keycloakClientSecret }
    };

    if (!string.IsNullOrWhiteSpace(req.CodeVerifier))
    {
        formParams["code_verifier"] = req.CodeVerifier;
    }

    var tokenResp = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(formParams));
    if (!tokenResp.IsSuccessStatusCode)
    {
        var errBody = await tokenResp.Content.ReadAsStringAsync();
        return Results.Json(new { error = "Keycloak token exchange failed", details = errBody }, statusCode: (int)tokenResp.StatusCode);
    }

    var tokenContent = await tokenResp.Content.ReadAsStringAsync();
    using var tokenDoc = JsonDocument.Parse(tokenContent);
    var accessToken = tokenDoc.RootElement.TryGetProperty("access_token", out var atEl) ? atEl.GetString() : "";
    var idToken = tokenDoc.RootElement.TryGetProperty("id_token", out var idtEl) ? idtEl.GetString() : "";
    var expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 300;

    // Lấy thông tin UserInfo từ Keycloak
    var reqMessage = new HttpRequestMessage(HttpMethod.Get, userinfoEndpoint);
    reqMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    var userInfoResp = await client.SendAsync(reqMessage);

    string username = "";
    string name = "";
    string email = "";
    string organizationalUnit = "";
    string memberType = "";

    if (userInfoResp.IsSuccessStatusCode)
    {
        var userInfoContent = await userInfoResp.Content.ReadAsStringAsync();
        using var userDoc = JsonDocument.Parse(userInfoContent);
        var root = userDoc.RootElement;
        username = root.TryGetProperty("preferred_username", out var puEl) ? puEl.GetString() ?? "" : "";
        name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : username;
        email = root.TryGetProperty("email", out var eEl) ? eEl.GetString() ?? "" : $"{username}@vnu.edu.vn";
        organizationalUnit = root.TryGetProperty("organizational_unit", out var ouEl) ? ouEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(organizationalUnit) && root.TryGetProperty("vnu_admin_unit", out var vauEl))
            organizationalUnit = vauEl.GetString() ?? "";
        memberType = root.TryGetProperty("vnu_member_type", out var mtEl) ? mtEl.GetString() ?? "" : "";
    }

    // Đăng ký hoạt động và IP của Cán bộ vào hệ thống SQLite Monitor
    var clientIp = GetRealClientIp(context);
    if (!string.IsNullOrWhiteSpace(username))
    {
        monitorDb.RegisterUserActivity(clientIp, username, organizationalUnit, memberType);
    }

    // Phân quyền Quản trị viên: Đơn vị 'pqtkt' (như phongnd, vuongwork) hoặc username 'admin', 'vuongwork'
    var isAdmin = (!string.IsNullOrEmpty(organizationalUnit) && organizationalUnit.Contains("pqtkt", StringComparison.OrdinalIgnoreCase)) ||
                  string.Equals(username, "phongnd", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(username, "vuongwork", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(username, "vuongwork@vnu.edu.vn", StringComparison.OrdinalIgnoreCase) ||
                  (!string.IsNullOrEmpty(email) && email.Contains("vuongwork", StringComparison.OrdinalIgnoreCase)) ||
                  string.Equals(username, "admin", StringComparison.OrdinalIgnoreCase);

    if (isAdmin && (string.IsNullOrEmpty(organizationalUnit) || organizationalUnit == "VNU"))
    {
        organizationalUnit = "PQTKT";
    }

    return Results.Json(new
    {
        success = true,
        accessToken = accessToken,
        idToken = idToken,
        expiresIn = expiresIn,
        user = username,
        name = name,
        email = email,
        organizationalUnit = organizationalUnit,
        memberType = memberType,
        isAdmin = isAdmin,
        issuer = keycloakAuthority
    }, jsonOptions);
});

// 7. API Cấp Token SSO Giả lập / Kiểm thử cho Extension & Dashboard (Dual-Mode)
// User thuộc đơn vị 'pqtkt' (như phongnd) được cấp quyền Admin
app.MapPost("/api/v1/auth/demo-token", (HttpContext context, DemoTokenRequest? req) =>
{
    var username = string.IsNullOrWhiteSpace(req?.Username) ? "phongnd" : req!.Username!;
    var unit = string.IsNullOrWhiteSpace(req?.OrganizationalUnit)
        ? (username.Contains("phongnd", StringComparison.OrdinalIgnoreCase) ? "pqtkt" : "general")
        : req!.OrganizationalUnit!;
    var isAdmin = unit.Equals("pqtkt", StringComparison.OrdinalIgnoreCase) ||
                  username.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
                  username.Equals("phongnd", StringComparison.OrdinalIgnoreCase) ||
                  username.Equals("vuongwork", StringComparison.OrdinalIgnoreCase) ||
                  username.Equals("vuongwork@vnu.edu.vn", StringComparison.OrdinalIgnoreCase) ||
                  username.Contains("vuongwork", StringComparison.OrdinalIgnoreCase);

    var clientIp = GetRealClientIp(context);
    monitorDb.RegisterUserActivity(clientIp, username, unit, "canbo");

    var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
    var payloadObj = new
    {
        sub = username,
        preferred_username = username,
        name = username == "phongnd" ? "Nguyễn Đức Phong (P. Quản trị & Kế hoạch)" : username,
        email = $"{username}@vnu.edu.vn",
        iss = "https://idp.vnu.edu.vn/auth/realms/vnu",
        aud = "vags-gateway",
        organizational_unit = unit,
        vnu_admin_unit = unit,
        role = isAdmin ? "vags-admin" : "vnu-academic-faculty",
        isAdmin = isAdmin,
        exp = exp
    };
    var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var sig = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{header}.{payload}.vags-vnu-secret"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    return Results.Json(new
    {
        accessToken = $"{header}.{payload}.{sig}",
        tokenType = "Bearer",
        expiresIn = 43200,
        user = username,
        name = username == "phongnd" ? "Nguyễn Đức Phong" : username,
        organizationalUnit = unit,
        isAdmin = isAdmin,
        issuer = "https://idp.vnu.edu.vn/auth/realms/vnu"
    }, jsonOptions);
});

// 7. API Kiểm tra IP & Chống Rò Rỉ Mạng (IP Leak Check)
app.MapGet("/api/v1/ip-check", (HttpContext http) =>
{
    var remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var forwardedFor = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    var clientIp = !string.IsNullOrEmpty(forwardedFor) ? forwardedFor.Split(',')[0].Trim() : remoteIp;
    var isVnu = clientIp.StartsWith("112.137.") || clientIp.StartsWith("10.241.") || clientIp.StartsWith("172.16.") || clientIp == serverIp || clientIp == "112.137.142.30" || clientIp == "112.137.133.14";

    return Results.Json(new
    {
        clientIp = clientIp,
        isVnuNetwork = isVnu,
        vnuEgressIp = serverIp,
        status = isVnu ? "VNU Internal / Tunneled Network" : "External Direct Network",
        gateway = $"{primaryDomain}:443",
        timestamp = DateTime.UtcNow.ToString("O")
    }, jsonOptions);
});

// 8. API Báo cáo Tổng quan Admin Dashboard & Phân tích Lưu lượng SQLite
app.MapGet("/api/v1/admin/dashboard", async () =>
{
    var data = await monitorDb.GetAnalyticsSummaryAsync(ruleStore.Version, ruleStore.ProxyMode, serverIp);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/analytics", async () =>
{
    var data = await monitorDb.GetAnalyticsSummaryAsync(ruleStore.Version, ruleStore.ProxyMode, serverIp);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/top-urls", async (int? limit) =>
{
    var data = await monitorDb.GetTopUrlsAsync(limit ?? 10);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/top-users", async (int? limit) =>
{
    var data = await monitorDb.GetTopUsersAsync(limit ?? 10);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/top-ips", async (int? limit) =>
{
    var data = await monitorDb.GetTopIpsAsync(limit ?? 10);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/top-bandwidth-urls", async (int? limit) =>
{
    var data = await monitorDb.GetTopBandwidthUrlsAsync(limit ?? 10);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/audit-logs", async (string? search, string? user, string? ip, string? domain, string? status, string? allowed, int? limit, int? offset) =>
{
    var (logs, total) = await monitorDb.GetAuditLogsAsync(search, user, ip, domain, status, allowed, limit ?? 50, offset ?? 0);
    return Results.Json(new { logs, total, limit = limit ?? 50, offset = offset ?? 0 }, jsonOptions);
});

app.MapGet("/api/v1/admin/domains-breakdown", async (string? search, string? category, int? limit, int? offset) =>
{
    var (domains, total) = await monitorDb.GetDomainBreakdownAsync(search, category, limit ?? 50, offset ?? 0);
    return Results.Json(new { domains, total, limit = limit ?? 50, offset = offset ?? 0 }, jsonOptions);
});

app.MapGet("/api/v1/admin/audit-logs/export", async (int? limit) =>
{
    var csv = await monitorDb.ExportAuditLogsCsvAsync(limit ?? 1000);
    return Results.Content(csv, "text/csv; charset=utf-8", Encoding.UTF8);
});

// API Ghi nhận nhịp tim hoạt động (Heartbeat) của Cán bộ từ Extension
app.MapPost("/api/v1/user/heartbeat", (HttpContext ctx, HeartbeatRequest? req) =>
{
    var clientIp = GetRealClientIp(ctx);
    var u = req?.User ?? ctx.Request.Query["user"].FirstOrDefault() ?? "";
    var unit = req?.Unit ?? ctx.Request.Query["unit"].FirstOrDefault() ?? "VNU";
    var memberType = req?.MemberType ?? ctx.Request.Query["memberType"].FirstOrDefault() ?? "canbo";
    var proxyEnabled = req?.ProxyEnabled == true;
    if (!string.IsNullOrWhiteSpace(u) && u != "Cán bộ VNU")
    {
        monitorDb.RegisterUserActivity(clientIp, u, unit, memberType, proxyEnabled);
    }
    return Results.Json(new { ok = true, clientIp, user = u, proxyEnabled });
});

// API Cập nhật trạng thái Bật/Tắt Proxy tức thì của Extension
app.MapPost("/api/v1/user/status", (HttpContext ctx, UserStatusRequest? req) =>
{
    var clientIp = GetRealClientIp(ctx);
    var u = req?.User ?? "";
    var proxyEnabled = req?.ProxyEnabled == true;
    if (!string.IsNullOrWhiteSpace(u) && u != "Cán bộ VNU")
    {
        monitorDb.UpdateUserProxyStatus(clientIp, u, proxyEnabled);
    }
    return Results.Json(new { ok = true, clientIp, user = u, proxyEnabled });
});

app.MapGet("/api/v1/user/heartbeat", (HttpContext ctx) =>
{
    var clientIp = GetRealClientIp(ctx);
    var u = ctx.Request.Query["user"].FirstOrDefault() ?? "";
    var unit = ctx.Request.Query["unit"].FirstOrDefault() ?? "VNU";
    var memberType = ctx.Request.Query["memberType"].FirstOrDefault() ?? "canbo";
    var isEnabled = !string.Equals(ctx.Request.Query["proxyEnabled"], "false", StringComparison.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(u) && u != "Cán bộ VNU")
    {
        monitorDb.RegisterUserActivity(clientIp, u, unit, memberType, isEnabled);
    }
    return Results.Json(new { ok = true, clientIp, user = u, proxyEnabled = isEnabled });
});

// 9. API Trạng thái Hệ thống & Giám sát Real-time
app.MapGet("/api/v1/health", () => Results.Json(new
{
    status = CheckMaintenanceMode(ruleStore) ? "maintenance" : "healthy",
    version = ruleStore.Version,
    proxyMode = ruleStore.ProxyMode,
    egressIp = serverIp,
    primaryDomain = primaryDomain,
    maintenanceMode = CheckMaintenanceMode(ruleStore)
}, jsonOptions));

app.MapGet("/api/v1/status", () =>
{
    return Results.Json(new
    {
        service = "VNU Academic Gateway (VAGS)",
        domain = primaryDomain,
        serverIp = serverIp,
        domainAliases = domainAliases,
        maintenanceMode = CheckMaintenanceMode(ruleStore),
        version = ruleStore.Version,
        authMode = ruleStore.AuthMode,
        keycloakAuthority = ruleStore.KeycloakAuthority,
        proxyEndpoint = $"{ruleStore.ProxyScheme} {ruleStore.ProxyHost}:{ruleStore.ProxyPort}",
        uptimeSeconds = (int)(DateTime.UtcNow - startTime).TotalSeconds,
        activeSseClients = sseClients.Count,
        sseClients = sseClients.Values.Select(c => new
        {
            c.Id,
            c.RemoteIp,
            c.ConnectedAt,
            c.UserAgent
        }).ToList(),
        metrics = new
        {
            totalProxyRequests = Interlocked.Read(ref totalProxyRequests),
            allowedProxyRequests = Interlocked.Read(ref allowedProxyRequests),
            blockedProxyRequests = Interlocked.Read(ref blockedProxyRequests)
        },
        recentProxyLogs = proxyLogs.Reverse().Take(50).ToList()
    }, jsonOptions);
});

// 10. API Đồng bộ danh mục CSDL điện tử từ LIC (lic.vnu.edu.vn)
IResult SyncLicDatabases()
{
    var licVerified = new List<string>
    {
        "*.sciencedirect.com", "sciencedirect.com",
        "*.elsevier.com", "elsevier.com",
        "*.sciencedirectassets.com", "*.elsevierpure.com",
        "*.springer.com", "springer.com",
        "*.springernature.com", "springernature.com",
        "*.nature.com", "nature.com",
        "*.acm.org", "acm.org",
        "*.wiley.com", "wiley.com",
        "*.onlinelibrary.wiley.com", "onlinelibrary.wiley.com",
        "*.scopus.com", "scopus.com",
        "*.webofscience.com", "webofscience.com",
        "*.clarivate.com", "clarivate.com",
        "*.worldscientific.com", "worldscientific.com",
        "*.tandfonline.com", "tandfonline.com",
        "*.taylorandfrancis.com", "taylorandfrancis.com",
        "*.mheducation.com", "*.accessengineeringlibrary.com",
        "*.bookboon.com", "bookboon.com",
        "*.jstor.org", "jstor.org",
        "*.ncbi.nlm.nih.gov", "ncbi.nlm.nih.gov",
        "*.pubmed.ncbi.nlm.nih.gov", "pubmed.ncbi.nlm.nih.gov",
        "*.iop.org", "iop.org",
        "*.aip.org", "aip.org",
        "*.oxfordacademic.com", "oxfordacademic.com",
        "*.academic.oup.com", "academic.oup.com",
        "*.cambridge.org", "cambridge.org",
        "*.emerald.com", "emerald.com"
    };

    int added = 0;
    foreach (var d in licVerified)
    {
        if (!ruleStore.GlobalAcademicWhitelist.Contains(d, StringComparer.OrdinalIgnoreCase))
        {
            ruleStore.GlobalAcademicWhitelist.Add(d);
            added++;
        }
    }

    if (added > 0)
    {
        var now = DateTime.UtcNow;
        ruleStore.Version = $"{now:yyyy.MM.dd.HHmmss}.{now.Millisecond:D3}";
        ruleStore.UpdatedAt = now.ToString("O");
        SaveRules(ruleStore);
    }

    return Results.Json(new
    {
        success = true,
        source = "https://lic.vnu.edu.vn/",
        addedDatabases = added,
        totalDatabases = ruleStore.GlobalAcademicWhitelist.Count,
        version = ruleStore.Version
    }, jsonOptions);
}

app.MapPost("/api/v1/lic/sync", () => SyncLicDatabases());
app.MapPost("/api/v1/lic/sync-databases", () => SyncLicDatabases());

// 11. API Quản lý Mật khẩu ứng dụng (Proxy Access Key) cho Cán bộ
app.MapGet("/api/v1/user/proxy-key", (HttpContext ctx) =>
{
    var user = ctx.Request.Query["user"].FirstOrDefault() ?? ctx.Request.Headers["X-VAGS-User"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(user) || user == "Cán bộ VNU")
        return Results.BadRequest(new { error = "Yêu cầu cung cấp username cán bộ hợp lệ" });
    var key = monitorDb.GetOrCreateAppKey(user);
    return Results.Json(new { ok = true, user, proxyKey = key, host = "vags.vnu.edu.vn", port = 443 }, jsonOptions);
});

app.MapPost("/api/v1/user/proxy-key/regenerate", (HttpContext ctx, KeyRegenerateRequest req) =>
{
    var user = req?.User ?? ctx.Request.Query["user"].FirstOrDefault() ?? ctx.Request.Headers["X-VAGS-User"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(user) || user == "Cán bộ VNU")
        return Results.BadRequest(new { error = "Yêu cầu cung cấp username cán bộ hợp lệ" });
    var key = monitorDb.RegenerateAppKey(user);
    return Results.Json(new { ok = true, user, proxyKey = key, message = "Đã thu hồi mã cũ và sinh Mật khẩu ứng dụng mới thành công!" }, jsonOptions);
});

// 12. API Phát sóng ép toàn bộ Extension cập nhật tức thì (SSE Force Update)
app.MapPost("/api/v1/admin/broadcast-update", async (BroadcastUpdateRequest? req) =>
{
    var targetVersion = req?.Version ?? "2.1.2";
    var payload = $"event: FORCE_UPDATE\ndata: {JsonSerializer.Serialize(new { minVersion = targetVersion, version = targetVersion, timestamp = DateTime.UtcNow }, sseJsonOptions)}\n\n";
    int sent = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        try
        {
            await kvp.Value.Response.WriteAsync(payload);
            await kvp.Value.Response.Body.FlushAsync();
            sent++;
        }
        catch
        {
            sseClients.TryRemove(kvp.Key, out _);
        }
    }
    return Results.Json(new { ok = true, version = targetVersion, broadcastedClients = sent, message = $"Đã phát tín hiệu ép toàn bộ {sent} Extension đang trực tuyến nâng cấp lên v{targetVersion}!" }, jsonOptions);
});

// 13. API Admin Force Tắt Proxy (Cá nhân hoặc Toàn bộ)
app.MapPost("/api/v1/admin/users/force-proxy-off", async (ForceUserActionRequest? req) =>
{
    var isAll = req?.All == true;
    var targetUser = req?.User?.Trim();
    var targetIp = req?.Ip?.Trim();

    var eventData = new
    {
        all = isAll,
        user = targetUser,
        ip = targetIp,
        timestamp = DateTime.UtcNow
    };

    var payload = $"event: FORCE_PROXY_OFF\ndata: {JsonSerializer.Serialize(eventData, sseJsonOptions)}\n\n";
    int sent = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        try
        {
            await kvp.Value.Response.WriteAsync(payload);
            await kvp.Value.Response.Body.FlushAsync();
            sent++;
        }
        catch
        {
            sseClients.TryRemove(kvp.Key, out _);
        }
    }

    var msg = isAll
        ? $"Đã phát tín hiệu ép TẮT PROXY TOÀN BỘ {sent} Extension đang trực tuyến!"
        : $"Đã gửi lệnh ép tắt Proxy tới '{targetUser ?? targetIp}' (đã phát sóng tới {sent} kết nối SSE).";
    return Results.Json(new { ok = true, all = isAll, user = targetUser, ip = targetIp, broadcastedClients = sent, message = msg }, jsonOptions);
});

// 14. API Admin Force Đăng Xuất (Cá nhân hoặc Toàn bộ)
app.MapPost("/api/v1/admin/users/force-logout", async (ForceUserActionRequest? req) =>
{
    var isAll = req?.All == true;
    var targetUser = req?.User?.Trim();
    var targetIp = req?.Ip?.Trim();

    if (isAll)
    {
        monitorDb.UnregisterAllUserActivities();
    }
    else
    {
        if (!string.IsNullOrWhiteSpace(targetIp))
        {
            monitorDb.UnregisterUserActivity(targetIp);
        }
        if (!string.IsNullOrWhiteSpace(targetUser))
        {
            monitorDb.UnregisterUserActivityByUser(targetUser);
        }
    }

    var eventData = new
    {
        all = isAll,
        user = targetUser,
        ip = targetIp,
        reason = req?.Reason ?? "Quản trị viên đã ngắt phiên và yêu cầu đăng xuất.",
        timestamp = DateTime.UtcNow
    };

    var payload = $"event: FORCE_LOGOUT\ndata: {JsonSerializer.Serialize(eventData, sseJsonOptions)}\n\n";
    int sent = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        try
        {
            await kvp.Value.Response.WriteAsync(payload);
            await kvp.Value.Response.Body.FlushAsync();
            sent++;
        }
        catch
        {
            sseClients.TryRemove(kvp.Key, out _);
        }
    }

    var msg = isAll
        ? $"Đã xoá toàn bộ phiên và ép ĐĂNG XUẤT TẤT CẢ người dùng ({sent} Extension đã nhận lệnh)!"
        : $"Đã xoá phiên và ép đăng xuất tài khoản '{targetUser ?? targetIp}'.";
    return Results.Json(new { ok = true, all = isAll, user = targetUser, ip = targetIp, broadcastedClients = sent, message = msg }, jsonOptions);
});

// 15. API Admin Khoá Tài Khoản (Lock User)
app.MapPost("/api/v1/admin/users/lock", async (LockUserRequest? req) =>
{
    if (string.IsNullOrWhiteSpace(req?.User))
    {
        return Results.BadRequest(new { ok = false, message = "Vui lòng nhập tên tài khoản cần khoá." });
    }
    var userToLock = req.User.Trim();

    ruleStore.UserOverrides ??= new UserOverridesModel();
    ruleStore.UserOverrides.Denied ??= new List<string>();
    ruleStore.UserOverrides.Allowed ??= new List<string>();

    if (!ruleStore.UserOverrides.Denied.Contains(userToLock, StringComparer.OrdinalIgnoreCase))
    {
        ruleStore.UserOverrides.Denied.Add(userToLock);
    }
    ruleStore.UserOverrides.Allowed.RemoveAll(u => string.Equals(u, userToLock, StringComparison.OrdinalIgnoreCase));

    SaveRules(ruleStore);
    monitorDb.UnregisterUserActivityByUser(userToLock);

    var eventData = new
    {
        user = userToLock,
        reason = req.Reason ?? "Tài khoản đã bị Quản trị viên khoá quyền sử dụng Proxy VAGS.",
        timestamp = DateTime.UtcNow
    };

    var lockPayload = $"event: FORCE_LOCK\ndata: {JsonSerializer.Serialize(eventData, sseJsonOptions)}\n\n";
    var rulesPayload = $"event: RULES_UPDATED\ndata: {JsonSerializer.Serialize(ruleStore, sseJsonOptions)}\n\n";

    int sent = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        try
        {
            await kvp.Value.Response.WriteAsync(lockPayload);
            await kvp.Value.Response.WriteAsync(rulesPayload);
            await kvp.Value.Response.Body.FlushAsync();
            sent++;
        }
        catch
        {
            sseClients.TryRemove(kvp.Key, out _);
        }
    }

    return Results.Json(new
    {
        ok = true,
        user = userToLock,
        deniedUsers = ruleStore.UserOverrides.Denied,
        message = $"Đã KHOÁ tài khoản '{userToLock}' thành công, ngắt phiên kết nối và thông báo tới {sent} Extension!",
        version = ruleStore.Version
    }, jsonOptions);
});

// 16. API Admin Mở Khoá Tài Khoản (Unlock User)
app.MapPost("/api/v1/admin/users/unlock", async (LockUserRequest? req) =>
{
    if (string.IsNullOrWhiteSpace(req?.User))
    {
        return Results.BadRequest(new { ok = false, message = "Vui lòng nhập tên tài khoản cần mở khoá." });
    }
    var userToUnlock = req.User.Trim();

    ruleStore.UserOverrides ??= new UserOverridesModel();
    ruleStore.UserOverrides.Denied ??= new List<string>();

    ruleStore.UserOverrides.Denied.RemoveAll(u => string.Equals(u, userToUnlock, StringComparison.OrdinalIgnoreCase));
    SaveRules(ruleStore);

    var rulesPayload = $"event: RULES_UPDATED\ndata: {JsonSerializer.Serialize(ruleStore, sseJsonOptions)}\n\n";
    int sent = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        try
        {
            await kvp.Value.Response.WriteAsync(rulesPayload);
            await kvp.Value.Response.Body.FlushAsync();
            sent++;
        }
        catch
        {
            sseClients.TryRemove(kvp.Key, out _);
        }
    }

    return Results.Json(new
    {
        ok = true,
        user = userToUnlock,
        deniedUsers = ruleStore.UserOverrides.Denied,
        message = $"Đã MỞ KHOÁ tài khoản '{userToUnlock}' thành công!",
        version = ruleStore.Version
    }, jsonOptions);
});

// 17. API Danh sách tài khoản đang bị khoá
app.MapGet("/api/v1/admin/users/locked", () =>
{
    var denied = ruleStore.UserOverrides?.Denied ?? new List<string>();
    return Results.Json(new { ok = true, deniedUsers = denied }, jsonOptions);
});

// 18. API Admin Đóng/Mở Cổng VAGS (Maintenance Mode Controller)
app.MapPost("/api/v1/admin/maintenance", async (HttpContext ctx) =>
{
    var enabledStr = ctx.Request.Query["enabled"].FirstOrDefault();
    bool enable = string.Equals(enabledStr, "true", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(enabledStr, "1", StringComparison.OrdinalIgnoreCase);
    ruleStore.MaintenanceMode = enable;
    var flagPath = Path.Combine(dataDir, "maintenance.flag");
    if (enable) File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
    else if (File.Exists(flagPath)) File.Delete(flagPath);
    SaveRules(ruleStore);

    var rulesPayload = $"event: RULES_UPDATED\ndata: {JsonSerializer.Serialize(ruleStore, sseJsonOptions)}\n\n";
    foreach (var kvp in sseClients.ToArray())
    {
        try { await kvp.Value.Response.WriteAsync(rulesPayload); await kvp.Value.Response.Body.FlushAsync(); } catch { sseClients.TryRemove(kvp.Key, out _); }
    }
    return Results.Json(new { ok = true, maintenanceMode = enable, version = ruleStore.Version, message = enable ? "Cổng VAGS đã ĐÓNG để bảo trì." : "Cổng VAGS đã MỞ hoạt động bình thường." }, jsonOptions);
});

// 19. API Admin Cấu hình Tên miền động (Domain Switcher)
app.MapPost("/api/v1/admin/domains", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    if (doc.RootElement.TryGetProperty("primaryDomain", out var pd))
    {
        var newPrimary = pd.GetString();
        if (!string.IsNullOrWhiteSpace(newPrimary)) ruleStore.ProxyHost = newPrimary.Trim();
    }
    if (doc.RootElement.TryGetProperty("domainAliases", out var da) && da.ValueKind == JsonValueKind.Array)
    {
        ruleStore.DomainAliases = da.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    }
    SaveRules(ruleStore);

    var rulesPayload = $"event: RULES_UPDATED\ndata: {JsonSerializer.Serialize(ruleStore, sseJsonOptions)}\n\n";
    foreach (var kvp in sseClients.ToArray())
    {
        try { await kvp.Value.Response.WriteAsync(rulesPayload); await kvp.Value.Response.Body.FlushAsync(); } catch { sseClients.TryRemove(kvp.Key, out _); }
    }
    return Results.Json(new { ok = true, primaryDomain = ruleStore.ProxyHost, domainAliases = ruleStore.DomainAliases, version = ruleStore.Version }, jsonOptions);
});

app.Run();

public class RuleStoreModel
{
    public string Version { get; set; } = "1.0";
    public string ProxyMode { get; set; } = "FULL_PROXY";
    public bool MaintenanceMode { get; set; } = false;
    public List<string> Blacklist { get; set; } = new();
    public List<string> InternalWhitelist { get; set; } = new();
    public List<string> GlobalAcademicWhitelist { get; set; } = new();
    public string AuthMode { get; set; } = "demo";
    public string KeycloakAuthority { get; set; } = "https://idp.vnu.edu.vn/auth/realms/vnu";
    public string ProxyHost { get; set; } = "vags.vnu.edu.vn";
    public int ProxyPort { get; set; } = 443;
    public string ProxyScheme { get; set; } = "HTTPS";
    public List<string> DomainAliases { get; set; } = new();
    public List<string> AllowedOUs { get; set; } = new() { "canbo", "pqtkt" };
    public List<string> DisallowedOUs { get; set; } = new() { "sinhvien" };
    public List<string> AllowedMemberTypes { get; set; } = new() { "canbo" };
    public List<string> DisallowedMemberTypes { get; set; } = new() { "sinhvien" };
    public string AdminOU { get; set; } = "pqtkt";
    public UserOverridesModel UserOverrides { get; set; } = new();
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("O");
}

public class UserOverridesModel
{
    public List<string> Allowed { get; set; } = new() { "phongnd" };
    public List<string> Denied { get; set; } = new();
    public Dictionary<string, UserRuleConfig> UserSpecificRules { get; set; } = new();
}

public class UserRuleConfig
{
    public List<string> ProxyDomains { get; set; } = new();
    public List<string> DirectDomains { get; set; } = new();
}

public class SseClientInfo
{
    public Guid Id { get; set; }
    public HttpResponse Response { get; set; } = default!;
    public string ConnectedAt { get; set; } = "";
    public string RemoteIp { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string User { get; set; } = "Cán bộ VNU";
    public string Unit { get; set; } = "VNU";
}

public class ProxyLogEntry
{
    public string Timestamp { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Allowed { get; set; }
    public string Reason { get; set; } = "";
    public string MatchedRule { get; set; } = "";
    public string User { get; set; } = "Cán bộ VNU";
    public string Unit { get; set; } = "VNU";
}

public record EvaluateRequest(string? Host, string? User = null);
public record DemoTokenRequest(string? Username, string? OrganizationalUnit);
public record ExchangeCodeRequest(string? Code, string? RedirectUri, string? CodeVerifier);
public record HeartbeatRequest(string? User, string? Unit, string? MemberType, bool? ProxyEnabled = true);
public record UserStatusRequest(string? User, bool? ProxyEnabled);
public record KeyRegenerateRequest(string? User);
public record BroadcastUpdateRequest(string? Version);
public record ForceUserActionRequest(string? User, string? Ip, bool? All, string? Reason);
public record LockUserRequest(string? User, string? Reason);
public record RouteDecision(
    string Host,
    string Action,
    string ProxyDirective,
    string MatchedRule,
    string MatchedCategory,
    bool AllowedForTunnel,
    string Reason
);

