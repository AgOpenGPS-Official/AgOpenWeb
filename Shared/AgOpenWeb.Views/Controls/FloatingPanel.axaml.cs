using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Metadata;

namespace AgOpenWeb.Views.Controls;

// Simple data class for menu buttons
public class MenuButton : AvaloniaObject
{
    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<MenuButton, string>(nameof(Icon), "");

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MenuButton, string>(nameof(Text), "");

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<MenuButton, ICommand?>(nameof(Command));

    public static readonly StyledProperty<bool> IsEnabledProperty =
        AvaloniaProperty.Register<MenuButton, bool>(nameof(IsEnabled), true);

    public static readonly StyledProperty<string?> ToolTipProperty =
        AvaloniaProperty.Register<MenuButton, string?>(nameof(ToolTip));

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool IsEnabled
    {
        get => GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    public string? ToolTip
    {
        get => GetValue(ToolTipProperty);
        set => SetValue(ToolTipProperty, value);
    }
}

public partial class FloatingPanel : UserControl
{
    private bool _isDragging = false;
    private Point _lastScreenPoint;

    public event EventHandler<PointerPressedEventArgs>? DragStarted;
    public event EventHandler<Vector>? DragMoved;
    public event EventHandler<PointerReleasedEventArgs>? DragEnded;

    // Title
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<FloatingPanel, string>(nameof(Title), "Untitled");

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    // Close Command
    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<FloatingPanel, ICommand?>(nameof(CloseCommand));

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    // Optional Back Command. When set, a Back (←) button appears in the header,
    // left of the title — used by Field Tools tool overlays to return to the
    // Field Tools fly-out. Fly-out menus and charts leave this null (no Back).
    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<FloatingPanel, ICommand?>(nameof(BackCommand));

    public ICommand? BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    // Menu items — default [Content] property
    public static readonly StyledProperty<IList<MenuButton>> MenuItemsProperty =
        AvaloniaProperty.Register<FloatingPanel, IList<MenuButton>>(nameof(MenuItems));

    [Content]
    public IList<MenuButton> MenuItems
    {
        get => GetValue(MenuItemsProperty);
        set => SetValue(MenuItemsProperty, value);
    }

    // Custom content for non-menu panels (charts, simulator etc.)
    public static readonly StyledProperty<object?> PanelContentProperty =
        AvaloniaProperty.Register<FloatingPanel, object?>(nameof(PanelContent));

    public object? PanelContent
    {
        get => GetValue(PanelContentProperty);
        set => SetValue(PanelContentProperty, value);
    }

    public FloatingPanel()
    {
        SetValue(MenuItemsProperty, new List<MenuButton>());
        InitializeComponent();
        Loaded += (s, e) => AttachDragHandler();
    }

    private void AttachDragHandler()
    {
        var dragHandle = this.FindControl<Grid>("DragHandle");
        if (dragHandle != null)
        {
            dragHandle.PointerPressed += DragHandle_PointerPressed;
            dragHandle.PointerMoved += DragHandle_PointerMoved;
            dragHandle.PointerReleased += DragHandle_PointerReleased;
        }
    }

    private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Grid handle)
        {
            _isDragging = false;
            var root = this.VisualRoot as Visual;
            _lastScreenPoint = root != null ? e.GetPosition(root) : e.GetPosition(this);
            e.Pointer.Capture(handle);
        }
    }

    private void DragHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Grid handle && e.Pointer.Captured == handle)
        {
            var root = this.VisualRoot as Visual;
            var currentPoint = root != null ? e.GetPosition(root) : e.GetPosition(this);
            var dx = currentPoint.X - _lastScreenPoint.X;
            var dy = currentPoint.Y - _lastScreenPoint.Y;

            if (!_isDragging && (dx * dx + dy * dy) > 25.0)
            {
                _isDragging = true;
                DragStarted?.Invoke(this, null!);
            }

            if (_isDragging)
            {
                var delta = currentPoint - _lastScreenPoint;
                DragMoved?.Invoke(this, delta);
                _lastScreenPoint = currentPoint;
            }
            e.Handled = true;
        }
    }

    private void DragHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Grid handle && e.Pointer.Captured == handle)
        {
            if (_isDragging)
                DragEnded?.Invoke(this, e);
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
