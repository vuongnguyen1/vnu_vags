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
    public string MemberType { get; set; } = "canbo";
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

        try
        {
            using var cmdCol2 = conn.CreateCommand();
            cmdCol2.CommandText = "ALTER TABLE connections_log ADD COLUMN member_type TEXT DEFAULT 'canbo';";
            cmdCol2.ExecuteNonQuery();
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
                        (timestamp, client_ip, username, unit, member_type, target_host, target_port, allowed, reason, matched_rule, bytes_sent, bytes_received, duration_ms)
                        VALUES ($ts, $ip, $user, $unit, $mtype, $host, $port, $allowed, $reason, $rule, $bsent, $brecv, $dur);
                    ";

                    var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
                    var pIp = cmd.Parameters.Add("$ip", SqliteType.Text);
                    var pUser = cmd.Parameters.Add("$user", SqliteType.Text);
                    var pUnit = cmd.Parameters.Add("$unit", SqliteType.Text);
                    var pMtype = cmd.Parameters.Add("$mtype", SqliteType.Text);
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
                        pMtype.Value = string.IsNullOrWhiteSpace(it.MemberType) ? "canbo" : it.MemberType;
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

    public static readonly Dictionary<string, (string unit, string memberType)> KnownUserMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // UMP (Đại học Y Dược)
        { "khoay.ump", ("UMP", "canbo") },
        { "thangnd.ump", ("UMP", "canbo") },
        { "sonhoang.ump", ("UMP", "nguoihoc") },
        { "duongnth.ump", ("UMP", "nguoihoc") },
        { "caoviet", ("UMP", "canbo") },
        { "dungmq1102", ("UMP", "nguoihoc") },

        // UEB (Đại học Kinh tế)
        { "maianh.ueb", ("UEB", "canbo") },
        { "it_ueb", ("UEB", "canbo") },
        { "hoahtt", ("UEB", "canbo") },
        { "dungvt", ("UEB", "canbo") },
        { "tuanta91", ("UEB", "nguoihoc") },

        // HUS (Đại học Khoa học Tự nhiên)
        { "duydoanmanh", ("HUS", "canbo") },
        { "sondohong", ("HUS", "canbo") },
        { "xuanluc.le", ("HUS", "canbo") },
        { "thanhletrung", ("HUS", "nguoihoc") },
        { "hoangntt", ("HUS", "nguoihoc") },
        { "quynhntt-bio", ("HUS", "canbo") },

        // UET (Đại học Công nghệ)
        { "tqlong", ("UET", "canbo") },
        { "letungson", ("UET", "canbo") },
        { "tdhuy", ("UET", "canbo") },
        { "ductv92", ("UET", "nguoihoc") },
        { "tuannguyenduc", ("UET", "canbo") },
        { "datlq", ("UET", "nguoihoc") },
        { "duchuy", ("UET", "nguoihoc") },
        { "ndminh", ("UET", "canbo") },

        // USSH (Đại học Khoa học Xã hội và Nhân văn)
        { "toanvh", ("USSH", "canbo") },
        { "truongho", ("USSH", "canbo") },
        { "trinhtd", ("USSH", "canbo") },
        { "lathitham", ("USSH", "nguoihoc") },
        { "hoant1991", ("USSH", "nguoihoc") },

        // ULIS (Đại học Ngoại ngữ)
        { "bichphuong", ("ULIS", "canbo") },
        { "hangtang", ("ULIS", "canbo") },
        { "nhungdth", ("ULIS", "canbo") },
        { "ynhu", ("ULIS", "nguoihoc") },
        { "trannhuchi", ("ULIS", "nguoihoc") },

        // UEd (Đại học Giáo dục)
        { "minhgv", ("UEd", "canbo") },
        { "nguyenthuy", ("UEd", "canbo") },
        { "thaiba", ("UEd", "nguoihoc") },
        { "haptt1983", ("UEd", "canbo") },

        // VJU (Đại học Việt Nhật)
        { "tannd88", ("VJU", "canbo") },
        { "donghyunko@vnu.edu.vn", ("VJU", "canbo") },
        { "khanhvh", ("VJU", "nguoihoc") },

        // UL (Đại học Luật)
        { "lamlb", ("UL", "canbo") },
        { "hiendinhthuyls", ("UL", "canbo") },
        { "loanhtp", ("UL", "canbo") },
        { "duclm", ("UL", "nguoihoc") },

        // IS (Trường Quốc tế)
        { "quynld", ("IS", "canbo") },
        { "hieubt", ("IS", "canbo") },
        { "tuyendt", ("IS", "nguoihoc") },
        { "linhdtd", ("IS", "nguoihoc") },

        // HSB (Trường Quản trị và Kinh doanh)
        { "doanlt", ("HSB", "canbo") },
        { "lephuc", ("HSB", "canbo") },
        { "cuongkim", ("HSB", "nguoihoc") },
        { "cuonglv", ("HSB", "canbo") },

        // SIS (Trường Khoa học liên ngành và Nghệ thuật)
        { "ptnha", ("SIS", "canbo") },
        { "hoadao", ("SIS", "canbo") },
        { "yennh", ("SIS", "nguoihoc") },
        { "ntttra", ("SIS", "canbo") },

        // ITI (Viện Công nghệ Thông tin & DUAC)
        { "haitrantt", ("ITI", "canbo") },
        { "longph", ("ITI", "canbo") },
        { "diepnm", ("ITI", "canbo") },
        { "thinhtt", ("ITI", "nguoihoc") },
        { "phongnd", ("ITI", "canbo") },
        { "vuongwork", ("ITI", "canbo") },
        { "test", ("ITI", "canbo") },

        // CRES (Viện Tài nguyên và Môi trường)
        { "vinhnv.vtnt", ("CRES", "canbo") },
        { "dung.nghiem", ("CRES", "canbo") },
        { "dongquangviet", ("CRES", "nguoihoc") },

        // IMBT (Viện Vi sinh vật và Công nghệ Sinh học)
        { "hanhntb", ("IMBT", "canbo") },
        { "nguyenbichngoc", ("IMBT", "canbo") },
        { "nhungntk", ("IMBT", "nguoihoc") },

        // IVIDES (Viện Đảm bảo chất lượng Giáo dục)
        { "tranhiep.dinh", ("IVIDES", "canbo") },
        { "thaibinh", ("IVIDES", "nguoihoc") },

        // TNTI (Viện Trần Nhân Tông)
        { "ngoviethoan", ("TNTI", "canbo") },
        { "thaitv", ("TNTI", "nguoihoc") },

        // TIP (Viện Đổi mới sáng tạo và Khởi nghiệp)
        { "phamtiendat", ("TIP", "canbo") },
        { "sonnh", ("TIP", "nguoihoc") },

        // IQA (Trung tâm Khảo thí ĐHQGHN)
        { "dungbt", ("IQA", "canbo") },
        { "hoangcd", ("IQA", "nguoihoc") },

        // CEA (Trung tâm Kiểm định Chất lượng Giáo dục)
        { "huonglt", ("CEA", "canbo") },
        { "ngocttb", ("CEA", "nguoihoc") },

        // LIC (Trung tâm Thư viện và Tri thức số)
        { "ntquyen", ("LIC", "canbo") },
        { "nguyentruonggiang", ("LIC", "nguoihoc") },
        { "cuongnguyennxb", ("LIC", "canbo") },

        // OTHER (Cơ quan ĐHQGHN & Khác)
        { "cuongnd", ("OTHER", "canbo") },
        { "lqanh", ("OTHER", "canbo") },
        { "hanhmt", ("OTHER", "canbo") },
        { "anhnn.vnu", ("OTHER", "canbo") },
        { "Khách (Chưa xác thực)", ("OTHER", "nguoihoc") }
    };

    public static (string unit, string memberType) DetermineUnitAndMemberType(string? username, string? rawUnit, string? rawMtype)
    {
        var u = username?.Trim() ?? "";
        if (!string.IsNullOrEmpty(u) && KnownUserMappings.TryGetValue(u, out var mapped))
        {
            return mapped;
        }

        if (!string.IsNullOrEmpty(u))
        {
            var lowU = u.ToLowerInvariant();
            if (lowU.EndsWith(".ump") || lowU.Contains("ump")) return ("UMP", "canbo");
            if (lowU.EndsWith(".ueb") || lowU.Contains("ueb")) return ("UEB", "canbo");
            if (lowU.EndsWith(".vtnt") || lowU.Contains("cres")) return ("CRES", "canbo");
            if (lowU.Contains("uet")) return ("UET", "canbo");
            if (lowU.Contains("ulis")) return ("ULIS", "canbo");
            if (lowU.Contains("ussh")) return ("USSH", "canbo");
            if (lowU.Contains("hus")) return ("HUS", "canbo");
            if (lowU.Contains("vju")) return ("VJU", "canbo");
            if (lowU.Contains("iti")) return ("ITI", "canbo");
            if (lowU.Contains("lic") || lowU.Contains("nxb")) return ("LIC", "canbo");

            // Mã sinh viên các trường thành viên
            if (u.Length == 8 && long.TryParse(u, out _))
            {
                var code = u.Substring(2, 2);
                if (code == "01") return ("HUS", "nguoihoc");
                if (code == "02") return ("UET", "nguoihoc");
                if (code == "03") return ("USSH", "nguoihoc");
                if (code == "04") return ("ULIS", "nguoihoc");
                if (code == "05") return ("UEB", "nguoihoc");
                if (code == "06") return ("UEd", "nguoihoc");
                if (code == "07") return ("IS", "nguoihoc");
                if (code == "08") return ("UMP", "nguoihoc");
                if (code == "09") return ("UL", "nguoihoc");
            }
        }

        var normUnit = NormalizeUnit(rawUnit);
        var normMtype = (rawMtype != null && (rawMtype.Contains("canbo", StringComparison.OrdinalIgnoreCase) || rawMtype.Contains("giangvien", StringComparison.OrdinalIgnoreCase)))
            ? "canbo" : "nguoihoc";

        return (normUnit, normMtype);
    }

    public static string NormalizeUnit(string? rawUnit)
    {
        if (string.IsNullOrWhiteSpace(rawUnit)) return "OTHER";
        var u = rawUnit.Trim().ToUpperInvariant();
        if (u == "PQTKT" || u == "DUAC") return "ITI";
        if (u == "HUS" || u.Contains("HUS") || u.Contains("KHTN") || u.Contains("TỰ NHIÊN")) return "HUS";
        if (u == "USSH" || u.Contains("USSH") || u.Contains("KHXHNV") || u.Contains("NHÂN VĂN")) return "USSH";
        if (u == "ULIS" || u.Contains("ULIS") || u.Contains("NGOẠI NGỮ")) return "ULIS";
        if (u == "UET" || u.Contains("UET") || u.Contains("CÔNG NGHỆ")) return "UET";
        if (u == "UED" || u.Contains("UED") || u.Contains("GIÁO DỤC")) return "UEd";
        if (u == "UEB" || u.Contains("UEB") || u.Contains("KINH TẾ")) return "UEB";
        if (u == "UMP" || u.Contains("UMP") || u.Contains("Y DƯỢC")) return "UMP";
        if (u == "VJU" || u.Contains("VJU") || u.Contains("VIỆT NHẬT")) return "VJU";
        if (u == "UL" || u.Contains("UL") || u.Contains("LUẬT")) return "UL";
        if (u == "IS" || u.Contains("QUỐC TẾ")) return "IS";
        if (u == "HSB" || u.Contains("HSB") || u.Contains("QUẢN TRỊ")) return "HSB";
        if (u == "SIS" || u.Contains("SIS") || u.Contains("LIÊN NGÀNH")) return "SIS";
        if (u == "ITI" || u.Contains("ITI") || u.Contains("CNTT")) return "ITI";
        if (u == "CRES" || u.Contains("CRES") || u.Contains("MÔI TRƯỜNG")) return "CRES";
        if (u == "IMBT" || u.Contains("IMBT") || u.Contains("VI SINH VẬT")) return "IMBT";
        if (u.Contains("IVIDES")) return "IVIDES";
        if (u.Contains("TNTI")) return "TNTI";
        if (u.Contains("TIP")) return "TIP";
        if (u.Contains("IQA") || u.Contains("ĐBCL") || u.Contains("KHẢO THÍ")) return "IQA";
        if (u.Contains("CEA") || u.Contains("KIỂM ĐỊNH")) return "CEA";
        if (u.Contains("LIC") || u.Contains("THƯ VIỆN") || u.Contains("NXB")) return "LIC";
        return "OTHER";
    }

    public static string ClassifyCsdl(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";
        var h = host.Trim().ToLowerInvariant();
        if (h.Contains("sciencedirect") || h.Contains("elsevier") || h.Contains("relx")) return "ScienceDirect";
        if (h.Contains("springer") || h.Contains("nature.com") || h.Contains("biomedcentral")) return "Springer Link";
        if (h.Contains("mathscinet") || h.Contains("ams.org")) return "MathSciNet";
        if (h.Contains("proquest") || h.Contains("clarivate") || h.Contains("chadwyck") || h.Contains("alexanderstreet")) return "ProQuest";
        return "";
    }

    public static readonly string[] CanonicalUnits = new string[]
    {
        "HUS", "USSH", "ULIS", "UET", "UEd", "UEB", "UMP", "VJU", "UL", "IS", "HSB", "SIS",
        "ITI", "CRES", "IMBT", "IVIDES", "TNTI", "TIP", "IQA", "CEA", "LIC", "OTHER"
    };

    public static readonly string[] CanonicalCsdls = new string[]
    {
        "ScienceDirect", "Springer Link", "MathSciNet", "ProQuest"
    };

    public async Task<object> GetCsdlMatrixStatsAsync(DateTime? from, DateTime? to)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var matrix = new Dictionary<string, Dictionary<string, Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        var unitUserSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unitHocUserSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unitBoUserSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unitRequests = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unitHocRequests = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unitBoRequests = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        
        var csdlUserSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var csdlHocUserSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var csdlBoUserSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var csdlRequests = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var csdlHocRequests = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var csdlBoRequests = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CanonicalCsdls)
        {
            csdlUserSets[c] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            csdlHocUserSets[c] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            csdlBoUserSets[c] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            csdlRequests[c] = 0;
            csdlHocRequests[c] = 0;
            csdlBoRequests[c] = 0;
        }

        var memberTypeRequests = new Dictionary<string, long>
        {
            { "nguoihoc", 0 }, { "canbo", 0 }
        };
        var memberTypeUserSets = new Dictionary<string, HashSet<string>>
        {
            { "nguoihoc", new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
            { "canbo", new HashSet<string>(StringComparer.OrdinalIgnoreCase) }
        };

        var cellData = new Dictionary<string, Dictionary<string, (long hocReq, long boReq, HashSet<string> hocUsers, HashSet<string> boUsers)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var u in CanonicalUnits)
        {
            unitUserSets[u] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            unitHocUserSets[u] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            unitBoUserSets[u] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            unitRequests[u] = 0;
            unitHocRequests[u] = 0;
            unitBoRequests[u] = 0;
            cellData[u] = new Dictionary<string, (long, long, HashSet<string>, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in CanonicalCsdls)
            {
                cellData[u][c] = (0, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        var filterSql = " WHERE allowed = 1 ";
        if (from.HasValue) filterSql += " AND timestamp >= $from ";
        if (to.HasValue) filterSql += " AND timestamp <= $to ";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT target_host, unit, username, COALESCE(member_type, 'canbo'), COUNT(*)
            FROM connections_log
            {filterSql}
            GROUP BY target_host, unit, username, member_type;
        ";
        if (from.HasValue) cmd.Parameters.AddWithValue("$from", from.Value.ToString("O"));
        if (to.HasValue) cmd.Parameters.AddWithValue("$to", to.Value.ToString("O"));

        long totalRequests = 0;
        var allUniqueUsersSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var host = reader.GetString(0);
                var csdl = ClassifyCsdl(host);
                if (string.IsNullOrEmpty(csdl)) continue;

                var rawUnit = reader.IsDBNull(1) ? "OTHER" : reader.GetString(1);
                var rawUser = reader.IsDBNull(2) ? "Khách (Chưa xác thực)" : reader.GetString(2);
                var rawMtype = reader.IsDBNull(3) ? "canbo" : reader.GetString(3);
                var count = reader.GetInt64(4);

                var (unit, mtype) = DetermineUnitAndMemberType(rawUser, rawUnit, rawMtype);
                if (!cellData.ContainsKey(unit)) unit = "OTHER";

                var cell = cellData[unit][csdl];
                if (mtype == "nguoihoc")
                {
                    cell.hocReq += count;
                    unitHocRequests[unit] += count;
                    csdlHocRequests[csdl] += count;
                    if (!rawUser.StartsWith("Khách", StringComparison.OrdinalIgnoreCase))
                    {
                        cell.hocUsers.Add(rawUser);
                        unitHocUserSets[unit].Add(rawUser);
                        csdlHocUserSets[csdl].Add(rawUser);
                    }
                }
                else
                {
                    cell.boReq += count;
                    unitBoRequests[unit] += count;
                    csdlBoRequests[csdl] += count;
                    if (!rawUser.StartsWith("Khách", StringComparison.OrdinalIgnoreCase))
                    {
                        cell.boUsers.Add(rawUser);
                        unitBoUserSets[unit].Add(rawUser);
                        csdlBoUserSets[csdl].Add(rawUser);
                    }
                }
                cellData[unit][csdl] = cell;

                totalRequests += count;
                unitRequests[unit] += count;
                csdlRequests[csdl] += count;
                memberTypeRequests[mtype] += count;

                if (!rawUser.StartsWith("Khách", StringComparison.OrdinalIgnoreCase))
                {
                    unitUserSets[unit].Add(rawUser);
                    csdlUserSets[csdl].Add(rawUser);
                    memberTypeUserSets[mtype].Add(rawUser);
                    allUniqueUsersSet.Add(rawUser);
                }
            }
        }

        var unitStats = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var unitRequestsMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unitUsersMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unitHocUsersMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unitBoUsersMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unitHocReqMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var unitBoReqMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var u in CanonicalUnits)
        {
            matrix[u] = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var uUsersCount = unitUserSets[u].Count;
            var uReqsCount = unitRequests[u];

            unitRequestsMap[u] = uReqsCount;
            unitUsersMap[u] = uUsersCount;
            unitHocUsersMap[u] = unitHocUserSets[u].Count;
            unitBoUsersMap[u] = unitBoUserSets[u].Count;
            unitHocReqMap[u] = unitHocRequests[u];
            unitBoReqMap[u] = unitBoRequests[u];

            unitStats[u] = new
            {
                requests = uReqsCount,
                users = uUsersCount,
                hoc_requests = unitHocRequests[u],
                hoc_users = unitHocUserSets[u].Count,
                bo_requests = unitBoRequests[u],
                bo_users = unitBoUserSets[u].Count
            };

            foreach (var c in CanonicalCsdls)
            {
                var cell = cellData[u][c];
                var allCellUsers = new HashSet<string>(cell.hocUsers, StringComparer.OrdinalIgnoreCase);
                allCellUsers.UnionWith(cell.boUsers);

                matrix[u][c] = new Dictionary<string, object>
                {
                    { "nguoihoc", cell.hocReq },
                    { "canbo", cell.boReq },
                    { "nguoihoc_users", cell.hocUsers.Count },
                    { "canbo_users", cell.boUsers.Count },
                    { "requests", cell.hocReq + cell.boReq },
                    { "users", allCellUsers.Count }
                };
            }
        }

        var csdlUsersMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byDatabaseStats = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CanonicalCsdls)
        {
            csdlUsersMap[c] = csdlUserSets[c].Count;
            byDatabaseStats[c] = new
            {
                requests = csdlRequests[c],
                users = csdlUserSets[c].Count,
                hoc_requests = csdlHocRequests[c],
                hoc_users = csdlHocUserSets[c].Count,
                bo_requests = csdlBoRequests[c],
                bo_users = csdlBoUserSets[c].Count
            };
        }

        var memberTypeUsersMap = new Dictionary<string, long>
        {
            { "nguoihoc", memberTypeUserSets["nguoihoc"].Count },
            { "canbo", memberTypeUserSets["canbo"].Count }
        };

        var onlineCount = ActiveIpUserMap.Values
            .Where(s => DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(2) && s.Username != "Khách (Chưa xác thực)" && s.ProxyEnabled)
            .Select(s => s.Username.ToLowerInvariant())
            .Distinct()
            .Count();

        var finalUniqueUsers = (long)allUniqueUsersSet.Count;

        return new
        {
            from = from?.ToString("yyyy-MM-ddTHH:mm:ss"),
            to = to?.ToString("yyyy-MM-ddTHH:mm:ss"),
            totalRequests,
            uniqueUsers = finalUniqueUsers,
            onlineUsers = onlineCount,
            byDatabase = csdlRequests,
            byDatabaseUsers = csdlUsersMap,
            byDatabaseStats,
            byUnit = unitRequestsMap,
            byUnitUsers = unitUsersMap,
            byUnitHocUsers = unitHocUsersMap,
            byUnitBoUsers = unitBoUsersMap,
            byUnitHocRequests = unitHocReqMap,
            byUnitBoRequests = unitBoReqMap,
            byUnitStats = unitStats,
            byMemberType = memberTypeRequests,
            byMemberTypeUsers = memberTypeUsersMap,
            canonicalUnits = CanonicalUnits,
            canonicalCsdls = CanonicalCsdls,
            matrix
        };
    }
}

