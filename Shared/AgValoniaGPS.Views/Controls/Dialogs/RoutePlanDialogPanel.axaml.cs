// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AgValoniaGPS.Views.Controls.Dialogs;

public partial class RoutePlanDialogPanel : UserControl
{
    private bool _isDragging;
    private Point _dragStart;
    private TranslateTransform? _translate;

    public RoutePlanDialogPanel()
    {
        InitializeComponent();
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStart = e.GetPosition(this.Parent as Visual);

            _translate = RenderTransform as TranslateTransform;
            if (_translate == null)
            {
                _translate = new TranslateTransform();
                RenderTransform = _translate;
            }

            e.Pointer.Capture((IInputElement)sender!);
            e.Handled = true;
        }
    }

    private void Header_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging && _translate != null)
        {
            var current = e.GetPosition(this.Parent as Visual);
            var delta = current - _dragStart;
            _translate.X += delta.X;
            _translate.Y += delta.Y;
            _dragStart = current;
            e.Handled = true;
        }
    }

    private void Header_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
