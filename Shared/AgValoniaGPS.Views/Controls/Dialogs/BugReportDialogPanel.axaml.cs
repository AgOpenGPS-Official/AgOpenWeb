// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AgValoniaGPS.Views.Controls.Dialogs;

public partial class BugReportDialogPanel : UserControl
{
    public BugReportDialogPanel()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, DropZone_DragOver);
        AddHandler(DragDrop.DropEvent, DropZone_Drop);
    }

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is AgValoniaGPS.ViewModels.MainViewModel vm)
            vm.CloseBugReportDialogCommand?.Execute(null);
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
    }

    private void DropZone_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not AgValoniaGPS.ViewModels.MainViewModel vm) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            if (!string.IsNullOrEmpty(path))
                vm.AddBugReportAttachment(path);
        }
    }

    private async void OnBrowseFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AgValoniaGPS.ViewModels.MainViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files to bug report",
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            if (!string.IsNullOrEmpty(path))
                vm.AddBugReportAttachment(path);
        }
    }
}
