// Phase 0 Q4 spike for the CompositionCustomVisualHandler pivot
// (Plans/GL_MAP_PIVOT_PLAN.md). Answers: does
// RegisterForNextAnimationFrameUpdate keep firing when the host control
// has IsVisible=false, or does Avalonia stop the animation loop?
//
// Setup: two CountingVisualControl instances stacked in a Grid. Each
// uses a CompositionCustomVisualHandler that re-registers for the next
// animation frame every render. Both increment a frame counter; the
// counters are emitted at 1 Hz as
//   [HiddenVisualSpike] visible=N hidden=M
//
// After 3 seconds, the SECOND control's IsVisible is flipped to false.
// We watch whether its counter keeps rising at the same rate (bad —
// means hidden controls still consume render-thread time) or drops to
// zero (good — IsVisible=false is sufficient to pause the animation).
//
// Verdict drives a Phase 1 architectural decision:
//   - If hidden=0 after toggle: keep the dual-control architecture
//     where 2D and 3D controls are mounted side-by-side, only one
//     visible.
//   - If hidden continues to rise: actively add/remove from the parent
//     panel's Children collection on toggle, not just bind IsVisible.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using SkiaSharp;

namespace AgValoniaGPS.Views.Controls.Spikes;

public class HiddenVisualSpike : Grid
{
    private readonly CountingVisualControl _top;
    private readonly CountingVisualControl _bottom;
    private DispatcherTimer? _emitTimer;
    private DispatcherTimer? _toggleTimer;
    private bool _hasToggled;

    public HiddenVisualSpike()
    {
        ClipToBounds = true;
        Background = Brushes.DarkSlateGray;
        RowDefinitions.Add(new RowDefinition(GridLength.Star));
        RowDefinitions.Add(new RowDefinition(GridLength.Star));

        _top = new CountingVisualControl(label: "TOP", fillColor: Colors.SteelBlue);
        SetRow(_top, 0);
        Children.Add(_top);

        _bottom = new CountingVisualControl(label: "BOTTOM", fillColor: Colors.IndianRed);
        SetRow(_bottom, 1);
        Children.Add(_bottom);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _emitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _emitTimer.Tick += (_, _) =>
        {
            int topRate = _top.ConsumeFrames();
            int bottomRate = _bottom.ConsumeFrames();
            string topState = _top.IsVisible ? "visible" : "HIDDEN";
            string bottomState = _bottom.IsVisible ? "visible" : "HIDDEN";
            Console.WriteLine($"[HiddenVisualSpike] top={topRate}fps ({topState}) bottom={bottomRate}fps ({bottomState})");
        };
        _emitTimer.Start();

        // After 3s, hide the bottom control. After 6s, hide both.
        // After 9s, restore both.
        _toggleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
        int phase = 0;
        _toggleTimer.Tick += (_, _) =>
        {
            phase++;
            switch (phase)
            {
                case 1:
                    Console.WriteLine("[HiddenVisualSpike] PHASE 1: hiding BOTTOM");
                    _bottom.IsVisible = false;
                    break;
                case 2:
                    Console.WriteLine("[HiddenVisualSpike] PHASE 2: hiding TOP too (both hidden)");
                    _top.IsVisible = false;
                    break;
                case 3:
                    Console.WriteLine("[HiddenVisualSpike] PHASE 3: restoring both");
                    _top.IsVisible = true;
                    _bottom.IsVisible = true;
                    _hasToggled = true;
                    break;
                case 4:
                    Console.WriteLine("[HiddenVisualSpike] PHASE 4: removing BOTTOM from visual tree (Children.Remove)");
                    Children.Remove(_bottom);
                    break;
                case 5:
                    Console.WriteLine("[HiddenVisualSpike] PHASE 5: re-adding BOTTOM");
                    Children.Add(_bottom);
                    SetRow(_bottom, 1);
                    break;
            }
        };
        _toggleTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _emitTimer?.Stop();
        _toggleTimer?.Stop();
        _emitTimer = null;
        _toggleTimer = null;
    }

    private class CountingVisualControl : Control
    {
        private readonly string _label;
        private readonly Color _fillColor;
        private CompositionCustomVisual? _customVisual;
        private CountingHandler? _handler;

        public CountingVisualControl(string label, Color fillColor)
        {
            _label = label;
            _fillColor = fillColor;
        }

        public int ConsumeFrames() => _handler?.ConsumeFrames() ?? 0;

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width)  ? 400 : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            var elementVisual = ElementComposition.GetElementVisual(this);
            if (elementVisual == null) return;
            _handler = new CountingHandler(_label, _fillColor);
            _customVisual = elementVisual.Compositor.CreateCustomVisual(_handler);
            ElementComposition.SetElementChildVisual(this, _customVisual);
            UpdateSize();
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var r = base.ArrangeOverride(finalSize);
            UpdateSize();
            return r;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty) UpdateSize();
        }

        private void UpdateSize()
        {
            if (_customVisual == null) return;
            _customVisual.Size = new Vector(Bounds.Width, Bounds.Height);
        }

        private class CountingHandler : CompositionCustomVisualHandler
        {
            private readonly string _label;
            private readonly Color _fillColor;
            private int _frameCounter;

            public CountingHandler(string label, Color fillColor)
            {
                _label = label;
                _fillColor = fillColor;
            }

            public int ConsumeFrames()
            {
                int n = _frameCounter;
                _frameCounter = 0;
                return n;
            }

            public override void OnAnimationFrameUpdate()
            {
                _frameCounter++;
                Invalidate();
                RegisterForNextAnimationFrameUpdate();
            }

            public override Rect GetRenderBounds() => new(0, 0, 10000, 10000);

            public override void OnRender(ImmediateDrawingContext context)
            {
                // First call: kick off the animation loop.
                if (_frameCounter == 0) RegisterForNextAnimationFrameUpdate();
                // Draw a colored rect just so the control has visible output
                // when on-screen.
                context.FillRectangle(
                    new ImmutableSolidColorBrush(_fillColor),
                    new Rect(0, 0, 9999, 9999));
            }
        }
    }
}
