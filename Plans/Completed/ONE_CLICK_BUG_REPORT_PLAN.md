# 1-Click Bug Report Feature

## Context

As AgOpenWeb approaches beta testing, structured bug reports from testers are critical. Currently, `DebugDumpService` silently creates a diagnostic zip in the temp directory — the user never gets to describe the problem, and the zip is hard to find. The goal is a 1-click experience: user taps a button, sees a dialog to describe the issue, and gets a zip they can attach to a GitHub issue. A future phase will auto-create GitHub issues and upload the data, enabling AI triage.

## Phase 1: 1-Click Bug Report Dialog (this implementation)

**User flow:**
1. User taps "Bug Report" in File menu (replaces current "Bug Report Dump" button)
2. Screenshot is captured immediately (before dialog obscures the screen)
3. Dialog appears with:
   - Title: "Report a Bug"
   - Read-only summary of what will be included (system info, config, GPS state, field data, logs, screenshot)
   - Text area for user to describe the issue (required — placeholder: "What happened? What did you expect?")
   - **File drop area** — dashed-border zone where users can drag & drop their own screenshots, screen recordings, videos, log files, or any other supporting files. On mobile, an "Add Files" button opens the system file picker instead. Attached files are shown in a list below with filename, size, and a remove button. All user-attached files get bundled into an `attachments/` folder inside the zip.
   - "Submit" button (creates zip, saves to Documents/AgOpenWeb/BugReports/, shows success with path)
   - "Cancel" button
4. On submit: busy overlay while zip is created, then status message with the file path

**Save location:** `Documents/AgOpenWeb/BugReports/bugreport_{timestamp}.zip` — visible to the user in their file browser, not buried in temp.

### Files to Modify

#### 1. Add InMemoryLoggerProvider to iOS and Android DI
Currently only Desktop captures logs. Bug reports from mobile devices would be useless without them.

- `Platforms/AgOpenWeb.iOS/DependencyInjection/ServiceCollectionExtensions.cs` — add `builder.AddProvider(new InMemoryLoggerProvider());`
- `Platforms/AgOpenWeb.Android/DependencyInjection/ServiceCollectionExtensions.cs` — same

#### 2. Add `BugReport` dialog type to UIState
- `Shared/AgOpenWeb.Models/State/UIState.cs`
  - Add `BugReport` to `DialogType` enum
  - Add `IsBugReportDialogVisible` computed property
  - Add `RaisePropertyChanged` call in `ShowDialog`/`CloseDialog`

#### 3. Create BugReportDialogPanel
- `Shared/AgOpenWeb.Views/Controls/Dialogs/BugReportDialogPanel.axaml` — new dialog
  - Semi-transparent backdrop (standard pattern)
  - Centered card with:
    - "Report a Bug" title
    - Info text listing what gets captured
    - TextBox for description (multiline, placeholder text)
    - File drop area — dashed border zone where users can drag & drop screenshots, videos, or other files to include. Uses Avalonia's `DragDrop` API. Shows attached file list with filename, size, and a remove button for each.
    - Submit + Cancel buttons
  - Binds `IsVisible` to `State.UI.IsBugReportDialogVisible`
- `Shared/AgOpenWeb.Views/Controls/Dialogs/BugReportDialogPanel.axaml.cs` — code-behind
  - Backdrop click handler to close
  - DragOver/Drop event handlers to accept file drops
  - On mobile (iOS/Android): include an "Add Files" button as alternative to drag & drop (touch doesn't support drag from outside the app easily)

#### 4. Register dialog in DialogOverlayHost
- `Shared/AgOpenWeb.Views/Controls/DialogOverlayHost.axaml` — add BugReportDialogPanel

#### 5. Update DebugDumpService for user-friendly output
- `Shared/AgOpenWeb.Services/DebugDumpService.cs`
  - Add overload or parameter for custom output directory (default to `Documents/AgOpenWeb/BugReports/`)
  - Rename output file pattern from `debug_dump_` to `bugreport_`
  - Add `userAttachments` parameter (list of file paths) — copies each into `attachments/` folder in the zip

#### 6. Wire up commands in MainViewModel
- `Shared/AgOpenWeb.ViewModels/MainViewModel.Commands.Settings.cs`
  - Replace `CreateDebugDumpCommand` with `ShowBugReportDialogCommand` (opens dialog)
  - Add `CloseBugReportDialogCommand`
  - Add `SubmitBugReportCommand`:
    1. Capture screenshot (already done before dialog opened, stored in field)
    2. Call `DebugDumpService.CreateDump()` with user's description
    3. Save to Documents/AgOpenWeb/BugReports/
    4. Close dialog, show status message with path
  - Add `BugReportDescription` string property for TextBox binding
  - Add `BugReportAttachments` ObservableCollection<BugReportAttachment> (filename + path) for the file list
  - Add `RemoveBugReportAttachmentCommand` to remove items from the list
  - Add `AddBugReportFilesCommand` for the mobile "Add Files" button (opens file picker via Avalonia StorageProvider)
  - Add `_bugReportScreenshot` byte[] field to hold pre-captured screenshot
- `Shared/AgOpenWeb.ViewModels/MainViewModel.cs` — add command property declarations

#### 7. Update File menu button
- `Shared/AgOpenWeb.Views/Controls/Panels/FileMenuPanel.axaml`
  - Change `CreateDebugDumpCommand` to `ShowBugReportDialogCommand`
  - Keep localization key or update to match

### Key design decisions

- **Screenshot captured BEFORE dialog opens** — otherwise the bug report screenshot just shows the dialog itself, not the state the user is reporting about
- **Save to Documents, not temp** — users can find the zip in Finder/Files and drag it into a GitHub issue
- **Keep existing `CreateDebugDumpCommand` as internal** — the silent dump is still useful for integration tests and automated diagnostics
- **User description is the only input** — everything else is automated. Minimize friction.

## Phase 2: Auto-create GitHub Issue (future)

- Add `Octokit` NuGet package for GitHub API
- After creating the zip, offer "Post to GitHub" button
- Creates an issue with structured body (system info, GPS state, field name, user description)
- Uploads the zip as an issue attachment
- Requires a GitHub personal access token — store in AppSettings (or use a bot token baked into the app for the beta program)
- Opens the created issue URL in the browser

## Phase 3: AI Bug Triage (future)

- GitHub Action triggered on new issues with "bug-report" label
- Downloads the zip, extracts structured data
- AI agent analyzes: is this a real bug, duplicate, user error, or feature request?
- Adds triage labels and a comment with analysis
- Escalates genuine bugs with reproduction steps extracted from the data

## Verification

1. Build all platforms (Desktop, iOS, Android)
2. Run `dotnet test Tests/` — existing tests should pass
3. On each platform:
   - Open File menu, tap "Bug Report"
   - Verify screenshot captured is of the screen BEFORE the dialog
   - Enter a description, tap Submit
   - Verify zip appears in Documents/AgOpenWeb/BugReports/
   - Open zip, confirm it contains: system_info.txt, appsettings.json, configuration.json, runtime_state.json, logs.txt, screenshot.png, user_notes.txt, field/ directory (if field open)
   - Drag & drop a file onto the drop area (Desktop), or tap "Add Files" (mobile) — verify it appears in the attached list
   - Remove an attachment, verify it disappears
   - Submit with attachments — verify zip contains `attachments/` folder with the user's files
4. Verify the old `CreateDebugDumpCommand` still works for integration tests
