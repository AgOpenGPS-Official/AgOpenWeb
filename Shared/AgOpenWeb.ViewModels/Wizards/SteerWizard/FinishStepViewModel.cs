// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// Final step - summary and completion. Says which of the wizard's tests were not run:
/// Next on every step finished the wizard with the sensor mounted the other way and nothing
/// caught it.
/// </summary>
public class FinishStepViewModel : WizardStepViewModel
{
    private readonly Func<IReadOnlyList<string>>? _notTested;

    public FinishStepViewModel() { }

    /// <param name="notTested">The tests the wizard's steps haven't run, by name, now.</param>
    public FinishStepViewModel(Func<IReadOnlyList<string>> notTested) => _notTested = notTested;

    public override string Title => "Setup Complete";

    /// <summary>The warning under the page when a test was skipped; empty when all were run.</summary>
    public string TestResult
    {
        get
        {
            var missing = _notTested?.Invoke();
            if (missing == null || missing.Count == 0) return "";
            return "Not tested: " + string.Join(", ", missing) + ". AutoSteer will steer with the values as "
                + "typed; a sensor or motor wired the other way was not caught. Go Back to run them, or Finish to keep what is there.";
        }
    }

    public override string Description =>
        "Your basic AutoSteer configuration is complete!\n\n" +
        "Click Finish to save your settings. You can always modify these values " +
        "later in the Configuration panel.\n\n" +
        "For advanced steering calibration (WAS, CPD, Ackermann), run this wizard again " +
        "or use the Tools menu.";

    public override bool CanSkip => false;
}
