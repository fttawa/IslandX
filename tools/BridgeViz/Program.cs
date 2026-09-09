using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IslandX.Rendering;

namespace BridgeViz;

/// <summary>
/// 几何验证台：把 <see cref="FluidBridge"/> 在一系列间距下的输出画成 PNG。
///
/// 存在的理由是流体桥这段是**解析几何**，算错了不会抛异常、只会画出一个怪形状 ——
/// 而它在产品里只在"媒体 + 瞬时事件同时存在"那几秒钟露面，靠碰运气截图去看不现实。
/// 这里能一次性铺开全部间距，包括桥刚好接上与刚好断开的临界点。
///
///   dotnet run --project tools/BridgeViz -- [输出路径]
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "bridge.png");

        double[] gaps = [-8, 0, 6, 12, 18, 24, 28, 34, 44];
        var maxGap = FluidBridge.MaxGap(40, 32);
        Console.WriteLine($"桥能撑住的最大间距: {maxGap:F1}px（超过即退回双形状）");

        const int width = 660;
        const int rowHeight = 92;
        var height = rowHeight * gaps.Length;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 28, 32)), null,
                new Rect(0, 0, width, height));

            var fill = new SolidColorBrush(Color.FromRgb(12, 12, 14));
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1);
            var ok = new SolidColorBrush(Color.FromRgb(0x7A, 0xD9, 0x8A));
            var no = new SolidColorBrush(Color.FromRgb(0xE0, 0x8C, 0x6A));

            for (var i = 0; i < gaps.Length; i++)
            {
                var cy = (i * rowHeight) + (rowHeight / 2.0);

                // 左：收起态胶囊尺度；右：瞬时事件的小圆
                var left = new Rect(30, cy - 20, 190, 40);
                var right = new Rect(left.Right + gaps[i], cy - 16, 32, 32);

                var bridged = FluidBridge.Build(left, right);
                string note;

                if (bridged is not null)
                {
                    dc.DrawGeometry(fill, edge, bridged);
                    note = $"gap {gaps[i],5:0} → 桥接";
                }
                else
                {
                    // 桥接不上时产品里会退回两个独立形状，这里照样画出来看临界点
                    dc.DrawGeometry(fill, edge, Squircle.Build(left, left.Height / 2));
                    dc.DrawGeometry(fill, edge, Squircle.Build(right, right.Height / 2));
                    note = $"gap {gaps[i],5:0} → 断开";
                }

                dc.DrawText(
                    new FormattedText(note, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Consolas"), 14, bridged is not null ? ok : no, 1.25),
                    new Point(width - 190, cy - 9));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(output);
        encoder.Save(stream);

        Console.WriteLine($"已输出 {output}");
        return 0;
    }
}
