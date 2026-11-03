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

namespace CrossPrint.Hosts.Avalonia.Tests;

/// <summary>无头窗口入口：走真实 Skia 合成器（关掉 stub 绘制），才能抓帧量像素。</summary>
public static class HeadlessRenderEntryPoint
{
    /// <summary>
    /// 整个测试程序共用一份无头会话。每个 <see cref="HeadlessUnitTestSession.StartNew"/> 都会
    /// 再注册一次 <c>avares</c> URI 解析器，同一进程里第二次会抛
    /// <c>InvalidOperationException: A URI scheme name 'avares' already has a registered custom parser</c>。
    /// </summary>
    internal static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.StartNew(typeof(HeadlessRenderEntryPoint));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessRenderApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>与样例桌面宿主一致的主题，保证量到的是用户看到的窗口。</summary>
public sealed class HeadlessRenderApp : Application
{
    public override void Initialize() => Styles.Add(new SemiTheme());
}

/// <summary>
/// 整窗像素回归：样例窗口里不允许出现"没画到"的区域。
/// 画布自绘只铺 <c>canvas.LocalClipBounds</c>，一旦控件重排后新露出的区域没被重绘，
/// 那里就会露出窗口底色（白）——用户看到的就是"右下方是透明的"。
/// </summary>
public sealed class WindowRenderTransparencyTests
{
    static readonly HeadlessUnitTestSession Session = HeadlessRenderEntryPoint.Session;

    [Fact]
    public async Task 放大窗口与放大画布后画布工作区仍是灰底()
    {
        var result = await Session.Dispatch(Run, CancellationToken.None);

        result.DefaultWorkspace.Should().Be(WorkspaceGray, "[默认尺寸] 画布右下角应是工作区灰底");
        result.ResizedWorkspace.Should().Be(WorkspaceGray, $"[放大窗口后] 新露出的右下角漏画了：{result.ResizedDetail}");
        result.ZoomedOutWorkspace.Should().Be(WorkspaceGray, $"[缩小画布后] 画布右下角漏画了：{result.ZoomedOutDetail}");
        result.ZoomedTransparentPixels.Should().Be(0, $"[放大画布后] 窗口内有透明像素：{result.ZoomedDetail}");
    }

    // EditorRendererOptions.WorkspaceColor = #B4B4B4。
    const string WorkspaceGray = "B4B4B4";

    static RenderCaseResult Run()
    {
        var window = new Window
        {
            Width = 1320,
            Height = 880,
            Content = new MainView(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var designer = window.GetVisualDescendants().OfType<DesignerControl>().Single();

        using var defaultFrame = Capture(window, "avalon-headless-default.png");
        var defaultWorkspace = SampleWorkspaceCorner(defaultFrame, designer, window);

        // 只放大窗口、不动画布：整页适配的视口里，新露出来的四边都应该是工作区灰底。
        window.Width = 1560;
        window.Height = 1000;
        Dispatcher.UIThread.RunJobs();
        using var resizedFrame = Capture(window, "avalon-headless-resized.png");
        var resizedPoint = WorkspaceCornerPoint(designer, window);
        var resizedWorkspace = resizedFrame.Sample(resizedPoint);
        var resizedDetail = resizedFrame.Describe(resizedPoint);

        // 缩小画布：页纸四周全是工作区，右下角不能露出窗口白底。
        var center = designer.TranslatePoint(
                         new Point(designer.Bounds.Width / 2, designer.Bounds.Height / 2), window)
                     ?? new Point(600, 400);
        for (var i = 0; i < 6; i++)
            window.MouseWheel(center, new Vector(0, -1), RawInputModifiers.Control);
        using var zoomedOutFrame = Capture(window, "avalon-headless-zoomed-out.png");
        var zoomedOutPoint = WorkspaceCornerPoint(designer, window);
        var zoomedOutWorkspace = zoomedOutFrame.Sample(zoomedOutPoint);
        var zoomedOutDetail = zoomedOutFrame.Describe(zoomedOutPoint);

        // 放大画布：整窗不允许有 alpha < 255 的像素。
        for (var i = 0; i < 12; i++)
            window.MouseWheel(center, new Vector(0, 1), RawInputModifiers.Control);
        using var zoomedFrame = Capture(window, "avalon-headless-zoomed.png");
        var zoomed = zoomedFrame.Analyze();

        window.Close();

        return new RenderCaseResult(
            defaultWorkspace,
            resizedWorkspace,
            resizedDetail,
            zoomedOutWorkspace,
            zoomedOutDetail,
            zoomed.TransparentPixels,
            zoomed.ToString());
    }

    /// <summary>画布右下角内侧 3px 处的点：那里只可能是工作区灰底，除非漏画。</summary>
    static Point WorkspaceCornerPoint(DesignerControl designer, Window window) =>
        designer.TranslatePoint(new Point(designer.Bounds.Width - 3, designer.Bounds.Height - 3), window)
        ?? throw new InvalidOperationException("画布不在窗口可视树里");

    static string SampleWorkspaceCorner(WindowFrame frame, DesignerControl designer, Window window) =>
        frame.Sample(WorkspaceCornerPoint(designer, window));

    static WindowFrame Capture(Window window, string artifactName)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var frame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("无头合成器没有产出帧；检查 UseSkia + UseHeadlessDrawing=false。");
        var windowFrame = new WindowFrame(frame);
        windowFrame.SavePng(artifactName);
        return windowFrame;
    }

    sealed record RenderCaseResult(
        string DefaultWorkspace,
        string ResizedWorkspace,
        string ResizedDetail,
        string ZoomedOutWorkspace,
        string ZoomedOutDetail,
        int ZoomedTransparentPixels,
        string ZoomedDetail);

    /// <summary>一帧的像素快照：按窗口 DIP 采样（DPR=1 时 1 DIP = 1 像素）。</summary>
    sealed class WindowFrame : IDisposable
    {
        readonly WriteableBitmap _frame;
        readonly byte[] _pixels;

        public WindowFrame(WriteableBitmap frame)
        {
            _frame = frame;
            using var buffer = frame.Lock();
            _pixels = new byte[buffer.RowBytes * buffer.Size.Height];
            Marshal.Copy(buffer.Address, _pixels, 0, _pixels.Length);
            RowBytes = buffer.RowBytes;
            Width = buffer.Size.Width;
            Height = buffer.Size.Height;
        }

        int RowBytes { get; }

        int Width { get; }

        int Height { get; }

        public string Sample(Point p)
        {
            var x = (int)Math.Round(p.X);
            var y = (int)Math.Round(p.Y);
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                return $"越界({x},{y})";

            var offset = y * RowBytes + x * 4;
            return $"{_pixels[offset]:X2}{_pixels[offset + 1]:X2}{_pixels[offset + 2]:X2}";
        }

        public string Describe(Point p)
        {
            var x = (int)Math.Round(p.X);
            var y = (int)Math.Round(p.Y);
            return $"点({x},{y})={Sample(p)} 帧={Width}x{Height}";
        }

        public FrameStats Analyze()
        {
            var transparent = 0;
            var left = Width;
            var top = Height;
            var right = -1;
            var bottom = -1;

            for (var y = 0; y < Height; y++)
            {
                var rowStart = y * RowBytes;
                for (var x = 0; x < Width; x++)
                {
                    if (_pixels[rowStart + x * 4 + 3] == 255)
                        continue;

                    transparent++;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            var box = right < 0 ? "无" : $"({left},{top})-({right},{bottom})";
            return new FrameStats(Width, Height, transparent, box, Describe(new Point(Width - 1, Height - 1)));
        }

        public void SavePng(string name)
        {
            var path = Path.Combine(Path.GetTempPath(), name);
            using var stream = File.Create(path);
            _frame.Save(stream);
        }

        public void Dispose() => _frame.Dispose();
    }

    sealed record FrameStats(int Width, int Height, int TransparentPixels, string TransparentBox, string BottomRightPixel)
    {
        public override string ToString() =>
            $"{Width}x{Height} 透明像素={TransparentPixels} 区域={TransparentBox} 右下角={BottomRightPixel}";
    }
}
