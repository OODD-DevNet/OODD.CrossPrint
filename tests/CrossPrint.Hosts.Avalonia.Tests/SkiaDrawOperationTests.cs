using Avalonia;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Hosts.Avalonia.Tests;

/// <summary>
/// Avalonia 合成器把 <c>ICustomDrawOperation.HitTest</c> 当作该视觉可命中的唯一依据；
/// 一旦这里返回 false，控件收不到任何指针事件，画布看起来正常却点不中元素。
/// 这条测试锁住"命中区 = 整块绘制区"的契约。
/// </summary>
public class SkiaDrawOperationTests
{
    [Fact]
    public void 命中区覆盖整块绘制区()
    {
        using var operation = new SkiaDrawOperation(new Rect(0, 0, 320, 200), (_, _) => { });

        operation.HitTest(new Point(10, 10)).Should().BeTrue("元素所在的左上区域必须可命中");
        operation.HitTest(new Point(160, 100)).Should().BeTrue("画布中心必须可命中");
        operation.HitTest(new Point(319.5, 199.5)).Should().BeTrue("右下内沿必须可命中");
        operation.HitTest(new Point(-1, 50)).Should().BeFalse("绘制区之外不应吞事件");
        operation.HitTest(new Point(321, 100)).Should().BeFalse("绘制区之外不应吞事件");
    }
}
