// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using AgOpenWeb.Models.State;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: lets the web client see and answer the host's confirmation
/// and error dialogs, and hear about failures (#109). The native overlay that used to
/// show these is gone, so without this a confirm waits forever and an error or refusal
/// is invisible.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Bumped every time a confirmation or error dialog opens. The web echoes it
    /// back with its answer so a stale tap can't answer a newer prompt.</summary>
    public int PromptSeq { get; private set; }

    /// <summary>True while a confirmation or error dialog is waiting for an answer.</summary>
    public bool IsPromptPending =>
        State.UI.ActiveDialog is DialogType.Confirmation or DialogType.Error;

    /// <summary>Answer the pending prompt from the web. Ignored unless <paramref name="seq"/>
    /// is the prompt currently showing. An error prompt only has OK, so any answer
    /// dismisses it.</summary>
    public bool AnswerPrompt(int seq, bool confirm, bool checkboxChecked)
    {
        if (seq != PromptSeq) return false;
        switch (State.UI.ActiveDialog)
        {
            case DialogType.Confirmation:
                ConfirmationDialogCheckboxChecked = checkboxChecked;
                var cmd = confirm ? ConfirmConfirmationDialogCommand : CancelConfirmationDialogCommand;
                cmd?.Execute(null);
                return true;
            case DialogType.Error:
                DismissErrorDialogCommand?.Execute(null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Raised with the message whenever <see cref="ReportFailure"/> is called; the
    /// web wiring shows it as a short notification.</summary>
    public event Action<string>? FailureReported;

    /// <summary>Set the status message for something that was refused or failed, and
    /// tell the web so the operator sees why nothing happened. Routine status stays on
    /// <see cref="StatusMessage"/> alone.</summary>
    public void ReportFailure(string message)
    {
        StatusMessage = message;
        FailureReported?.Invoke(message);
    }
}
