using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace IslandX.Core;

/// <summary>
/// 当前位置。走系统的 <see cref="Geolocator"/>，失败时回落到配置里手填的坐标。
///
/// ⚠ 这个类本身不联网，但它的结果**会被发到天气服务商**。位置比曲目名敏感得多，
/// 所以天气功能默认关闭（见 <c>AppConfig.WeatherEnabled</c>），
/// 而且坐标在发出前会降精度（见 <see cref="Coordinate.Rounded"/>）。
/// </summary>
public static class GeoLocation
{
    /// <summary>一个坐标，以及它是怎么来的（供探针诊断）。</summary>
    public readonly record struct Coordinate(double Latitude, double Longitude, string Source)
    {
        /// <summary>
        /// 降到 2 位小数（约 1km）再发出去。
        ///
        /// 天气只需要城市级精度，而系统给的是 ±100m 级 —— 那个精度足以定位到具体楼栋，
        /// 发给第三方没有任何必要。少的这几位不影响预报，却明显缩小了泄露面。
        /// </summary>
        public Coordinate Rounded() => this with
        {
            Latitude = Math.Round(Latitude, 2),
            Longitude = Math.Round(Longitude, 2),
        };

        public override string ToString() => $"{Latitude:F2},{Longitude:F2}/{Source}";
    }

    /// <summary>最近一次失败的原因，供探针诊断。</summary>
    public static string LastError { get; private set; } = "-";

    /// <summary>
    /// 解析当前位置。手填的坐标**优先** —— 用户明确指定过，就不该再去问系统，
    /// 那既是多余的权限使用，也让"我明明填了却不生效"变成一个难查的问题。
    /// </summary>
    public static async Task<Coordinate?> TryResolveAsync(double? manualLat, double? manualLon)
    {
        if (manualLat is { } lat && manualLon is { } lon
            && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180
            && (lat != 0 || lon != 0))
        {
            return new Coordinate(lat, lon, "manual");
        }

        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                // 非打包桌面应用能否定位，取决于系统隐私设置里那两个开关。
                // 这里把具体状态记下来 —— "定位失败"和"用户没开权限"是两回事，
                // 后者要引导用户去设置，前者要查代码
                LastError = $"权限{access}";
                return null;
            }

            // 3km 精度就够天气用了。要更高精度会去开更耗电的定位源，
            // 也提高了权限门槛，换来的精度对降水预报毫无意义
            var locator = new Geolocator { DesiredAccuracyInMeters = 3000 };
            var position = await locator.GetGeopositionAsync();
            var point = position.Coordinate.Point.Position;

            LastError = "-";
            return new Coordinate(
                point.Latitude, point.Longitude,
                position.Coordinate.PositionSource.ToString());
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name;
            return null;
        }
    }

    /// <summary>
    /// 坐标 → 行政区。用于把全国预警列表筛成本地的。
    ///
    /// 为什么需要这一步：系统定位**只给坐标**，实测 <c>CivicAddress</c> 在桌面上是空的
    /// （本机只返回国家 CN），而气象预警是按行政区组织的。
    ///
    /// 用 OpenStreetMap 的 Nominatim：免费、不需要 key。它有速率限制（1 次/秒）
    /// 和必须带可识别 UA 的要求 —— 这里 6 小时才查一次，远在限制之内。
    /// </summary>
    public static async Task<Region?> TryResolveRegionAsync(
        HttpClient http, Coordinate at, CancellationToken ct = default)
    {
        try
        {
            var url =
                $"https://nominatim.openstreetmap.org/reverse?lat={Fmt(at.Latitude)}"
                + $"&lon={Fmt(at.Longitude)}&format=json&zoom=10&accept-language=zh-CN";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Nominatim 明确要求可识别的 UA，用默认值会被封
            request.Headers.TryAddWithoutValidation("User-Agent", "IslandX/1.0 (weather alerts)");

            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseRegion(json);
        }
        catch (Exception ex)
        {
            LastError = $"geo/{ex.GetType().Name}";
            return null;
        }
    }

    /// <summary>
    /// 从 Nominatim 的响应里抽出省与本地名。单独拆出来是为了能喂样本断言 ——
    /// 字段命名在不同国家/层级下并不一致，光靠读文档定不下来。
    /// </summary>
    internal static Region? ParseRegion(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("address", out var address)) return null;

        var province = Read(address, "state") ?? Read(address, "province");
        if (string.IsNullOrWhiteSpace(province)) return null;

        var locals = new List<string>();

        // city 字段**不一定是地级市**：实测本机所在的市辖区里，它给的是区名而不是市名。
        // 所以除了这几个字段，还要从 display_name 里把带行政后缀的名字全捞出来 ——
        // "沈阳市"只出现在那里，而预警标题恰恰是按"省+市+区"写的。
        foreach (var field in new[] { "city", "county", "district", "town", "municipality" })
        {
            var value = Read(address, field);
            if (!string.IsNullOrWhiteSpace(value) && !locals.Contains(value)) locals.Add(value);
        }

        if (root.TryGetProperty("display_name", out var displayName)
            && displayName.GetString() is { } full)
        {
            foreach (var part in full.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == province) continue;
                if (!EndsWithAdminSuffix(part)) continue;
                if (locals.Contains(part)) continue;

                locals.Add(part);
            }
        }

        // 从粗到细排一遍。上面两处来源给出的顺序是任意的 —— 本机实测拿到的是
        // ["浑南区", "沈阳市"]（city 字段给了区、市名是从 display_name 里捞的）。
        //
        // 匹配预警时它是个集合、顺序无所谓，所以这个问题一直没显形；
        // 但设置页会把它拼成配置文本给用户看，那时"辽宁省浑南区沈阳市"就是一句乱码。
        // 稳定排序，同级保持原顺序。
        locals.Sort((a, b) => AdminRank(a).CompareTo(AdminRank(b)));

        return new Region(province, locals);

        static string? Read(JsonElement address, string field)
            => address.TryGetProperty(field, out var v) ? v.GetString() : null;
    }

    /// <summary>
    /// 行政层级的粗细，数字越小越粗。只用于把名字排成人能读的顺序。
    ///
    /// 不追求覆盖全部层级 —— 分成"地级"、"县级"、"其余"三档就够把
    /// 市与区县摆对，而这正是唯一会读错的地方。
    /// </summary>
    private static int AdminRank(string name) => name.Length == 0
        ? 3
        : name[^1] switch
        {
            '市' or '州' or '盟' => 1,
            '区' or '县' or '旗' => 2,
            _ => 3,
        };

    /// <summary>
    /// 是不是一个行政区名。用后缀判断而不是收一份行政区划表：
    /// 表要维护、会过期，而这里只需要把 display_name 里的街道、邮编、国名滤掉。
    /// </summary>
    private static bool EndsWithAdminSuffix(string name)
    {
        if (name.Length < 2) return false;

        return name[^1] switch
        {
            '市' or '区' or '县' or '旗' or '盟' or '州' => true,
            _ => false,
        };
    }

    private static string Fmt(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
}
