// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.Interfaces
{
    /// <summary>
    /// Service for managing the OpenGL rendering pipeline.
    /// Abstracts rendering operations from platform-specific OpenGL implementations.
    /// </summary>
    public interface IRenderService
    {
        /// <summary>
        /// Initializes the rendering context and resources.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Renders a single frame.
        /// </summary>
        /// <param name="deltaTime">Time since last frame in seconds</param>
        void Render(double deltaTime);

        /// <summary>
        /// Updates viewport dimensions when window is resized.
        /// </summary>
        /// <param name="width">New width in pixels</param>
        /// <param name="height">New height in pixels</param>
        void UpdateViewport(int width, int height);

        /// <summary>
        /// Cleans up rendering resources.
        /// </summary>
        void Dispose();

        /// <summary>
        /// Requests the next frame to be rendered.
        /// </summary>
        void RequestFrame();
    }
}