using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;

namespace QuiverLauncher.Views;

/// <summary>Startup-only animation that continues on the render thread during card layout.</summary>
public sealed class LibraryLoadingBar : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LibraryLoadingBar, bool>(nameof(IsActive));
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<LibraryLoadingBar, IBrush?>(nameof(Foreground));
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private CompositionCustomVisual? _visual;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;
        _visual = compositor.CreateCustomVisual(new LoadingAnimation());
        ElementComposition.SetElementChildVisual(this, _visual);
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _visual?.SendHandlerMessage(new AnimationState(false, Colors.Transparent));
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == IsActiveProperty || change.Property == ForegroundProperty)
            UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (_visual == null) return;
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        _visual.SendHandlerMessage(new AnimationState(IsActive,
            (Foreground as ISolidColorBrush)?.Color ?? Color.FromRgb(0, 120, 215)));
    }

    private sealed record AnimationState(bool Active, Color Color);

    private sealed class LoadingAnimation : CompositionCustomVisualHandler
    {
        private bool _active;
        private TimeSpan _started;
        private ImmutableSolidColorBrush _brush = new(Colors.Transparent);

        public override void OnMessage(object message)
        {
            if (message is not AnimationState state) return;
            if (state.Active && !_active) _started = CompositionNow;
            _active = state.Active;
            _brush = new(state.Color);
            Invalidate();
            if (_active) RegisterForNextAnimationFrameUpdate();
        }

        public override void OnAnimationFrameUpdate()
        {
            if (!_active) return;
            Invalidate();
            RegisterForNextAnimationFrameUpdate();
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (!_active || EffectiveSize.X <= 0 || EffectiveSize.Y <= 0) return;
            var width = EffectiveSize.X * 0.35;
            var phase = (CompositionNow - _started).TotalSeconds % 1.2 / 1.2;
            var x = -width + (EffectiveSize.X + width) * phase;
            using (context.PushClip(new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y)))
                context.DrawRectangle(_brush, null, new Rect(x, 0, width, EffectiveSize.Y), EffectiveSize.Y / 2, EffectiveSize.Y / 2);
        }
    }
}
