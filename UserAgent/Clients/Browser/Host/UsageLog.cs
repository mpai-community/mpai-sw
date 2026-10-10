// WHO USES THE SERVICE, WHEN IT IS REACHED THROUGH A PROXY.
//
// On a RunPod pod the host is reached through RunPod's proxy: every request arrives from an
// address inside the proxy, and the person's own address is only in a header the proxy adds
// (Cloudflare's CF-Connecting-IP, or X-Forwarded-For). This writes, for each person who opens
// the client ("visit") and each session they start ("session"), a line in
//
//     <Directory>/usage-YYYY-MM-DD.jsonl      {"t":"...","e":"visit","ip":"...","cc":"IT","src":"CF-Connecting-IP"}
//
// and answers GET /MPAI/Usage?days=7 - the numbers of visits, sessions and distinct addresses, per
// day and per address - to whoever presents the bearer token in Usage:SummaryToken.
//
// IT IS OFF UNLESS SWITCHED ON: Usage:Enabled = true in appsettings.json beside this program (or the
// environment variable MPAI_USAGE_LOG=1). Nothing else of the client changes, and nothing of what a
// person says or shows is looked at: only the address, the country the proxy states, the time and
// which of the two events it was.
//
// AN ADDRESS IS PERSONAL DATA (GDPR). Usage:IpMode says what is kept:
//     full       the address as the proxy gives it
//     truncated  IPv4 to /24 (a.b.c.0), IPv6 to /48: the network, not the person
//     hashed     a keyed hash (12 hex digits): distinct addresses can be counted, none read back
// and Usage:RetentionDays (90 by default) is how long a file is kept before it is deleted.
//
// WHICH HEADER HOLDS THE ADDRESS is Usage:IpHeaders, in order of preference. A header that a person
// can send themselves (X-Forwarded-For) can be forged; the proxy's own (CF-Connecting-IP) cannot.
// Usage:Probe = true writes, for the first 20 requests, every header that may hold an address to
// usage-probe.log, to see which one the proxy sets; leave it on only until that is known.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class UsageLog
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly string[] DefaultHeaders = { "CF-Connecting-IP", "True-Client-IP", "X-Real-IP", "X-Forwarded-For" };

    private readonly string directory;
    private readonly string mode;
    private readonly string[] ipHeaders;
    private readonly string? summaryToken;
    private readonly int retentionDays;
    private readonly bool probe;
    private readonly object gate = new();
    private byte[]? hashKey;
    private int probed;
    private DateOnly purged;

    private UsageLog(string directory, string mode, string[] ipHeaders, string? summaryToken, int retentionDays, bool probe)
    {
        this.directory = directory; this.mode = mode; this.ipHeaders = ipHeaders;
        this.summaryToken = summaryToken; this.retentionDays = retentionDays; this.probe = probe;
    }

    // Null when the usage log is not switched on.
    public static UsageLog? Create(IConfiguration configuration, string root)
    {
        var s = configuration.GetSection("Usage");
        var on = s.GetValue<bool?>("Enabled") ?? Environment.GetEnvironmentVariable("MPAI_USAGE_LOG") == "1";
        if (!on) return null;

        var dir = s["Directory"] is { Length: > 0 } d ? (Path.IsPathRooted(d) ? d : Path.Combine(root, d)) : Path.Combine(root, "logs");
        Directory.CreateDirectory(dir);
        var mode = (s["IpMode"] ?? "full").Trim().ToLowerInvariant();
        if (mode is not ("full" or "truncated" or "hashed")) mode = "full";
        var headers = s.GetSection("IpHeaders").Get<string[]>() is { Length: > 0 } h ? h : DefaultHeaders;
        var token = s["SummaryToken"] is { Length: > 0 } t ? t : Environment.GetEnvironmentVariable("MPAI_USAGE_TOKEN");
        var days = s.GetValue<int?>("RetentionDays") ?? 90;
        var log = new UsageLog(dir, mode, headers, string.IsNullOrEmpty(token) ? null : token, Math.Max(1, days), s.GetValue<bool>("Probe"));
        Console.WriteLine($"  Usage log: {dir} (addresses: {mode}; kept {log.retentionDays} days; summary {(log.summaryToken is null ? "off" : "on, at /MPAI/Usage")})");
        return log;
    }

    // THE MIDDLEWARE: notes the two events, and passes every request on.
    public async Task Invoke(HttpContext ctx, RequestDelegate next)
    {
        try
        {
            var ev = Classify(ctx.Request);
            if (ev is not null) Note(ctx, ev);
            if (probe) Probe(ctx);
        }
        catch (Exception ex) { Console.WriteLine($"[usage] not written: {ex.Message}"); }   // never at the cost of a request
        await next(ctx);
    }

    private static string? Classify(HttpRequest r)
    {
        var p = r.Path.Value ?? "";
        if (HttpMethods.IsGet(r.Method) && (p == "/" || p.Equals("/index.html", StringComparison.OrdinalIgnoreCase))) return "visit";
        if (HttpMethods.IsPost(r.Method) && p.TrimEnd('/').Equals("/MPAI/AIFU/Controller", StringComparison.OrdinalIgnoreCase)) return "session";
        return null;
    }

    private (string Ip, string Source) Address(HttpContext ctx)
    {
        foreach (var name in ipHeaders)
            if (ctx.Request.Headers.TryGetValue(name, out var value))
            {
                var first = value.ToString().Split(',')[0].Trim();
                if (IPAddress.TryParse(first, out var a)) return (Normal(a).ToString(), name);
            }
        var remote = ctx.Connection.RemoteIpAddress;
        return (remote is null ? "unknown" : Normal(remote).ToString(), "remote");
    }

    private static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private string Keep(string ip)
    {
        if (mode == "full" || !IPAddress.TryParse(ip, out var a)) return ip;
        if (mode == "truncated")
        {
            var b = a.GetAddressBytes();
            var keep = b.Length == 4 ? 3 : 6;
            for (var i = keep; i < b.Length; i++) b[i] = 0;
            return new IPAddress(b).ToString() + (b.Length == 4 ? "/24" : "/48");
        }
        hashKey ??= Key();
        return Convert.ToHexString(HMACSHA256.HashData(hashKey, Encoding.UTF8.GetBytes(ip)))[..12].ToLowerInvariant();
    }

    // The key of the hash is kept beside the logs, so the same address gives the same hash from day to day.
    private byte[] Key()
    {
        var path = Path.Combine(directory, "usage.key");
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        return key;
    }

    private void Note(HttpContext ctx, string ev)
    {
        var (ip, src) = Address(ctx);
        var country = ctx.Request.Headers["CF-IPCountry"].ToString();
        var line = JsonSerializer.Serialize(new Line(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ev, Keep(ip),
            country.Length is 2 ? country.ToUpperInvariant() : null, src), Json);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        lock (gate)
        {
            File.AppendAllText(Path.Combine(directory, $"usage-{day:yyyy-MM-dd}.jsonl"), line + "\n");
            if (purged != day) { purged = day; Purge(day); }
        }
    }

    private void Probe(HttpContext ctx)
    {
        lock (gate)
        {
            if (probed >= 20) return;
            probed++;
            var sb = new StringBuilder($"{DateTime.UtcNow:O} {ctx.Request.Method} {ctx.Request.Path} remote={ctx.Connection.RemoteIpAddress}");
            foreach (var h in ctx.Request.Headers)
                if (h.Key.Contains("forward", StringComparison.OrdinalIgnoreCase) || h.Key.Contains("ip", StringComparison.OrdinalIgnoreCase)
                    || h.Key.StartsWith("cf-", StringComparison.OrdinalIgnoreCase) || h.Key.Contains("real", StringComparison.OrdinalIgnoreCase)
                    || h.Key.Contains("client", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Via", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"\n    {h.Key}: {h.Value}");
            File.AppendAllText(Path.Combine(directory, "usage-probe.log"), sb + "\n");
        }
    }

    private void Purge(DateOnly today)
    {
        foreach (var f in Directory.EnumerateFiles(directory, "usage-????-??-??.jsonl"))
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f)[6..], "yyyy-MM-dd", out var d) && d < today.AddDays(-retentionDays))
                try { File.Delete(f); } catch { }
    }

    // GET /MPAI/Usage?days=7 - with the bearer token of Usage:SummaryToken; without one set, there is no such page.
    public IResult Summary(HttpContext ctx)
    {
        if (summaryToken is null) return Results.NotFound();
        var given = ctx.Request.Headers.Authorization.ToString();
        given = given.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? given[7..].Trim() : ctx.Request.Headers["X-Usage-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(summaryToken)))
            return Results.Unauthorized();

        var days = int.TryParse(ctx.Request.Query["days"], out var n) ? Math.Clamp(n, 1, 3650) : 7;
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1 - days);
        var perDay = new SortedDictionary<string, (int Visits, int Sessions, HashSet<string> Ips)>();
        var perIp = new Dictionary<string, Row>();
        var all = new HashSet<string>();
        int visits = 0, sessions = 0;
        lock (gate)
            foreach (var f in Directory.EnumerateFiles(directory, "usage-????-??-??.jsonl").OrderBy(x => x))
            {
                if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f)[6..], "yyyy-MM-dd", out var d) || d < from) continue;
                foreach (var text in File.ReadLines(f))
                {
                    Line? l;
                    try { l = JsonSerializer.Deserialize<Line>(text, Json); } catch { continue; }
                    if (l?.Ip is null) continue;
                    var key = d.ToString("yyyy-MM-dd");
                    if (!perDay.TryGetValue(key, out var day)) perDay[key] = day = (0, 0, new HashSet<string>());
                    if (!perIp.TryGetValue(l.Ip, out var row)) perIp[l.Ip] = row = new Row { Ip = l.Ip, First = l.T };
                    row.Last = l.T; row.Country ??= l.Cc;
                    all.Add(l.Ip); day.Ips.Add(l.Ip);
                    if (l.E == "session") { sessions++; row.Sessions++; perDay[key] = (day.Visits, day.Sessions + 1, day.Ips); }
                    else { visits++; row.Visits++; perDay[key] = (day.Visits + 1, day.Sessions, day.Ips); }
                }
            }
        return Results.Json(new
        {
            from = from.ToString("yyyy-MM-dd"), to = DateTime.UtcNow.ToString("yyyy-MM-dd"), addresses = mode,
            visits, sessions, distinctAddresses = all.Count,
            days = perDay.Select(p => new { date = p.Key, visits = p.Value.Visits, sessions = p.Value.Sessions, addresses = p.Value.Ips.Count }),
            byAddress = perIp.Values.OrderByDescending(r => r.Sessions).ThenByDescending(r => r.Visits).Take(500)
                .Select(r => new { address = r.Ip, country = r.Country, visits = r.Visits, sessions = r.Sessions, first = r.First, last = r.Last }),
            byCountry = perIp.Values.GroupBy(r => r.Country ?? "?").OrderByDescending(g => g.Count())
                .Select(g => new { country = g.Key, addresses = g.Count(), sessions = g.Sum(r => r.Sessions) })
        });
    }

    private sealed record Line(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("ip")] string Ip,
        [property: JsonPropertyName("cc")] string? Cc,
        [property: JsonPropertyName("src")] string? Src);

    private sealed class Row { public string Ip = ""; public string? Country, First, Last; public int Visits, Sessions; }
}
