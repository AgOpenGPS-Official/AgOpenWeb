// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Interfaces
{
    public interface IErrorPresenter
    {
        void PresentTimedMessage(
            TimeSpan timeSpan,
            string titleString,
            string messageString);
    }
}
