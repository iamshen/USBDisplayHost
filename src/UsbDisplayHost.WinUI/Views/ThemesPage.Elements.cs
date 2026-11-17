using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using UsbDisplayHost.Core.Themes;
using UsbDisplayHost.Desktop.ViewModels;
using Windows.System;
using Windows.Foundation;

namespace UsbDisplayHost.WinUI.Views;

public sealed partial class ThemesPage
{
    private ThemeElement? SelectedElement => scene is { } current && elementIndex >= 0 && elementIndex < current.Elements.Length
        ? current.Elements[elementIndex] : null;

    private void SelectElement(int index, bool rebuild = false)
    {
        if (scene is not { } current) { return; }
        elementIndex = Math.Clamp(index, -1, current.Elements.Length - 1);
        refreshing = true;
        try
        {
            if (rebuild) { ElementList.ItemsSource = current.Elements.Select(element => ElementLabel(element.Kind)).ToArray(); }
            ElementList.SelectedIndex = elementIndex;
        }
        finally { refreshing = false; }
        RefreshElement();
    }

    private static string ElementLabel(ThemeElementKind kind) => kind switch
    {
        ThemeElementKind.Clock => "时钟",
        ThemeElementKind.FlipClock => "翻页时钟",
        ThemeElementKind.AnalogClock => "指针时钟",
        ThemeElementKind.CpuUsage => "CPU 使用率",
        ThemeElementKind.MemoryUsage => "内存占用",
        ThemeElementKind.HostTemperature => "主机温度",
        ThemeElementKind.DeviceTemperature => "设备温度",
        ThemeElementKind.Image => "图片",
        ThemeElementKind.Text => "文字",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private void OnElementSelected(object sender, SelectionChangedEventArgs args)
    {
        if (!refreshing) { SelectElement(ElementList.SelectedIndex); }
    }

    private void RefreshElement()
    {
        var selected = SelectedElement;
        Properties.Visibility = VisibleWhen(selected is not null);
        SelectionOverlay.Children.Clear();
        if (selected is null) { return; }
        var (width, height) = selected.GetSize();
        bool image = selected.Kind == ThemeElementKind.Image;
        refreshing = true;
        try
        {
            ElementX.Minimum = image ? 1 - ThemeElement.CanvasWidth : 0;
            ElementX.Maximum = image ? ThemeElement.CanvasWidth - 1 : ThemeElement.CanvasWidth - width;
            ElementY.Minimum = image ? 1 - ThemeElement.CanvasHeight : 0;
            ElementY.Maximum = image ? ThemeElement.CanvasHeight - 1 : ThemeElement.CanvasHeight - height;
            ElementX.Value = selected.X;
            ElementY.Value = selected.Y;
            ElementFont.Value = selected.FontSize;
            ElementColor.Text = ThemeFields.ColorText(selected.Color);
            ElementText.Text = selected.Text;
            Clock24Hours.IsChecked = selected.Use24HourClock;
            ElementFit.SelectedIndex = (int)selected.Fit;
            FontRow.Visibility = ColorRow.Visibility = VisibleWhen(!image);
            ElementText.Visibility = VisibleWhen(selected.Kind == ThemeElementKind.Text);
            Clock24Hours.Visibility = VisibleWhen(selected.Kind is ThemeElementKind.Clock or ThemeElementKind.FlipClock);
            ElementFit.Visibility = VisibleWhen(image);
        }
        finally { refreshing = false; }

        var stroke = WorkspacePalette.GetBrush("WorkspaceFocusBrush");
        if (!image)
        {
            double centerX = selected.X + width / 2.0;
            double centerY = selected.Y + height / 2.0;
            SelectionOverlay.Children.Add(new Line { X1 = centerX, X2 = centerX, Y2 = ThemeElement.CanvasHeight,
                Stroke = stroke, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 5 } });
            SelectionOverlay.Children.Add(new Line { X2 = ThemeElement.CanvasWidth, Y1 = centerY, Y2 = centerY,
                Stroke = stroke, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 5 } });
        }
        var selection = new Rectangle { Width = width, Height = height, StrokeThickness = 3, Stroke = stroke };
        Canvas.SetLeft(selection, selected.X);
        Canvas.SetTop(selection, selected.Y);
        SelectionOverlay.Children.Add(selection);
        if (!image)
        {
            foreach (var (x, y) in new[] { (0, 0), (width, 0), (0, height), (width, height) })
            {
                var corner = new Rectangle { Width = 16, Height = 16, Fill = stroke };
                Canvas.SetLeft(corner, selected.X + x - 8);
                Canvas.SetTop(corner, selected.Y + y - 8);
                SelectionOverlay.Children.Add(corner);
            }
        }
    }

    private bool ChangeScene(ThemeContent.Scene updated, int selection, bool recordUndo = true, bool rebuild = false)
    {
        if (viewModel is not { CanEdit: true } model || scene is not { } previous || updated == previous) { return false; }
        if (!model.SetEditorContent(updated)) { return false; }
        if (recordUndo) { undo.Push(new EditorUndo(previous, elementIndex)); }
        scene = updated;
        EditStatus.Text = "";
        SelectElement(selection, rebuild);
        RefreshEditorEnabled();
        return true;
    }

    private void UpdateElement(ThemeElement element, bool recordUndo = true)
    {
        if (scene is not { } current || elementIndex < 0 || elementIndex >= current.Elements.Length) { return; }
        ChangeScene(current with { Elements = current.Elements.SetItem(elementIndex, element.MoveTo(element.X, element.Y)) },
            elementIndex, recordUndo);
    }

    private void RefreshEditorEnabled()
    {
        bool enabled = editing && !saving && viewModel is { CanEdit: true };
        SaveButton.IsEnabled = enabled;
        PropertyInputs.IsEnabled = enabled;
        ElementList.IsEnabled = enabled;
        AddElementButton.IsEnabled = enabled && scene?.Elements.Length < ThemeElement.MaximumCount;
        UndoButton.IsEnabled = enabled && undo.Count != 0;
    }

    private void OnPositionChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!refreshing && SelectedElement is { } element && double.IsFinite(ElementX.Value) && double.IsFinite(ElementY.Value))
        {
            UpdateElement(element.MoveTo((int)ElementX.Value, (int)ElementY.Value));
        }
    }

    private void OnFontChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!refreshing && SelectedElement is { } element && double.IsFinite(args.NewValue)
            && args.NewValue is >= ThemeElement.MinimumFontSize and <= ThemeElement.MaximumFontSize)
        {
            UpdateElement(element with { FontSize = (int)args.NewValue });
        }
    }

    private void CommitColor()
    {
        if (refreshing || SelectedElement is not { } element || ElementColor.Text == ThemeFields.ColorText(element.Color)) { return; }
        if (ThemeFields.TryColor(ElementColor.Text, out var color)) { UpdateElement(element with { Color = color }); }
        else
        {
            ElementColor.Text = ThemeFields.ColorText(element.Color);
            EditStatus.Text = "文字颜色请输入 #RRGGBB。";
        }
    }

    private void CommitText()
    {
        if (!refreshing && SelectedElement is { Kind: ThemeElementKind.Text } element)
        {
            UpdateElement(element with { Text = ElementText.Text });
        }
    }

    private void OnColorLost(object sender, RoutedEventArgs args) => CommitColor();
    private void OnTextLost(object sender, RoutedEventArgs args) => CommitText();

    private void OnColorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter) { CommitColor(); args.Handled = true; }
    }

    private void OnTextKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter) { CommitText(); args.Handled = true; }
    }

    private void OnClockFormatChanged(object sender, RoutedEventArgs args)
    {
        if (!refreshing && SelectedElement is { } element)
        {
            UpdateElement(element with { Use24HourClock = Clock24Hours.IsChecked == true });
        }
    }

    private void OnFitChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!refreshing && SelectedElement is { Kind: ThemeElementKind.Image } element && ElementFit.SelectedIndex >= 0)
        {
            UpdateElement(element with { Fit = (ImageFit)ElementFit.SelectedIndex });
        }
    }

    private async void OnAddElement(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } || !Enum.TryParse<ThemeElementKind>(tag, out var kind)
            || scene is not { } current || current.Elements.Length >= ThemeElement.MaximumCount
            || viewModel is not { CanEdit: true } model) { return; }
        if (kind == ThemeElementKind.Image)
        {
            using var imported = await model.PickSceneImageAsync(CancellationToken.None);
            if (imported is null || scene != current) { return; }
            var existing = current.Elements.FirstOrDefault(element => element.Kind == ThemeElementKind.Image);
            int index = existing is null ? -1 : current.Elements.IndexOf(existing);
            var updated = current with { AssetId = imported.AssetId };
            if (index < 0)
            {
                updated = updated with { Elements = updated.Elements.Insert(0, ThemePresets.Element(kind, 0, 0, 100)) };
                index = 0;
            }
            ChangeScene(updated, index, rebuild: true);
            return;
        }
        var added = ThemePresets.Element(kind, 145 + current.Elements.Length * 20, 150 + current.Elements.Length * 25,
            kind is ThemeElementKind.Clock ? 176 : 72);
        ChangeScene(current with { Elements = current.Elements.Add(added) }, current.Elements.Length, rebuild: true);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs args)
    {
        if (scene is not { } current || SelectedElement is not { } element) { return; }
        ChangeScene(current with
        {
            Elements = current.Elements.RemoveAt(elementIndex),
            AssetId = element.Kind == ThemeElementKind.Image ? null : current.AssetId
        }, Math.Min(elementIndex, current.Elements.Length - 2), rebuild: true);
    }

    private void OnUndoClick(object sender, RoutedEventArgs args)
    {
        if (!undo.TryPeek(out var previous)) { return; }
        if (ChangeScene(previous.Scene, previous.Selection, recordUndo: false, rebuild: true)) { undo.Pop(); }
        RefreshEditorEnabled();
    }

    private void OnCanvasPressed(object sender, PointerRoutedEventArgs args)
    {
        if (scene is not { } current || viewModel is not { CanEdit: true } || saving
            || dragPointer is not null || !args.GetCurrentPoint(SceneSurface).Properties.IsLeftButtonPressed) { return; }
        var point = args.GetCurrentPoint(SceneSurface).Position;
        int hit = -1;
        for (int i = current.Elements.Length - 1; i >= 0; i--)
        {
            var element = current.Elements[i];
            var (width, height) = element.GetSize();
            if (new Rect(element.X, element.Y, width, height).Contains(point))
            {
                hit = i;
                break;
            }
        }
        SelectElement(hit);
        PreviewInput.Focus(FocusState.Programmatic);
        if (hit < 0 || !PreviewInput.CapturePointer(args.Pointer)) { return; }
        dragPointer = args.Pointer.PointerId;
        dragOrigin = point;
        dragStart = new EditorUndo(current, hit);
        dragMoved = false;
        args.Handled = true;
    }

    private void OnCanvasMoved(object sender, PointerRoutedEventArgs args)
    {
        if (dragPointer != args.Pointer.PointerId || dragStart is not { } start || SelectedElement is not { } element) { return; }
        var point = args.GetCurrentPoint(SceneSurface).Position;
        var origin = start.Scene.Elements[start.Selection];
        double dx = point.X - dragOrigin.X;
        double dy = point.Y - dragOrigin.Y;
        if (!dragMoved && Math.Abs(dx) + Math.Abs(dy) < 5) { return; }
        dragMoved = true;
        UpdateElement(element.MoveTo(origin.X + (int)Math.Round(dx), origin.Y + (int)Math.Round(dy)), recordUndo: false);
        args.Handled = true;
    }

    private void OnCanvasReleased(object sender, PointerRoutedEventArgs args)
    {
        if (dragPointer != args.Pointer.PointerId) { return; }
        FinishDrag();
        PreviewInput.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void OnCanvasCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (dragPointer == args.Pointer.PointerId) { FinishDrag(); }
    }

    private void FinishDrag()
    {
        if (dragStart is { } start && scene != start.Scene) { undo.Push(start); }
        dragPointer = null;
        dragStart = null;
        RefreshEditorEnabled();
    }

    private void OnCanvasKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (SelectedElement is not { } element || saving || viewModel is not { CanEdit: true }) { return; }
        var (dx, dy) = args.Key switch
        {
            VirtualKey.Left => (-1, 0), VirtualKey.Right => (1, 0),
            VirtualKey.Up => (0, -1), VirtualKey.Down => (0, 1), _ => (0, 0)
        };
        if (dx == 0 && dy == 0) { return; }
        UpdateElement(element.MoveTo(element.X + dx, element.Y + dy));
        args.Handled = true;
    }
}
