// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// One section button in the section control bar. The button is a stable
/// object keyed by <see cref="Index"/>; only <see cref="ColorCode"/> changes as
/// the section cycles Off → Auto → On, so the bar repartitions into rows
/// without recreating buttons.
/// </summary>
public sealed partial class SectionButtonViewModel : ObservableObject
{
    /// <summary>Zero-based section index (0 = section 1).</summary>
    public int Index { get; }

    /// <summary>One-based number shown on the button.</summary>
    public int Number => Index + 1;

    private int _colorCode;

    /// <summary>
    /// 6-state color code driving the button background, matching
    /// SectionColorCodeToBackgroundConverter:
    /// 0=Off, 1=Manual On, 2=Auto On, 3=Turning Off, 4=Turning On, 5=Auto Off.
    /// </summary>
    public int ColorCode
    {
        get => _colorCode;
        set => SetProperty(ref _colorCode, value);
    }

    public IRelayCommand ToggleCommand { get; }

    public SectionButtonViewModel(int index, Action<int> onToggle)
    {
        Index = index;
        ToggleCommand = new RelayCommand(() => onToggle(index));
    }
}

/// <summary>
/// A single row of section buttons in the control bar. The bar shows one row
/// for up to 16 sections; above 16 it splits into evenly-sized rows
/// (ceil(N/16), max 4), with earlier rows taking the extra when N doesn't
/// divide evenly so the top row holds the greater count.
/// </summary>
public sealed class SectionRowViewModel
{
    public IReadOnlyList<SectionButtonViewModel> Buttons { get; }

    public SectionRowViewModel(IReadOnlyList<SectionButtonViewModel> buttons)
    {
        Buttons = buttons;
    }
}
