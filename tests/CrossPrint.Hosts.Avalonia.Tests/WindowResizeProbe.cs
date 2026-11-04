using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrossPrint.Hosts.Avalonia;
using CrossPrint.Samples.Avalonia;
using FluentAssertions;
using Semi.Avalonia;
using Xunit;
using Xunit.Abstractions;

namespace CrossPrint.Hosts.Avalonia.Tests;

public sealed class WindowResizeProbe
{
    static readonly HeadlessUnitTestSession Session = HeadlessRenderEntryPoint.Session;

    readonly ITestOutputHelper _out;

    public WindowResizeProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task 平台层放大窗口后整帧不能有透明区域()
    {
        var lines = await Session.Dispatch(Run, CancellationToken.None);
        foreach (var line in lines)
            _out.WriteLine(line);

        lines.Should().ContainMatch("*透明像素=0*");
    }

    static List<string> Run()
    {
        var lines = new List<string>();
        var window = new Window
        {
            Width = 1320,
            Height = 880,
            Content = new MainView(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var designer = window.GetVisualDescendants().OfType<DesignerControl>().Single();
        lines.Add($"show: client={window.ClientSize} bounds={window.Bounds} frame={CaptureSize(window)}");

        // 模拟 Win32 拖动边框/最大化：平台层改客户区尺寸，Width/Height 由 Window.HandleResized 同步。
        foreach (var size in new[] { new Size(1593, 969), new Size(1920, 1280), new Size(1600, 900) })
        {
            // 模拟 Win32 拖动边框/最大化：直接走平台层 Resize（HeadlessWindowImpl 上是 public，
            // 但接口成员在 Avalonia 里不公开，所以用反射调用）。
            var impl = window.PlatformImpl!;
            impl.GetType()
                .GetMethod("Resize", new[] { typeof(Size), typeof(WindowResizeReason) })!
                .Invoke(impl, new object[] { size, WindowResizeReason.User });
            Dispatcher.UIThread.RunJobs();

            using var frame = Capture(window, $"avalon-probe-{(int)size.Width}x{(int)size.Height}.png");
            var corner = designer.TranslatePoint(new Point(designer.Bounds.Width - 3, designer.Bounds.Height - 3), window);
            lines.Add($"resize->{size}: client={window.ClientSize} bounds={window.Bounds} designer={designer.Bounds} " +
                      $"corner={corner} pixel={frame?.Sample(corner ?? default)} {frame?.Analyze()}");

            // 放大画布后同样不允许漏画。
            var center = designer.TranslatePoint(new Point(designer.Bounds.Width / 2, designer.Bounds.Height / 2), window)
                         ?? new Point(600, 400);
            for (var i = 0; i < 10; i++)
                window.MouseWheel(center, new Vector(0, 1), RawInputModifiers.Control);
            using var zoomed = Capture(window, $"avalon-probe-{(int)size.Width}x{(int)size.Height}-zoom.png");
            var zoomCorner = designer.TranslatePoint(new Point(designer.Bounds.Width - 3, designer.Bounds.Height - 3), window);
            lines.Add($"  zoom+: corner={zoomCorner} pixel={zoomed?.Sample(zoomCorner ?? default)} {zoomed?.Analyze()}");
            for (var i = 0; i < 10; i++)
                window.MouseWheel(center, new Vector(0, -1), RawInputModifiers.Control);
        }

        window.Close();
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "avalon-probe.txt"), lines);
        return lines;
    }

    static string CaptureSize(Window window) => window.CaptureRenderedFrame()?.PixelSize.ToString() ?? "null";

    static Shot? Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
            return null;

        var shot = new Shot(frame);
        shot.SavePng(name);
        return shot;
    }

    sealed class Shot : IDisposable
    {
        readonly WriteableBitmap _frame;
        readonly byte[] _pixels;
        readonly int _rowBytes;

        public Shot(WriteableBitmap frame)
        {
            _frame = frame;
            using var buffer = frame.Lock();
            _pixels = new byte[buffer.RowBytes * buffer.Size.Height];
            Marshal.Copy(buffer.Address, _pixels, 0, _pixels.Length);
            _rowBytes = buffer.RowBytes;
            Width = buffer.Size.Width;
            Height = buffer.Size.Height;
        }

        int Width { get; }
        int Height { get; }

        public string Sample(Point p)
        {
            var x = (int)Math.Round(p.X);
            var y = (int)Math.Round(p.Y);
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                return $"越界({x},{y})";
            var o = y * _rowBytes + x * 4;
            return $"{_pixels[o + 2]:X2}{_pixels[o + 1]:X2}{_pixels[o]:X2}/a{_pixels[o + 3]:X2}";
        }

        public string Analyze()
        {
            var transparent = 0;
            var bbox = "";
            var left = Width;
            var top = Height;
            var right = -1;
            var bottom = -1;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    if (_pixels[y * _rowBytes + x * 4 + 3] == 255)
                        continue;
                    transparent++;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            if (right >= 0)
                bbox = $"({left},{top})-({right},{bottom})";
            return $"帧={Width}x{Height} 透明像素={transparent} {(bbox.Length > 0 ? "区域=" + bbox : "")}";
        }

        public void SavePng(string name)
        {
            using var stream = File.Create(Path.Combine(Path.GetTempPath(), name));
            _frame.Save(stream);
        }

        public void Dispose() => _frame.Dispose();
    }
}
