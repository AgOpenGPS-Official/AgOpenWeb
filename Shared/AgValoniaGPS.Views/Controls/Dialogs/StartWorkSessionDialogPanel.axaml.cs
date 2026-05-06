// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using Avalonia.Controls;
using Avalonia.Input;
using AgValoniaGPS.ViewModels;

namespace AgValoniaGPS.Views.Controls.Dialogs;

public partial class StartWorkSessionDialogPanel : UserControl
{
    public StartWorkSessionDialogPanel()
    {
        InitializeComponent();
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm
            && vm.CancelStartWorkSessionDialogCommand?.CanExecute(null) == true)
        {
            vm.CancelStartWorkSessionDialogCommand.Execute(null);
        }
        e.Handled = true;
    }

    private StartWorkSessionDialogViewModel? DialogVm =>
        (DataContext as MainViewModel)?.StartWorkSessionDialogVm;

    /// <summary>Real keystrokes mark the task-name field as user-edited
    /// so auto-recompute from WorkType stops clobbering it.</summary>
    private void TaskNameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Filter out modifier-only / navigation keys so that arrow keys,
        // tab, etc. don't trigger the "user edited" flag prematurely.
        if (IsTextProducingKey(e.Key))
            DialogVm?.MarkTaskNameUserEdited();
    }

    private void InsertDate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        InsertAtCursor(DateTime.Now.ToString("yyyy-MM-dd"));

    private void InsertTime_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        // HH-mm not HH:mm — colons are filesystem-unsafe on Windows.
        InsertAtCursor(DateTime.Now.ToString("HH-mm"));

    private void InsertAtCursor(string snippet)
    {
        var box = this.FindControl<TextBox>("TaskNameBox");
        if (box == null) return;

        var text = box.Text ?? string.Empty;
        var caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        box.Text = text.Insert(caret, snippet);
        box.CaretIndex = caret + snippet.Length;
        box.Focus();

        DialogVm?.MarkTaskNameUserEdited();
    }

    private static bool IsTextProducingKey(Key k) =>
        (k >= Key.A && k <= Key.Z)
        || (k >= Key.D0 && k <= Key.D9)
        || (k >= Key.NumPad0 && k <= Key.NumPad9)
        || k == Key.Back || k == Key.Delete
        || k == Key.Space || k == Key.OemMinus
        || k == Key.OemPeriod || k == Key.OemComma;
}
