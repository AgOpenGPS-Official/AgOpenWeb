// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Threading;

/// <summary>
/// The headless host's stand-in for the Avalonia UI thread. Owns ONE dedicated
/// background thread that pumps a work queue; everything posted via
/// <see cref="IUiDispatcher"/> and every timer tick from the companion
/// <see cref="IUiTimerFactory"/> runs serialized on that single thread.
///
/// In the windowed app the render-pull timer (30 Hz), the status timer (5 Hz),
/// and the RemoteServer command handler all execute on the Avalonia UI thread,
/// so they never overlap. The headless host has no UI thread, so this class
/// reproduces that exact single-threaded serialization for the VM/services layer.
/// The 100 Hz control loop keeps running on its own thread (as it does in the
/// windowed app); only the UI-thread-equivalent work is funnelled here.
///
/// Framework-free (no Avalonia). See Plans/WEBUI_MIGRATION_PLAN.md Phase 10 and
/// Plans/CONFIG_STATE_AUDIT.md §11.
/// </summary>
public sealed class HostLoopDispatcher : IUiDispatcher, IUiTimerFactory, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private readonly int _threadId;
    private volatile bool _running = true;

    public HostLoopDispatcher()
    {
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "HostLoop",
        };
        // ManagedThreadId is assigned at construction and is stable for the
        // thread's life, so it matches Environment.CurrentManagedThreadId once
        // Pump runs — capture it before Start so CheckAccess is correct immediately.
        _threadId = _thread.ManagedThreadId;
        _thread.Start();
    }

    private void Pump()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex)
            {
                // A single bad callback must never tear down the host loop —
                // log and keep pumping (mirrors the UI thread surviving an
                // unhandled handler exception in a debugger-less run).
                System.Diagnostics.Debug.WriteLine($"[HostLoop] callback threw: {ex}");
            }
        }
    }

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action, UiDispatcherPriority priority = UiDispatcherPriority.Default)
    {
        if (!_running) return;
        // Priority is ignored: the host loop is FIFO. The VM only uses priority
        // to yield until a frame paints (Render) or to defer bookkeeping
        // (Background); with no renderer those degrade harmlessly to "run next".
        _queue.Add(action);
    }

    public Task InvokeAsync(Action action, UiDispatcherPriority priority = UiDispatcherPriority.Default)
    {
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running)
        {
            tcs.SetCanceled();
            return tcs.Task;
        }
        _queue.Add(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    // --- IUiTimerFactory: timers that marshal their Tick onto this same loop ---
    public IUiTimer Create() => new HostLoopTimer(this);

    public void Dispose()
    {
        _running = false;
        _queue.CompleteAdding();
    }

    /// <summary>
    /// Periodic timer backed by <see cref="System.Threading.Timer"/> whose Tick
    /// is posted onto the owning <see cref="HostLoopDispatcher"/>, so handlers run
    /// on the single host-loop thread (matching DispatcherTimer semantics). A
    /// pending-guard coalesces ticks if the loop falls behind, so a slow handler
    /// can never build an unbounded backlog (DispatcherTimer can't re-enter either).
    /// </summary>
    private sealed class HostLoopTimer : IUiTimer, IDisposable
    {
        private readonly HostLoopDispatcher _dispatcher;
        private readonly Timer _timer;
        private TimeSpan _interval = TimeSpan.FromSeconds(1);
        private int _pending; // 0 = none queued, 1 = a tick is already in flight

        public HostLoopTimer(HostLoopDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _timer = new Timer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        private void OnTimer()
        {
            // Already a tick queued/running on the loop? Skip — don't pile up.
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            _dispatcher.Post(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                Tick?.Invoke(this, EventArgs.Empty);
            });
        }

        public TimeSpan Interval
        {
            get => _interval;
            set { _interval = value; if (IsEnabled) _timer.Change(value, value); }
        }

        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;

        public void Start() { IsEnabled = true; _timer.Change(_interval, _interval); }
        public void Stop() { IsEnabled = false; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        public void Dispose() => _timer.Dispose();
    }
}
