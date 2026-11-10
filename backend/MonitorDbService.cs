using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace VagsBackend;

public class ConnectionLogItem
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ClientIp { get; set; } = "unknown";
    public string Username { get; set; } = "Khách (Chưa xác thực)";
    public string Unit { get; set; } = "VNU";
    public string TargetHost { get; set; } = "";
    public int TargetPort { get; set; } = 443;
    public bool Allowed { get; set; } = true;
    public string Reason { get; set; } = "";
    public string MatchedRule { get; set; } = "";
    public long BytesSent { get; set; } = 0;
    public long BytesReceived { get; set; } = 0;
    public long DurationMs { get; set; } = 0;
}

public class ActiveUserSession
{
    public string Username { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string Unit { get; set; } = "VNU";
    public string MemberType { get; set; } = "canbo";
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public long TotalRequests { get; set; } = 0;
    public long TotalBytes { get; set; } = 0;
    public bool ProxyEnabled { get; set; } = true;
}

public class MonitorDbService
{
    private readonly string _connectionString;
    private readonly Channel<ConnectionLogItem> _logChannel = Channel.CreateUnbounded<ConnectionLogItem>();
    public readonly ConcurrentDictionary<string, ActiveUserSession> ActiveIpUserMap = new();

    public MonitorDbService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath};";
        InitializeDatabase();
        _ = Task.Run(ProcessLogQueueAsync);
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS connections_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                client_ip TEXT NOT NULL,
                username TEXT NOT NULL,
                unit TEXT NOT NULL,
                target_host TEXT NOT NULL,
                target_port INTEGER NOT NULL,
                allowed INTEGER NOT NULL,
                reason TEXT,
                matched_rule TEXT,
                bytes_sent INTEGER DEFAULT 0,
                bytes_received INTEGER DEFAULT 0,
                duration_ms INTEGER DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_conn_timestamp ON connections_log(timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_conn_user ON connections_log(username);
            CREATE INDEX IF NOT EXISTS idx_conn_ip ON connections_log(client_ip);
            CREATE INDEX IF NOT EXISTS idx_conn_host ON connections_log(target_host);

            CREATE TABLE IF NOT EXISTS active_sessions (
                session_key TEXT PRIMARY KEY,
                username TEXT NOT NULL,
                client_ip TEXT NOT NULL,
                unit TEXT,
                member_type TEXT,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                total_requests INTEGER DEFAULT 0,
                total_bytes INTEGER DEFAULT 0,
                is_proxy_enabled INTEGER DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_sess_last_seen ON active_sessions(last_seen DESC);

            CREATE TABLE IF NOT EXISTS proxy_app_keys (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT NOT NULL UNIQUE,
                app_key TEXT NOT NULL,
                note TEXT,
                created_at TEXT NOT NULL,
                last_used_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_app_key_user ON proxy_app_keys(username);
        ";
        cmd.ExecuteNonQuery();

        try
        {
            using var cmdCol = conn.CreateCommand();
            cmdCol.CommandText = "ALTER TABLE active_sessions ADD COLUMN is_proxy_enabled INTEGER DEFAULT 1;";
            cmdCol.ExecuteNonQuery();
        }
        catch { }

        // Tự động khôi phục các phiên Cán bộ gần nhất (trong vòng 15 phút) vào bộ nhớ RAM
        try
        {
            using var cmdLoad = conn.CreateCommand();
            cmdLoad.CommandText = "SELECT client_ip, username, unit, member_type, first_seen, last_seen, total_requests, is_proxy_enabled FROM active_sessions WHERE last_seen >= $cutoff AND username != 'Khách (Chưa xác thực)';";
            cmdLoad.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddMinutes(-15).ToString("O"));
            using var r = cmdLoad.ExecuteReader();
            while (r.Read())
            {
                var ip = r.GetString(0);
                var uname = r.GetString(1);
                if (!string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(uname))
                {
                    DateTime.TryParse(r.GetString(4), out var fs);
                    DateTime.TryParse(r.GetString(5), out var ls);
                    var pEnabled = r.IsDBNull(7) || r.GetInt32(7) == 1;
                    ActiveIpUserMap[ip] = new ActiveUserSession
                    {
                        ClientIp = ip,
                        Username = uname,
                        Unit = r.IsDBNull(2) ? "VNU" : r.GetString(2),
                        MemberType = r.IsDBNull(3) ? "canbo" : r.GetString(3),
                        FirstSeen = fs == default ? DateTime.UtcNow : fs,
                        LastSeen = ls == default ? DateTime.UtcNow : ls,
                        TotalRequests = r.IsDBNull(6) ? 1 : r.GetInt64(6),
                        ProxyEnabled = pEnabled
                    };
                }
            }
        }
        catch { }
    }

    public void RegisterUserActivity(string clientIp, string username, string unit = "VNU", string memberType = "canbo", bool proxyEnabled = true)
    {
        if (string.IsNullOrWhiteSpace(clientIp) || clientIp == "unknown" || clientIp.StartsWith("127.") || clientIp.StartsWith("172.23.")) return;
        if (string.IsNullOrWhiteSpace(username) || username == "Cán bộ VNU") return;

        var cleanIp = clientIp.Trim();
        var cleanUser = username.Trim();
        var cleanUnit = string.IsNullOrWhiteSpace(unit) ? "VNU" : unit.Trim().ToUpperInvariant();

        ActiveIpUserMap.AddOrUpdate(cleanIp,
            ip => new ActiveUserSession
            {
                Username = cleanUser,
                ClientIp = cleanIp,
                Unit = cleanUnit,
                MemberType = memberType,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                TotalRequests = 1,
                ProxyEnabled = proxyEnabled
            },
            (ip, existing) =>
            {
                existing.Username = cleanUser;
                existing.Unit = cleanUnit;
                existing.MemberType = memberType;
                existing.LastSeen = DateTime.UtcNow;
                existing.ProxyEnabled = proxyEnabled;
                existing.TotalRequests++;
                return existing;
            });

        // Async update active_sessions table in SQLite
        Task.Run(async () =>
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO active_sessions (session_key, username, client_ip, unit, member_type, first_seen, last_seen, total_requests, total_bytes, is_proxy_enabled)
                    VALUES ($key, $user, $ip, $unit, $mtype, $first, $last, 1, 0, $enabled)
                    ON CONFLICT(session_key) DO UPDATE SET
                        username = excluded.username,
                        client_ip = excluded.client_ip,
                        unit = excluded.unit,
                        member_type = excluded.member_type,
                        last_seen = excluded.last_seen,
                        is_proxy_enabled = excluded.is_proxy_enabled,
                        total_requests = active_sessions.total_requests + 1;

                    UPDATE connections_log 
                    SET username = $user, unit = $unit 
                    WHERE client_ip = $ip AND (username LIKE 'Khách%' OR username = '') AND timestamp >= $cutoff;
                ";
                var sessionKey = $"{cleanUser}_{cleanIp}";
                cmd.Parameters.AddWithValue("$key", sessionKey);
                cmd.Parameters.AddWithValue("$user", cleanUser);
                cmd.Parameters.AddWithValue("$ip", cleanIp);
                cmd.Parameters.AddWithValue("$unit", cleanUnit);
                cmd.Parameters.AddWithValue("$mtype", memberType);
                cmd.Parameters.AddWithValue("$first", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$last", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$enabled", proxyEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddMinutes(-30).ToString("O"));
                await cmd.ExecuteNonQueryAsync();
            }
            catch { }
        });
    }

    public void UpdateUserProxyStatus(string clientIp, string username, bool isProxyEnabled)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var cleanUser = username.Trim();
        var cleanIp = (clientIp ?? "").Trim();

        foreach (var kvp in ActiveIpUserMap.ToArray())
        {
            if (string.Equals(kvp.Value.Username, cleanUser, StringComparison.OrdinalIgnoreCase))
            {
                kvp.Value.ProxyEnabled = isProxyEnabled;
                kvp.Value.LastSeen = DateTime.UtcNow;
            }
        }

        Task.Run(async () =>
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE active_sessions SET is_proxy_enabled = $enabled, last_seen = $last WHERE username = $user COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("$enabled", isProxyEnabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$last", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$user", cleanUser);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { }
        });
    }

    public void UnregisterUserActivity(string clientIp)
    {
        if (string.IsNullOrWhiteSpace(clientIp)) return;
        var cleanIp = clientIp.Trim();
        if (cleanIp.StartsWith("::ffff:")) cleanIp = cleanIp.Substring(7);
        if (cleanIp.StartsWith("[") && cleanIp.Contains("]")) cleanIp = cleanIp.Substring(1, cleanIp.IndexOf(']') - 1);

        ActiveIpUserMap.TryRemove(cleanIp, out _);

        Task.Run(async () =>
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM active_sessions WHERE client_ip = $ip;";
                cmd.Parameters.AddWithValue("$ip", cleanIp);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { }
        });
    }

    public void UnregisterUserActivityByUser(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var cleanUser = username.Trim();

        foreach (var kvp in ActiveIpUserMap.ToArray())
        {
            if (string.Equals(kvp.Value.Username, cleanUser, StringComparison.OrdinalIgnoreCase))
            {
                ActiveIpUserMap.TryRemove(kvp.Key, out _);
            }
        }

        Task.Run(async () =>
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM active_sessions WHERE username = $user COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("$user", cleanUser);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { }
        });
    }

    public void UnregisterAllUserActivities()
    {
        ActiveIpUserMap.Clear();

        Task.Run(async () =>
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM active_sessions;";
                await cmd.ExecuteNonQueryAsync();
            }
            catch { }
        });
    }

    public (string username, string unit, string memberType) ResolveUser(string clientIp, string? authHeaderUser, string? authHeaderUnit, string? authHeaderMemberType)
    {
        if (!string.IsNullOrWhiteSpace(authHeaderUser) && authHeaderUser != "Cán bộ VNU")
        {
            var u = authHeaderUser.Trim();
            var unit = string.IsNullOrWhiteSpace(authHeaderUnit) ? "VNU" : authHeaderUnit.Trim().ToUpperInvariant();
            RegisterUserActivity(clientIp, u, unit, authHeaderMemberType ?? "canbo");
            return (u, unit, authHeaderMemberType ?? "canbo");
        }

        var cleanIp = clientIp.Trim();
        if (ActiveIpUserMap.TryGetValue(cleanIp, out var session))
        {
            if (DateTime.UtcNow - session.LastSeen < TimeSpan.FromHours(12))
            {
                session.LastSeen = DateTime.UtcNow;
                session.TotalRequests++;
                return (session.Username, session.Unit, session.MemberType);
            }
        }

        // Khôi phục từ SQLite active_sessions nếu vừa khởi động lại máy chủ hoặc chưa nạp cache
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT username, unit, member_type, last_seen, total_requests FROM active_sessions WHERE client_ip = $ip AND username != 'Khách (Chưa xác thực)' ORDER BY last_seen DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$ip", cleanIp);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                var u = r.GetString(0);
                var un = r.IsDBNull(1) ? "VNU" : r.GetString(1);
                var mt = r.IsDBNull(2) ? "canbo" : r.GetString(2);
                DateTime.TryParse(r.GetString(3), out var ls);
                if (DateTime.UtcNow - ls < TimeSpan.FromHours(12))
                {
                    ActiveIpUserMap[cleanIp] = new ActiveUserSession
                    {
                        ClientIp = cleanIp,
                        Username = u,
                        Unit = un,
                        MemberType = mt,
                        FirstSeen = ls,
                        LastSeen = DateTime.UtcNow,
                        TotalRequests = (r.IsDBNull(4) ? 1 : r.GetInt64(4)) + 1
                    };
                    return (u, un, mt);
                }
            }
        }
        catch { }

        return ("Khách (Chưa xác thực)", "VNU", "guest");
    }

    public void EnqueueLog(ConnectionLogItem item)
    {
        _logChannel.Writer.TryWrite(item);
    }

    private async Task ProcessLogQueueAsync()
    {
        var batch = new List<ConnectionLogItem>(64);
        while (await _logChannel.Reader.WaitToReadAsync())
        {
            while (_logChannel.Reader.TryRead(out var item))
            {
                batch.Add(item);
                if (batch.Count >= 64) break;
            }

            if (batch.Count > 0)
            {
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync();
                    using var transaction = conn.BeginTransaction();
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO connections_log 
                        (timestamp, client_ip, username, unit, target_host, target_port, allowed, reason, matched_rule, bytes_sent, bytes_received, duration_ms)
                        VALUES ($ts, $ip, $user, $unit, $host, $port, $allowed, $reason, $rule, $bsent, $brecv, $dur);
                    ";

                    var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
                    var pIp = cmd.Parameters.Add("$ip", SqliteType.Text);
                    var pUser = cmd.Parameters.Add("$user", SqliteType.Text);
                    var pUnit = cmd.Parameters.Add("$unit", SqliteType.Text);
                    var pHost = cmd.Parameters.Add("$host", SqliteType.Text);
                    var pPort = cmd.Parameters.Add("$port", SqliteType.Integer);
                    var pAllowed = cmd.Parameters.Add("$allowed", SqliteType.Integer);
                    var pReason = cmd.Parameters.Add("$reason", SqliteType.Text);
                    var pRule = cmd.Parameters.Add("$rule", SqliteType.Text);
                    var pBsent = cmd.Parameters.Add("$bsent", SqliteType.Integer);
                    var pBrecv = cmd.Parameters.Add("$brecv", SqliteType.Integer);
                    var pDur = cmd.Parameters.Add("$dur", SqliteType.Integer);

                    foreach (var it in batch)
                    {
                        pTs.Value = it.Timestamp.ToString("O");
                        pIp.Value = it.ClientIp;
                        pUser.Value = it.Username;
                        pUnit.Value = it.Unit;
                        pHost.Value = it.TargetHost;
                        pPort.Value = it.TargetPort;
                        pAllowed.Value = it.Allowed ? 1 : 0;
                        pReason.Value = it.Reason ?? "";
                        pRule.Value = it.MatchedRule ?? "";
                        pBsent.Value = it.BytesSent;
                        pBrecv.Value = it.BytesReceived;
                        pDur.Value = it.DurationMs;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] SQLite batch insert error: {ex.Message}");
                }
                batch.Clear();
            }
        }
    }

    public async Task<object> GetAnalyticsSummaryAsync(string ruleVersion, string proxyMode, string egressIp)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // 1. Sliding Window 2-min online users (trực tuyến và đang bật proxy)
        var twoMinAgo = DateTime.UtcNow.AddMinutes(-2).ToString("O");
        using var cmdOnline = conn.CreateCommand();
        cmdOnline.CommandText = @"
            SELECT COUNT(DISTINCT username) 
            FROM active_sessions 
            WHERE last_seen >= $cutoff AND username != 'Khách (Chưa xác thực)' AND (is_proxy_enabled IS NULL OR is_proxy_enabled = 1);
        ";
        cmdOnline.Parameters.AddWithValue("$cutoff", twoMinAgo);
        var onlineCount = Convert.ToInt32(await cmdOnline.ExecuteScalarAsync());

        // In-memory fallback: Đếm chuẩn người dùng gửi heartbeat trong 2 phút và ProxyEnabled == true
        var inMemoryOnline = ActiveIpUserMap.Values
            .Where(s => DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(2) && s.Username != "Khách (Chưa xác thực)" && s.ProxyEnabled)
            .Select(s => s.Username.ToLowerInvariant())
            .Distinct()
            .Count();
        onlineCount = Math.Max(onlineCount, inMemoryOnline);

        // 2. Bandwidth & counts
        using var cmdTotals = conn.CreateCommand();
        cmdTotals.CommandText = @"
            SELECT 
                COUNT(*), 
                COALESCE(SUM(CASE WHEN allowed = 1 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN allowed = 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(bytes_sent), 0),
                COALESCE(SUM(bytes_received), 0)
            FROM connections_log;
        ";
        using var reader = await cmdTotals.ExecuteReaderAsync();
        long totalReqs = 0, allowedReqs = 0, blockedReqs = 0, totalSent = 0, totalRecv = 0;
        if (await reader.ReadAsync())
        {
            totalReqs = reader.GetInt64(0);
            allowedReqs = reader.GetInt64(1);
            blockedReqs = reader.GetInt64(2);
            totalSent = reader.GetInt64(3);
            totalRecv = reader.GetInt64(4);
        }

        // 3. Recent 50 logs
        var recentLogs = new List<object>();
        using var cmdRecent = conn.CreateCommand();
        cmdRecent.CommandText = @"
            SELECT timestamp, client_ip, username, unit, target_host, target_port, allowed, reason, matched_rule, bytes_sent, bytes_received, duration_ms
            FROM connections_log 
            ORDER BY id DESC LIMIT 50;
        ";
        using var rLogs = await cmdRecent.ExecuteReaderAsync();
        while (await rLogs.ReadAsync())
        {
            recentLogs.Add(new
            {
                timestamp = rLogs.GetString(0),
                clientIp = rLogs.GetString(1),
                user = rLogs.GetString(2),
                unit = rLogs.GetString(3),
                target = $"{rLogs.GetString(4)}:{rLogs.GetInt32(5)}",
                allowed = rLogs.GetInt32(6) == 1,
                reason = rLogs.GetString(7),
                matchedRule = rLogs.GetString(8),
                bytesSent = rLogs.GetInt64(9),
                bytesReceived = rLogs.GetInt64(10),
                durationMs = rLogs.GetInt64(11)
            });
        }

        // Active sessions list (hiển thị phiên trong vòng 30 phút gần nhất)
        var activeSessionsList = ActiveIpUserMap.Values
            .Where(s => DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(30))
            .OrderByDescending(s => s.LastSeen)
            .Take(30)
            .Select(s => new
            {
                username = s.Username,
                clientIp = s.ClientIp,
                unit = s.Unit,
                memberType = s.MemberType,
                firstSeen = s.FirstSeen.ToString("O"),
                lastSeen = s.LastSeen.ToString("O"),
                isOnline = DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(2) && s.ProxyEnabled,
                proxyEnabled = s.ProxyEnabled,
                totalRequests = s.TotalRequests
            })
            .ToList();

        return new
        {
            onlineUsersCount = onlineCount,
            version = ruleVersion,
            proxyMode = proxyMode,
            egressIp = egressIp,
            metrics = new
            {
                totalProxyRequests = totalReqs,
                allowedProxyRequests = allowedReqs,
                blockedProxyRequests = blockedReqs,
                totalBytesSent = totalSent,
                totalBytesReceived = totalRecv,
                totalBytes = totalSent + totalRecv
            },
            activeSessions = activeSessionsList,
            recentProxyLogs = recentLogs
        };
    }

    public async Task<List<object>> GetTopUrlsAsync(int limit = 10)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT target_host, COUNT(*) as req_count, COALESCE(SUM(bytes_sent + bytes_received), 0) as total_bytes
            FROM connections_log
            WHERE allowed = 1
            GROUP BY target_host
            ORDER BY req_count DESC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<object>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new
            {
                domain = r.GetString(0),
                requestsCount = r.GetInt64(1),
                totalBytes = r.GetInt64(2)
            });
        }
        return list;
    }

    public async Task<List<object>> GetTopUsersAsync(int limit = 10)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT username, unit, COUNT(*) as req_count, COALESCE(SUM(bytes_sent + bytes_received), 0) as total_bytes, MAX(timestamp) as last_seen
            FROM connections_log
            WHERE username != 'Khách (Chưa xác thực)' AND username != ''
            GROUP BY username
            ORDER BY req_count DESC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<object>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new
            {
                username = r.GetString(0),
                unit = r.GetString(1),
                requestsCount = r.GetInt64(2),
                totalBytes = r.GetInt64(3),
                lastSeen = r.GetString(4)
            });
        }
        return list;
    }

    public async Task<List<object>> GetTopIpsAsync(int limit = 10)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                client_ip, 
                COALESCE(
                    (SELECT s.username FROM active_sessions s WHERE s.client_ip = connections_log.client_ip LIMIT 1),
                    (SELECT c2.username FROM connections_log c2 WHERE c2.client_ip = connections_log.client_ip AND c2.username NOT LIKE 'Khách%' ORDER BY c2.id DESC LIMIT 1),
                    'Khách (Chưa xác thực)'
                ) as display_user, 
                COUNT(*) as req_count, 
                COALESCE(SUM(bytes_sent + bytes_received), 0) as total_bytes, 
                MAX(timestamp) as last_seen
            FROM connections_log
            WHERE client_ip != 'unknown' AND client_ip NOT LIKE '%127.%' AND client_ip NOT LIKE '%172.23.%' AND client_ip NOT LIKE '%172.17.%'
            GROUP BY client_ip
            ORDER BY req_count DESC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<object>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new
            {
                clientIp = r.GetString(0),
                user = r.GetString(1),
                requestsCount = r.GetInt64(2),
                totalBytes = r.GetInt64(3),
                lastSeen = r.GetString(4)
            });
        }
        return list;
    }

    public async Task<List<object>> GetTopBandwidthUrlsAsync(int limit = 10)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                target_host, 
                COUNT(*) as req_count, 
                COALESCE(SUM(bytes_sent + bytes_received), 0) as total_bytes,
                COALESCE(SUM(bytes_received), 0) as rx_bytes,
                COALESCE(SUM(bytes_sent), 0) as tx_bytes,
                MAX(timestamp) as last_seen
            FROM connections_log
            GROUP BY target_host
            HAVING total_bytes > 0
            ORDER BY total_bytes DESC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new
            {
                host = reader.GetString(0),
                reqCount = reader.GetInt64(1),
                totalBytes = reader.GetInt64(2),
                rxBytes = reader.GetInt64(3),
                txBytes = reader.GetInt64(4),
                lastSeen = reader.IsDBNull(5) ? "" : reader.GetString(5)
            });
        }
        return list;
    }

    public async Task<(List<object> logs, int total)> GetAuditLogsAsync(string? search, string? user, string? ip, string? domain, string? status, string? allowed, int limit = 50, int offset = 0)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var whereClauses = new List<string>();
        var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(search))
        {
            whereClauses.Add("(username LIKE $search OR client_ip LIKE $search OR target_host LIKE $search OR reason LIKE $search)");
            cmd.Parameters.AddWithValue("$search", $"%{search.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(user))
        {
            whereClauses.Add("username LIKE $user");
            cmd.Parameters.AddWithValue("$user", $"%{user.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(ip))
        {
            whereClauses.Add("client_ip LIKE $ip");
            cmd.Parameters.AddWithValue("$ip", $"%{ip.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(domain))
        {
            whereClauses.Add("target_host LIKE $domain");
            cmd.Parameters.AddWithValue("$domain", $"%{domain.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status.Equals("allowed", StringComparison.OrdinalIgnoreCase)) whereClauses.Add("allowed = 1");
            else if (status.Equals("blocked", StringComparison.OrdinalIgnoreCase)) whereClauses.Add("allowed = 0");
        }
        if (!string.IsNullOrWhiteSpace(allowed))
        {
            if (allowed.Equals("true", StringComparison.OrdinalIgnoreCase) || allowed == "1") whereClauses.Add("allowed = 1");
            else if (allowed.Equals("false", StringComparison.OrdinalIgnoreCase) || allowed == "0") whereClauses.Add("allowed = 0");
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        // Total count
        cmd.CommandText = $"SELECT COUNT(*) FROM connections_log {whereSql};";
        var total = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        // Rows
        cmd.CommandText = $@"
            SELECT id, timestamp, client_ip, username, unit, target_host, target_port, allowed, reason, matched_rule, bytes_sent, bytes_received, duration_ms
            FROM connections_log
            {whereSql}
            ORDER BY id DESC
            LIMIT $limit OFFSET $offset;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var logs = new List<object>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            logs.Add(new
            {
                id = r.GetInt64(0),
                timestamp = r.GetString(1),
                clientIp = r.GetString(2),
                user = r.GetString(3),
                unit = r.GetString(4),
                target = $"{r.GetString(5)}:{r.GetInt32(6)}",
                allowed = r.GetInt32(7) == 1,
                reason = r.GetString(8),
                matchedRule = r.GetString(9),
                bytesSent = r.GetInt64(10),
                bytesReceived = r.GetInt64(11),
                durationMs = r.GetInt64(12)
            });
        }

        return (logs, total);
    }

    public async Task<(List<object> domains, int total)> GetDomainBreakdownAsync(string? search, string? category, int limit = 50, int offset = 0)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var whereClauses = new List<string>();
        var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(search))
        {
            whereClauses.Add("target_host LIKE $search");
            cmd.Parameters.AddWithValue("$search", $"%{search.Trim()}%");
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        // Total distinct domains
        cmd.CommandText = $"SELECT COUNT(DISTINCT target_host) FROM connections_log {whereSql};";
        var total = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        // Domain breakdown
        cmd.CommandText = $@"
            SELECT 
                target_host,
                COUNT(*) as total_requests,
                COALESCE(SUM(CASE WHEN allowed = 1 THEN 1 ELSE 0 END), 0) as allowed_requests,
                COALESCE(SUM(CASE WHEN allowed = 0 THEN 1 ELSE 0 END), 0) as blocked_requests,
                COALESCE(SUM(bytes_received), 0) as rx_bytes,
                COALESCE(SUM(bytes_sent), 0) as tx_bytes,
                COALESCE(SUM(bytes_received + bytes_sent), 0) as total_bytes,
                COUNT(DISTINCT username) as unique_users,
                MAX(timestamp) as last_seen,
                MAX(matched_rule) as sample_rule
            FROM connections_log
            {whereSql}
            GROUP BY target_host
            ORDER BY total_requests DESC
            LIMIT $limit OFFSET $offset;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new
            {
                domain = reader.GetString(0),
                totalRequests = reader.GetInt64(1),
                allowedRequests = reader.GetInt64(2),
                blockedRequests = reader.GetInt64(3),
                rxBytes = reader.GetInt64(4),
                txBytes = reader.GetInt64(5),
                totalBytes = reader.GetInt64(6),
                uniqueUsers = reader.GetInt64(7),
                lastSeen = reader.IsDBNull(8) ? "" : reader.GetString(8),
                sampleRule = reader.IsDBNull(9) ? "" : reader.GetString(9)
            });
        }
        return (list, total);
    }

    public async Task<string> ExportAuditLogsCsvAsync(int limit = 1000)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT timestamp, client_ip, username, unit, target_host, target_port, allowed, bytes_sent, bytes_received, reason
            FROM connections_log
            ORDER BY id DESC
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,ClientIP,User,Unit,TargetHost,TargetPort,Allowed,BytesSent,BytesReceived,Reason");

        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            sb.AppendLine($"\"{r.GetString(0)}\",\"{r.GetString(1)}\",\"{r.GetString(2)}\",\"{r.GetString(3)}\",\"{r.GetString(4)}\",{r.GetInt32(5)},{(r.GetInt32(6) == 1 ? "YES" : "NO")},{r.GetInt64(7)},{r.GetInt64(8)},\"{r.GetString(9).Replace("\"", "\"\"")}\"");
        }

        return sb.ToString();
    }

    public string GetOrCreateAppKey(string username, string note = "Thiết bị ngoài / PAC")
    {
        if (string.IsNullOrWhiteSpace(username)) return "";
        var cleanUser = username.Trim().ToLowerInvariant();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmdCheck = conn.CreateCommand();
        cmdCheck.CommandText = "SELECT app_key FROM proxy_app_keys WHERE LOWER(username) = $u LIMIT 1;";
        cmdCheck.Parameters.AddWithValue("$u", cleanUser);
        var existing = cmdCheck.ExecuteScalar()?.ToString();
        if (!string.IsNullOrEmpty(existing)) return existing;

        var newKey = GenerateSecureKey();
        using var cmdInsert = conn.CreateCommand();
        cmdInsert.CommandText = @"
            INSERT INTO proxy_app_keys (username, app_key, note, created_at)
            VALUES ($u, $k, $n, $c);
        ";
        cmdInsert.Parameters.AddWithValue("$u", cleanUser);
        cmdInsert.Parameters.AddWithValue("$k", newKey);
        cmdInsert.Parameters.AddWithValue("$n", note);
        cmdInsert.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
        cmdInsert.ExecuteNonQuery();
        return newKey;
    }

    public string RegenerateAppKey(string username, string note = "Thiết bị ngoài / PAC")
    {
        if (string.IsNullOrWhiteSpace(username)) return "";
        var cleanUser = username.Trim().ToLowerInvariant();
        var newKey = GenerateSecureKey();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO proxy_app_keys (username, app_key, note, created_at)
            VALUES ($u, $k, $n, $c)
            ON CONFLICT(username) DO UPDATE SET
                app_key = excluded.app_key,
                created_at = excluded.created_at,
                note = excluded.note;
        ";
        cmd.Parameters.AddWithValue("$u", cleanUser);
        cmd.Parameters.AddWithValue("$k", newKey);
        cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return newKey;
    }

    public bool ValidateAppKey(string username, string appKey, out string unit, out string memberType)
    {
        unit = "CANBO";
        memberType = "canbo";
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(appKey)) return false;
        var cleanUser = username.Trim().ToLowerInvariant();
        var cleanKey = appKey.Trim();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM proxy_app_keys WHERE LOWER(username) = $u AND app_key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$u", cleanUser);
        cmd.Parameters.AddWithValue("$k", cleanKey);
        var obj = cmd.ExecuteScalar();
        if (obj != null)
        {
            unit = (cleanUser == "phongnd" || cleanUser == "vuongwork" || cleanUser == "vuongwork@vnu.edu.vn" || cleanUser == "admin" || cleanUser.Contains("pqtkt")) ? "PQTKT" : "CANBO";
            using var cmdUpdate = conn.CreateCommand();
            cmdUpdate.CommandText = "UPDATE proxy_app_keys SET last_used_at = $now WHERE LOWER(username) = $u;";
            cmdUpdate.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmdUpdate.Parameters.AddWithValue("$u", cleanUser);
            cmdUpdate.ExecuteNonQuery();
            return true;
        }
        return false;
    }

    private static string GenerateSecureKey()
    {
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"vags-{hex.Substring(0, 4)}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}";
    }
}

