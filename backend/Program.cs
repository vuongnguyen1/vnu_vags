using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using VagsBackend;

var startTime = DateTime.UtcNow;
var dataDir = Environment.GetEnvironmentVariable("VAGS_DATA_DIR") ?? "/app/data";
var rulesFilePath = Path.Combine(dataDir, "rules.json");
var certChainPath = Environment.GetEnvironmentVariable("VAGS_CERT_FILE") ?? "/app/certs/fullchain.pem";
var certKeyPath = Environment.GetEnvironmentVariable("VAGS_KEY_FILE") ?? "/app/certs/privkey.pem";
var webRootPath = Environment.GetEnvironmentVariable("VAGS_WEBROOT") ?? "/app/wwwroot";
var keycloakAuthority = (Environment.GetEnvironmentVariable("VAGS_KEYCLOAK_AUTHORITY") ?? "https://idp.vnu.edu.vn/auth/realms/vnu").TrimEnd('/');
var keycloakClientId = Environment.GetEnvironmentVariable("VAGS_KEYCLOAK_CLIENT_ID") ?? "vags-gateway";
var keycloakClientSecret = Environment.GetEnvironmentVariable("VAGS_KEYCLOAK_CLIENT_SECRET") ?? "";

var sessionSecretSource = Environment.GetEnvironmentVariable("VAGS_SESSION_SECRET");
if (string.IsNullOrWhiteSpace(sessionSecretSource)) sessionSecretSource = keycloakClientSecret;
byte[] sessionSigningKey = !string.IsNullOrWhiteSpace(sessionSecretSource)
    ? SHA256.HashData(Encoding.UTF8.GetBytes("vags-session-key:" + sessionSecretSource.Trim()))
    : RandomNumberGenerator.GetBytes(32);

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
            if (loaded != null)
            {
                loaded.MaxConcurrentSessionsPerUser = loaded.MaxConcurrentSessionsPerUser <= 0 ? 5 : Math.Clamp(loaded.MaxConcurrentSessionsPerUser, 1, 100);
                loaded.MaxConcurrentSessionsAdmin = loaded.MaxConcurrentSessionsAdmin <= 0 ? 10 : Math.Clamp(loaded.MaxConcurrentSessionsAdmin, 1, 100);
                return loaded;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Could not read {rulesFilePath}: {ex.Message}");
    }

    var initial = new RuleStoreModel
    {
        Version = "2026.11.07.01",
        MaxConcurrentSessionsPerUser = 5,
        MaxConcurrentSessionsAdmin = 10,
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

string ExtractHostOnly(string? rawHost)
{
    if (string.IsNullOrWhiteSpace(rawHost)) return "";
    var s = rawHost.Trim();
    if (s.StartsWith('['))
    {
        var closeBracket = s.IndexOf(']');
        if (closeBracket > 1) return s.Substring(1, closeBracket - 1).Trim().ToLowerInvariant();
    }
    var firstColon = s.IndexOf(':');
    if (firstColon > 0 && firstColon == s.LastIndexOf(':'))
    {
        s = s.Substring(0, firstColon);
    }
    return s.Trim().TrimEnd('.').ToLowerInvariant();
}

bool IsVagsSelfHost(string h, RuleStoreModel rStore)
{
    if (string.IsNullOrWhiteSpace(h)) return false;
    var cleanHost = ExtractHostOnly(h);
    if (string.IsNullOrEmpty(cleanHost)) return false;
    if (cleanHost == serverIp.ToLowerInvariant() || cleanHost == "112.137.142.30" || cleanHost == "112.137.133.14" || cleanHost == "127.0.0.1" || cleanHost == "::1" || cleanHost == "localhost")
        return true;
    var cleanPrimary = primaryDomain.Trim().TrimEnd('.').ToLowerInvariant();
    if (!string.IsNullOrEmpty(cleanPrimary) && (cleanHost == cleanPrimary || cleanHost.EndsWith("." + cleanPrimary)))
        return true;
    var hostFromRules = (rStore?.ProxyHost ?? "").Trim().TrimEnd('.').ToLowerInvariant();
    if (!string.IsNullOrEmpty(hostFromRules) && (cleanHost == hostFromRules || cleanHost.EndsWith("." + hostFromRules)))
        return true;
    foreach (var alias in domainAliases)
    {
        var cleanAlias = alias.Trim().TrimEnd('.').ToLowerInvariant();
        if (!string.IsNullOrEmpty(cleanAlias) && (cleanHost == cleanAlias || cleanHost.EndsWith("." + cleanAlias)))
            return true;
    }
    if (rStore?.DomainAliases != null)
    {
        foreach (var alias in rStore.DomainAliases)
        {
            var cleanAlias = (alias ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(cleanAlias) && (cleanHost == cleanAlias || cleanHost.EndsWith("." + cleanAlias)))
                return true;
        }
    }
    return false;
}

var monitorDb = new MonitorDbService(
    Path.Combine(dataDir, "vags_monitor.db"),
    (uname, unit) =>
    {
        var isAdminRole = !string.IsNullOrEmpty(ResolveAdminRole(uname, null, unit));
        lock (ruleLock)
        {
            return isAdminRole
                ? Math.Clamp(ruleStore.MaxConcurrentSessionsAdmin > 0 ? ruleStore.MaxConcurrentSessionsAdmin : 10, 1, 100)
                : Math.Clamp(ruleStore.MaxConcurrentSessionsPerUser > 0 ? ruleStore.MaxConcurrentSessionsPerUser : 5, 1, 100);
        }
    });
var sseClients = new ConcurrentDictionary<Guid, SseClientInfo>();
var pendingForceProxyOff = new ConcurrentDictionary<string, PendingForceOffEntry>(StringComparer.OrdinalIgnoreCase);
var proxyLogs = new ConcurrentQueue<ProxyLogEntry>();
var secLogLock = new object();
var activeUserTunnels = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var verifiedSessionTokens = new ConcurrentDictionary<string, VerifiedSessionEntry>(StringComparer.Ordinal);
var userSessionRevokedAfter = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
var revokedTokenJtis = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
var jwksLock = new SemaphoreSlim(1, 1);
List<(string? Kid, RSAParameters Params)> jwksKeysCache = new();
DateTimeOffset jwksFetchedAt = DateTimeOffset.MinValue;
DateTimeOffset jwksLastAttemptAt = DateTimeOffset.MinValue;
IHttpClientFactory? jwksHttpClientFactory = null;
bool isDevelopmentEnv = false;

long totalProxyRequests = 0;
long allowedProxyRequests = 0;
long blockedProxyRequests = 0;

bool IsTrustedProxyAddress(IPAddress? addr)
{
    if (addr == null) return false;
    if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
    if (IPAddress.IsLoopback(addr)) return true;
    if (addr.AddressFamily == AddressFamily.InterNetwork)
    {
        var b = addr.GetAddressBytes();
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
    }
    return false;
}

string CleanIp(string? ip)
{
    if (string.IsNullOrWhiteSpace(ip)) return "unknown";
    var s = ip.Trim();

    if (IPAddress.TryParse(s, out var directAddr))
    {
        return directAddr.IsIPv4MappedToIPv6 ? directAddr.MapToIPv4().ToString() : directAddr.ToString();
    }

    if (IPEndPoint.TryParse(s, out var ep))
    {
        var addr = ep.Address;
        return addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
    }

    if (s.StartsWith('['))
    {
        var closeBracket = s.IndexOf(']');
        if (closeBracket > 1)
        {
            var inner = s.Substring(1, closeBracket - 1);
            if (IPAddress.TryParse(inner, out var bracketAddr))
            {
                return bracketAddr.IsIPv4MappedToIPv6 ? bracketAddr.MapToIPv4().ToString() : bracketAddr.ToString();
            }
        }
    }

    var firstColon = s.IndexOf(':');
    if (firstColon > 0 && firstColon == s.LastIndexOf(':'))
    {
        var hostPart = s.Substring(0, firstColon);
        if (IPAddress.TryParse(hostPart, out var v4Addr))
        {
            return v4Addr.IsIPv4MappedToIPv6 ? v4Addr.MapToIPv4().ToString() : v4Addr.ToString();
        }
    }

    return "unknown";
}

string GetRealClientIp(HttpContext http)
{
    if (http.Items.TryGetValue("RealClientIp", out var rip) && rip is string srip && !string.IsNullOrWhiteSpace(srip))
    {
        var cleaned = CleanIp(srip);
        if (cleaned != "unknown") return cleaned;
    }

    var remoteAddr = http.Connection.RemoteIpAddress;
    if (IsTrustedProxyAddress(remoteAddr))
    {
        var xReal = http.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xReal))
        {
            var cleaned = CleanIp(xReal);
            if (cleaned != "unknown") return cleaned;
        }
        var xFwd = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xFwd))
        {
            var parts = xFwd.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                var cleaned = CleanIp(parts[i]);
                if (cleaned != "unknown" && !(IPAddress.TryParse(cleaned, out var pAddr) && IsTrustedProxyAddress(pAddr)))
                {
                    return cleaned;
                }
            }
            if (parts.Length > 0)
            {
                var cleanedFirst = CleanIp(parts[0]);
                if (cleanedFirst != "unknown") return cleanedFirst;
            }
        }
    }

    var rem = remoteAddr?.ToString() ?? "unknown";
    return CleanIp(rem);
}

string SanitizeLogField(string? val)
{
    if (string.IsNullOrEmpty(val)) return "";
    return val.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
}

void AddProxyLog(string clientIp, string targetHost, int targetPort, bool allowed, string reason, string matchedRule, string user = "Khách (Chưa xác thực)", string unit = "VNU", string memberType = "canbo", long bytesSent = 0, long bytesReceived = 0, long durationMs = 0)
{
    Interlocked.Increment(ref totalProxyRequests);
    if (allowed) Interlocked.Increment(ref allowedProxyRequests);
    else Interlocked.Increment(ref blockedProxyRequests);

    var cleanUser = string.IsNullOrWhiteSpace(user) ? "Khách (Chưa xác thực)" : SanitizeLogField(user);
    var cleanUnit = string.IsNullOrWhiteSpace(unit) ? "VNU" : SanitizeLogField(unit);
    var cleanMtype = string.IsNullOrWhiteSpace(memberType) ? "canbo" : SanitizeLogField(memberType);
    var safeIp = SanitizeLogField(clientIp);
    var safeHost = SanitizeLogField(targetHost);
    var safeReason = SanitizeLogField(reason);
    var safeRule = SanitizeLogField(matchedRule);

    proxyLogs.Enqueue(new ProxyLogEntry
    {
        Timestamp = DateTime.UtcNow.ToString("O"),
        ClientIp = safeIp,
        Target = $"{safeHost}:{targetPort}",
        Allowed = allowed,
        Reason = safeReason,
        MatchedRule = safeRule,
        User = cleanUser,
        Unit = cleanUnit,
        MemberType = cleanMtype
    });

    while (proxyLogs.Count > 100 && proxyLogs.TryDequeue(out _)) { }

    monitorDb.EnqueueLog(new ConnectionLogItem
    {
        Timestamp = DateTime.UtcNow,
        ClientIp = safeIp,
        Username = cleanUser,
        Unit = cleanUnit,
        MemberType = cleanMtype,
        TargetHost = safeHost,
        TargetPort = targetPort,
        Allowed = allowed,
        Reason = safeReason,
        MatchedRule = safeRule,
        BytesSent = bytesSent,
        BytesReceived = bytesReceived,
        DurationMs = durationMs
    });

    if (!allowed)
    {
        try
        {
            var secLine = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} vags-gateway[security]: BLOCKED client_ip={safeIp} user=\"{cleanUser}\" unit=\"{cleanUnit}\" target=\"{safeHost}:{targetPort}\" rule=\"{safeRule}\" reason=\"{safeReason}\"\n";
            lock (secLogLock)
            {
                File.AppendAllText(Path.Combine(dataDir, "security.log"), secLine);
            }
        }
        catch { }
    }
}

void WriteSecurityLog(string eventType, string message)
{
    try
    {
        var safeType = SanitizeLogField(eventType);
        var safeMsg = SanitizeLogField(message);
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} vags-gateway[security]: {safeType} {safeMsg}\n";
        lock (secLogLock)
        {
            File.AppendAllText(Path.Combine(dataDir, "security.log"), line);
        }
    }
    catch { }
}

async Task<bool> WriteSseEventAsync(SseClientInfo client, string payload, CancellationToken ct = default)
{
    try
    {
        await client.WriteLock.WaitAsync(ct);
        try
        {
            await client.Response.WriteAsync(payload, ct);
            await client.Response.Body.FlushAsync(ct);
            return true;
        }
        finally
        {
            client.WriteLock.Release();
        }
    }
    catch
    {
        sseClients.TryRemove(client.Id, out _);
        return false;
    }
}

string PendingOffKey(string? username, string? ip)
{
    var u = NormalizeUsername(username);
    var cleanIp = MonitorDbService.NormalizeClientIp(ip);
    if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(cleanIp)) return "";
    return $"{u}|{cleanIp}";
}

void SetPendingForceOff(string? username, string? ip, string reason)
{
    var key = PendingOffKey(username, ip);
    if (string.IsNullOrEmpty(key)) return;
    var now = DateTimeOffset.UtcNow;
    if (pendingForceProxyOff.Count > 5000)
    {
        foreach (var kvp in pendingForceProxyOff.ToArray())
        {
            if (now - kvp.Value.CreatedAtUtc >= TimeSpan.FromMinutes(3))
            {
                pendingForceProxyOff.TryRemove(kvp.Key, out _);
            }
        }
    }
    var normIp = MonitorDbService.NormalizeClientIp(ip);
    pendingForceProxyOff[key] = new PendingForceOffEntry((username ?? "").Trim(), normIp, reason, now);
}

bool TryGetPendingForceOff(string? username, string? ip, out PendingForceOffEntry? entry)
{
    entry = null;
    var key = PendingOffKey(username, ip);
    if (string.IsNullOrEmpty(key)) return false;
    if (pendingForceProxyOff.TryGetValue(key, out var found))
    {
        if (DateTimeOffset.UtcNow - found.CreatedAtUtc < TimeSpan.FromMinutes(3))
        {
            entry = found;
            return true;
        }
        pendingForceProxyOff.TryRemove(key, out _);
    }
    return false;
}

void ClearPendingForceOff(string? username, string? ip)
{
    var key = PendingOffKey(username, ip);
    if (string.IsNullOrEmpty(key)) return;
    pendingForceProxyOff.TryRemove(key, out _);
}

bool HasOtherActiveSseClient(string? username, string? ip, Guid? excludeId = null)
{
    var normIp = MonitorDbService.NormalizeClientIp(ip);
    if (string.IsNullOrEmpty(normIp) || string.IsNullOrWhiteSpace(username)) return false;
    foreach (var c in sseClients.Values)
    {
        if (excludeId.HasValue && c.Id == excludeId.Value) continue;
        if (!c.ProxyEnabled) continue;
        if (MonitorDbService.NormalizeClientIp(c.RemoteIp) == normIp && IsSameUser(c.User, username))
        {
            return true;
        }
    }
    return false;
}

async Task<int> SendTargetedForceProxyOffAsync(string username, IReadOnlyList<string> targetIps, string reason, bool recordTombstone = true)
{
    int totalDelivered = 0;
    var effectiveReason = string.IsNullOrWhiteSpace(reason)
        ? "Hệ thống đã tự động tắt Proxy trên thiết bị kết nối sớm nhất."
        : reason.Trim();

    foreach (var rawTargetIp in targetIps)
    {
        var normIp = MonitorDbService.NormalizeClientIp(rawTargetIp);
        if (string.IsNullOrEmpty(normIp)) continue;

        if (recordTombstone && !string.IsNullOrWhiteSpace(username))
        {
            SetPendingForceOff(username, normIp, effectiveReason);
        }

        var eventData = new
        {
            all = false,
            user = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            ip = normIp,
            reason = effectiveReason,
            timestamp = DateTime.UtcNow
        };
        var payload = $"event: FORCE_PROXY_OFF\ndata: {JsonSerializer.Serialize(eventData, sseJsonOptions)}\n\n";

        int deliveredForIp = 0;
        foreach (var kvp in sseClients.ToArray())
        {
            var client = kvp.Value;
            if (MonitorDbService.NormalizeClientIp(client.RemoteIp) != normIp) continue;

            var userMatches = string.IsNullOrWhiteSpace(username) ||
                              IsSameUser(client.User, username) ||
                              string.Equals(client.User, "Khách (Chưa xác thực)", StringComparison.OrdinalIgnoreCase) ||
                              string.IsNullOrWhiteSpace(client.User);
            if (!userMatches) continue;

            client.ProxyEnabled = false;
            if (await WriteSseEventAsync(client, payload))
            {
                deliveredForIp++;
                totalDelivered++;
            }
        }

        WriteSecurityLog("SESSION_EVICT_FORCE_PROXY_OFF",
            $"user=\"{username}\" evicted_ip={normIp} sse_delivered={deliveredForIp} reason=\"{effectiveReason}\"");
        if (recordTombstone)
        {
            AddProxyLog(normIp, "vags-session-limit", 443, false, effectiveReason, "session_limit_auto_off",
                string.IsNullOrWhiteSpace(username) ? "Khách (Chưa xác thực)" : username.Trim(), "VNU", "canbo");
        }
    }

    return totalDelivered;
}

monitorDb.OnSessionsEvicted = (evUser, evIps, evReason) =>
{
    _ = Task.Run(() => SendTargetedForceProxyOffAsync(evUser, evIps, evReason, recordTombstone: true));
};

// Wildcard matching equivalent to PAC shExpMatch
bool ShExpMatch(string host, string pattern)
{
    if (string.IsNullOrWhiteSpace(pattern)) return false;
    var p = pattern.Trim().TrimEnd('.').ToLowerInvariant();
    var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
    if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(h)) return false;
    if (p == h) return true;
    if (p.StartsWith("*.") && h == p.Substring(2)) return true;

    var regexPattern = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    return Regex.IsMatch(h, regexPattern, RegexOptions.IgnoreCase);
}

bool TryParseConnectAuthority(string? rawAuthority, out string host, out int port)
{
    host = string.Empty;
    port = 0;
    if (string.IsNullOrWhiteSpace(rawAuthority)) return false;

    foreach (var ch in rawAuthority)
    {
        if (char.IsWhiteSpace(ch) || ch == '@' || ch == '/' || ch == '\\' || ch == '?' || ch == '#' || ch < 0x20 || ch == 0x7F)
            return false;
    }

    var s = rawAuthority.Trim();
    if (s.StartsWith('['))
    {
        var closeBracket = s.IndexOf(']');
        if (closeBracket <= 1) return false;
        var inner = s.Substring(1, closeBracket - 1);
        if (!IPAddress.TryParse(inner, out var ip6) || ip6.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var remainder = s.Substring(closeBracket + 1);
        if (!remainder.StartsWith(':')) return false;
        var portPart = remainder.Substring(1);
        if (portPart.Length == 0 || !portPart.All(char.IsAsciiDigit)) return false;
        if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            return false;

        host = ip6.ToString();
        return true;
    }

    var colonIdx = s.IndexOf(':');
    if (colonIdx <= 0 || colonIdx != s.LastIndexOf(':')) return false;

    var rawHostPart = s.Substring(0, colonIdx);
    var portStr = s.Substring(colonIdx + 1);
    if (portStr.Length == 0 || !portStr.All(char.IsAsciiDigit)) return false;
    if (!int.TryParse(portStr, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
        return false;

    if (rawHostPart.EndsWith("..", StringComparison.Ordinal)) return false;
    var normalizedHost = rawHostPart.Trim().TrimEnd('.').ToLowerInvariant();
    if (normalizedHost.Length == 0 || normalizedHost.Length > 253) return false;
    if (normalizedHost.StartsWith('.') || normalizedHost.Contains("..")) return false;
    foreach (var c in normalizedHost)
    {
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_'))
            return false;
    }
    if (!char.IsLetterOrDigit(normalizedHost[0]) || !char.IsLetterOrDigit(normalizedHost[^1]))
        return false;

    host = normalizedHost;
    return true;
}

bool IsForbiddenTargetIp(IPAddress ip)
{
    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

    if (IPAddress.TryParse(serverIp, out var srvIp))
    {
        var normSrv = srvIp.IsIPv4MappedToIPv6 ? srvIp.MapToIPv4() : srvIp;
        if (ip.Equals(normSrv)) return true;
    }
    if (ip.Equals(IPAddress.Parse("112.137.142.30")) || ip.Equals(IPAddress.Parse("112.137.133.14")))
        return true;

    if (IPAddress.IsLoopback(ip)) return true;

    if (ip.AddressFamily == AddressFamily.InterNetwork)
    {
        var b = ip.GetAddressBytes();
        if (b[0] == 0) return true; // 0.0.0.0/8 unspecified
        if (b[0] == 127) return true; // 127.0.0.0/8 loopback
        if (b[0] == 10) return true; // 10.0.0.0/8 RFC1918
        if (b[0] == 100 && (b[1] & 0xC0) == 64) return true; // 100.64.0.0/10 CGNAT
        if (b[0] == 169 && b[1] == 254) return true; // 169.254.0.0/16 link-local / metadata
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12 RFC1918
        if (b[0] == 192 && b[1] == 168) return true; // 192.168.0.0/16 RFC1918
        if (b[0] >= 224) return true; // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
        return false;
    }

    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
    {
        if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return true;
        var b = ip.GetAddressBytes();
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true; // fe80::/10 link-local
        if ((b[0] & 0xfe) == 0xfc) return true; // fc00::/7 ULA
        if (b[0] == 0xff) return true; // ff00::/8 multicast
        return false;
    }

    return true;
}

RouteDecision EvaluateHost(string rawHost, RuleStoreModel currentRules, string? user = null)
{
    var host = ExtractHostOnly(rawHost);
    var proxyDirective = $"{currentRules.ProxyScheme} {currentRules.ProxyHost}:{currentRules.ProxyPort}";

    // -1. Máy chủ VAGS & IP Cổng: Kết nối trực tiếp (DIRECT), không bao giờ tunnel qua chính nó
    if (IsVagsSelfHost(host, currentRules))
    {
        return new RouteDecision(host, "DIRECT", "DIRECT", primaryDomain, "vagsServerBypass", false, "Máy chủ Cổng VAGS kết nối trực tiếp (DIRECT)");
    }

    // 0. High-Bandwidth Blacklist (YouTube, Facebook, TikTok, etc.) -> ALWAYS DIRECT
    foreach (var pattern in currentRules.Blacklist ?? new List<string>())
    {
        if (ShExpMatch(host, pattern))
        {
            return new RouteDecision(host, "DIRECT", "DIRECT", pattern, "blacklist", false, "Matched Blacklist (High-Bandwidth / Non-Academic)");
        }
    }

    // 1. User Specific Overrides (chỉ áp dụng cho đúng tài khoản đã xác thực, không leak sang user khác)
    if (!string.IsNullOrWhiteSpace(user) && currentRules.UserOverrides?.UserSpecificRules != null)
    {
        var u = user.Trim().ToLowerInvariant();
        UserRuleConfig? uConfig = null;
        foreach (var kvp in currentRules.UserOverrides.UserSpecificRules)
        {
            if (string.Equals(kvp.Key?.Trim(), u, StringComparison.OrdinalIgnoreCase))
            {
                uConfig = kvp.Value;
                break;
            }
        }
        if (uConfig != null)
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

    // 2. Internal VNU Library Whitelist (lic.vnu.edu.vn, iplocation...) -> PROXY nếu cờ EnableLicProxy = true
    if (currentRules.EnableLicProxy)
    {
        foreach (var pattern in currentRules.InternalWhitelist ?? new List<string>())
        {
            if (ShExpMatch(host, pattern))
            {
                return new RouteDecision(host, "PROXY", proxyDirective, pattern, "internalWhitelist", true, "Matched VNU Internal Library / Tool Whitelist");
            }
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

string Base64UrlEncode(byte[] data) =>
    Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

bool TryBase64UrlDecode(string? input, out byte[] bytes)
{
    bytes = Array.Empty<byte>();
    if (string.IsNullOrWhiteSpace(input)) return false;
    try
    {
        var s = input.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return false;
        }
        bytes = Convert.FromBase64String(s);
        return bytes.Length > 0;
    }
    catch
    {
        return false;
    }
}

bool IsUserDenied(string? username)
{
    if (string.IsNullOrWhiteSpace(username)) return false;
    var u = username.Trim();
    lock (ruleLock)
    {
        return ruleStore.UserOverrides?.Denied?.Contains(u, StringComparer.OrdinalIgnoreCase) == true;
    }
}

bool IsTokenRevokedForUser(string? username, DateTimeOffset issuedAtUtc)
{
    if (string.IsNullOrWhiteSpace(username)) return false;
    if (userSessionRevokedAfter.TryGetValue(username.Trim(), out var revokedAfter) && issuedAtUtc <= revokedAfter)
        return true;
    var clean = NormalizeUsername(username);
    if (!string.IsNullOrEmpty(clean) && userSessionRevokedAfter.TryGetValue(clean, out var revokedClean) && issuedAtUtc <= revokedClean)
        return true;
    return false;
}

void RevokeUserTokens(string? username)
{
    if (string.IsNullOrWhiteSpace(username)) return;
    var u = username.Trim();
    var clean = NormalizeUsername(u);
    var now = DateTimeOffset.UtcNow;
    userSessionRevokedAfter[u] = now;
    if (!string.IsNullOrEmpty(clean)) userSessionRevokedAfter[clean] = now;

    foreach (var kvp in verifiedSessionTokens.ToArray())
    {
        var entryUser = kvp.Value.Username;
        if (string.Equals(entryUser, u, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(clean) && string.Equals(NormalizeUsername(entryUser), clean, StringComparison.OrdinalIgnoreCase)))
        {
            verifiedSessionTokens.TryRemove(kvp.Key, out _);
        }
    }
}

string CreateSignedSessionToken(string username, string name, string email, string unit, string memberType, bool isAdmin, string adminRole)
{
    var now = DateTimeOffset.UtcNow;
    var iat = now.ToUnixTimeSeconds();
    var exp = now.AddDays(7).ToUnixTimeSeconds();
    var jti = Guid.NewGuid().ToString("N");
    var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
    var payloadObj = new
    {
        jti = jti,
        sub = username,
        preferred_username = username,
        name = name,
        email = email,
        iss = "https://vags.vnu.edu.vn/vags-sess",
        aud = "vags-gateway",
        token_use = "vags-sess",
        organizational_unit = unit,
        vnu_admin_unit = unit,
        vnu_member_type = string.IsNullOrWhiteSpace(memberType) ? "canbo" : memberType,
        member_type = string.IsNullOrWhiteSpace(memberType) ? "canbo" : memberType,
        role = isAdmin ? "vags-admin" : "vnu-academic-faculty",
        isAdmin = isAdmin,
        adminRole = adminRole,
        iat = iat,
        exp = exp
    };
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj)));
    var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
    var sig = Base64UrlEncode(HMACSHA256.HashData(sessionSigningKey, signingInput));
    return $"{header}.{payload}.{sig}";
}

async Task<bool> VerifyKeycloakRs256SignatureAsync(string headerB64, string payloadB64, byte[] sigBytes, string? kid)
{
    async Task<List<(string? Kid, RSAParameters Params)>> GetKeysAsync(bool forceRefresh)
    {
        var now = DateTimeOffset.UtcNow;
        var current = jwksKeysCache;
        if (!forceRefresh && current.Count > 0 && now - jwksFetchedAt < TimeSpan.FromHours(6))
        {
            return current;
        }
        if (forceRefresh && now - jwksLastAttemptAt < TimeSpan.FromSeconds(60))
        {
            return current;
        }

        await jwksLock.WaitAsync();
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh && jwksKeysCache.Count > 0 && now - jwksFetchedAt < TimeSpan.FromHours(6))
                return jwksKeysCache;
            if (forceRefresh && now - jwksLastAttemptAt < TimeSpan.FromSeconds(60))
                return jwksKeysCache;

            jwksLastAttemptAt = now;
            var client = jwksHttpClientFactory != null ? jwksHttpClientFactory.CreateClient() : new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var certsUrl = $"{keycloakAuthority}/protocol/openid-connect/certs";
            using var resp = await client.GetAsync(certsUrl);
            if (!resp.IsSuccessStatusCode) return jwksKeysCache;

            var json = await resp.Content.ReadAsStringAsync();
            using var jwksDoc = JsonDocument.Parse(json);
            if (!jwksDoc.RootElement.TryGetProperty("keys", out var keysArr) || keysArr.ValueKind != JsonValueKind.Array)
                return jwksKeysCache;

            var loaded = new List<(string? Kid, RSAParameters Params)>();
            foreach (var k in keysArr.EnumerateArray())
            {
                var kty = k.TryGetProperty("kty", out var ktyEl) ? ktyEl.GetString() : null;
                var use = k.TryGetProperty("use", out var useEl) ? useEl.GetString() : null;
                var alg = k.TryGetProperty("alg", out var algEl) ? algEl.GetString() : null;
                if (!string.Equals(kty, "RSA", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(use) && !string.Equals(use, "sig", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(alg) && !string.Equals(alg, "RS256", StringComparison.OrdinalIgnoreCase)) continue;

                var keyId = k.TryGetProperty("kid", out var kidEl) ? kidEl.GetString() : null;
                var nStr = k.TryGetProperty("n", out var nEl) ? nEl.GetString() : null;
                var eStr = k.TryGetProperty("e", out var eEl) ? eEl.GetString() : null;
                if (TryBase64UrlDecode(nStr, out var nBytes) && TryBase64UrlDecode(eStr, out var eBytes))
                {
                    loaded.Add((keyId, new RSAParameters { Modulus = nBytes, Exponent = eBytes }));
                }
            }

            if (loaded.Count > 0)
            {
                jwksKeysCache = loaded;
                jwksFetchedAt = now;
            }
            return jwksKeysCache;
        }
        catch
        {
            return jwksKeysCache;
        }
        finally
        {
            jwksLock.Release();
        }
    }

    var signedBytes = Encoding.ASCII.GetBytes($"{headerB64}.{payloadB64}");
    var keys = await GetKeysAsync(false);
    if (!string.IsNullOrEmpty(kid) && !keys.Any(k => string.Equals(k.Kid, kid, StringComparison.Ordinal)))
    {
        keys = await GetKeysAsync(true);
    }

    var candidates = !string.IsNullOrEmpty(kid)
        ? keys.Where(k => string.Equals(k.Kid, kid, StringComparison.Ordinal)).ToList()
        : keys;

    foreach (var candidate in candidates)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(candidate.Params);
            if (rsa.VerifyData(signedBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return true;
        }
        catch { }
    }
    return false;
}

async Task<VerifiedTokenIdentity?> VerifyJwtTokenCoreAsync(string? rawToken, bool expectExpired)
{
    if (string.IsNullOrWhiteSpace(rawToken)) return null;
    var token = rawToken.Trim();
    if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        token = token.Substring(7).Trim();
    if (string.IsNullOrWhiteSpace(token)) return null;

    // (b) Check verified session token cache (populated by /auth/exchange)
    if (!expectExpired && verifiedSessionTokens.TryGetValue(token, out var cached))
    {
        if (DateTimeOffset.UtcNow <= cached.ExpiresAtUtc &&
            !IsTokenRevokedForUser(cached.Username, cached.IssuedAtUtc) &&
            !IsUserDenied(cached.Username))
        {
            return new VerifiedTokenIdentity(cached.Username, cached.Email, cached.Unit, cached.MemberType, cached.Jti);
        }
        verifiedSessionTokens.TryRemove(token, out _);
    }

    var parts = token.Split('.');
    if (parts.Length != 3) return null;
    if (!TryBase64UrlDecode(parts[0], out var headerBytes) ||
        !TryBase64UrlDecode(parts[1], out var payloadBytes) ||
        !TryBase64UrlDecode(parts[2], out var sigBytes))
    {
        return null;
    }

    try
    {
        using var headerDoc = JsonDocument.Parse(headerBytes);
        var hRoot = headerDoc.RootElement;
        var alg = hRoot.TryGetProperty("alg", out var algEl) ? (algEl.GetString() ?? "") : "";
        var kid = hRoot.TryGetProperty("kid", out var kidEl) ? kidEl.GetString() : null;

        using var payloadDoc = JsonDocument.Parse(payloadBytes);
        var pRoot = payloadDoc.RootElement;

        if (!pRoot.TryGetProperty("exp", out var expEl) || !expEl.TryGetInt64(out var expUnix))
            return null;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expectExpired)
        {
            if (nowUnix <= expUnix) return null;
            if (pRoot.TryGetProperty("nbf", out var nbfEl) && nElTryInt64(nbfEl, out var nbfUnix) && expUnix < nbfUnix)
                return null;
        }
        else
        {
            if (nowUnix > expUnix) return null;
            if (pRoot.TryGetProperty("nbf", out var nbfEl) && nElTryInt64(nbfEl, out var nbfUnix) && nowUnix + 30 < nbfUnix)
                return null;
        }

        var iss = pRoot.TryGetProperty("iss", out var issEl) ? (issEl.GetString() ?? "").TrimEnd('/') : "";
        var jti = pRoot.TryGetProperty("jti", out var jtiEl) ? jtiEl.GetString() : null;
        if (!string.IsNullOrEmpty(jti) && revokedTokenJtis.ContainsKey(jti))
            return null;

        if (string.Equals(alg, "HS256", StringComparison.Ordinal))
        {
            var tokenUse = pRoot.TryGetProperty("token_use", out var tuEl) ? tuEl.GetString() : "";
            if (!string.Equals(iss, "https://vags.vnu.edu.vn/vags-sess", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tokenUse, "vags-sess", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var expectedSig = HMACSHA256.HashData(sessionSigningKey, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            if (sigBytes.Length != expectedSig.Length || !CryptographicOperations.FixedTimeEquals(expectedSig, sigBytes))
                return null;
        }
        else if (string.Equals(alg, "RS256", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(iss) || !string.Equals(iss, keycloakAuthority, StringComparison.OrdinalIgnoreCase))
                return null;

            bool audOk = false;
            if (pRoot.TryGetProperty("azp", out var azpEl) && string.Equals(azpEl.GetString(), keycloakClientId, StringComparison.OrdinalIgnoreCase))
                audOk = true;
            if (!audOk && pRoot.TryGetProperty("aud", out var audEl))
            {
                if (audEl.ValueKind == JsonValueKind.String && string.Equals(audEl.GetString(), keycloakClientId, StringComparison.OrdinalIgnoreCase))
                    audOk = true;
                else if (audEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in audEl.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String && string.Equals(a.GetString(), keycloakClientId, StringComparison.OrdinalIgnoreCase))
                        {
                            audOk = true;
                            break;
                        }
                    }
                }
            }
            if (!audOk) return null;

            if (!await VerifyKeycloakRs256SignatureAsync(parts[0], parts[1], sigBytes, kid))
                return null;
        }
        else
        {
            return null;
        }

        string? username = pRoot.TryGetProperty("preferred_username", out var puEl) ? puEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(username) && pRoot.TryGetProperty("sub", out var subEl))
            username = subEl.GetString();
        if (string.IsNullOrWhiteSpace(username)) return null;
        username = username.Trim();

        var email = pRoot.TryGetProperty("email", out var emEl) ? (emEl.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(email))
            email = username.Contains('@') ? username : $"{username}@vnu.edu.vn";

        var unit = pRoot.TryGetProperty("organizational_unit", out var ouEl) ? (ouEl.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(unit) && pRoot.TryGetProperty("vnu_admin_unit", out var vauEl))
            unit = vauEl.GetString() ?? "";

        var memberType = pRoot.TryGetProperty("vnu_member_type", out var mtEl) ? (mtEl.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(memberType) && pRoot.TryGetProperty("member_type", out var mtEl2))
            memberType = mtEl2.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(memberType) && pRoot.TryGetProperty("role", out var rEl))
        {
            var rVal = rEl.GetString() ?? "";
            if (string.Equals(rVal, "sinhvien", StringComparison.OrdinalIgnoreCase) || string.Equals(rVal, "nguoihoc", StringComparison.OrdinalIgnoreCase))
                memberType = rVal.ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(memberType)) memberType = "canbo";

        var issuedAtUtc = pRoot.TryGetProperty("iat", out var iatEl) && iatEl.TryGetInt64(out var iatUnix)
            ? DateTimeOffset.FromUnixTimeSeconds(iatUnix)
            : DateTimeOffset.UtcNow;

        if (IsTokenRevokedForUser(username, issuedAtUtc)) return null;
        if (IsUserDenied(username)) return null;

        return new VerifiedTokenIdentity(username, email.Trim(), unit.Trim(), memberType.Trim().ToLowerInvariant(), jti);
    }
    catch
    {
        return null;
    }

    static bool nElTryInt64(JsonElement el, out long val) => el.TryGetInt64(out val);
}

async Task<VerifiedTokenIdentity?> TryVerifyBearerTokenAsync(string? rawToken) =>
    await VerifyJwtTokenCoreAsync(rawToken, expectExpired: false);

async Task<VerifiedTokenIdentity?> TryExtractExpiredGenuineTokenAsync(string? rawToken) =>
    await VerifyJwtTokenCoreAsync(rawToken, expectExpired: true);

async Task<(bool Ok, string AuthNote, string? Username, string? Unit, string? MemberType)> ValidateProxyAuthAsync(string? authHeader, RuleStoreModel currentRules)
{
    if (string.IsNullOrWhiteSpace(authHeader))
    {
        return (false, "Missing Proxy-Authorization", null, null, null);
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

                if (!MonitorDbService.IsValidUsername(user))
                {
                    return (false, "Invalid username format", null, null, null);
                }

                bool isDenied;
                lock (ruleLock)
                {
                    isDenied = (currentRules.UserOverrides?.Denied?.Contains(user, StringComparer.OrdinalIgnoreCase) == true);
                }
                if (isDenied)
                {
                    return (false, $"Account locked/denied ({user})", null, null, null);
                }

                if (monitorDb.ValidateAppKey(user, pass, out var uUnit, out var uMember))
                {
                    return (true, $"App Password (user={user}, unit={uUnit})", user, uUnit, uMember);
                }
            }
        }
        catch { }

        return (false, "Invalid Basic Proxy-Authorization credentials", null, null, null);
    }

    var identity = await TryVerifyBearerTokenAsync(token);
    if (identity != null)
    {
        return (true, $"Verified Token (user={identity.Username}, memberType={identity.MemberType})", identity.Username, identity.Unit, identity.MemberType);
    }

    return (false, "Invalid or expired Proxy-Authorization token", null, null, null);
}

async Task WriteConnectErrorAsync(PipeWriter output, int statusCode, string reasonPhrase, string body, CancellationToken ct)
{
    try
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
                     "Proxy-Agent: VAGS-Gateway/2.2\r\n" +
                     "Content-Type: text/plain; charset=utf-8\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        var payload = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, payload, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, payload, headerBytes.Length, bodyBytes.Length);
        await output.WriteAsync(payload, ct);
        await output.FlushAsync(ct);
    }
    catch { }
}

// Kestrel Connection Middleware that transparently handles HTTP CONNECT tunneling
// and passes normal HTTP GET/POST/SSE requests to ASP.NET Core
async Task HandleConnectionAsync(ConnectionContext connectionContext, ConnectionDelegate next)
{
    var input = connectionContext.Transport.Input;
    var output = connectionContext.Transport.Output;

    ReadResult readResult;
    string headerBlock = string.Empty;
    using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(connectionContext.ConnectionClosed))
    {
        headerCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            readResult = await input.ReadAsync(headerCts.Token);
        }
        catch
        {
            return;
        }

        var buffer = readResult.Buffer;
        if (readResult.IsCanceled || buffer.IsEmpty)
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
            headerCts.Dispose();
            await next(connectionContext);
            return;
        }

        // Read full CONNECT request headers until \r\n\r\n
        while (true)
        {
            buffer = readResult.Buffer;
            if (TryExtractHeaderBlock(buffer, out headerBlock, out var consumedPos))
            {
                input.AdvanceTo(consumedPos);
                break;
            }

            if (readResult.IsCompleted || readResult.IsCanceled || buffer.Length > 16384)
            {
                input.AdvanceTo(buffer.End);
                return;
            }

            input.AdvanceTo(buffer.Start, buffer.End);
            try
            {
                readResult = await input.ReadAsync(headerCts.Token);
            }
            catch
            {
                return;
            }
        }
    }

    var lines = headerBlock.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length == 0) return;

    var rawClientIp = connectionContext.RemoteEndPoint?.ToString() ?? "unknown";
    if (connectionContext.Items.TryGetValue("RealClientIp", out var rIpObj) && rIpObj is string rIp && !string.IsNullOrWhiteSpace(rIp))
    {
        rawClientIp = rIp;
    }
    var clientIp = CleanIp(rawClientIp);

    var requestLineParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (requestLineParts.Length < 2 || requestLineParts.Length > 3 ||
        !requestLineParts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase) ||
        (requestLineParts.Length == 3 && !requestLineParts[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) ||
        !TryParseConnectAuthority(requestLineParts[1], out var targetHost, out var targetPort))
    {
        AddProxyLog(clientIp, requestLineParts.Length >= 2 ? requestLineParts[1] : "", 0, false, "Malformed CONNECT authority (RFC 9110)", "invalid_connect_authority");
        await WriteConnectErrorAsync(output, 400, "Bad Request",
            "Bad Request: Invalid CONNECT authority.\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    string? proxyAuth = null;
    foreach (var line in lines.Skip(1))
    {
        if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            proxyAuth = line.Substring("Proxy-Authorization:".Length).Trim();
        else if (proxyAuth == null && line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            proxyAuth = line.Substring("Authorization:".Length).Trim();
    }

    RuleStoreModel currentRules;
    lock (ruleLock)
    {
        currentRules = ruleStore;
    }

    // 1. Inspect Proxy Auth if provided
    string? authUser = null;
    string? authUnit = null;
    string? authMemberType = null;
    if (!string.IsNullOrWhiteSpace(proxyAuth))
    {
        var authRes = await ValidateProxyAuthAsync(proxyAuth, currentRules);
        if (!authRes.Ok)
        {
            AddProxyLog(clientIp, targetHost, targetPort, false,
                $"Từ chối: {authRes.AuthNote}", "invalid_proxy_auth", "Khách (Chưa xác thực)", "Khách");
            await WriteConnectErrorAsync(output, 403, "Forbidden",
                "Blocked by VAGS Gateway: Thông tin xác thực Proxy không hợp lệ hoặc tài khoản đã bị khoá.\r\n",
                connectionContext.ConnectionClosed);
            return;
        }
        authUser = authRes.Username;
        authUnit = authRes.Unit;
        authMemberType = authRes.MemberType;
    }

    // A. Phân giải danh tính người dùng từ Token hoặc IP Session đã xác thực
    var (resolvedUser, resolvedUnit, resolvedMemberType) = monitorDb.ResolveUser(clientIp, authUser, authUnit, authMemberType);
    var isUserAuthenticated = !string.IsNullOrEmpty(resolvedUser) && resolvedUser != "Khách (Chưa xác thực)";
    var logUser = isUserAuthenticated ? resolvedUser : "Khách (Chưa xác thực)";
    var logUnit = string.IsNullOrEmpty(resolvedUnit) ? "VNU" : resolvedUnit;
    var logMemberType = string.IsNullOrEmpty(resolvedMemberType) ? (isUserAuthenticated ? "canbo" : "guest") : resolvedMemberType;

    // Kiểm tra Chế độ Bảo trì (Maintenance Mode / Đóng cổng VAGS)
    if (CheckMaintenanceMode(currentRules))
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Hệ thống đang bảo trì", "maintenance_mode", logUser, logUnit, logMemberType);
        await WriteConnectErrorAsync(output, 503, "Service Unavailable",
            "Blocked by VAGS Gateway: Hệ thống Cổng VAGS đang tạm thời bảo trì để nâng cấp hạ tầng 1Gbps. Trình duyệt vui lòng chuyển sang kết nối trực tiếp (DIRECT).\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    // Chặn tuyệt đối CONNECT tự trỏ vào cổng VAGS (tránh loop HAProxy / SSRF nội bộ)
    if (IsVagsSelfHost(targetHost, currentRules))
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Từ chối CONNECT vào chính Cổng VAGS (Phải truy cập trực tiếp HTTPS)", "vags_self_bypass", logUser, logUnit, logMemberType);
        await WriteConnectErrorAsync(output, 403, "Forbidden",
            "Blocked by VAGS Gateway: Không cho phép tunnel qua chính máy chủ VAGS. Vui lòng truy cập trực tiếp.\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    // Chỉ cho phép cổng HTTPS 443 qua HTTP CONNECT
    if (targetPort != 443)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, $"Từ chối cổng {targetPort} (Chỉ cho phép HTTPS port 443)", "non_443_port_blocked", logUser, logUnit, logMemberType);
        await WriteConnectErrorAsync(output, 403, "Forbidden",
            $"Blocked by VAGS Gateway: Chỉ cho phép kết nối HTTPS cổng 443 qua Proxy VAGS (Port {targetPort} bị từ chối).\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    // Chuyển hướng kiểm tra IP của Extension cũ (Chrome Web Store) từ api4.ipify.org sang /api/v1/ip-check nội bộ
    if (targetHost.EndsWith("ipify.org", StringComparison.OrdinalIgnoreCase))
    {
        await WriteConnectErrorAsync(output, 403, "Forbidden", "", connectionContext.ConnectionClosed);
        return;
    }

    // BẢO VỆ AN TOÀN: Chặn tuyệt đối tunnel vào Loopback, Docker và Mạng nội bộ cụm máy chủ
    var isLocalOrInternal = targetHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.EndsWith(".docker.internal", StringComparison.OrdinalIgnoreCase) ||
                            targetHost.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase) ||
                            (IPAddress.TryParse(targetHost, out var rawTargetIp) && IsForbiddenTargetIp(rawTargetIp));
    if (isLocalOrInternal)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Từ chối Proxy tới địa chỉ Loopback / Nội bộ / Docker (Bắt buộc Bypass)", "internal_loopback_bypass", logUser, logUnit, logMemberType);
        await WriteConnectErrorAsync(output, 403, "Forbidden",
            "Blocked by VAGS Gateway: Địa chỉ Loopback / Nội bộ / Docker không được phép đi qua Proxy VAGS. Trình duyệt phải kết nối trực tiếp (DIRECT).\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    // BẮT BUỘC XÁC THỰC DANH TÍNH (Chống Open Proxy tuyệt đối, không miễn trừ domain cloud nào)
    if (!isUserAuthenticated)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false,
            "Từ chối truy cập: Chưa xác thực danh tính Cán bộ ĐHQGHN (Chống Open Proxy)",
            "unauthenticated_client_blocked", "Khách (Chưa xác thực)", "Khách", "guest");
        await WriteConnectErrorAsync(output, 403, "Forbidden",
            "Blocked by VAGS Gateway: Bạn chưa đăng nhập tài khoản Cán bộ ĐHQGHN. Vui lòng truy cập https://vags.vnu.edu.vn để đăng nhập.\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    // Kiểm tra tài khoản bị khoá (Admin Lock / Denied Override)
    bool isUserDenied;
    bool isOverrideUser;
    lock (ruleLock)
    {
        isUserDenied = currentRules.UserOverrides?.Denied?.Contains(resolvedUser, StringComparer.OrdinalIgnoreCase) == true;
        isOverrideUser = currentRules.UserOverrides?.Allowed?.Contains(resolvedUser, StringComparer.OrdinalIgnoreCase) == true;
    }
    if (isUserDenied)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Tài khoản đã bị Quản trị viên KHOÁ quyền sử dụng Proxy VAGS", "user_locked", logUser, logUnit, logMemberType);
        await WriteConnectErrorAsync(output, 403, "Forbidden",
            "Blocked by VAGS Gateway: Tài khoản của bạn đã bị Quản trị viên khoá quyền sử dụng Proxy VAGS. Vui lòng liên hệ Quản trị viên ĐHQGHN để được hỗ trợ.\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    // Chặn tài khoản Sinh viên (trừ tài khoản nằm trong Allowed Override)
    if (string.Equals(resolvedMemberType, "sinhvien", StringComparison.OrdinalIgnoreCase) && !isOverrideUser)
    {
        AddProxyLog(clientIp, targetHost, targetPort, false, "Tài khoản Sinh viên không có quyền sử dụng Proxy CSDL VAGS", "student_blocked", logUser, logUnit, logMemberType);
        await WriteConnectErrorAsync(output, 403, "Forbidden",
            "Blocked by VAGS Gateway: Tài khoản Sinh viên không được cấp quyền sử dụng Proxy VAGS.\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    // 2. Evaluate Whitelist / Blacklist Policy (dùng đúng resolvedUser đã xác thực)
    RouteDecision decision;
    lock (ruleLock)
    {
        decision = EvaluateHost(targetHost, currentRules, resolvedUser);
    }

    // Enforce strict policy allowlist BEFORE connecting upstream
    if (!decision.AllowedForTunnel || !string.Equals(decision.Action, "PROXY", StringComparison.OrdinalIgnoreCase))
    {
        var blockCode = decision.MatchedCategory == "blacklist" ? "blacklist_blocked" : $"policy_blocked_{decision.MatchedCategory}";
        AddProxyLog(clientIp, targetHost, targetPort, false, $"Chính sách định tuyến từ chối tunnel: {decision.Reason}", blockCode, logUser, logUnit, logMemberType);
        await WriteConnectErrorAsync(output, 403, "Forbidden",
            $"Blocked by VAGS Gateway: {decision.Reason} (Phải kết nối trực tiếp DIRECT).\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    // Reject raw IP literals on CONNECT unless explicitly allowed in whitelist
    IPAddress? explicitAllowedLiteralIp = null;
    if (IPAddress.TryParse(targetHost, out var parsedLiteralIp))
    {
        var normLiteral = parsedLiteralIp.IsIPv4MappedToIPv6 ? parsedLiteralIp.MapToIPv4() : parsedLiteralIp;
        bool explicitlyAllowed = !IsForbiddenTargetIp(normLiteral) &&
                                 decision.MatchedRule != "*" &&
                                 (decision.MatchedCategory == "userOverride" ||
                                  decision.MatchedCategory == "internalWhitelist" ||
                                  decision.MatchedCategory == "globalAcademicWhitelist");
        if (!explicitlyAllowed)
        {
            AddProxyLog(clientIp, targetHost, targetPort, false, "Từ chối kết nối trực tiếp bằng địa chỉ IP (IP literal không nằm trong whitelist)", "ip_literal_blocked", logUser, logUnit, logMemberType);
            await WriteConnectErrorAsync(output, 403, "Forbidden",
                "Blocked by VAGS Gateway: Không cho phép CONNECT trực tiếp tới địa chỉ IP.\r\n",
                connectionContext.ConnectionClosed);
            return;
        }
        explicitAllowedLiteralIp = normLiteral;
    }

    // Enforce max 80 concurrent tunnels per resolvedUser
    var tunnelUserKey = resolvedUser.Trim().ToLowerInvariant();
    int currentUserTunnels = activeUserTunnels.AddOrUpdate(tunnelUserKey, 1, (_, c) => c + 1);
    if (currentUserTunnels > 80)
    {
        activeUserTunnels.AddOrUpdate(tunnelUserKey, 0, (_, c) => Math.Max(0, c - 1));
        AddProxyLog(clientIp, targetHost, targetPort, false, "Vượt giới hạn 80 kết nối đồng thời trên mỗi tài khoản", "rate_limit_tunnels", logUser, logUnit, logMemberType);
        await WriteConnectErrorAsync(output, 429, "Too Many Requests",
            "Blocked by VAGS Gateway: Vượt quá giới hạn 80 kết nối đồng thời trên mỗi tài khoản.\r\n",
            connectionContext.ConnectionClosed);
        return;
    }

    try
    {
        // 3. Resolve DNS ONCE (anti-DNS-rebinding & SSRF) and establish TCP tunnel
        TcpClient? tcpClient = null;
        try
        {
            IPAddress[] validIps;
            if (explicitAllowedLiteralIp != null)
            {
                validIps = new[] { explicitAllowedLiteralIp };
            }
            else
            {
                using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(connectionContext.ConnectionClosed);
                dnsCts.CancelAfter(TimeSpan.FromSeconds(10));
                var resolvedAddrs = await Dns.GetHostAddressesAsync(targetHost, dnsCts.Token);
                validIps = resolvedAddrs
                    .Select(ip => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)
                    .ToArray();

                if (validIps.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);

                if (validIps.Any(IsForbiddenTargetIp))
                {
                    AddProxyLog(clientIp, targetHost, targetPort, false,
                        "Từ chối tên miền phân giải ra IP nội bộ / Loopback / Metadata (Chống DNS Rebinding & SSRF)",
                        "ssrf_dns_blocked", logUser, logUnit, logMemberType);
                    await WriteConnectErrorAsync(output, 403, "Forbidden",
                        "Blocked by VAGS Gateway: Tên miền đích phân giải ra địa chỉ IP nội bộ không hợp lệ.\r\n",
                        connectionContext.ConnectionClosed);
                    return;
                }
            }

            var ipv4Ips = validIps.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToArray();
            var ipv6Ips = validIps.Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
            var connectIps = ipv4Ips.Length > 0 ? ipv4Ips : ipv6Ips;
            var connectFamily = ipv4Ips.Length > 0 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;

            Exception? lastConnectEx = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                TcpClient? attemptClient = null;
                try
                {
                    attemptClient = new TcpClient(connectFamily);
                    attemptClient.NoDelay = true;
                    attemptClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(connectionContext.ConnectionClosed);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    await attemptClient.ConnectAsync(connectIps, targetPort, cts.Token);
                    tcpClient = attemptClient;
                    attemptClient = null;
                    break;
                }
                catch (OperationCanceledException) when (connectionContext.ConnectionClosed.IsCancellationRequested)
                {
                    attemptClient?.Dispose();
                    return;
                }
                catch (OperationCanceledException oce) when (attempt == 0 && !connectionContext.ConnectionClosed.IsCancellationRequested)
                {
                    attemptClient?.Dispose();
                    lastConnectEx = oce;
                    await Task.Delay(150, connectionContext.ConnectionClosed);
                }
                catch (SocketException sockEx) when (attempt == 0 && !connectionContext.ConnectionClosed.IsCancellationRequested)
                {
                    attemptClient?.Dispose();
                    lastConnectEx = sockEx;
                    await Task.Delay(150, connectionContext.ConnectionClosed);
                }
                catch (Exception ex)
                {
                    attemptClient?.Dispose();
                    lastConnectEx = ex;
                    break;
                }
            }

            if (tcpClient == null)
            {
                if (connectionContext.ConnectionClosed.IsCancellationRequested)
                    return;
                throw lastConnectEx ?? new SocketException((int)SocketError.HostNotFound);
            }
        }
        catch (OperationCanceledException) when (connectionContext.ConnectionClosed.IsCancellationRequested)
        {
            tcpClient?.Dispose();
            return;
        }
        catch (Exception ex)
        {
            tcpClient?.Dispose();
            if (connectionContext.ConnectionClosed.IsCancellationRequested)
                return;

            AddProxyLog(clientIp, targetHost, targetPort, false, $"Upstream connect failed: {ex.Message}", decision.MatchedRule, logUser, logUnit, logMemberType);
            try
            {
                var resp502 = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 502 Bad Gateway\r\n" +
                    "Proxy-Agent: VAGS-Gateway/2.2\r\n" +
                    "Connection: close\r\n\r\n");
                await output.WriteAsync(resp502, connectionContext.ConnectionClosed);
                await output.FlushAsync(connectionContext.ConnectionClosed);
            }
            catch { }
            return;
        }

        using var _ownedTcpClient = tcpClient;

        var resp200 = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 Connection Established\r\n" +
            "Proxy-Agent: VAGS-Gateway/2.2 (vags.vnu.edu.vn)\r\n\r\n");
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

        AddProxyLog(clientIp, targetHost, targetPort, true, decision.Reason, decision.MatchedRule, logUser, logUnit, logMemberType, bytesSent, bytesReceived, durationMs);
    }
    finally
    {
        var remaining = activeUserTunnels.AddOrUpdate(tunnelUserKey, 0, (_, c) => Math.Max(0, c - 1));
        if (remaining <= 0)
        {
            activeUserTunnels.TryRemove(new KeyValuePair<string, int>(tunnelUserKey, 0));
        }
    }
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
        var remoteAddr = (context.RemoteEndPoint as IPEndPoint)?.Address;
        if (!IsTrustedProxyAddress(remoteAddr))
        {
            await next(context);
            return;
        }

        var readResult = await input.ReadAsync(context.ConnectionClosed);
        var buffer = readResult.Buffer;

        if (buffer.Length >= 6)
        {
            var prefixBytes = buffer.Slice(0, 6).ToArray();
            var prefix = Encoding.ASCII.GetString(prefixBytes);
            if (prefix.Equals("PROXY ", StringComparison.OrdinalIgnoreCase))
            {
                SequencePosition? lineEnd = buffer.PositionOf((byte)'\n');
                while (lineEnd == null && !readResult.IsCompleted && buffer.Length < 256)
                {
                    input.AdvanceTo(buffer.Start, buffer.End);
                    readResult = await input.ReadAsync(context.ConnectionClosed);
                    buffer = readResult.Buffer;
                    lineEnd = buffer.PositionOf((byte)'\n');
                }

                if (lineEnd != null)
                {
                    var lineBytes = buffer.Slice(0, lineEnd.Value).ToArray();
                    var lineStr = Encoding.ASCII.GetString(lineBytes).Trim();
                    // PROXY TCP4 <client_ip> <server_ip> <client_port> <server_port>
                    var parts = lineStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 &&
                        (parts[1].Equals("TCP4", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("TCP6", StringComparison.OrdinalIgnoreCase)))
                    {
                        var realIp = CleanIp(parts[2]);
                        if (realIp != "unknown" && IPAddress.TryParse(realIp, out var parsedIp))
                        {
                            context.Items["RealClientIp"] = realIp;
                            int port = 0;
                            if (parts.Length >= 5 && int.TryParse(parts[4], out var p) && p >= 0 && p <= 65535) port = p;
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
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(65);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

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
jwksHttpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
isDevelopmentEnv = app.Environment.IsDevelopment();

var allowedCorsOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "https://thamkhao.vnu.edu.vn",
    "https://vags.vnu.edu.vn"
};
if (!string.IsNullOrWhiteSpace(primaryDomain))
{
    allowedCorsOrigins.Add($"https://{primaryDomain.Trim().TrimEnd('/')}");
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseCors(policy => policy
    .SetIsOriginAllowed(origin =>
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (allowedCorsOrigins.Contains(origin)) return true;
        if (isDevelopmentEnv && Uri.TryCreate(origin, UriKind.Absolute, out var u))
        {
            return u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   u.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    })
    .AllowAnyMethod()
    .AllowAnyHeader());

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
        bool isMaint;
        lock (ruleLock) { isMaint = CheckMaintenanceMode(ruleStore); }
        if (isMaint)
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
    var htmlNoCachePrepare = new Action<StaticFileResponseContext>(ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    });

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = provider,
        ContentTypeProvider = contentTypeProvider,
        ServeUnknownFileTypes = true,
        OnPrepareResponse = htmlNoCachePrepare
    });
    // Hỗ trợ cả tiền tố /public/... nếu người dùng truy cập trực tiếp đường dẫn thư mục
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = provider,
        RequestPath = "/public",
        ContentTypeProvider = contentTypeProvider,
        ServeUnknownFileTypes = true,
        OnPrepareResponse = htmlNoCachePrepare
    });
}

RuleStoreModel BuildPublicRulesProjection(RuleStoreModel src)
{
    var configuredInternal = (src.InternalWhitelist ?? new List<string>()).ToList();
    var userOverridesCopy = new UserOverridesModel
    {
        Allowed = (src.UserOverrides?.Allowed ?? new List<string>()).ToList(),
        Denied = (src.UserOverrides?.Denied ?? new List<string>()).ToList(),
        UserSpecificRules = (src.UserOverrides?.UserSpecificRules ?? new Dictionary<string, UserRuleConfig>())
            .ToDictionary(
                kvp => kvp.Key,
                kvp => new UserRuleConfig
                {
                    ProxyDomains = (kvp.Value?.ProxyDomains ?? new List<string>()).ToList(),
                    DirectDomains = (kvp.Value?.DirectDomains ?? new List<string>()).ToList()
                })
    };

    return new RuleStoreModel
    {
        Version = src.Version,
        ProxyMode = src.ProxyMode,
        MaintenanceMode = src.MaintenanceMode,
        EnableLicProxy = src.EnableLicProxy,
        MaxConcurrentSessionsPerUser = src.MaxConcurrentSessionsPerUser > 0 ? Math.Clamp(src.MaxConcurrentSessionsPerUser, 1, 100) : 5,
        MaxConcurrentSessionsAdmin = src.MaxConcurrentSessionsAdmin > 0 ? Math.Clamp(src.MaxConcurrentSessionsAdmin, 1, 100) : 10,
        Blacklist = (src.Blacklist ?? new List<string>()).ToList(),
        InternalWhitelist = src.EnableLicProxy ? configuredInternal.ToList() : new List<string>(),
        ConfiguredInternalWhitelist = configuredInternal,
        GlobalAcademicWhitelist = (src.GlobalAcademicWhitelist ?? new List<string>()).ToList(),
        AuthMode = src.AuthMode,
        KeycloakAuthority = src.KeycloakAuthority,
        ProxyHost = src.ProxyHost,
        ProxyPort = src.ProxyPort,
        ProxyScheme = src.ProxyScheme,
        DomainAliases = (src.DomainAliases ?? new List<string>()).ToList(),
        AllowedOUs = (src.AllowedOUs ?? new List<string>()).ToList(),
        DisallowedOUs = (src.DisallowedOUs ?? new List<string>()).ToList(),
        AllowedMemberTypes = (src.AllowedMemberTypes ?? new List<string>()).ToList(),
        DisallowedMemberTypes = (src.DisallowedMemberTypes ?? new List<string>()).ToList(),
        AdminOU = src.AdminOU,
        UserOverrides = userOverridesCopy,
        ManualAdmins = new List<AdminAccountEntry>(),
        UpdatedAt = src.UpdatedAt
    };
}

bool IsSameUser(string? a, string? b)
{
    if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
    var ca = a.Trim().ToLowerInvariant();
    var cb = b.Trim().ToLowerInvariant();
    if (ca == cb) return true;
    if (ca.EndsWith("@vnu.edu.vn", StringComparison.Ordinal) && ca.Substring(0, ca.Length - 11) == cb) return true;
    if (cb.EndsWith("@vnu.edu.vn", StringComparison.Ordinal) && cb.Substring(0, cb.Length - 11) == ca) return true;
    return false;
}

async Task<(IResult? Error, VerifiedTokenIdentity? Identity, string AdminRole)> TryAuthorizeRequestAsync(HttpContext ctx, bool requireSuperAdmin = true, bool allowReportViewer = false)
{
    var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return (Results.Json(new { ok = false, error = "Unauthorized: Missing Bearer token" }, statusCode: 401), null, "");
    }

    var identity = await TryVerifyBearerTokenAsync(authHeader);
    if (identity == null)
    {
        return (Results.Json(new { ok = false, error = "Unauthorized: Invalid or expired Bearer token" }, statusCode: 401), null, "");
    }

    var adminRole = ResolveAdminRole(identity.Username, identity.Email, identity.Unit);
    if (requireSuperAdmin)
    {
        bool allowed = adminRole == "super_admin" || (allowReportViewer && adminRole == "report_viewer");
        if (!allowed)
        {
            return (Results.Json(new { ok = false, error = "Forbidden: Insufficient permissions" }, statusCode: 403), identity, adminRole);
        }
    }

    return (null, identity, adminRole);
}

// Phục vụ giao diện Quản trị viên (/admin -> admin.html)
app.MapMethods("/admin", new[] { "GET", "HEAD" }, (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    ctx.Response.Headers.Pragma = "no-cache";
    ctx.Response.Headers.Expires = "0";
    return Results.File(Path.Combine(webRootPath, "admin.html"), "text/html");
});

// Phục vụ trang Chính sách quyền riêng tư (/privacy & /privacy/ -> privacy.html)
app.MapMethods("/privacy", new[] { "GET", "HEAD" }, () => Results.File(Path.Combine(webRootPath, "privacy.html"), "text/html; charset=utf-8"));

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
    var rawUser = ctx.Request.Query["user"].ToString().Trim();
    var user = MonitorDbService.IsValidUsername(rawUser) ? rawUser : "";

    RuleStoreModel snap;
    bool isMaint;
    lock (ruleLock)
    {
        snap = BuildPublicRulesProjection(ruleStore);
        isMaint = CheckMaintenanceMode(ruleStore);
    }

    var proxyServer = $"{snap.ProxyScheme} {snap.ProxyHost}:{snap.ProxyPort}";

    // Nếu đang ở Chế độ Bảo trì (Maintenance Mode), trả về PAC toàn bộ DIRECT
    if (isMaint)
    {
        return Results.Text("function FindProxyForURL(url, host) {\n    // VAGS Maintenance Mode Active -> Bypass All Direct\n    return 'DIRECT';\n}", "application/x-ns-proxy-autoconfig; charset=utf-8");
    }

    // Nếu tài khoản bị khoá, trả về PAC toàn bộ DIRECT
    var isUserDenied = !string.IsNullOrWhiteSpace(user) && (snap.UserOverrides?.Denied?.Contains(user, StringComparer.OrdinalIgnoreCase) == true);
    if (isUserDenied)
    {
        return Results.Text("function FindProxyForURL(url, host) { return 'DIRECT'; }", "application/x-ns-proxy-autoconfig; charset=utf-8");
    }

    var userProxyDomains = new List<string>();
    if (!string.IsNullOrWhiteSpace(user) && snap.UserOverrides?.UserSpecificRules != null)
    {
        foreach (var kvp in snap.UserOverrides.UserSpecificRules)
        {
            if (string.Equals(kvp.Key, user, StringComparison.OrdinalIgnoreCase))
            {
                userProxyDomains = kvp.Value.ProxyDomains ?? new List<string>();
                break;
            }
        }
    }

    var isUserAuth = !string.IsNullOrWhiteSpace(user) && user != "Khách (Chưa xác thực)";
    var isFullProxy = string.Equals(snap.ProxyMode, "FULL_PROXY", StringComparison.OrdinalIgnoreCase);
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
    if (!string.IsNullOrWhiteSpace(snap.ProxyHost)) pacBypasses.Add(snap.ProxyHost);
    foreach (var a in domainAliases) pacBypasses.Add(a);
    if (snap.DomainAliases != null) foreach (var a in snap.DomainAliases) pacBypasses.Add(a);

    var safeBypasses = pacBypasses
        .Select(b => (b ?? "").Trim().ToLowerInvariant())
        .Where(b => Regex.IsMatch(b, @"^[a-z0-9.-]{1,253}$"));
    var pacBypassChecks = string.Join(" || ", safeBypasses.Select(b => $"lHost === '{b}' || shExpMatch(lHost, '*.{b}')"));

    var pac = $@"function FindProxyForURL(url, host) {{
    var lHost = host.toLowerCase().replace(/\.+$/, '');
    if ({pacBypassChecks}) return 'DIRECT';
    if (shExpMatch(lHost, '*.google.com') || lHost === 'google.com' || shExpMatch(lHost, '*.googleapis.com') || shExpMatch(lHost, '*.gstatic.com') || shExpMatch(lHost, '*.googleusercontent.com')) return 'DIRECT';
    var userWL = {JsonSerializer.Serialize(userProxyDomains)};
    for (var u = 0; u < userWL.length; u++) {{
        if (shExpMatch(lHost, userWL[u])) return ""{proxyServer}"";
    }}
    var blacklist = {JsonSerializer.Serialize(snap.Blacklist)};
    for (var i = 0; i < blacklist.length; i++) {{
        if (shExpMatch(lHost, blacklist[i])) return ""DIRECT"";
    }}
    var internalWL = {(snap.EnableLicProxy ? JsonSerializer.Serialize(snap.InternalWhitelist) : "[]")};
    for (var j = 0; j < internalWL.length; j++) {{
        if (shExpMatch(lHost, internalWL[j])) return ""{proxyServer}"";
    }}
    if (shExpMatch(lHost, ""*.vnu.edu.vn"") || lHost === ""vnu.edu.vn"") return ""DIRECT"";
    var academicWL = {JsonSerializer.Serialize(snap.GlobalAcademicWhitelist)};
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
app.MapGet("/api/v1/rules", () =>
{
    RuleStoreModel projection;
    lock (ruleLock) { projection = BuildPublicRulesProjection(ruleStore); }
    return Results.Json(projection, jsonOptions);
});
app.MapGet("/api/v1/rules/latest", () =>
{
    RuleStoreModel projection;
    lock (ruleLock) { projection = BuildPublicRulesProjection(ruleStore); }
    return Results.Json(projection, jsonOptions);
});

// 2. API Kiểm tra phiên bản (Startup Catch-up)
app.MapGet("/api/v1/rules/version", () =>
{
    object versionInfo;
    lock (ruleLock)
    {
        versionInfo = new
        {
            version = ruleStore.Version,
            authMode = ruleStore.AuthMode,
            proxy = $"{ruleStore.ProxyScheme} {ruleStore.ProxyHost}:{ruleStore.ProxyPort}",
            updatedAt = ruleStore.UpdatedAt
        };
    }
    return Results.Json(versionInfo, jsonOptions);
});

// 3. API Nhận SSE Stream Real-time từ Extension
app.MapGet("/api/v1/rules/stream", async context =>
{
    var qUserRaw = context.Request.Query["user"].FirstOrDefault();
    var qUnitRaw = context.Request.Query["unit"].FirstOrDefault();
    var qProxyEnabledStr = context.Request.Query["proxyEnabled"].FirstOrDefault();
    bool qProxyEnabled = string.Equals(qProxyEnabledStr, "true", StringComparison.OrdinalIgnoreCase);

    var qUser = (qUserRaw ?? "").Trim();
    bool hasRealUserParam = !string.IsNullOrWhiteSpace(qUser) && qUser != "Cán bộ VNU";
    if (hasRealUserParam && !MonitorDbService.IsValidUsername(qUser))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid username parameter");
        return;
    }
    if (!string.IsNullOrWhiteSpace(qUnitRaw) && !MonitorDbService.IsValidUnit(qUnitRaw))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid unit parameter");
        return;
    }

    var minRespRateFeature = context.Features.Get<IHttpMinResponseDataRateFeature>();
    if (minRespRateFeature != null)
    {
        minRespRateFeature.MinDataRate = null;
    }

    context.Response.Headers.Append("Content-Type", "text/event-stream; charset=utf-8");
    context.Response.Headers.Append("Cache-Control", "no-cache, no-transform");
    context.Response.Headers.Append("Connection", "keep-alive");
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    var clientId = Guid.NewGuid();
    var rawIp = GetRealClientIp(context);

    string effectiveUser = "Khách (Chưa xác thực)";
    string effectiveUnit = "VNU";
    bool effectiveProxyEnabled = false;
    PendingForceOffEntry? pendingOff = null;

    if (hasRealUserParam)
    {
        var unitVal = string.IsNullOrWhiteSpace(qUnitRaw) ? "VNU" : qUnitRaw.Trim();
        var bearerId = await TryVerifyBearerTokenAsync(context.Request.Headers.Authorization.FirstOrDefault());
        bool bearerMatches = bearerId != null && IsSameUser(qUser, bearerId.Username);
        var effectiveMemberType = bearerMatches
            ? bearerId!.MemberType
            : (string.Equals(monitorDb.GetLatestKnownMemberType(qUser, rawIp), "sinhvien", StringComparison.OrdinalIgnoreCase) ? "sinhvien" : "canbo");

        if (!qProxyEnabled)
        {
            ClearPendingForceOff(qUser, rawIp);
            if (!HasOtherActiveSseClient(qUser, rawIp, clientId))
            {
                monitorDb.RegisterUserActivity(rawIp, qUser, unitVal, effectiveMemberType, proxyEnabled: false);
            }
            effectiveProxyEnabled = false;
        }
        else if (TryGetPendingForceOff(qUser, rawIp, out pendingOff))
        {
            effectiveProxyEnabled = false;
        }
        else
        {
            monitorDb.RegisterUserActivity(rawIp, qUser, unitVal, effectiveMemberType, proxyEnabled: true);
            effectiveProxyEnabled = true;
        }

        effectiveUser = qUser;
        effectiveUnit = unitVal.ToUpperInvariant();
    }

    var clientInfo = new SseClientInfo
    {
        Id = clientId,
        Response = context.Response,
        ConnectedAt = DateTime.UtcNow.ToString("O"),
        RemoteIp = rawIp,
        UserAgent = context.Request.Headers["User-Agent"].FirstOrDefault() ?? "VAGS-Client",
        User = effectiveUser,
        Unit = effectiveUnit,
        ProxyEnabled = effectiveProxyEnabled
    };

    sseClients[clientId] = clientInfo;

    try
    {
        string initMsg;
        lock (ruleLock)
        {
            initMsg = JsonSerializer.Serialize(new
            {
                type = "CONNECTED",
                clientId = clientId,
                version = ruleStore.Version,
                authMode = ruleStore.AuthMode
            }, sseJsonOptions);
        }
        if (!await WriteSseEventAsync(clientInfo, $"event: CONNECTED\ndata: {initMsg}\n\n", context.RequestAborted))
            return;

        if (pendingOff != null && hasRealUserParam)
        {
            var forceOffJson = JsonSerializer.Serialize(new
            {
                all = false,
                user = qUser,
                ip = MonitorDbService.NormalizeClientIp(rawIp),
                reason = pendingOff.Reason,
                timestamp = DateTime.UtcNow
            }, sseJsonOptions);
            if (!await WriteSseEventAsync(clientInfo, $"event: FORCE_PROXY_OFF\ndata: {forceOffJson}\n\n", context.RequestAborted))
                return;
        }

        while (!context.RequestAborted.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
            if (!await WriteSseEventAsync(clientInfo, $": keep-alive {DateTime.UtcNow:O}\n\n", context.RequestAborted))
                break;
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

    string serialized;
    string savedVersion;
    string savedUpdatedAt;
    lock (ruleLock)
    {
        if (newRules.Blacklist == null || newRules.Blacklist.Count == 0)
            newRules.Blacklist = ruleStore.Blacklist;
        if (newRules.InternalWhitelist == null || newRules.InternalWhitelist.Count == 0)
        {
            newRules.InternalWhitelist = (newRules.ConfiguredInternalWhitelist != null && newRules.ConfiguredInternalWhitelist.Count > 0)
                ? newRules.ConfiguredInternalWhitelist.ToList()
                : ruleStore.InternalWhitelist;
        }
        newRules.ConfiguredInternalWhitelist = null;
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

        if (newRules.MaxConcurrentSessionsPerUser <= 0)
            newRules.MaxConcurrentSessionsPerUser = ruleStore.MaxConcurrentSessionsPerUser > 0 ? Math.Clamp(ruleStore.MaxConcurrentSessionsPerUser, 1, 100) : 5;
        else
            newRules.MaxConcurrentSessionsPerUser = Math.Clamp(newRules.MaxConcurrentSessionsPerUser, 1, 100);

        if (newRules.MaxConcurrentSessionsAdmin <= 0)
            newRules.MaxConcurrentSessionsAdmin = ruleStore.MaxConcurrentSessionsAdmin > 0 ? Math.Clamp(ruleStore.MaxConcurrentSessionsAdmin, 1, 100) : 10;
        else
            newRules.MaxConcurrentSessionsAdmin = Math.Clamp(newRules.MaxConcurrentSessionsAdmin, 1, 100);

        newRules.ManualAdmins = (ruleStore.ManualAdmins ?? new List<AdminAccountEntry>())
            .Select(a => new AdminAccountEntry
            {
                Username = a.Username,
                Email = a.Email,
                FullName = a.FullName,
                Unit = a.Unit,
                Role = a.Role,
                AddedBy = a.AddedBy,
                CreatedAt = a.CreatedAt
            })
            .ToList();
        ruleStore = newRules;
        SaveRules(ruleStore);
        serialized = JsonSerializer.Serialize(BuildPublicRulesProjection(ruleStore), sseJsonOptions);
        savedVersion = ruleStore.Version;
        savedUpdatedAt = ruleStore.UpdatedAt;
    }

    var payload = $"event: RULES_UPDATED\ndata: {serialized}\n\n";

    int delivered = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        if (await WriteSseEventAsync(kvp.Value, payload))
        {
            delivered++;
        }
    }

    monitorDb.EnforceAllUserSessionLimits();

    return Results.Json(new
    {
        success = true,
        message = "Đã lưu và phát sóng tập luật thành công",
        version = savedVersion,
        updatedAt = savedUpdatedAt,
        broadcastedClients = delivered
    }, jsonOptions);
}

app.MapPost("/api/v1/admin/rules", async (HttpContext ctx, RuleStoreModel newRules) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;
    return await HandleSaveRulesAsync(newRules);
});
app.MapPost("/api/v1/rules", async (HttpContext ctx, RuleStoreModel newRules) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;
    return await HandleSaveRulesAsync(newRules);
});

// 5. API Kiểm thử Phân loại Routing cho 1 Domain bất kỳ (Hỗ trợ theo tài khoản User nếu đã xác thực)
app.MapPost("/api/v1/rules/evaluate", async (HttpContext ctx, EvaluateRequest req) =>
{
    string? evalUser = null;
    if (!string.IsNullOrWhiteSpace(req.User))
    {
        var (authErr, identity, adminRole) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: false);
        if (authErr == null && identity != null && (adminRole == "super_admin" || IsSameUser(req.User, identity.Username)))
        {
            evalUser = req.User;
        }
    }
    RouteDecision decision;
    lock (ruleLock) { decision = EvaluateHost(req.Host ?? "", ruleStore, evalUser); }
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

async Task<IResult> HandleLogoutAsync(HttpContext context)
{
    var clientIp = GetRealClientIp(context);
    var normLogoutIp = MonitorDbService.NormalizeClientIp(clientIp);
    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
    VerifiedTokenIdentity? id = null;
    if (!string.IsNullOrWhiteSpace(authHeader))
    {
        var rawTok = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader.Substring(7).Trim()
            : authHeader.Trim();
        id = await TryVerifyBearerTokenAsync(authHeader) ?? await TryExtractExpiredGenuineTokenAsync(authHeader);
        verifiedSessionTokens.TryRemove(rawTok, out _);
        if (id != null)
        {
            ClearPendingForceOff(id.Username, clientIp);
            if (!string.IsNullOrEmpty(id.Jti))
            {
                revokedTokenJtis[id.Jti] = DateTimeOffset.UtcNow;
            }
        }
    }
    string? logoutUser = id?.Username;
    monitorDb.UnregisterUserActivity(clientIp, logoutUser);
    foreach (var c in sseClients.Values)
    {
        if (!string.IsNullOrEmpty(normLogoutIp) &&
            MonitorDbService.NormalizeClientIp(c.RemoteIp) == normLogoutIp &&
            (string.IsNullOrEmpty(logoutUser) || IsSameUser(c.User, logoutUser)))
        {
            c.ProxyEnabled = false;
        }
    }
    return Results.Json(new { ok = true, clientIp, message = "Đã thu hồi phiên cán bộ thành công." }, jsonOptions);
}

// 6.1 API Đăng xuất - Thu hồi phiên hoạt động theo IP & Bearer token nếu có
app.MapPost("/api/v1/auth/logout", (Delegate)(Func<HttpContext, Task<IResult>>)HandleLogoutAsync);
app.MapPost("/api/v1/user/logout", (Delegate)(Func<HttpContext, Task<IResult>>)HandleLogoutAsync);

string NormalizeUsername(string? raw)
{
    var s = (raw ?? "").Trim().ToLowerInvariant();
    var atIdx = s.IndexOf('@');
    return atIdx >= 0 ? s.Substring(0, atIdx).Trim() : s;
}

string ResolveAdminRole(string? username, string? email, string? ou)
{
    var rawUser = (username ?? "").Trim().ToLowerInvariant();
    var cleanUser = NormalizeUsername(username);
    var cleanEmail = (email ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(cleanEmail) && rawUser.Contains('@'))
    {
        cleanEmail = rawUser;
    }
    var ouLower = (ou ?? "").Trim().ToLowerInvariant();

    if (ouLower.Contains("pqtkt") ||
        ouLower.Contains("ttqtdhs") ||
        ouLower.Contains("duac") ||
        cleanUser == "admin" ||
        cleanUser == "phongnd" ||
        cleanUser == "vuongwork" ||
        cleanEmail.Contains("vuongwork"))
    {
        return "super_admin";
    }

    lock (ruleLock)
    {
        var manualAdmins = ruleStore.ManualAdmins;
        if (manualAdmins != null)
        {
            var matched = manualAdmins.FirstOrDefault(a =>
            {
                var entryUser = NormalizeUsername(a.Username);
                var entryEmail = (a.Email ?? "").Trim().ToLowerInvariant();
                return (!string.IsNullOrEmpty(cleanUser) && !string.IsNullOrEmpty(entryUser) && entryUser == cleanUser) ||
                       (!string.IsNullOrEmpty(cleanEmail) && !string.IsNullOrEmpty(entryEmail) && entryEmail == cleanEmail);
            });

            if (matched != null)
            {
                return string.Equals(matched.Role?.Trim(), "super_admin", StringComparison.OrdinalIgnoreCase)
                    ? "super_admin"
                    : "report_viewer";
            }
        }
    }

    return "";
}

List<AdminAccountEntry> SnapshotManualAdminsLocked()
{
    return (ruleStore.ManualAdmins ?? new List<AdminAccountEntry>())
        .Select(a => new AdminAccountEntry
        {
            Username = a.Username,
            Email = a.Email,
            FullName = a.FullName,
            Unit = a.Unit,
            Role = a.Role,
            AddedBy = a.AddedBy,
            CreatedAt = a.CreatedAt
        })
        .ToList();
}

// 7. API Trao đổi Authorization Code lấy Token & UserInfo từ Keycloak Production
app.MapPost("/api/v1/auth/exchange", async (HttpContext context, ExchangeCodeRequest req, IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(req?.Code))
        return Results.BadRequest(new { error = "Thiếu authorization code từ Keycloak." });

    var redirectUri = string.IsNullOrWhiteSpace(req.RedirectUri)
        ? "https://vags.vnu.edu.vn/auth/callback"
        : req.RedirectUri.Trim();

    bool isAllowedRedirect = false;
    if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirUri))
    {
        var normalizedRedir = $"{redirUri.Scheme.ToLowerInvariant()}://{redirUri.Authority.ToLowerInvariant()}{redirUri.AbsolutePath}";
        var cleanPrimary = primaryDomain.Trim().TrimEnd('/').ToLowerInvariant();
        if (normalizedRedir == "https://thamkhao.vnu.edu.vn/auth/callback" ||
            normalizedRedir == "https://vags.vnu.edu.vn/auth/callback" ||
            (!string.IsNullOrEmpty(cleanPrimary) && normalizedRedir == $"https://{cleanPrimary}/auth/callback"))
        {
            isAllowedRedirect = true;
        }
        else if (isDevelopmentEnv &&
                 (redirUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || redirUri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)) &&
                 redirUri.AbsolutePath == "/auth/callback")
        {
            isAllowedRedirect = true;
        }
    }
    if (!isAllowedRedirect)
    {
        return Results.BadRequest(new { error = "RedirectUri không hợp lệ hoặc không thuộc danh sách tên miền VAGS cho phép." });
    }

    var client = httpClientFactory.CreateClient();
    var tokenEndpoint = $"{keycloakAuthority}/protocol/openid-connect/token";
    var userinfoEndpoint = $"{keycloakAuthority}/protocol/openid-connect/userinfo";

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
    var accessToken = tokenDoc.RootElement.TryGetProperty("access_token", out var atEl) ? (atEl.GetString() ?? "") : "";
    var idToken = tokenDoc.RootElement.TryGetProperty("id_token", out var idtEl) ? (idtEl.GetString() ?? "") : "";
    var expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out var expSec) ? expSec : 300;

    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return Results.Json(new { error = "Keycloak không trả về access_token hợp lệ" }, statusCode: 502);
    }

    // Lấy thông tin UserInfo từ Keycloak (fail closed nếu lỗi)
    var reqMessage = new HttpRequestMessage(HttpMethod.Get, userinfoEndpoint);
    reqMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    var userInfoResp = await client.SendAsync(reqMessage);

    if (!userInfoResp.IsSuccessStatusCode)
    {
        var failStatus = userInfoResp.StatusCode == HttpStatusCode.Unauthorized ? 401 : 502;
        return Results.Json(new { error = "Không thể xác thực thông tin người dùng từ Keycloak UserInfo." }, statusCode: failStatus);
    }

    var userInfoContent = await userInfoResp.Content.ReadAsStringAsync();
    using var userDoc = JsonDocument.Parse(userInfoContent);
    var root = userDoc.RootElement;

    static string ExtractClaimString(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var el)) return "";
        if (el.ValueKind == JsonValueKind.String) return (el.GetString() ?? "").Trim();
        if (el.ValueKind == JsonValueKind.Array)
        {
            var items = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = (item.GetString() ?? "").Trim();
                    if (!string.IsNullOrEmpty(s)) items.Add(s);
                }
            }
            return string.Join(",", items);
        }
        return "";
    }

    var username = ExtractClaimString(root, "preferred_username");
    if (string.IsNullOrWhiteSpace(username))
        username = ExtractClaimString(root, "sub");
    if (string.IsNullOrWhiteSpace(username))
    {
        return Results.Json(new { error = "Keycloak UserInfo thiếu preferred_username." }, statusCode: 401);
    }

    var name = ExtractClaimString(root, "name");
    if (string.IsNullOrWhiteSpace(name)) name = username;
    var email = ExtractClaimString(root, "email");
    if (string.IsNullOrWhiteSpace(email)) email = $"{username}@vnu.edu.vn";
    var organizationalUnit = ExtractClaimString(root, "organizational_unit");
    if (string.IsNullOrEmpty(organizationalUnit))
        organizationalUnit = ExtractClaimString(root, "vnu_admin_unit");
    if (string.IsNullOrEmpty(organizationalUnit))
        organizationalUnit = ExtractClaimString(root, "ou");
    if (string.IsNullOrEmpty(organizationalUnit))
        organizationalUnit = ExtractClaimString(root, "department");
    var memberType = ExtractClaimString(root, "vnu_member_type");

    if (IsUserDenied(username))
    {
        return Results.Json(new { error = "Tài khoản đã bị Quản trị viên khoá quyền sử dụng Proxy VAGS." }, statusCode: 403);
    }

    // Phân quyền Quản trị viên (2 cấp: super_admin & report_viewer)
    var adminRole = ResolveAdminRole(username, email, organizationalUnit);
    bool isAdmin = !string.IsNullOrEmpty(adminRole);

    if (adminRole == "super_admin" && (string.IsNullOrEmpty(organizationalUnit) || organizationalUnit == "VNU"))
    {
        organizationalUnit = "PQTKT";
    }

    var safeDbUnit = MonitorDbService.IsValidUnit(organizationalUnit)
        ? organizationalUnit.Trim()
        : MonitorDbService.NormalizeUnit(organizationalUnit);
    var effectiveMemberType = string.IsNullOrWhiteSpace(memberType) ? "canbo" : memberType.Trim().ToLowerInvariant();

    // Đăng ký hoạt động và IP của Cán bộ vào hệ thống SQLite Monitor
    var clientIp = GetRealClientIp(context);
    ClearPendingForceOff(username, clientIp);
    monitorDb.RegisterUserActivity(clientIp, username, safeDbUnit, effectiveMemberType, proxyEnabled: true);

    // Ký VAGS Session JWT (7 ngày) & lưu cache phiên đã xác thực
    var sessionToken = CreateSignedSessionToken(username, name, email, organizationalUnit, effectiveMemberType, isAdmin, adminRole);
    var nowUtc = DateTimeOffset.UtcNow;
    if (verifiedSessionTokens.Count > 5000)
    {
        foreach (var kvp in verifiedSessionTokens.ToArray())
        {
            if (kvp.Value.ExpiresAtUtc < nowUtc) verifiedSessionTokens.TryRemove(kvp.Key, out _);
        }
    }
    verifiedSessionTokens[sessionToken] = new VerifiedSessionEntry(
        username, email, organizationalUnit, effectiveMemberType, null, nowUtc, nowUtc.AddDays(7));
    verifiedSessionTokens[accessToken] = new VerifiedSessionEntry(
        username, email, organizationalUnit, effectiveMemberType, null, nowUtc, nowUtc.AddSeconds(Math.Clamp(expiresIn, 60, 43200)));

    return Results.Json(new
    {
        success = true,
        accessToken = accessToken,
        sessionToken = sessionToken,
        idToken = idToken,
        expiresIn = expiresIn,
        sessionExpiresIn = 604800,
        user = username,
        name = name,
        email = email,
        organizationalUnit = organizationalUnit,
        memberType = memberType,
        isAdmin = isAdmin,
        adminRole = adminRole,
        issuer = keycloakAuthority
    }, jsonOptions);
});

// 7. API Cấp Token SSO Giả lập / Kiểm thử cho Extension & Dashboard (Chỉ cho phép trong Development)
app.MapPost("/api/v1/auth/demo-token", (HttpContext context, DemoTokenRequest? req) =>
{
    if (!isDevelopmentEnv)
    {
        return Results.Json(new { error = "Demo token endpoint is disabled in Production." }, statusCode: 404);
    }

    var username = string.IsNullOrWhiteSpace(req?.Username) ? "phongnd" : req!.Username!.Trim();
    if (!MonitorDbService.IsValidUsername(username))
    {
        return Results.BadRequest(new { error = "Tên tài khoản không hợp lệ." });
    }
    var unit = string.IsNullOrWhiteSpace(req?.OrganizationalUnit)
        ? (username.Contains("phongnd", StringComparison.OrdinalIgnoreCase) ? "pqtkt" : "general")
        : req!.OrganizationalUnit!.Trim();
    if (!MonitorDbService.IsValidUnit(unit))
    {
        return Results.BadRequest(new { error = "Đơn vị không hợp lệ." });
    }
    var email = username.Contains('@') ? username : $"{username}@vnu.edu.vn";
    var adminRole = ResolveAdminRole(username, email, unit);
    bool isAdmin = !string.IsNullOrEmpty(adminRole);

    var clientIp = GetRealClientIp(context);
    ClearPendingForceOff(username, clientIp);
    monitorDb.RegisterUserActivity(clientIp, username, unit, "canbo", proxyEnabled: true);

    var displayName = username == "phongnd" ? "Nguyễn Đức Phong (P. Quản trị & Kế hoạch)" : username;
    var sessionToken = CreateSignedSessionToken(username, displayName, email, unit, "canbo", isAdmin, adminRole);
    var nowUtc = DateTimeOffset.UtcNow;
    verifiedSessionTokens[sessionToken] = new VerifiedSessionEntry(
        username, email, unit, "canbo", null, nowUtc, nowUtc.AddDays(7));

    return Results.Json(new
    {
        accessToken = sessionToken,
        sessionToken = sessionToken,
        tokenType = "Bearer",
        expiresIn = 604800,
        user = username,
        name = username == "phongnd" ? "Nguyễn Đức Phong" : username,
        organizationalUnit = unit,
        isAdmin = isAdmin,
        adminRole = adminRole,
        issuer = "https://vags.vnu.edu.vn/vags-sess"
    }, jsonOptions);
});

// 7.1 API Kiểm tra quyền Admin & Role RBAC hiện tại
app.MapGet("/api/v1/auth/check-admin", async (HttpContext context) =>
{
    var user = context.Request.Query["user"].FirstOrDefault();
    var email = context.Request.Query["email"].FirstOrDefault();
    var ou = context.Request.Query["ou"].FirstOrDefault();

    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
    bool hasBearer = !string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    if (hasBearer)
    {
        var identity = await TryVerifyBearerTokenAsync(authHeader);
        if (identity != null)
        {
            var verifiedRole = ResolveAdminRole(identity.Username, identity.Email, identity.Unit);
            return Results.Json(new
            {
                isAdmin = !string.IsNullOrEmpty(verifiedRole),
                adminRole = verifiedRole,
                user = identity.Username,
                ou = identity.Unit,
                tokenPresent = true,
                tokenValid = true,
                tokenExpired = false
            }, jsonOptions);
        }

        var queryRole = ResolveAdminRole(user, email, ou);
        return Results.Json(new
        {
            isAdmin = !string.IsNullOrEmpty(queryRole),
            adminRole = queryRole,
            user = user ?? "",
            ou = ou ?? "",
            tokenPresent = true,
            tokenValid = false,
            tokenExpired = true
        }, jsonOptions);
    }

    var role = ResolveAdminRole(user, email, ou);
    return Results.Json(new
    {
        isAdmin = !string.IsNullOrEmpty(role),
        adminRole = role,
        user = user ?? "",
        ou = ou ?? "",
        tokenPresent = false,
        tokenValid = false,
        tokenExpired = false
    }, jsonOptions);
});

// 7.2 API Danh sách tài khoản Admin gốc & Admin cấp quyền thủ công
app.MapGet("/api/v1/admin/accounts", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var rootAdmins = new[]
    {
        new { identifier = "ou=pqtkt", label = "Phòng Quản trị & Kế hoạch (ou=pqtkt)", role = "super_admin", type = "ou" },
        new { identifier = "ou=ttqtdhs", label = "Trung tâm Quản trị Đại học số (ou=ttqtdhs)", role = "super_admin", type = "ou" },
        new { identifier = "ou=duac", label = "Trung tâm Ứng dụng CNTT / DUAC (ou=duac)", role = "super_admin", type = "ou" },
        new { identifier = "phongnd", label = "phongnd@vnu.edu.vn (Nguyễn Đức Phong - PQTKT)", role = "super_admin", type = "user" },
        new { identifier = "vuongwork", label = "vuongwork@vnu.edu.vn (Root Admin)", role = "super_admin", type = "user" },
        new { identifier = "admin", label = "admin (Tài khoản Quản trị hệ thống)", role = "super_admin", type = "user" }
    };

    List<AdminAccountEntry> manualAdminsSnapshot;
    lock (ruleLock)
    {
        manualAdminsSnapshot = SnapshotManualAdminsLocked();
    }

    return Results.Json(new
    {
        ok = true,
        rootAdmins = rootAdmins,
        manualAdmins = manualAdminsSnapshot
    }, jsonOptions);
});

// 7.3 API Thêm mới hoặc cập nhật vai trò tài khoản Admin thủ công
app.MapPost("/api/v1/admin/accounts", async (HttpContext ctx, ManualAdminUpsertRequest? req) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    if (req == null || (string.IsNullOrWhiteSpace(req.Username) && string.IsNullOrWhiteSpace(req.Email)))
    {
        return Results.BadRequest(new { ok = false, message = "Vui lòng nhập tên tài khoản hoặc email VNU." });
    }

    var rawUser = (req.Username ?? "").Trim().ToLowerInvariant();
    var rawEmail = (req.Email ?? "").Trim().ToLowerInvariant();

    var cleanUser = NormalizeUsername(rawUser);
    if (string.IsNullOrWhiteSpace(cleanUser) && !string.IsNullOrWhiteSpace(rawEmail))
    {
        cleanUser = NormalizeUsername(rawEmail);
    }

    if (string.IsNullOrWhiteSpace(cleanUser) || !MonitorDbService.IsValidUsername(cleanUser))
    {
        return Results.BadRequest(new { ok = false, message = "Tên tài khoản không hợp lệ." });
    }

    var explicitEmail = !string.IsNullOrWhiteSpace(rawEmail)
        ? rawEmail
        : (rawUser.Contains('@') ? rawUser : "");
    if (!string.IsNullOrWhiteSpace(explicitEmail) && !explicitEmail.Contains('@'))
    {
        explicitEmail = $"{explicitEmail}@vnu.edu.vn";
    }
    var cleanEmail = !string.IsNullOrWhiteSpace(explicitEmail) ? explicitEmail : $"{cleanUser}@vnu.edu.vn";

    var cleanRole = string.Equals(req.Role?.Trim(), "super_admin", StringComparison.OrdinalIgnoreCase)
        ? "super_admin"
        : "report_viewer";

    AdminAccountEntry entry;
    List<AdminAccountEntry> manualAdminsSnapshot;
    bool isUpdate = false;

    lock (ruleLock)
    {
        ruleStore.ManualAdmins ??= new List<AdminAccountEntry>();
        var idx = ruleStore.ManualAdmins.FindIndex(a =>
            NormalizeUsername(a.Username) == cleanUser ||
            (!string.IsNullOrEmpty(explicitEmail) && string.Equals((a.Email ?? "").Trim(), explicitEmail, StringComparison.OrdinalIgnoreCase)));

        if (idx >= 0)
        {
            isUpdate = true;
            var existing = ruleStore.ManualAdmins[idx];
            entry = new AdminAccountEntry
            {
                Username = cleanUser,
                Email = !string.IsNullOrWhiteSpace(explicitEmail)
                    ? explicitEmail
                    : (!string.IsNullOrWhiteSpace(existing.Email) ? existing.Email : cleanEmail),
                FullName = !string.IsNullOrWhiteSpace(req.FullName) ? req.FullName.Trim() : existing.FullName,
                Unit = !string.IsNullOrWhiteSpace(req.Unit) ? req.Unit.Trim() : existing.Unit,
                Role = cleanRole,
                AddedBy = !string.IsNullOrWhiteSpace(req.AddedBy) ? req.AddedBy.Trim() : existing.AddedBy,
                CreatedAt = !string.IsNullOrWhiteSpace(existing.CreatedAt) ? existing.CreatedAt : DateTime.UtcNow.ToString("O")
            };
            ruleStore.ManualAdmins[idx] = entry;
        }
        else
        {
            entry = new AdminAccountEntry
            {
                Username = cleanUser,
                Email = cleanEmail,
                FullName = (req.FullName ?? "").Trim(),
                Unit = (req.Unit ?? "").Trim(),
                Role = cleanRole,
                AddedBy = string.IsNullOrWhiteSpace(req.AddedBy) ? "admin" : req.AddedBy.Trim(),
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
            ruleStore.ManualAdmins.Add(entry);
        }

        SaveRules(ruleStore);
        manualAdminsSnapshot = SnapshotManualAdminsLocked();
    }

    var roleLabel = cleanRole == "super_admin" ? "Admin Toàn Quyền" : "Admin Xem Báo Cáo";
    var msg = isUpdate
        ? $"Đã cập nhật quyền '{roleLabel}' cho tài khoản '{cleanUser}'."
        : $"Đã cấp quyền '{roleLabel}' cho tài khoản '{cleanUser}'.";

    return Results.Json(new
    {
        ok = true,
        message = msg,
        account = entry,
        manualAdmins = manualAdminsSnapshot
    }, jsonOptions);
});

// 7.4 API Thu hồi quyền Admin thủ công
app.MapDelete("/api/v1/admin/accounts/{username}", async (HttpContext ctx, string username) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var rawTarget = (username ?? "").Trim().ToLowerInvariant();
    var cleanTarget = NormalizeUsername(username);
    if (string.IsNullOrWhiteSpace(cleanTarget))
    {
        return Results.BadRequest(new { ok = false, message = "Tên tài khoản không hợp lệ." });
    }

    List<AdminAccountEntry> manualAdminsSnapshot;
    lock (ruleLock)
    {
        ruleStore.ManualAdmins ??= new List<AdminAccountEntry>();
        int removed = ruleStore.ManualAdmins.RemoveAll(a =>
            NormalizeUsername(a.Username) == cleanTarget ||
            (!string.IsNullOrEmpty(rawTarget) && string.Equals((a.Email ?? "").Trim(), rawTarget, StringComparison.OrdinalIgnoreCase)));

        if (removed == 0)
        {
            return Results.NotFound(new
            {
                ok = false,
                message = $"Không tìm thấy tài khoản admin thủ công '{username}'."
            });
        }

        SaveRules(ruleStore);
        manualAdminsSnapshot = SnapshotManualAdminsLocked();
    }

    return Results.Json(new
    {
        ok = true,
        message = $"Đã thu hồi quyền Admin thủ công của tài khoản '{cleanTarget}'.",
        manualAdmins = manualAdminsSnapshot
    }, jsonOptions);
});

// 7. API Kiểm tra IP & Chống Rò Rỉ Mạng (IP Leak Check)
app.MapGet("/api/v1/ip-check", (HttpContext http) =>
{
    var clientIp = GetRealClientIp(http);
    var (resolvedUser, _, _) = monitorDb.ResolveUser(clientIp, null, null, null);
    var isAuthUser = !string.IsNullOrEmpty(resolvedUser) && resolvedUser != "Khách (Chưa xác thực)";
    var isVnu = isAuthUser || clientIp.StartsWith("112.137.") || clientIp.StartsWith("10.241.") || clientIp.StartsWith("172.16.") || clientIp == serverIp || clientIp == "112.137.142.30" || clientIp == "112.137.133.14";

    return Results.Json(new
    {
        clientIp = isVnu ? serverIp : clientIp,
        realClientIp = clientIp,
        isVnuNetwork = isVnu,
        vnuEgressIp = serverIp,
        status = isVnu ? "VNU Internal / Tunneled Network" : "External Direct Network",
        gateway = $"{primaryDomain}:443",
        timestamp = DateTime.UtcNow.ToString("O")
    }, jsonOptions);
});

// 8. API Báo cáo Tổng quan Admin Dashboard & Phân tích Lưu lượng SQLite
app.MapGet("/api/v1/admin/dashboard", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    string ver, mode;
    lock (ruleLock) { ver = ruleStore.Version; mode = ruleStore.ProxyMode; }
    var data = await monitorDb.GetAnalyticsSummaryAsync(ver, mode, serverIp);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/analytics", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    string ver, mode;
    lock (ruleLock) { ver = ruleStore.Version; mode = ruleStore.ProxyMode; }
    var data = await monitorDb.GetAnalyticsSummaryAsync(ver, mode, serverIp);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/top-urls", async (HttpContext ctx, int? limit) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var data = await monitorDb.GetTopUrlsAsync(limit ?? 10);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/top-users", async (HttpContext ctx, int? limit) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var data = await monitorDb.GetTopUsersAsync(limit ?? 10);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/top-ips", async (HttpContext ctx, int? limit) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var data = await monitorDb.GetTopIpsAsync(limit ?? 10);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/top-bandwidth-urls", async (HttpContext ctx, int? limit) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var data = await monitorDb.GetTopBandwidthUrlsAsync(limit ?? 10);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/admin/audit-logs", async (HttpContext ctx, string? search, string? user, string? ip, string? domain, string? status, string? allowed, int? limit, int? offset) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var clampedLimit = Math.Clamp(limit ?? 50, 1, 5000);
    var clampedOffset = Math.Max(0, offset ?? 0);
    var (logs, total) = await monitorDb.GetAuditLogsAsync(search, user, ip, domain, status, allowed, clampedLimit, clampedOffset);
    return Results.Json(new { logs, total, limit = clampedLimit, offset = clampedOffset }, jsonOptions);
});

app.MapGet("/api/v1/admin/domains-breakdown", async (HttpContext ctx, string? search, string? category, int? limit, int? offset) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var clampedLimit = Math.Clamp(limit ?? 50, 1, 5000);
    var clampedOffset = Math.Max(0, offset ?? 0);
    var (domains, total) = await monitorDb.GetDomainBreakdownAsync(search, category, clampedLimit, clampedOffset);
    return Results.Json(new { domains, total, limit = clampedLimit, offset = clampedOffset }, jsonOptions);
});

app.MapGet("/api/v1/admin/audit-logs/export", async (HttpContext ctx, int? limit) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var csv = await monitorDb.ExportAuditLogsCsvAsync(limit ?? 1000);
    ctx.Response.Headers.ContentDisposition = "attachment; filename=\"vags_audit_logs.csv\"";
    return Results.Content(csv, "text/csv; charset=utf-8", Encoding.UTF8);
});

// ================= BÁO CÁO & THỐNG KÊ 4 CSDL ĐIỆN TỬ VNU =================
(DateTime? FromUtc, DateTime? ToUtc) ParseVnuDateRange(string? fromQuery, string? toQuery)
{
    var vnOffset = TimeSpan.FromHours(7);
    DateTime? fromUtc = null;
    DateTime? toUtc = null;

    if (!string.IsNullOrWhiteSpace(fromQuery))
    {
        var s = fromQuery.Trim();
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            if (s.Length == 10) // "yyyy-MM-dd"
            {
                // Start of Vietnam day (00:00:00 VN) -> convert to UTC (-7h)
                var dto = new DateTimeOffset(dt.Year, dt.Month, dt.Day, 0, 0, 0, vnOffset);
                fromUtc = dto.UtcDateTime;
            }
            else
            {
                fromUtc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            }
        }
    }

    if (!string.IsNullOrWhiteSpace(toQuery))
    {
        var s = toQuery.Trim();
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            if (s.Length == 10) // "yyyy-MM-dd"
            {
                // End of Vietnam day (23:59:59.999 VN) -> convert to UTC (-7h)
                var dto = new DateTimeOffset(dt.Year, dt.Month, dt.Day, 23, 59, 59, 999, vnOffset);
                toUtc = dto.UtcDateTime;
            }
            else
            {
                toUtc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            }
        }
    }

    return (fromUtc, toUtc);
}

app.MapGet("/api/v1/analytics/csdl-stats", async (HttpContext ctx) =>
{
    var (from, to) = ParseVnuDateRange(ctx.Request.Query["from"], ctx.Request.Query["to"]);

    var stats = await monitorDb.GetCsdlMatrixStatsAsync(from, to);
    return Results.Json(stats, jsonOptions);
});

app.MapGet("/api/v1/analytics/export-excel", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true, allowReportViewer: true);
    if (authErr != null) return authErr;

    var (from, to) = ParseVnuDateRange(ctx.Request.Query["from"], ctx.Request.Query["to"]);

    var statsObj = await monitorDb.GetCsdlMatrixStatsAsync(from, to);
    var statsJson = JsonSerializer.Serialize(statsObj);
    using var doc = JsonDocument.Parse(statsJson);
    var root = doc.RootElement;

    var vnNow = DateTime.UtcNow.AddHours(7);
    var fromDateDisplay = from.HasValue ? from.Value.AddHours(7).ToString("dd/MM/yyyy") : "08/11/2026";
    var toDateDisplay = to.HasValue ? to.Value.AddHours(7).ToString("dd/MM/yyyy") : vnNow.ToString("dd/MM/yyyy");
    var dateTitle = (fromDateDisplay == toDateDisplay)
        ? $"THỐNG KÊ SỐ LƯỢNG TRUY CẬP CSDL ĐIỆN TỬ NGÀY {fromDateDisplay}"
        : $"THỐNG KÊ SỐ LƯỢNG TRUY CẬP CSDL ĐIỆN TỬ TỪ NGÀY {fromDateDisplay} ĐẾN NGÀY {toDateDisplay}";

    var tmpPayload = Path.Combine(Path.GetTempPath(), $"vags_csdl_req_{Guid.NewGuid():N}.json");
    var tmpOutput = Path.Combine(Path.GetTempPath(), $"vags_csdl_export_{Guid.NewGuid():N}.xlsx");
    var pyScriptPath = File.Exists(Path.Combine(AppContext.BaseDirectory, "export_csdl_excel.py"))
        ? Path.Combine(AppContext.BaseDirectory, "export_csdl_excel.py")
        : "/mnt/data/websites/vags.vnu.edu.vn/backend/export_csdl_excel.py";

    try
    {
        var pyPayload = new
        {
            title = dateTitle,
            from_date = fromDateDisplay,
            to_date = toDateDisplay,
            matrix = root.GetProperty("matrix"),
            by_unit_users = root.GetProperty("byUnitUsers"),
            by_unit_hoc_users = root.GetProperty("byUnitHocUsers"),
            by_unit_bo_users = root.GetProperty("byUnitBoUsers"),
            by_unit_hoc_requests = root.GetProperty("byUnitHocRequests"),
            by_unit_bo_requests = root.GetProperty("byUnitBoRequests"),
            by_unit_stats = root.GetProperty("byUnitStats"),
            by_database_stats = root.GetProperty("byDatabaseStats"),
            by_member_type_users = root.GetProperty("byMemberTypeUsers"),
            unique_users = root.GetProperty("uniqueUsers"),
            total_requests = root.GetProperty("totalRequests"),
            output_path = tmpOutput
        };
        await File.WriteAllTextAsync(tmpPayload, JsonSerializer.Serialize(pyPayload));

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = $"\"{pyScriptPath}\" \"{tmpPayload}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync();
        }

        if (File.Exists(tmpOutput))
        {
            var bytes = await File.ReadAllBytesAsync(tmpOutput);
            var downloadName = $"Bao_cao_CSDL_VNU_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx";
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", downloadName);
        }
        return Results.Problem("Không thể sinh tệp báo cáo Excel");
    }
    finally
    {
        try { if (File.Exists(tmpPayload)) File.Delete(tmpPayload); } catch { }
        try { if (File.Exists(tmpOutput)) File.Delete(tmpOutput); } catch { }
    }
});

app.MapPost("/api/v1/admin/toggle-lic", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    bool newEnableLic;
    string newVer;
    string serialized;
    lock (ruleLock)
    {
        if (bool.TryParse(ctx.Request.Query["enable"], out var targetEnable))
        {
            ruleStore.EnableLicProxy = targetEnable;
        }
        else
        {
            ruleStore.EnableLicProxy = !ruleStore.EnableLicProxy;
        }
        SaveRules(ruleStore);
        newEnableLic = ruleStore.EnableLicProxy;
        newVer = ruleStore.Version;
        serialized = JsonSerializer.Serialize(BuildPublicRulesProjection(ruleStore), sseJsonOptions);
    }

    var rulesPayload = $"event: RULES_UPDATED\ndata: {serialized}\n\n";
    foreach (var kvp in sseClients.ToArray())
    {
        await WriteSseEventAsync(kvp.Value, rulesPayload);
    }
    return Results.Json(new { success = true, enableLicProxy = newEnableLic, version = newVer }, jsonOptions);
});

async Task<IResult> HandleUserHeartbeatAsync(HttpContext ctx, string? rawUser, string? rawUnit, string? rawMemberType, bool proxyEnabled)
{
    var clientIp = GetRealClientIp(ctx);
    var u = (rawUser ?? "").Trim();
    var unit = string.IsNullOrWhiteSpace(rawUnit) ? "VNU" : rawUnit.Trim();
    var memberType = string.IsNullOrWhiteSpace(rawMemberType) ? "canbo" : rawMemberType.Trim();

    if (!string.IsNullOrWhiteSpace(u) && u != "Cán bộ VNU")
    {
        if (!MonitorDbService.IsValidUsername(u))
        {
            return Results.BadRequest(new { ok = false, error = "Invalid username format" });
        }
        if (!MonitorDbService.IsValidUnit(unit))
        {
            return Results.BadRequest(new { ok = false, error = "Invalid unit format" });
        }

        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        VerifiedTokenIdentity? bearerId = null;
        bool bearerMatches = false;
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            bearerId = await TryVerifyBearerTokenAsync(authHeader);
            if (bearerId == null)
            {
                var expiredId = await TryExtractExpiredGenuineTokenAsync(authHeader);
                if (expiredId != null && IsSameUser(u, expiredId.Username))
                {
                    if (!proxyEnabled)
                    {
                        return Results.Json(new { ok = true, clientIp, user = u, proxyEnabled = false, tokenExpired = true }, jsonOptions);
                    }
                    monitorDb.UpdateUserProxyStatus(clientIp, u, false);
                    var expReason = "Phiên đăng nhập VNU SSO đã hết hạn. Hệ thống tự động tắt Proxy — vui lòng đăng nhập lại tại thamkhao.vnu.edu.vn.";
                    await SendTargetedForceProxyOffAsync(u, new[] { clientIp }, expReason, recordTombstone: false);
                    return Results.Json(new
                    {
                        ok = true,
                        clientIp,
                        user = u,
                        proxyEnabled = false,
                        forceProxyOff = true,
                        tokenExpired = true,
                        reason = expReason
                    }, jsonOptions);
                }

                if (IsUserDenied(u) && proxyEnabled)
                {
                    await SendTargetedForceProxyOffAsync(u, new[] { clientIp }, "Tài khoản đã bị Quản trị viên khoá quyền sử dụng Proxy VAGS.", recordTombstone: false);
                }
                return Results.Json(new { ok = false, error = "Invalid Bearer token", tokenExpired = true, forceProxyOff = true }, statusCode: 401);
            }
            if (!IsSameUser(u, bearerId.Username))
            {
                return Results.Json(new { ok = false, error = "Bearer token does not match target user" }, statusCode: 403);
            }
            bearerMatches = true;
        }

        var effectiveMemberType = bearerMatches
            ? bearerId!.MemberType
            : (string.Equals(monitorDb.GetLatestKnownMemberType(u, clientIp), "sinhvien", StringComparison.OrdinalIgnoreCase) ? "sinhvien" : memberType);

        if (!proxyEnabled)
        {
            ClearPendingForceOff(u, clientIp);
            if (!HasOtherActiveSseClient(u, clientIp))
            {
                monitorDb.RegisterUserActivity(clientIp, u, unit, effectiveMemberType, proxyEnabled: false);
            }
            return Results.Json(new { ok = true, clientIp, user = u, proxyEnabled = false }, jsonOptions);
        }

        if (TryGetPendingForceOff(u, clientIp, out var pendingOff))
        {
            await SendTargetedForceProxyOffAsync(u, new[] { clientIp }, pendingOff!.Reason, recordTombstone: false);
            return Results.Json(new
            {
                ok = true,
                clientIp,
                user = u,
                proxyEnabled = false,
                forceProxyOff = true,
                reason = pendingOff.Reason
            }, jsonOptions);
        }

        monitorDb.RegisterUserActivity(clientIp, u, unit, effectiveMemberType, proxyEnabled: true);
        var normHbIp = MonitorDbService.NormalizeClientIp(clientIp);
        foreach (var c in sseClients.Values)
        {
            if (MonitorDbService.NormalizeClientIp(c.RemoteIp) == normHbIp &&
                (IsSameUser(c.User, u) || string.Equals(c.User, "Khách (Chưa xác thực)", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(c.User)))
            {
                c.User = u;
                c.Unit = unit.ToUpperInvariant();
                c.ProxyEnabled = true;
            }
        }
    }

    return Results.Json(new { ok = true, clientIp, user = u, proxyEnabled }, jsonOptions);
}

// API Ghi nhận nhịp tim hoạt động (Heartbeat) của Cán bộ từ Extension
app.MapPost("/api/v1/user/heartbeat", async (HttpContext ctx, HeartbeatRequest? req) =>
{
    var u = req?.User ?? ctx.Request.Query["user"].FirstOrDefault() ?? "";
    var unit = req?.Unit ?? ctx.Request.Query["unit"].FirstOrDefault() ?? "VNU";
    var memberType = req?.MemberType ?? ctx.Request.Query["memberType"].FirstOrDefault() ?? "canbo";
    var proxyEnabled = req?.ProxyEnabled ?? !string.Equals(ctx.Request.Query["proxyEnabled"], "false", StringComparison.OrdinalIgnoreCase);
    return await HandleUserHeartbeatAsync(ctx, u, unit, memberType, proxyEnabled);
});

// API Cập nhật trạng thái Bật/Tắt Proxy tức thì của Extension
app.MapPost("/api/v1/user/status", async (HttpContext ctx, UserStatusRequest? req) =>
{
    var clientIp = GetRealClientIp(ctx);
    var u = (req?.User ?? "").Trim();
    var proxyEnabled = req?.ProxyEnabled == true;
    if (!string.IsNullOrWhiteSpace(u) && u != "Cán bộ VNU")
    {
        if (!MonitorDbService.IsValidUsername(u))
        {
            return Results.BadRequest(new { ok = false, error = "Invalid username format" });
        }

        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        VerifiedTokenIdentity? bearerId = null;
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            bearerId = await TryVerifyBearerTokenAsync(authHeader);
            if (bearerId == null)
            {
                if (proxyEnabled)
                {
                    var expiredId = await TryExtractExpiredGenuineTokenAsync(authHeader);
                    if (expiredId != null && IsSameUser(u, expiredId.Username))
                    {
                        monitorDb.UpdateUserProxyStatus(clientIp, u, false);
                        await SendTargetedForceProxyOffAsync(u, new[] { clientIp }, "Phiên đăng nhập VNU SSO đã hết hạn. Vui lòng đăng nhập lại tại thamkhao.vnu.edu.vn.", recordTombstone: false);
                    }
                }
                return Results.Json(new { ok = false, error = "Invalid or expired Bearer token", tokenExpired = true, forceProxyOff = true }, statusCode: 401);
            }
            if (!IsSameUser(u, bearerId.Username))
                return Results.Json(new { ok = false, error = "Bearer token does not match target user" }, statusCode: 403);
        }

        ClearPendingForceOff(u, clientIp);
        var normStatusIp = MonitorDbService.NormalizeClientIp(clientIp);

        if (proxyEnabled)
        {
            string unitVal = "VNU";
            string effectiveMemberType = "canbo";
            if (bearerId != null && IsSameUser(u, bearerId.Username))
            {
                unitVal = string.IsNullOrWhiteSpace(bearerId.Unit) ? "VNU" : bearerId.Unit;
                effectiveMemberType = string.IsNullOrWhiteSpace(bearerId.MemberType) ? "canbo" : bearerId.MemberType;
            }
            else if (!string.IsNullOrEmpty(normStatusIp) && monitorDb.ActiveIpUserMap.TryGetValue(normStatusIp, out var curSess))
            {
                if (!string.IsNullOrWhiteSpace(curSess.Unit)) unitVal = curSess.Unit;
                if (!string.IsNullOrWhiteSpace(curSess.MemberType)) effectiveMemberType = curSess.MemberType;
            }
            else
            {
                var knownMt = monitorDb.GetLatestKnownMemberType(u, clientIp);
                if (string.Equals(knownMt, "sinhvien", StringComparison.OrdinalIgnoreCase)) effectiveMemberType = "sinhvien";
            }

            monitorDb.RegisterUserActivity(clientIp, u, unitVal, effectiveMemberType, proxyEnabled: true);
            foreach (var c in sseClients.Values)
            {
                if (MonitorDbService.NormalizeClientIp(c.RemoteIp) == normStatusIp &&
                    (IsSameUser(c.User, u) || string.Equals(c.User, "Khách (Chưa xác thực)", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(c.User)))
                {
                    c.User = u;
                    c.Unit = unitVal.ToUpperInvariant();
                    c.ProxyEnabled = true;
                }
            }
        }
        else
        {
            monitorDb.UpdateUserProxyStatus(clientIp, u, false);
            foreach (var c in sseClients.Values)
            {
                if (MonitorDbService.NormalizeClientIp(c.RemoteIp) == normStatusIp && IsSameUser(c.User, u))
                {
                    c.ProxyEnabled = false;
                }
            }
        }
    }
    return Results.Json(new { ok = true, clientIp, user = u, proxyEnabled }, jsonOptions);
});

app.MapGet("/api/v1/user/heartbeat", async (HttpContext ctx) =>
{
    var u = ctx.Request.Query["user"].FirstOrDefault() ?? "";
    var unit = ctx.Request.Query["unit"].FirstOrDefault() ?? "VNU";
    var memberType = ctx.Request.Query["memberType"].FirstOrDefault() ?? "canbo";
    var isEnabled = !string.Equals(ctx.Request.Query["proxyEnabled"], "false", StringComparison.OrdinalIgnoreCase);
    return await HandleUserHeartbeatAsync(ctx, u, unit, memberType, isEnabled);
});

// 9. API Trạng thái Hệ thống & Giám sát Real-time
app.MapGet("/api/v1/health", () =>
{
    bool isMaint;
    string ver, mode;
    lock (ruleLock)
    {
        isMaint = CheckMaintenanceMode(ruleStore);
        ver = ruleStore.Version;
        mode = ruleStore.ProxyMode;
    }
    return Results.Json(new
    {
        status = isMaint ? "maintenance" : "healthy",
        version = ver,
        proxyMode = mode,
        egressIp = serverIp,
        primaryDomain = primaryDomain,
        maintenanceMode = isMaint
    }, jsonOptions);
});

app.MapGet("/api/v1/status", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    bool isMaint;
    string ver, authMode, kcAuth, proxyEndpoint;
    lock (ruleLock)
    {
        isMaint = CheckMaintenanceMode(ruleStore);
        ver = ruleStore.Version;
        authMode = ruleStore.AuthMode;
        kcAuth = ruleStore.KeycloakAuthority;
        proxyEndpoint = $"{ruleStore.ProxyScheme} {ruleStore.ProxyHost}:{ruleStore.ProxyPort}";
    }

    return Results.Json(new
    {
        service = "VNU Academic Gateway (VAGS)",
        domain = primaryDomain,
        serverIp = serverIp,
        domainAliases = domainAliases,
        maintenanceMode = isMaint,
        version = ver,
        authMode = authMode,
        keycloakAuthority = kcAuth,
        proxyEndpoint = proxyEndpoint,
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
    int totalDatabases;
    string ver;
    lock (ruleLock)
    {
        ruleStore.GlobalAcademicWhitelist ??= new List<string>();
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
            SaveRules(ruleStore);
        }
        totalDatabases = ruleStore.GlobalAcademicWhitelist.Count;
        ver = ruleStore.Version;
    }

    return Results.Json(new
    {
        success = true,
        source = "https://lic.vnu.edu.vn/",
        addedDatabases = added,
        totalDatabases = totalDatabases,
        version = ver
    }, jsonOptions);
}

app.MapPost("/api/v1/lic/sync", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;
    return SyncLicDatabases();
});
app.MapPost("/api/v1/lic/sync-databases", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;
    return SyncLicDatabases();
});

// 11. API Quản lý Mật khẩu ứng dụng (Proxy Access Key) cho Cán bộ
app.MapGet("/api/v1/user/proxy-key", async (HttpContext ctx) =>
{
    var (authErr, identity, adminRole) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: false);
    if (authErr != null) return authErr;

    var user = ctx.Request.Query["user"].FirstOrDefault()
               ?? ctx.Request.Headers["X-VAGS-User"].FirstOrDefault()
               ?? identity!.Username;
    if (string.IsNullOrWhiteSpace(user) || user == "Cán bộ VNU" || !MonitorDbService.IsValidUsername(user))
        return Results.BadRequest(new { error = "Yêu cầu cung cấp username cán bộ hợp lệ" });

    user = user.Trim();
    if (!IsSameUser(user, identity!.Username) && adminRole != "super_admin")
    {
        return Results.Json(new { ok = false, error = "Forbidden: Không thể truy cập khóa của tài khoản khác" }, statusCode: 403);
    }

    var key = monitorDb.GetOrCreateAppKey(user);
    return Results.Json(new { ok = true, user, proxyKey = key, host = "vags.vnu.edu.vn", port = 443 }, jsonOptions);
});

app.MapPost("/api/v1/user/proxy-key/regenerate", async (HttpContext ctx, KeyRegenerateRequest? req) =>
{
    var (authErr, identity, adminRole) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: false);
    if (authErr != null) return authErr;

    var user = req?.User
               ?? ctx.Request.Query["user"].FirstOrDefault()
               ?? ctx.Request.Headers["X-VAGS-User"].FirstOrDefault()
               ?? identity!.Username;
    if (string.IsNullOrWhiteSpace(user) || user == "Cán bộ VNU" || !MonitorDbService.IsValidUsername(user))
        return Results.BadRequest(new { error = "Yêu cầu cung cấp username cán bộ hợp lệ" });

    user = user.Trim();
    if (!IsSameUser(user, identity!.Username) && adminRole != "super_admin")
    {
        return Results.Json(new { ok = false, error = "Forbidden: Không thể đổi khóa của tài khoản khác" }, statusCode: 403);
    }

    var key = monitorDb.RegenerateAppKey(user);
    return Results.Json(new { ok = true, user, proxyKey = key, message = "Đã thu hồi mã cũ và sinh Mật khẩu ứng dụng mới thành công!" }, jsonOptions);
});

// 12. API Phát sóng ép toàn bộ Extension cập nhật tức thì (SSE Force Update)
app.MapPost("/api/v1/admin/broadcast-update", async (HttpContext ctx, BroadcastUpdateRequest? req) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var targetVersion = req?.Version ?? "2.1.2";
    var payload = $"event: FORCE_UPDATE\ndata: {JsonSerializer.Serialize(new { minVersion = targetVersion, version = targetVersion, timestamp = DateTime.UtcNow }, sseJsonOptions)}\n\n";
    int sent = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        if (await WriteSseEventAsync(kvp.Value, payload))
        {
            sent++;
        }
    }
    return Results.Json(new { ok = true, version = targetVersion, broadcastedClients = sent, message = $"Đã phát tín hiệu ép toàn bộ {sent} Extension đang trực tuyến nâng cấp lên v{targetVersion}!" }, jsonOptions);
});

// 13. API Admin Force Tắt Proxy (Cá nhân hoặc Toàn bộ)
app.MapPost("/api/v1/admin/users/force-proxy-off", async (HttpContext ctx, ForceUserActionRequest? req) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var isAll = req?.All == true;
    var targetUser = req?.User?.Trim();
    var targetIp = req?.Ip?.Trim();
    var reasonText = string.IsNullOrWhiteSpace(req?.Reason)
        ? "Quản trị viên đã tắt Proxy trên thiết bị này."
        : req!.Reason!.Trim();

    int sent = 0;
    if (isAll)
    {
        foreach (var kvp in monitorDb.ActiveIpUserMap.ToArray())
        {
            kvp.Value.ProxyEnabled = false;
            if (!string.IsNullOrWhiteSpace(kvp.Value.Username) && kvp.Value.Username != "Khách (Chưa xác thực)")
            {
                SetPendingForceOff(kvp.Value.Username, kvp.Key, reasonText);
            }
        }
        var payload = $"event: FORCE_PROXY_OFF\ndata: {JsonSerializer.Serialize(new { all = true, user = (string?)null, ip = (string?)null, reason = reasonText, timestamp = DateTime.UtcNow }, sseJsonOptions)}\n\n";
        foreach (var kvp in sseClients.ToArray())
        {
            kvp.Value.ProxyEnabled = false;
            if (await WriteSseEventAsync(kvp.Value, payload))
            {
                sent++;
            }
        }
    }
    else if (!string.IsNullOrWhiteSpace(targetIp))
    {
        monitorDb.UpdateUserProxyStatus(targetIp, targetUser ?? "", false);
        sent = await SendTargetedForceProxyOffAsync(
            targetUser ?? "",
            new[] { targetIp },
            reasonText,
            recordTombstone: !string.IsNullOrWhiteSpace(targetUser));
    }
    else if (!string.IsNullOrWhiteSpace(targetUser))
    {
        var activeIps = monitorDb.GetActiveProxyIpsForUser(targetUser);
        monitorDb.UpdateUserProxyStatus("", targetUser, false);
        foreach (var ip in activeIps)
        {
            SetPendingForceOff(targetUser, ip, reasonText);
        }
        var ipSet = new HashSet<string>(activeIps.Select(MonitorDbService.NormalizeClientIp).Where(x => !string.IsNullOrEmpty(x)), StringComparer.OrdinalIgnoreCase);
        var payload = $"event: FORCE_PROXY_OFF\ndata: {JsonSerializer.Serialize(new { all = false, user = targetUser, ip = (string?)null, reason = reasonText, timestamp = DateTime.UtcNow }, sseJsonOptions)}\n\n";
        foreach (var kvp in sseClients.ToArray())
        {
            var c = kvp.Value;
            var normC = MonitorDbService.NormalizeClientIp(c.RemoteIp);
            var isMatchClient = IsSameUser(c.User, targetUser) ||
                                ((string.Equals(c.User, "Khách (Chưa xác thực)", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(c.User)) && !string.IsNullOrEmpty(normC) && ipSet.Contains(normC));
            if (!isMatchClient) continue;
            c.ProxyEnabled = false;
            if (await WriteSseEventAsync(c, payload))
            {
                sent++;
            }
        }
    }
    else
    {
        return Results.BadRequest(new { ok = false, message = "Vui lòng chỉ định User, IP hoặc All." });
    }

    var msg = isAll
        ? $"Đã phát tín hiệu ép TẮT PROXY TOÀN BỘ {sent} Extension đang trực tuyến!"
        : $"Đã gửi lệnh ép tắt Proxy đích danh tới '{targetUser ?? targetIp}' (đã gửi tới {sent} kết nối SSE khớp mục tiêu).";
    return Results.Json(new { ok = true, all = isAll, user = targetUser, ip = targetIp, broadcastedClients = sent, message = msg }, jsonOptions);
});

// 14. API Admin Force Đăng Xuất (Cá nhân hoặc Toàn bộ)
app.MapPost("/api/v1/admin/users/force-logout", async (HttpContext ctx, ForceUserActionRequest? req) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var isAll = req?.All == true;
    var targetUser = req?.User?.Trim();
    var targetIp = req?.Ip?.Trim();

    if (isAll)
    {
        monitorDb.UnregisterAllUserActivities();
        pendingForceProxyOff.Clear();
    }
    else
    {
        if (!string.IsNullOrWhiteSpace(targetIp))
        {
            monitorDb.UnregisterUserActivity(targetIp);
            if (!string.IsNullOrWhiteSpace(targetUser)) ClearPendingForceOff(targetUser, targetIp);
        }
        if (!string.IsNullOrWhiteSpace(targetUser))
        {
            monitorDb.UnregisterUserActivityByUser(targetUser);
            monitorDb.RegenerateAppKey(targetUser, "Auto-rotated on admin force-logout");
            RevokeUserTokens(targetUser);
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
        if (await WriteSseEventAsync(kvp.Value, payload))
        {
            sent++;
        }
    }

    var msg = isAll
        ? $"Đã xoá toàn bộ phiên và ép ĐĂNG XUẤT TẤT CẢ người dùng ({sent} Extension đã nhận lệnh)!"
        : $"Đã xoá phiên, thu hồi token/app-key và ép đăng xuất tài khoản '{targetUser ?? targetIp}'.";
    return Results.Json(new { ok = true, all = isAll, user = targetUser, ip = targetIp, broadcastedClients = sent, message = msg }, jsonOptions);
});

// 15. API Admin Khoá Tài Khoản (Lock User)
app.MapPost("/api/v1/admin/users/lock", async (HttpContext ctx, LockUserRequest? req) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    if (string.IsNullOrWhiteSpace(req?.User))
    {
        return Results.BadRequest(new { ok = false, message = "Vui lòng nhập tên tài khoản cần khoá." });
    }
    var userToLock = req.User.Trim();
    if (!MonitorDbService.IsValidUsername(userToLock))
    {
        return Results.BadRequest(new { ok = false, message = "Tên tài khoản không hợp lệ." });
    }

    List<string> deniedSnapshot;
    string versionSnapshot;
    string serializedRules;
    lock (ruleLock)
    {
        ruleStore.UserOverrides ??= new UserOverridesModel();
        ruleStore.UserOverrides.Denied ??= new List<string>();
        ruleStore.UserOverrides.Allowed ??= new List<string>();

        if (!ruleStore.UserOverrides.Denied.Contains(userToLock, StringComparer.OrdinalIgnoreCase))
        {
            ruleStore.UserOverrides.Denied.Add(userToLock);
        }
        ruleStore.UserOverrides.Allowed.RemoveAll(u => string.Equals(u, userToLock, StringComparison.OrdinalIgnoreCase));

        SaveRules(ruleStore);
        deniedSnapshot = ruleStore.UserOverrides.Denied.ToList();
        versionSnapshot = ruleStore.Version;
        serializedRules = JsonSerializer.Serialize(BuildPublicRulesProjection(ruleStore), sseJsonOptions);
    }

    monitorDb.UnregisterUserActivityByUser(userToLock);
    monitorDb.RegenerateAppKey(userToLock, "Auto-rotated on admin lock");
    RevokeUserTokens(userToLock);

    var eventData = new
    {
        user = userToLock,
        reason = req.Reason ?? "Tài khoản đã bị Quản trị viên khoá quyền sử dụng Proxy VAGS.",
        timestamp = DateTime.UtcNow
    };

    var lockPayload = $"event: FORCE_LOCK\ndata: {JsonSerializer.Serialize(eventData, sseJsonOptions)}\n\n";
    var rulesPayload = $"event: RULES_UPDATED\ndata: {serializedRules}\n\n";

    int sent = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        if (await WriteSseEventAsync(kvp.Value, lockPayload + rulesPayload))
        {
            sent++;
        }
    }

    return Results.Json(new
    {
        ok = true,
        user = userToLock,
        deniedUsers = deniedSnapshot,
        message = $"Đã KHOÁ tài khoản '{userToLock}' thành công, ngắt phiên kết nối, xoay vòng App Key và thông báo tới {sent} Extension!",
        version = versionSnapshot
    }, jsonOptions);
});

// 16. API Admin Mở Khoá Tài Khoản (Unlock User)
app.MapPost("/api/v1/admin/users/unlock", async (HttpContext ctx, LockUserRequest? req) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    if (string.IsNullOrWhiteSpace(req?.User))
    {
        return Results.BadRequest(new { ok = false, message = "Vui lòng nhập tên tài khoản cần mở khoá." });
    }
    var userToUnlock = req.User.Trim();

    List<string> deniedSnapshot;
    string versionSnapshot;
    string serializedRules;
    lock (ruleLock)
    {
        ruleStore.UserOverrides ??= new UserOverridesModel();
        ruleStore.UserOverrides.Denied ??= new List<string>();

        ruleStore.UserOverrides.Denied.RemoveAll(u => string.Equals(u, userToUnlock, StringComparison.OrdinalIgnoreCase));
        SaveRules(ruleStore);
        deniedSnapshot = ruleStore.UserOverrides.Denied.ToList();
        versionSnapshot = ruleStore.Version;
        serializedRules = JsonSerializer.Serialize(BuildPublicRulesProjection(ruleStore), sseJsonOptions);
    }

    var rulesPayload = $"event: RULES_UPDATED\ndata: {serializedRules}\n\n";
    int sent = 0;
    foreach (var kvp in sseClients.ToArray())
    {
        if (await WriteSseEventAsync(kvp.Value, rulesPayload))
        {
            sent++;
        }
    }

    return Results.Json(new
    {
        ok = true,
        user = userToUnlock,
        deniedUsers = deniedSnapshot,
        message = $"Đã MỞ KHOÁ tài khoản '{userToUnlock}' thành công!",
        version = versionSnapshot
    }, jsonOptions);
});

// 17. API Danh sách tài khoản đang bị khoá
app.MapGet("/api/v1/admin/users/locked", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    List<string> denied;
    lock (ruleLock)
    {
        denied = (ruleStore.UserOverrides?.Denied ?? new List<string>()).ToList();
    }
    return Results.Json(new { ok = true, deniedUsers = denied }, jsonOptions);
});

// 18. API Admin Đóng/Mở Cổng VAGS (Maintenance Mode Controller)
app.MapPost("/api/v1/admin/maintenance", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    var enabledStr = ctx.Request.Query["enabled"].FirstOrDefault();
    bool enable = string.Equals(enabledStr, "true", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(enabledStr, "1", StringComparison.OrdinalIgnoreCase);

    string versionSnapshot;
    string serializedRules;
    lock (ruleLock)
    {
        ruleStore.MaintenanceMode = enable;
        var flagPath = Path.Combine(dataDir, "maintenance.flag");
        if (enable) File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
        else if (File.Exists(flagPath)) File.Delete(flagPath);
        SaveRules(ruleStore);
        versionSnapshot = ruleStore.Version;
        serializedRules = JsonSerializer.Serialize(BuildPublicRulesProjection(ruleStore), sseJsonOptions);
    }

    var rulesPayload = $"event: RULES_UPDATED\ndata: {serializedRules}\n\n";
    foreach (var kvp in sseClients.ToArray())
    {
        await WriteSseEventAsync(kvp.Value, rulesPayload);
    }
    return Results.Json(new { ok = true, maintenanceMode = enable, version = versionSnapshot, message = enable ? "Cổng VAGS đã ĐÓNG để bảo trì." : "Cổng VAGS đã MỞ hoạt động bình thường." }, jsonOptions);
});

// 19. API Admin Cấu hình Tên miền động (Domain Switcher)
app.MapPost("/api/v1/admin/domains", async (HttpContext ctx) =>
{
    var (authErr, _, _) = await TryAuthorizeRequestAsync(ctx, requireSuperAdmin: true);
    if (authErr != null) return authErr;

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    JsonDocument doc;
    try
    {
        doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }
    catch
    {
        return Results.BadRequest(new { ok = false, message = "JSON body không hợp lệ." });
    }

    using (doc)
    {
        string primaryDomainSnapshot;
        List<string> aliasesSnapshot;
        string versionSnapshot;
        string serializedRules;
        lock (ruleLock)
        {
            if (doc.RootElement.TryGetProperty("primaryDomain", out var pd))
            {
                var newPrimary = (pd.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(newPrimary))
                {
                    if (!Regex.IsMatch(newPrimary, @"^[a-zA-Z0-9.-]{1,253}$"))
                        return Results.BadRequest(new { ok = false, message = "Tên miền chính không hợp lệ." });
                    ruleStore.ProxyHost = newPrimary;
                }
            }
            if (doc.RootElement.TryGetProperty("domainAliases", out var da) && da.ValueKind == JsonValueKind.Array)
            {
                var parsedAliases = new List<string>();
                foreach (var x in da.EnumerateArray())
                {
                    var alias = (x.GetString() ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(alias)) continue;
                    if (!Regex.IsMatch(alias, @"^[a-zA-Z0-9.-]{1,253}$"))
                        return Results.BadRequest(new { ok = false, message = $"Domain alias không hợp lệ: {alias}" });
                    parsedAliases.Add(alias);
                }
                ruleStore.DomainAliases = parsedAliases;
            }
            SaveRules(ruleStore);
            primaryDomainSnapshot = ruleStore.ProxyHost;
            aliasesSnapshot = (ruleStore.DomainAliases ?? new List<string>()).ToList();
            versionSnapshot = ruleStore.Version;
            serializedRules = JsonSerializer.Serialize(BuildPublicRulesProjection(ruleStore), sseJsonOptions);
        }

        var rulesPayload = $"event: RULES_UPDATED\ndata: {serializedRules}\n\n";
        foreach (var kvp in sseClients.ToArray())
        {
            await WriteSseEventAsync(kvp.Value, rulesPayload);
        }
        return Results.Json(new { ok = true, primaryDomain = primaryDomainSnapshot, domainAliases = aliasesSnapshot, version = versionSnapshot }, jsonOptions);
    }
});

app.Run();

public class RuleStoreModel
{
    public string Version { get; set; } = "1.0";
    public string ProxyMode { get; set; } = "FULL_PROXY";
    public bool MaintenanceMode { get; set; } = false;
    public bool EnableLicProxy { get; set; } = true;
    public int MaxConcurrentSessionsPerUser { get; set; } = 5;
    public int MaxConcurrentSessionsAdmin { get; set; } = 10;
    public List<string> Blacklist { get; set; } = new();
    public List<string> InternalWhitelist { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ConfiguredInternalWhitelist { get; set; }
    public List<string> GlobalAcademicWhitelist { get; set; } = new();
    public string AuthMode { get; set; } = "demo";
    public string KeycloakAuthority { get; set; } = "https://idp.vnu.edu.vn/auth/realms/vnu";
    public string ProxyHost { get; set; } = "vags.vnu.edu.vn";
    public int ProxyPort { get; set; } = 443;
    public string ProxyScheme { get; set; } = "HTTPS";
    public List<string> DomainAliases { get; set; } = new();
    public List<string> AllowedOUs { get; set; } = new() { "canbo", "giangvien", "sinhvien", "nguoihoc", "pqtkt", "duac", "*" };
    public List<string> DisallowedOUs { get; set; } = new();
    public List<string> AllowedMemberTypes { get; set; } = new() { "canbo", "giangvien", "sinhvien", "nguoihoc" };
    public List<string> DisallowedMemberTypes { get; set; } = new();
    public string AdminOU { get; set; } = "duac";
    public UserOverridesModel UserOverrides { get; set; } = new();
    public List<AdminAccountEntry> ManualAdmins { get; set; } = new();
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("O");
}

public class AdminAccountEntry
{
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Role { get; set; } = "report_viewer";
    public string AddedBy { get; set; } = "";
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
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
    public bool ProxyEnabled { get; set; } = true;
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
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
    public string MemberType { get; set; } = "canbo";
}

public record PendingForceOffEntry(string Username, string Ip, string Reason, DateTimeOffset CreatedAtUtc);

public record VerifiedTokenIdentity(string Username, string Email, string Unit, string MemberType, string? Jti);
public record VerifiedSessionEntry(string Username, string Email, string Unit, string MemberType, string? Jti, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc);
public record EvaluateRequest(string? Host, string? User = null);
public record DemoTokenRequest(string? Username, string? OrganizationalUnit);
public record ExchangeCodeRequest(string? Code, string? RedirectUri, string? CodeVerifier);
public record HeartbeatRequest(string? User, string? Unit, string? MemberType, bool? ProxyEnabled = true);
public record UserStatusRequest(string? User, bool? ProxyEnabled);
public record KeyRegenerateRequest(string? User);
public record BroadcastUpdateRequest(string? Version);
public record ForceUserActionRequest(string? User, string? Ip, bool? All, string? Reason);
public record LockUserRequest(string? User, string? Reason);
public record ManualAdminUpsertRequest(string? Username, string? Email, string? FullName, string? Unit, string? Role, string? AddedBy);
public record RouteDecision(
    string Host,
    string Action,
    string ProxyDirective,
    string MatchedRule,
    string MatchedCategory,
    bool AllowedForTunnel,
    string Reason
);

