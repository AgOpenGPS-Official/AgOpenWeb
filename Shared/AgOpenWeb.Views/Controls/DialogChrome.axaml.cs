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
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using AgOpenWeb.Views.Controls.Panels;

namespace AgOpenWeb.Views.Controls;

/// <summary>
/// Standard chrome for a chain dialog: a draggable header bar with a Back button
/// (left), title (center) and Close button (right), wrapping the dialog body in a
/// rounded card. Visual sibling of <see cref="FloatingPanel"/> so fly-outs and
/// dialogs share one look and feel.
///
/// Non-modal: the host renders no darkening backdrop, so the map stays visible and
/// clickable behind it, and the header can be dragged to move the card aside for a
/// peek at the map. The card re-centers each time it is shown. Back/Close bind to
/// the shared chain-navigation commands (NavBackCommand / NavCloseChainCommand).
/// </summary>
public partial class DialogChrome : UserControl
{
    private readonly TranslateTransform _drag = new();
    private bool _isDragging;
    private Point _lastScreenPoint;
    private IDisposable? _hostVisibilitySub;

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<DialogChrome, string>(nameof(Title), "");

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly StyledProperty<bool> ShowBackButtonProperty =
        AvaloniaProperty.Register<DialogChrome, bool>(nameof(ShowBackButton), true);

    public bool ShowBackButton
    {
        get => GetValue(ShowBackButtonProperty);
        set => SetValue(ShowBackButtonProperty, value);
    }

    // When true (default) the panel opens at the chain anchor and is header-draggable.
    // Set false for full-screen workspace dialogs (Field Builder, Vehicle Config,
    // AutoSteer) that fill their parent — there is no position to anchor or drag.
    public static readonly StyledProperty<bool> AnchoredProperty =
        AvaloniaProperty.Register<DialogChrome, bool>(nameof(Anchored), true);

    public bool Anchored
    {
        get => GetValue(AnchoredProperty);
        set => SetValue(AnchoredProperty, value);
    }

    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<DialogChrome, ICommand?>(nameof(BackCommand));

    public ICommand? BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<DialogChrome, ICommand?>(nameof(CloseCommand));

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    // The dialog body — default [Content] property so panels nest naturally.
    public static readonly StyledProperty<object?> PanelContentProperty =
        AvaloniaProperty.Register<DialogChrome, object?>(nameof(PanelContent));

    [Content]
    public object? PanelContent
    {
        get => GetValue(PanelContentProperty);
        set => SetValue(PanelContentProperty, value);
    }

    public DialogChrome()
    {
        InitializeComponent();

        // Translate the whole control (not the inner card) so the dragged-away
        // portion isn't clipped by the card's own bounds. The control is centered
        // in a full-window Grid that does not clip, so it moves freely.
        RenderTransform = _drag;

        var handle = this.FindControl<Grid>("DragHandle");
        if (handle != null)
        {
            handle.PointerPressed += Handle_PointerPressed;
            handle.PointerMoved += Handle_PointerMoved;
            handle.PointerReleased += Handle_PointerReleased;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The hosting dialog panel toggles its own IsVisible (bound to the dialog
        // flag). Re-center the card each time it is shown, so a panel always opens
        // at its home location rather than wherever it was last dragged.
        _hostVisibilitySub?.Dispose();
        var host = this.FindAncestorOfType<UserControl>();
        if (host != null)
        {
            _hostVisibilitySub = host.GetObservable(Visual.IsVisibleProperty)
                .Subscribe(new AnonymousObserver<bool>(visible =>
                {
                    if (visible)
                    {
                        _drag.X = 0;
                        _drag.Y = 0;
                        PositionAtChainAnchor();
                    }
                }));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _hostVisibilitySub?.Dispose();
        _hostVisibilitySub = null;
    }

    /// <summary>
    /// Open with the dialog's upper-left aligned over the chain anchor — i.e. over
    /// the panel that launched it, at that panel's current position. Falls back to
    /// the fly-out's home position (nav top + 90) if no anchor was captured. After
    /// positioning, republish the anchor so the next panel in the chain stacks on
    /// top of this one.
    /// </summary>
    private void PositionAtChainAnchor()
    {
        if (!Anchored) return; // full-screen workspaces fill their parent
        var top = TopLevel.GetTopLevel(this);
        if (top == null || this.Parent is not Visual parent) return;
        var parentOrigin = parent.TranslatePoint(new Point(0, 0), top) ?? default;

        var anchor = ChainPanelAnchor.Current
                     ?? NavFlyoutHome(top)
                     ?? new Point(parentOrigin.X + Margin.Left, parentOrigin.Y + Margin.Top);

        Margin = new Thickness(anchor.X - parentOrigin.X, anchor.Y - parentOrigin.Y, 0, 0);
        ChainPanelAnchor.Current = anchor;
    }

    /// <summary>Window-space upper-left of a left-nav fly-out at its home position
    /// (the nav panel's on-screen origin plus the fly-out's Canvas.Left of 90).</summary>
    private static Point? NavFlyoutHome(Visual top)
    {
        var nav = top.GetVisualDescendants().OfType<LeftNavigationPanel>().FirstOrDefault();
        return nav?.TranslatePoint(new Point(90, 0), top);
    }

    /// <summary>Publish this dialog's current on-screen upper-left (Margin + drag)
    /// so a child panel opens aligned over it.</summary>
    private void PublishAnchor()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top != null && this.Parent is Visual parent &&
            parent.TranslatePoint(new Point(0, 0), top) is { } o)
        {
            ChainPanelAnchor.Current =
                new Point(o.X + Margin.Left + _drag.X, o.Y + Margin.Top + _drag.Y);
        }
    }

    private void Handle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!Anchored) return; // full-screen workspaces don't drag
        if (sender is Grid handle)
        {
            _isDragging = false;
            var root = this.VisualRoot as Visual;
            _lastScreenPoint = root != null ? e.GetPosition(root) : e.GetPosition(this);
            e.Pointer.Capture(handle);
        }
    }

    private void Handle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Grid handle && e.Pointer.Captured == handle)
        {
            var root = this.VisualRoot as Visual;
            var current = root != null ? e.GetPosition(root) : e.GetPosition(this);
            var dx = current.X - _lastScreenPoint.X;
            var dy = current.Y - _lastScreenPoint.Y;

            if (!_isDragging && (dx * dx + dy * dy) > 25.0)
                _isDragging = true;

            if (_isDragging)
            {
                _drag.X += dx;
                _drag.Y += dy;
                _lastScreenPoint = current;
                PublishAnchor();
            }
            e.Handled = true;
        }
    }

    private void Handle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Grid handle && e.Pointer.Captured == handle)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
