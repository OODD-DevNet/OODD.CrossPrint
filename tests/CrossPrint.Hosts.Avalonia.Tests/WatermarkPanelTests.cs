using System;
using System.Linq;
using System.Threading;
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
/// 水印设置区回归（用户反馈"输入文本都不生效"2026-11-04）：
/// 真实键盘输入「文字」「字段绑定」必须落到内核模型（headless 下直接赋 Text 不触发事件）。
/// 「测试数据」框已撤（与「文字」重复，用户要求只留一个）——模型字段保留作 JSON/导入兼容，面板不再露。
/// </summary>
public sealed class WatermarkPanelTests
{
    static readonly HeadlessUnitTestSession Session = HeadlessRenderEntryPoint.Session;

    [Fact]
    public async Task 水印面板_键盘输入文字与字段绑定_落内核模型()
    {
        await Session.Dispatch(Run, CancellationToken.None);
    }

    static void Run()
    {
        var window = new Window { Width = 1280, Height = 860, Content = new MainView() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var kernel = window.GetVisualDescendants().OfType<DesignerControl>().Single().Kernel;

        var text = FindBox(window, "水印文字");
        text.Should().NotBeNull("水印区「文字」框应随空态面板出现");
        FindBox(window, "需配合字段绑定").Should().BeNull("「测试数据」框已按用户要求撤除，面板不应再有第二个文本框");

        // 文字：真实键盘输入（全选替换，模拟用户改文字）+ Enter 提交 → Watermark.Text 必须变化。
        text!.Focus();
        text.SelectAll();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("新文字水印");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();
        kernel.Template.Watermark.Text.Should().Be("新文字水印", "「文字」框的键盘输入必须经提交链路落模型");

        // 字段绑定：真实键盘输入（全选替换）+ Enter → Watermark.Binding 必须变化。
        var binding = FindBox(window, "纯字段名或");
        binding.Should().NotBeNull("水印区「字段绑定」框应随空态面板出现");
        binding!.Focus();
        binding.SelectAll();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("customer");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();
        kernel.Template.Watermark.Binding.Should().Be("customer", "「字段绑定」框的键盘输入必须经提交链路落模型");

        window.Close();
    }

    /// <summary>按 Watermark 前缀找水印区的编辑框（空态面板里的 TextBox）。</summary>
    static TextBox? FindBox(Window window, string watermarkPrefix) =>
        window.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(b => (b.Watermark as string)?.Contains(watermarkPrefix, StringComparison.Ordinal) == true);
}
