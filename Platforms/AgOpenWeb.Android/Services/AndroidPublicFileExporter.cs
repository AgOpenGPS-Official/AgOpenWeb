// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using AgOpenWeb.Services.Interfaces;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;

namespace AgOpenWeb.Android.Services;

/// <summary>
/// Exports files (such as bug report dumps) to the public Documents/AgOpenWeb folder on Android.
/// On Android 10+ (API 29+), uses MediaStore to save directly into Documents without requiring
/// MANAGE_EXTERNAL_STORAGE. On earlier APIs or if direct file access is permitted, uses standard
/// file copy to the public Documents directory.
/// </summary>
internal sealed class AndroidPublicFileExporter : IPublicFileExporter
{
    public string? ExportToPublicDocuments(string sourceFilePath, string subFolder, string fileName)
    {
        if (!File.Exists(sourceFilePath))
            return null;

        string relativeSubDir = Path.Combine("AgOpenWeb", subFolder);

        // 1. On Android 10+ (API 29+), write to public Documents using MediaStore.
        // This does not require MANAGE_EXTERNAL_STORAGE and is visible in all file managers.
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            try
            {
                var context = Application.Context;
                if (context != null)
                {
                    var values = new ContentValues();
                    values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
                    values.Put(MediaStore.IMediaColumns.MimeType, "application/zip");
                    values.Put(MediaStore.IMediaColumns.RelativePath, Path.Combine(global::Android.OS.Environment.DirectoryDocuments!, relativeSubDir));

                    var externalContentUri = MediaStore.Files.GetContentUri("external");
                    if (externalContentUri != null)
                    {
                        var insertedUri = context.ContentResolver?.Insert(externalContentUri, values);
                        if (insertedUri != null)
                        {
                            using (var inStream = File.OpenRead(sourceFilePath))
                            using (var outStream = context.ContentResolver?.OpenOutputStream(insertedUri))
                            {
                                if (outStream != null)
                                {
                                    inStream.CopyTo(outStream);
                                    outStream.Flush();
                                }
                            }

                            return Path.Combine("/storage/emulated/0", global::Android.OS.Environment.DirectoryDocuments!, relativeSubDir, fileName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AndroidPublicFileExporter] MediaStore save failed: {ex.Message}");
            }
        }

        // 2. Direct copy fallback (if MANAGE_EXTERNAL_STORAGE is granted or API < 29)
        try
        {
            var publicDocs = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDocuments)?.AbsolutePath;
            if (!string.IsNullOrEmpty(publicDocs))
            {
                var targetDir = Path.Combine(publicDocs, relativeSubDir);
                Directory.CreateDirectory(targetDir);
                var targetPath = Path.Combine(targetDir, fileName);
                File.Copy(sourceFilePath, targetPath, overwrite: true);
                return targetPath;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidPublicFileExporter] Direct file copy failed: {ex.Message}");
        }

        return null;
    }
}
