using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace CrossPrint.Samples.Maui;

/// <summary>
/// 页内模态覆盖层（MAUI 8 没有 ContentDialog）：盖住整窗的半透明层 + 居中卡片（标题/内容/错误行/按钮）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShowAsync"/> 的 <c>validate</c> 在点「确定」时执行：返回 null 视为通过并关闭（true），
/// 返回错误串则把错误显示在卡片内、对话框留在原地——与 WinUI ContentDialog 的
/// <c>PrimaryButtonClick args.Cancel</c> 校验习惯等价。
/// </para>
/// <para>
/// 点击背景等价于取消（false）。同一时刻只允许一个模态，重复调用直接返回 false。
/// </para>
/// </remarks>
internal sealed class ModalHost : Grid
{
    readonly Label _title = new();
    readonly Label _error = new();
    readonly VerticalStackLayout _contentSlot = new();
    TaskCompletionSource<bool>? _tcs;
    Func<string?>? _validate;

    public ModalHost()
    {
        // 半透明遮罩（比不透明灰更接近系统对话框观感）；IsVisible=false 常驻视觉树。
        Background = new SolidColorBrush(Color.FromRgba(0x20, 0x20, 0x20, 0x66));
        IsVisible = false;

        var backdrop = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = 0,
        };
        backdrop.Clicked += (_, _) => Close(false);

        _title.FontAttributes = FontAttributes.Bold;
        _title.FontSize = 16;
        _title.Margin = new Thickness(0, 0, 0, 10);

        _error.TextColor = Colors.Firebrick;
        _error.LineBreakMode = LineBreakMode.WordWrap;
        _error.IsVisible = false;
        _error.Margin = new Thickness(0, 8, 0, 0);

        var ok = new Button { Text = "确定", HorizontalOptions = LayoutOptions.Fill };
        ok.Clicked += (_, _) => Confirm();

        var cancel = new Button { Text = "取消", HorizontalOptions = LayoutOptions.Fill };
        cancel.Clicked += (_, _) => Close(false);

        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(ok, 0);
        Grid.SetColumn(cancel, 2);
        buttons.Add(ok);
        buttons.Add(cancel);

        var card = new VerticalStackLayout { _title, _contentSlot, _error, buttons };
        var cardBorder = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            Stroke = Colors.LightGray,
            StrokeThickness = 1,
            Padding = new Thickness(16),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = card,
        };

        Add(backdrop);
        Add(cardBorder);
    }

    /// <summary>打开模态。<c>validate</c> 返回 null 表示通过；返回错误串则留在原地显示。</summary>
    /// <returns>true = 确定，false = 取消/背景点击。</returns>
    public Task<bool> ShowAsync(string title, View content, Func<string?>? validate)
    {
        if (_tcs is not null)
            return Task.FromResult(false);

        _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _validate = validate;
        _title.Text = title;
        _contentSlot.Clear();
        _contentSlot.Add(content);
        SetError(null);
        IsVisible = true;
        return _tcs.Task;
    }

    void Confirm()
    {
        string? error = null;
        try
        {
            error = _validate?.Invoke();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            SetError(error);
            return;
        }

        Close(true);
    }

    void Close(bool confirmed)
    {
        if (_tcs is null)
            return;

        IsVisible = false;
        _contentSlot.Clear();
        _validate = null;
        var tcs = _tcs;
        _tcs = null;
        tcs.TrySetResult(confirmed);
    }

    void SetError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.IsVisible = message is not null;
    }
}
