using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrossPrint.Hosts.Avalonia;
using CrossPrint.Samples.Avalonia;
using FluentAssertions;
using Xunit;

namespace CrossPrint.Hosts.Avalonia.Tests;

/// <summary>
/// 样式库弹窗回归：两个样式间切换不得卡死（用户真机反馈 2026-11-04）。
/// 复现路径按真实操作：点「新增」加两个样式 → 点选样式 1 → 聚焦字号框 → 鼠标点样式 2
/// （点击引发 LostFocus 提交 → 列表在指针处理中途重建，这条路径才会触发卡死）。
/// </summary>
public sealed class StyleLibraryDialogTests
{
    static readonly HeadlessUnitTestSession Session = HeadlessRenderEntryPoint.Session;

    [Fact]
    public async Task 样式库_两个样式间真实点击切换_不卡死()
    {
        var lines = await Session.Dispatch(Run, CancellationToken.None);

        lines.Should().Contain(l => l.StartsWith("add ok"));
        lines.Should().Contain("pick-1 ok");
        lines.Should().Contain("pick-2 ok");
        lines.Should().Contain("pick-1-again ok");
    }

    static List<string> Run()
    {
        var lines = new List<string>();
        var window = new Window { Width = 1280, Height = 860, Content = new MainView() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var kernel = window.GetVisualDescendants().OfType<DesignerControl>().Single().Kernel;
        var before = kernel.Template.Styles.Count;

        _ = StyleLibraryDialog.Show(window, kernel);
        Dispatcher.UIThread.RunJobs();

        var styleWindow = window.OwnedWindows.OfType<Window>().Single(w => w.Title == "样式库");
        var list = styleWindow.GetVisualDescendants().OfType<ListBox>().Single();

        // 真实点击「新增」两次：与用户操作一致（点击按钮会移动焦点）。
        var add = styleWindow.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "新增");
        Click(styleWindow, add);
        Click(styleWindow, add);
        Dispatcher.UIThread.RunJobs();
        lines.Add($"add ok (styles {before}→{kernel.Template.Styles.Count})");

        styleWindow.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        styleWindow.UpdateLayout();

        void Pick(int index, string tag)
        {
            // 每次点击前重新取容器：切换会触发 RefreshList 整表重建，旧容器已脱离视觉树。
            var fresh = list.Items.OfType<ListBoxItem>().ToList();
            Click(styleWindow, fresh[index]);
            Dispatcher.UIThread.RunJobs();
            lines.Add($"{tag} ok");
        }

        Pick(0, "pick-1");

        // 聚焦字号框再点样式 2：点击引发 LostFocus → Commit → 列表在指针处理中途重建。
        var fontBox = styleWindow.GetVisualDescendants().OfType<TextBox>().First(b => b.Watermark?.ToString() == "倍数");
        fontBox.Focus();
        Dispatcher.UIThread.RunJobs();

        Pick(1, "pick-2");
        Pick(0, "pick-1-again");

        window.Close();
        return lines;
    }

    static void Click(Window window, Visual target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException("目标不可见");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
