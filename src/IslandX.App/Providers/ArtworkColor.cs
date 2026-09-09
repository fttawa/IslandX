using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandX.Providers;

internal static class ArtworkColor
{
    /// <summary>色相分桶数（每桶 30°）。</summary>
    private const int HueBuckets = 12;

    /// <summary>低于此饱和度的像素算作"无色调"，并入灰度统计。</summary>
    private const double ChromaFloor = 0.12;

    /// <summary>有色像素权重占比低于此值时，认定整张封面是灰度的。</summary>
    private const double ColorfulShare = 0.05;

    /// <summary>灰度封面使用的中性冷白，略偏蓝以免显得发黄。</summary>
    private static readonly Color NeutralAccent = Color.FromRgb(188, 196, 210);

#if DEBUG
    /// <summary>最近一次取色走到哪个分支，用于定位"为什么没取到色"。</summary>
    internal static string DebugLastReason = "-";
#endif

    /// <summary>
    /// 从封面提取主色，用于频谱条着色。
    ///
    /// 用**色相直方图取主导色**，而不是对整幅图做加权平均：
    /// 平均会让不同色相互相抵消（红 + 绿 = 灰），于是鲜艳但色相分散的封面
    /// 也会算出一个接近灰的结果，进而被误判成"这张封面没有色调"。
    /// 分桶后取权重最大的那一桶，抓到的是真正的主色。
    ///
    /// 确实是黑白封面时返回中性冷白，而不是返回 null —— 中性色比错色好，也比没颜色好。
    /// </summary>
    internal static Color? Extract(BitmapSource source)
    {
        try
        {
            var scaled = new TransformedBitmap(
                source,
                new ScaleTransform(16.0 / source.PixelWidth, 16.0 / source.PixelHeight));

            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                Mark($"bad-size {width}x{height}");
                return null;
            }

            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var bucketWeight = new double[HueBuckets];
            var bucketR = new double[HueBuckets];
            var bucketG = new double[HueBuckets];
            var bucketB = new double[HueBuckets];

            double colorfulWeight = 0, grayWeight = 0;

            // 不受亮度过滤影响的整体亮度，用于纯白/纯黑封面的兜底
            double lumSum = 0;
            var lumCount = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (pixels[i + 3] < 32) continue;

                RgbToHsl((byte)r, (byte)g, (byte)b, out var h, out var s, out var l);

                lumSum += l;
                lumCount++;

                // 压掉接近纯黑与纯白的像素：它们定不了调子
                var lumCenter = 1 - Math.Abs((l * 2) - 1);
                if (lumCenter <= 0.02) continue;

                if (s < ChromaFloor)
                {
                    grayWeight += lumCenter;
                    continue;
                }

                var weight = s * lumCenter;
                var bucket = (int)(h * HueBuckets) % HueBuckets;
                if (bucket < 0) bucket += HueBuckets;

                bucketWeight[bucket] += weight;
                bucketR[bucket] += r * weight;
                bucketG[bucket] += g * weight;
                bucketB[bucket] += b * weight;
                colorfulWeight += weight;
            }

            var total = colorfulWeight + grayWeight;
            if (total <= 0)
            {
                // 整张图都是极亮或极暗（纯白封面就属于这种），加权统计全被压成 0。
                // 这时仍要给个颜色：亮封面用它自己的白，暗封面用中性冷白（纯黑没法当强调色）。
                if (lumCount == 0)
                {
                    Mark("no-pixels");
                    return null;
                }

                var avgLum = lumSum / lumCount;
                Mark($"extreme lum={avgLum:F2}");
                return avgLum > 0.5 ? Color.FromRgb(228, 232, 240) : NeutralAccent;
            }

            // 有色像素太少 —— 这是张黑白/极低饱和的封面
            if (colorfulWeight / total < ColorfulShare)
            {
                Mark($"neutral share={colorfulWeight / total:F3}");
                return NeutralAccent;
            }

            var best = 0;
            for (var i = 1; i < HueBuckets; i++)
                if (bucketWeight[i] > bucketWeight[best]) best = i;

            if (bucketWeight[best] <= 0)
            {
                Mark("neutral empty-bucket");
                return NeutralAccent;
            }

            var w = bucketWeight[best];
            Mark($"hue-bucket {best} share={colorfulWeight / total:F3}");
            return BoostForAccent(
                (byte)Math.Clamp(bucketR[best] / w, 0, 255),
                (byte)Math.Clamp(bucketG[best] / w, 0, 255),
                (byte)Math.Clamp(bucketB[best] / w, 0, 255));
        }
        catch (Exception ex)
        {
            Mark($"exception {ex.GetType().Name}");
            return null;
        }
    }

    private static void Mark(string reason)
    {
#if DEBUG
        DebugLastReason = reason;
#endif
    }

    /// <summary>
    /// 把主色调整到"能当强调色用"的状态。
    /// 关键是提饱和度而非只提亮度：主色本身可能偏暗或偏灰，
    /// 只提亮度的话结果仍是一团灰，压在近黑岛体上根本看不见。
    /// </summary>
    private static Color BoostForAccent(byte r, byte g, byte b)
    {
        RgbToHsl(r, g, b, out var h, out var s, out var l);

        s = Math.Max(s, 0.55);
        l = Math.Clamp(l, 0.45, 0.68);   // 收进一个在近黑岛体上足够醒目的亮度区间

        return HslToRgb(h, s, l);
    }

    private static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
    {
        double dr = r / 255.0, dg = g / 255.0, db = b / 255.0;
        var max = Math.Max(dr, Math.Max(dg, db));
        var min = Math.Min(dr, Math.Min(dg, db));
        var delta = max - min;

        l = (max + min) / 2;

        if (delta < 1e-6)
        {
            h = 0;
            s = 0;
            return;
        }

        s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);

        if (Math.Abs(max - dr) < 1e-9) h = ((dg - db) / delta) + (dg < db ? 6 : 0);
        else if (Math.Abs(max - dg) < 1e-9) h = ((db - dr) / delta) + 2;
        else h = ((dr - dg) / delta) + 4;

        h /= 6;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        if (s <= 0)
        {
            var v = (byte)Math.Clamp(l * 255, 0, 255);
            return Color.FromRgb(v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;

        return Color.FromRgb(
            (byte)Math.Clamp(HueToChannel(p, q, h + (1.0 / 3)) * 255, 0, 255),
            (byte)Math.Clamp(HueToChannel(p, q, h) * 255, 0, 255),
            (byte)Math.Clamp(HueToChannel(p, q, h - (1.0 / 3)) * 255, 0, 255));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;

        if (t < 1.0 / 6) return p + ((q - p) * 6 * t);
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + ((q - p) * ((2.0 / 3) - t) * 6);
        return p;
    }
}
