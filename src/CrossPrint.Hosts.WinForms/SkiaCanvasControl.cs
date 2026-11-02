using System.Drawing.Imaging;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace CrossPrint.Hosts.WinForms;

/// <summary>
/// 最小 Skia 画布控件基类：把 Skia 渲染结果画到 WinForms 控件上。
/// </summary>
/// <remarks>
/// SkiaSharp 2.88.9 的 <c>SkiaSharp.Views.Desktop.Common</c> 只带 <see cref="SKPaintSurfaceEventArgs"/>，
/// <c>SKControl</c> 实际位于未引用的 <c>SkiaSharp.Views.WindowsForms</c> 包中。
/// 为保持依赖清单不变，这里按官方 SKControl 的同一套"锁定位图 + SKSurface"做法实现等价基类，
/// 事件形状与绘制约定保持一致。
/// </remarks>
public abstract class SkiaCanvasControl : Control
{
    Bitmap? _bitmap;

    /// <summary>创建画布控件并启用双缓冲与尺寸变化重绘。</summary>
    protected SkiaCanvasControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    /// <summary>绘制表面就绪事件；在 UI 线程上同步触发，直接向 <c>e.Surface.Canvas</c> 作画。</summary>
    public event EventHandler<SKPaintSurfaceEventArgs>? PaintSurface;

    /// <summary>当前画布位图尺寸（设备像素）；尚未创建时为 <see cref="SKSize.Empty"/>。</summary>
    public SKSize CanvasSize => _bitmap is null ? SKSize.Empty : new SKSize(_bitmap.Width, _bitmap.Height);

    /// <inheritdoc/>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var info = CreateBitmap();
        var bitmap = _bitmap;
        if (bitmap is null || info.Width <= 0 || info.Height <= 0)
            return;

        var data = bitmap.LockBits(
            new Rectangle(0, 0, info.Width, info.Height),
            ImageLockMode.WriteOnly,
            bitmap.PixelFormat);
        try
        {
            using var surface = SKSurface.Create(info, data.Scan0, data.Stride);
            OnPaintSurface(new SKPaintSurfaceEventArgs(surface, info));
            surface.Canvas.Flush();
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // 位图按设备像素建好、默认 96dpi，PerMonitorV2 下 DrawImage 会按 Graphics 的 DPI 再放大一次
        // （150% 屏即 1.5×）导致画面溢出且与指针坐标错位：切像素页单位、按位图像素尺寸贴回得到 1:1。
        var state = e.Graphics.Save();
        try
        {
            e.Graphics.PageUnit = GraphicsUnit.Pixel;
            e.Graphics.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }
        finally
        {
            e.Graphics.Restore(state);
        }
    }

    /// <summary>触发 <see cref="PaintSurface"/>；派生类可覆写以拦截绘制。</summary>
    /// <param name="e">画布与尺寸信息。</param>
    protected virtual void OnPaintSurface(SKPaintSurfaceEventArgs e) => PaintSurface?.Invoke(this, e);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }

        base.Dispose(disposing);
    }

    SKImageInfo CreateBitmap()
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        var info = new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);

        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = width > 0 && height > 0
                ? new Bitmap(width, height, PixelFormat.Format32bppPArgb)
                : null;
        }

        return info;
    }
}
