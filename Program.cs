using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CsSimConnect;
using CsSimConnect.DataDefs;
using CsSimConnect.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace C919Bridge;

public class PerfData
{
    [DataDefinition("TOTAL WEIGHT", Units = "kilograms", Type = DataType.Float64)]
    public double TotalWeight { get; set; }

    [DataDefinition("CG PERCENT", Units = "percent over 100", Type = DataType.Float64)]
    public double CgPercent { get; set; }

    [DataDefinition("AMBIENT TEMPERATURE", Units = "celsius", Type = DataType.Float64)]
    public double Oat { get; set; }

    [DataDefinition("SEA LEVEL PRESSURE", Units = "millibar", Type = DataType.Float64)]
    public double Qnh { get; set; }

    [DataDefinition("GROUND ALTITUDE", Units = "feet", Type = DataType.Float64)]
    public double GroundAlt { get; set; }

    [DataDefinition("AMBIENT WIND VELOCITY", Units = "knots", Type = DataType.Float64)]
    public double WindSpeed { get; set; }

    [DataDefinition("AMBIENT WIND DIRECTION", Units = "degrees", Type = DataType.Float64)]
    public double WindDir { get; set; }

    [DataDefinition("FUEL TOTAL QUANTITY WEIGHT", Units = "kilograms", Type = DataType.Float64)]
    public double FuelWeight { get; set; }

    [DataDefinition("GPS POSITION LAT", Units = "degrees", Type = DataType.Float64)]
    public double Lat { get; set; }

    [DataDefinition("GPS POSITION LON", Units = "degrees", Type = DataType.Float64)]
    public double Lon { get; set; }
}

public sealed class PerfSnapshot
{
    public double TotalWeight { get; init; }
    public double CgPercent { get; init; }
    public double Oat { get; init; }
    public double Qnh { get; init; }
    public double GroundAlt { get; init; }
    public double WindSpeed { get; init; }
    public double WindDir { get; init; }
    public double FuelWeight { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
}

public static class NavData
{
    public sealed class Airport
    {
        public string Icao = "";
        public string Name = "";
        public double Lat, Lon;
    }

    public sealed class Runway
    {
        public string Ident = "";
        public double HeadingMag;
        public double LengthM;
    }

    private static readonly Dictionary<string, Airport> airports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Runway>> runways = new(StringComparer.OrdinalIgnoreCase);

    public static bool Loaded { get; private set; }
    public static string Status { get; private set; } = "未导入导航数据";
    public static string SourcePath { get; private set; } = "";

    public static string NavDir => Path.Combine(AppContext.BaseDirectory, "navdata");

    public static void AutoLoad()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // 1. 机模 WASM 目录下用户安装的导航数据（NAIP 2610n 等，最新）
        var wasmNav = Path.Combine(
            appData, "Microsoft Flight Simulator 2024", "WASM", "MSFS2020",
            "fycyc-aircraft-c919x", "work", "NavigationData", "db.s3db");
        try
        {
            if (File.Exists(wasmNav))
            {
                Load(wasmNav);
                return;
            }
        }
        catch { }

        // 2. 机模 Community 目录捆绑的默认导航数据（.s3db，取最新周期）
        var bundledDir = Path.Combine(
            appData, "Microsoft Flight Simulator 2024", "Packages", "Community",
            "fycyc-aircraft-c919x", "Navigraph", "BundledData");
        try
        {
            if (Directory.Exists(bundledDir))
            {
                var s3 = Directory.GetFiles(bundledDir, "*.s3db")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();
                if (s3 != null)
                {
                    Load(s3);
                    return;
                }
            }
        }
        catch { }

        // 3. Fenix 导航数据
        const string fenixPath = @"C:\ProgramData\Fenix\Navdata\imported.db3";
        if (File.Exists(fenixPath))
        {
            Load(fenixPath);
            return;
        }

        // 4. 手动导入（应用目录 navdata 文件夹）
        try
        {
            if (Directory.Exists(NavDir))
            {
                var db = Directory.GetFiles(NavDir, "*.s3db")
                    .Concat(Directory.GetFiles(NavDir, "*.db3"))
                    .FirstOrDefault();
                if (db != null)
                {
                    Load(db);
                    return;
                }
            }
        }
        catch { }

        Loaded = false;
        Status = "未找到导航数据。请点击「导航数据」按钮，将机模的 .s3db 放入 navdata 文件夹";
    }

    public static void OpenNavDir()
    {
        try
        {
            Directory.CreateDirectory(NavDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = NavDir,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    public static void Load(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath))
            {
                Status = "文件不存在: " + dbPath;
                Loaded = false;
                return;
            }
            airports.Clear();
            runways.Clear();

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            if (HasTable(conn, "tbl_pa_airports"))
                LoadNavigraph(conn);
            else if (HasTable(conn, "Airports"))
                LoadFenix(conn);
            else
            {
                Loaded = false;
                Status = "无法识别的导航数据库格式";
                return;
            }

            Loaded = airports.Count > 0;
            SourcePath = dbPath;
            Status = Loaded
                ? $"已加载 {airports.Count} 个机场 / {runways.Values.Sum(l => l.Count)} 条跑道"
                : "数据库为空";
        }
        catch (Exception ex)
        {
            Loaded = false;
            Status = "导入失败: " + ex.Message;
        }
    }

    private static bool HasTable(SqliteConnection conn, string name)
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        c.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }

    private static void LoadNavigraph(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT airport_identifier, airport_name, airport_ref_latitude, airport_ref_longitude FROM tbl_pa_airports WHERE airport_identifier IS NOT NULL AND TRIM(airport_identifier) <> ''";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var icao = r.GetString(0).Trim();
                if (icao.Length != 4 || r.IsDBNull(2) || r.IsDBNull(3)) continue;
                airports[icao] = new Airport
                {
                    Icao = icao,
                    Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    Lat = r.GetDouble(2),
                    Lon = r.GetDouble(3),
                };
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT airport_identifier, runway_identifier, runway_length, runway_magnetic_bearing FROM tbl_pg_runways WHERE airport_identifier IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var icao = r.GetString(0).Trim();
                var ident = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                if (string.IsNullOrEmpty(ident)) continue;
                if (ident.StartsWith("RW", StringComparison.OrdinalIgnoreCase))
                    ident = ident.Substring(2);
                var lenM = r.GetDouble(2) * 0.3048;
                var hdg = r.IsDBNull(3) ? IdentToHeading(ident) : r.GetDouble(3);
                var rw = new Runway { Ident = ident, HeadingMag = hdg, LengthM = lenM };
                if (!runways.TryGetValue(icao, out var list))
                {
                    list = new List<Runway>();
                    runways[icao] = list;
                }
                list.Add(rw);
            }
        }
    }

    private static void LoadFenix(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ICAO, Name, Latitude, Longtitude FROM Airports WHERE ICAO IS NOT NULL AND TRIM(ICAO) <> ''";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var icao = r.GetString(0).Trim();
                if (icao.Length != 4 || r.IsDBNull(2) || r.IsDBNull(3)) continue;
                airports[icao] = new Airport
                {
                    Icao = icao,
                    Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    Lat = r.GetDouble(2),
                    Lon = r.GetDouble(3),
                };
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT ap.ICAO, rw.Ident, rw.Length
FROM Runways rw
JOIN Airports ap ON rw.AirportID = ap.ID
WHERE ap.ICAO IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var icao = r.GetString(0).Trim();
                var ident = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                if (string.IsNullOrEmpty(ident)) continue;
                var rw = new Runway
                {
                    Ident = ident,
                    HeadingMag = IdentToHeading(ident),
                    LengthM = r.GetInt64(2) * 0.3048,
                };
                if (!runways.TryGetValue(icao, out var list))
                {
                    list = new List<Runway>();
                    runways[icao] = list;
                }
                list.Add(rw);
            }
        }
    }

    public static Airport? FindNearest(double lat, double lon, out double distKm)
    {
        distKm = 0;
        Airport? best = null;
        double bd = double.MaxValue;
        foreach (var ap in airports.Values)
        {
            var d = (ap.Lat - lat) * (ap.Lat - lat) + (ap.Lon - lon) * (ap.Lon - lon);
            if (d < bd) { bd = d; best = ap; }
        }
        if (best != null)
            distKm = DistanceKm(lat, lon, best.Lat, best.Lon);
        return best;
    }

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    public static Airport? GetAirport(string icao) =>
        airports.TryGetValue(icao, out var ap) ? ap : null;

    public static List<Runway> GetRunways(string icao) =>
        runways.TryGetValue(icao, out var list) ? list : new List<Runway>();

    private static double IdentToHeading(string ident)
    {
        var digits = new string(ident.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return 0;
        return int.TryParse(digits, out var n) ? n * 10.0 : 0;
    }
}

public static class Bridge
{
    public static readonly SimConnect Sim = SimConnect.Instance;
    private static readonly PerfData live = new();
    private static volatile PerfSnapshot? snapshot = null;
    private static DateTime lastUpdate = DateTime.MinValue;
    private static string streamError = "";
    private static int streamStarted = 0;
    private static TcpListener? tcpListener;
    private static CancellationTokenSource? cts;
    private static readonly string HtmlPath = Path.Combine(AppContext.BaseDirectory, "www", "index.html");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool IsConnected => Sim.IsConnected;
    public static DateTime LastUpdate => lastUpdate;
    public static bool HasData => snapshot != null;
    public static int Port { get; private set; } = 8090;

    public static void Start()
    {
        cts = new CancellationTokenSource();
        Sim.OnOpen += _ => StartDataStream();
        TryConnect();
        NavData.AutoLoad();

        for (int p = 8090; p <= 8099; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                tcpListener = l;
                Port = p;
                break;
            }
            catch { }
        }

        if (tcpListener == null)
            throw new Exception("无法绑定端口 8090-8099，请检查是否有其他程序占用。");

        Task.Run(AcceptLoop, cts.Token);
        Task.Run(ReconnectLoop, cts.Token);
    }

    public static void Shutdown()
    {
        try { tcpListener?.Stop(); tcpListener = null; } catch { }
        try { cts?.Cancel(); cts?.Dispose(); cts = null; } catch { }
        try { Sim.Disconnect(); } catch { }
    }

    private static void AcceptLoop()
    {
        while (tcpListener != null && cts != null && !cts.IsCancellationRequested)
        {
            try
            {
                var client = tcpListener.AcceptTcpClient();
                Task.Run(() => HandleClient(client));
            }
            catch { break; }
        }
    }

    private static void HandleClient(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, true);
            var requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0].ToUpperInvariant();
            var path = parts[1];

            var contentLength = 0;
            string? headerLine;
            while (!string.IsNullOrEmpty(headerLine = reader.ReadLine()))
            {
                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(headerLine.Substring("Content-Length:".Length).Trim(), out contentLength);
            }

            byte[] body;
            string contentType;
            var statusCode = 200;

            if (path.StartsWith("/theme", StringComparison.Ordinal))
            {
                var mode = "dark";
                var qi = path.IndexOf('?');
                if (qi >= 0)
                {
                    foreach (var kv in path.Substring(qi + 1).Split('&'))
                    {
                        var kvp = kv.Split('=');
                        if (kvp.Length == 2 && kvp[0] == "mode") mode = kvp[1];
                    }
                }
                MainForm.Current?.ApplyTheme(mode);
                body = Encoding.UTF8.GetBytes("{\"ok\":true}");
                contentType = "application/json; charset=utf-8";
            }
            else if (path == "/data")
            {
                body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GetPayload(), JsonOpts));
                contentType = "application/json; charset=utf-8";
            }
            else if (path == "/airport" || path.StartsWith("/airport?"))
            {
                var icao = "";
                var qi = path.IndexOf('?');
                if (qi >= 0)
                {
                    foreach (var kv in path.Substring(qi + 1).Split('&'))
                    {
                        var p = kv.Split('=');
                        if (p.Length == 2 && p[0] == "icao")
                            icao = Uri.UnescapeDataString(p[1]);
                    }
                }
                body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GetAirportPayload(icao), JsonOpts));
                contentType = "application/json; charset=utf-8";
            }
            else if (path.StartsWith("/simbrief"))
            {
                var qi = path.IndexOf('?');
                var userid = "";
                if (qi >= 0)
                {
                    foreach (var kv in path.Substring(qi + 1).Split('&'))
                    {
                        var p = kv.Split('=');
                        if (p.Length == 2 && p[0] == "userid")
                            userid = Uri.UnescapeDataString(p[1]);
                    }
                }
                var sb = GetSimBrief(userid).GetAwaiter().GetResult();
                body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sb, JsonOpts));
                contentType = "application/json; charset=utf-8";
            }
            else if (path == "/" || path == "/index.html")
            {
                if (File.Exists(HtmlPath))
                {
                    body = File.ReadAllBytes(HtmlPath);
                    contentType = "text/html; charset=utf-8";
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("<meta charset='utf-8'><h1>缺少 www/index.html</h1>");
                    contentType = "text/html; charset=utf-8";
                    statusCode = 404;
                }
            }
            else
            {
                body = Encoding.UTF8.GetBytes("Not Found");
                contentType = "text/plain; charset=utf-8";
                statusCode = 404;
            }

            var statusText = statusCode == 200 ? "200 OK"
                : statusCode == 400 ? "400 Bad Request"
                : statusCode == 405 ? "405 Method Not Allowed"
                : "404 Not Found";
            var header = $"HTTP/1.1 {statusText}\r\n" +
                         $"Content-Type: {contentType}\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Access-Control-Allow-Origin: *\r\n" +
                         "Cache-Control: no-store\r\n" +
                         "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }
        catch { }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private static void ReconnectLoop()
    {
        while (cts != null && !cts.IsCancellationRequested)
        {
            if (!Sim.IsConnected)
            {
                try { Sim.Connect(); if (Sim.IsConnected) StartDataStream(); } catch { }
            }
            try { Task.Delay(5000, cts.Token).Wait(); } catch { break; }
        }
    }

    private static void TryConnect()
    {
        try { Sim.Connect(); } catch { }
    }

    private static void StartDataStream()
    {
        if (Interlocked.CompareExchange(ref streamStarted, 1, 0) != 0) return;
        try
        {
            DataManager.Instance.RequestData(
                live,
                onNext: () =>
                {
                    snapshot = new PerfSnapshot
                    {
                        TotalWeight = live.TotalWeight,
                        CgPercent = live.CgPercent,
                        Oat = live.Oat,
                        Qnh = live.Qnh,
                        GroundAlt = live.GroundAlt,
                        WindSpeed = live.WindSpeed,
                        WindDir = live.WindDir,
                        FuelWeight = live.FuelWeight,
                        Lat = live.Lat,
                        Lon = live.Lon,
                    };
                    lastUpdate = DateTime.Now;
                },
                onError: ex =>
                {
                    streamError = ex.Message;
                    Interlocked.Exchange(ref streamStarted, 0);
                },
                period: ObjectDataPeriod.PerSecond
            );
        }
        catch (Exception ex)
        {
            streamError = ex.Message;
            Interlocked.Exchange(ref streamStarted, 0);
        }
    }

    private static object GetPayload()
    {
        if (!Sim.IsConnected)
            return new { ok = false, msg = "未连接 MSFS，请启动模拟器并载入 C919。" };

        if (snapshot == null && Interlocked.CompareExchange(ref streamStarted, 1, 0) == 0)
            StartDataStream();

        var s = snapshot;
        if (s == null)
            return new { ok = false, msg = streamError.Length > 0 ? "数据流错误: " + streamError : "等待数据到达..." };

        if (DateTime.Now - lastUpdate > TimeSpan.FromSeconds(10))
            return new { ok = false, msg = "数据已过期（模拟器可能暂停）。" };

        return new
        {
            ok = true,
            tow = Math.Round(s.TotalWeight),
            cg = Math.Round(s.CgPercent * 100, 1),
            qnh = Math.Round(s.Qnh, 1),
            elev = Math.Round(s.GroundAlt),
            oat = Math.Round(s.Oat, 1),
            windSpd = Math.Round(s.WindSpeed),
            windDir = Math.Round(s.WindDir),
            fuel = Math.Round(s.FuelWeight),
            lat = Math.Round(s.Lat, 5),
            lon = Math.Round(s.Lon, 5),
        };
    }

    private static object GetAirportPayload(string icao)
    {
        if (!NavData.Loaded)
            return new { ok = false, msg = NavData.Status };

        NavData.Airport? ap = null;
        double distKm = 0;

        if (!string.IsNullOrEmpty(icao))
        {
            ap = NavData.GetAirport(icao.Trim().ToUpperInvariant());
            if (ap == null)
                return new { ok = false, msg = "机场 " + icao.ToUpperInvariant() + " 不在导航数据中" };
            if (snapshot != null)
                distKm = Math.Round(NavData.DistanceKm(snapshot.Lat, snapshot.Lon, ap.Lat, ap.Lon));
        }
        else
        {
            var s = snapshot;
            if (s == null)
                return new { ok = false, msg = "等待位置数据..." };
            ap = NavData.FindNearest(s.Lat, s.Lon, out distKm);
            distKm = Math.Round(distKm);
            if (ap == null)
                return new { ok = false, msg = "未找到附近机场" };
        }

        var rws = NavData.GetRunways(ap.Icao)
            .OrderBy(r => r.Ident)
            .Select(r => new
            {
                ident = r.Ident,
                hdg = r.HeadingMag,
                len = Math.Round(r.LengthM),
            })
            .ToList();

        return new
        {
            ok = true,
            icao = ap.Icao,
            name = ap.Name,
            lat = ap.Lat,
            lon = ap.Lon,
            dist = distKm,
            runways = rws,
        };
    }

    private static async Task<object> GetSimBrief(string userid)
    {
        try
        {
            var url = "https://www.simbrief.com/api/xml.fetcher.php?userid=" +
                      Uri.EscapeDataString(userid) + "&json=1";
            var resp = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;

            if (!root.TryGetProperty("fetch", out var fetch))
                return new { ok = false, msg = "SimBrief 响应异常" };
            var status = fetch.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            if (!status.Contains("Success"))
                return new { ok = false, msg = "SimBrief: " + (status.Length > 0 ? status : "未知错误") };

            string icao = "", rwy = "", dest = "", flight = "", airline = "", reg = "", acft = "";
            double elev = 0, towKg = 0, zfwKg = 0, fuelKg = 0;
            int pax = 0;
            double cargoKg = 0;

            if (root.TryGetProperty("general", out var gen))
            {
                if (gen.TryGetProperty("icao_airline", out var al)) airline = al.GetString() ?? "";
                if (gen.TryGetProperty("flight_number", out var fn)) flight = fn.GetString() ?? "";
            }

            if (root.TryGetProperty("origin", out var origin))
            {
                if (origin.TryGetProperty("icao_code", out var ic)) icao = ic.GetString() ?? "";
                if (origin.TryGetProperty("plan_rwy", out var pr)) rwy = pr.GetString() ?? "";
                if (origin.TryGetProperty("elevation", out var el) && el.ValueKind == JsonValueKind.Number)
                    elev = el.GetDouble();
            }

            if (root.TryGetProperty("destination", out var dst) &&
                dst.TryGetProperty("icao_code", out var dc)) dest = dc.GetString() ?? "";

            if (root.TryGetProperty("aircraft", out var ac))
            {
                if (ac.TryGetProperty("reg", out var rg)) reg = rg.GetString() ?? "";
                if (ac.TryGetProperty("name", out var nm)) acft = nm.GetString() ?? "";
            }

            if (root.TryGetProperty("weights", out var w))
            {
                if (w.TryGetProperty("est_tow", out var tow) && tow.ValueKind == JsonValueKind.Number)
                    towKg = Math.Round(tow.GetDouble() * 0.45359237);
                if (w.TryGetProperty("est_zfw", out var zfw) && zfw.ValueKind == JsonValueKind.Number)
                    zfwKg = Math.Round(zfw.GetDouble() * 0.45359237);
                if (w.TryGetProperty("est_fuel", out var fu) && fu.ValueKind == JsonValueKind.Number)
                    fuelKg = Math.Round(fu.GetDouble() * 0.45359237);
                if (w.TryGetProperty("cargo", out var cg) && cg.ValueKind == JsonValueKind.Number)
                    cargoKg = Math.Round(cg.GetDouble() * 0.45359237);
                if (w.TryGetProperty("pax_count", out var px))
                {
                    if (px.ValueKind == JsonValueKind.Number) pax = (int)Math.Round(px.GetDouble());
                    else if (px.ValueKind == JsonValueKind.String && int.TryParse(px.GetString(), out var pv)) pax = pv;
                }
            }

            return new
            {
                ok = true,
                icao,
                dest,
                rwy,
                elev = Math.Round(elev),
                tow = towKg,
                zfw = zfwKg,
                fuel = fuelKg,
                pax,
                cargo = cargoKg,
                flight = (airline + flight).Trim(),
                reg,
                acft,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, msg = "SimBrief 请求失败: " + ex.Message };
        }
    }
}

public class MainForm : Form
{
    private readonly WebView2 webView;
    private readonly Label statusLabel;
    private readonly Label timeLabel;
    private readonly TableLayoutPanel bar;
    private readonly Button navButton;
    private readonly System.Windows.Forms.Timer timer;
    private string theme = "dark";
    public static MainForm? Current;

    public MainForm()
    {
        Current = this;
        Text = "C919 起飞性能计算器 v1.0.0";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        ClientSize = new Size(1140, 800);
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 15, 28);
        Font = new Font("Segoe UI", 9f);

        bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            ColumnCount = 3,
            BackColor = Color.FromArgb(13, 17, 32),
            Padding = new Padding(14, 0, 14, 0),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));

        statusLabel = new Label
        {
            Text = "○ 正在连接 MSFS...",
            ForeColor = Color.FromArgb(251, 191, 36),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        navButton = new Button
        {
            Text = NavData.Loaded ? "导航数据 ✓" : "导航数据：点击导入",
            FlatStyle = FlatStyle.Flat,
            Dock = DockStyle.Fill,
            Margin = new Padding(2),
            BackColor = Color.FromArgb(22, 30, 50),
            ForeColor = Color.FromArgb(52, 211, 153),
            Font = new Font("Segoe UI", 8.5f),
            Cursor = Cursors.Hand,
        };
        navButton.FlatAppearance.BorderSize = 0;
        navButton.Click += (s, e) =>
        {
            NavData.OpenNavDir();
            NavData.AutoLoad();
            navButton.Text = NavData.Loaded ? "导航数据 ✓" : "导航数据：点击导入";
            navButton.ForeColor = NavData.Loaded ? OkColor : WarnColor;
        };
        timeLabel = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(147, 167, 198),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
        };
        bar.Controls.Add(statusLabel, 0, 0);
        bar.Controls.Add(navButton, 1, 0);
        bar.Controls.Add(timeLabel, 2, 0);

        webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(webView);
        Controls.Add(bar);

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (s, e) => UpdateStatus();
        timer.Start();

        Load += async (s, e) => await InitializeWebViewAsync();
        FormClosed += (s, e) => Bridge.Shutdown();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "C919TakeoffCalc");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await webView.EnsureCoreWebView2Async(env);
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.DefaultBackgroundColor = Color.FromArgb(10, 15, 28);
            webView.CoreWebView2.Navigate($"http://localhost:{Bridge.Port}/");
        }
        catch (Exception ex)
        {
            statusLabel.Text = "⚠ 界面加载失败: " + ex.Message;
            statusLabel.ForeColor = ErrColor;
        }
    }

    private bool Light => theme == "light";
    private Color OkColor => Light ? Color.FromArgb(15, 122, 90) : Color.FromArgb(52, 211, 153);
    private Color WarnColor => Light ? Color.FromArgb(138, 90, 0) : Color.FromArgb(251, 191, 36);
    private Color ErrColor => Light ? Color.FromArgb(179, 38, 30) : Color.FromArgb(248, 113, 113);

    public void ApplyTheme(string mode)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => ApplyTheme(mode))); return; }
        theme = mode == "light" ? "light" : "dark";
        BackColor = Light ? Color.FromArgb(233, 228, 218) : Color.FromArgb(10, 15, 28);
        bar.BackColor = Light ? Color.FromArgb(233, 228, 218) : Color.FromArgb(13, 17, 32);
        statusLabel.ForeColor = Light ? Color.FromArgb(38, 34, 27) : Color.FromArgb(230, 238, 252);
        timeLabel.ForeColor = Light ? Color.FromArgb(139, 130, 114) : Color.FromArgb(147, 167, 198);
        navButton.BackColor = Light ? Color.FromArgb(236, 231, 220) : Color.FromArgb(22, 30, 50);
        navButton.ForeColor = NavData.Loaded ? OkColor : WarnColor;
        bar.Invalidate();
    }

    private void UpdateStatus()
    {
        if (Bridge.IsConnected)
        {
            statusLabel.Text = "● MSFS 已连接";
            statusLabel.ForeColor = OkColor;
        }
        else
        {
            statusLabel.Text = "○ 未连接 MSFS（自动重试中）";
            statusLabel.ForeColor = WarnColor;
        }
        timeLabel.Text = Bridge.HasData ? "数据更新 " + Bridge.LastUpdate.ToString("HH:mm:ss") : "";
    }
}

public static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        try
        {
            Bridge.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "启动失败: " + ex.Message + "\n\n可能原因：8090-8099 端口均被占用。",
                "C919 起飞性能计算器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Bridge.Shutdown();
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Bridge.Shutdown();
        AppDomain.CurrentDomain.ProcessExit += (s, e) => Bridge.Shutdown();

        Application.Run(new MainForm());

        Bridge.Shutdown();
    }
}
