using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace Iris_MediaPlayer.Services;

/// <summary>
/// Composition animations with Fluent timing, so they run off the UI thread and stay smooth
/// while mpv and thumbnail loading keep the UI thread busy.
/// Decelerate is cubic-bezier(0,0,0,1) for things that enter or move into place;
/// standard is cubic-bezier(0.2,0,0,1) for state changes.
/// </summary>
internal static class Motion
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(167);
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(333);
    public static readonly TimeSpan Entrance = TimeSpan.FromMilliseconds(450);

    private static Visual GetVisual(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    private static CompositionEasingFunction Decelerate(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));

    private static CompositionEasingFunction Standard(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));

    private static CompositionEasingFunction Ease(Compositor compositor, bool decelerate)
        => decelerate ? Decelerate(compositor) : Standard(compositor);

    /// <summary>Moves an element by an offset on top of its layout position.</summary>
    public static void Translate(UIElement element, Vector3 to, TimeSpan duration, bool decelerate = true, TimeSpan delay = default)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = GetVisual(element);
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, to, Ease(compositor, decelerate));
        animation.Duration = duration;
        animation.DelayTime = delay;
        visual.StartAnimation("Translation", animation);
    }

    /// <summary>Sets the translation immediately, without animating.</summary>
    public static void SetTranslation(UIElement element, Vector3 value)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = GetVisual(element);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", value);
    }

    public static void Fade(UIElement element, float to, TimeSpan duration, TimeSpan delay = default, bool decelerate = false)
    {
        var visual = GetVisual(element);
        var compositor = visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, to, Ease(compositor, decelerate));
        animation.Duration = duration;
        animation.DelayTime = delay;
        visual.StartAnimation("Opacity", animation);
    }

    public static void SetOpacity(UIElement element, float value)
    {
        var visual = GetVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = value;
    }

    /// <summary>Scales around the element's centre (or <paramref name="center"/>, as fractions of its size).</summary>
    public static void Scale(FrameworkElement element, float to, TimeSpan duration, bool decelerate = false, Vector2? center = null)
    {
        var visual = GetVisual(element);
        SetCenter(element, visual, center ?? new Vector2(0.5f, 0.5f));
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, new Vector3(to, to, 1), Ease(compositor, decelerate));
        animation.Duration = duration;
        visual.StartAnimation("Scale", animation);
    }

    public static void SetScale(FrameworkElement element, float value, Vector2? center = null)
    {
        var visual = GetVisual(element);
        SetCenter(element, visual, center ?? new Vector2(0.5f, 0.5f));
        visual.StopAnimation("Scale");
        visual.Scale = new Vector3(value, value, 1);
    }

    /// <summary>Rotates (degrees) and translates together, e.g. the fanned thumbnails on folder cards.</summary>
    public static void RotateAndTranslate(FrameworkElement element, float degrees, Vector3 translation, float scale,
        TimeSpan duration, bool animate = true)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = GetVisual(element);
        SetCenter(element, visual, new Vector2(0.5f, 0.5f));

        if (!animate)
        {
            visual.StopAnimation("RotationAngleInDegrees");
            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            visual.RotationAngleInDegrees = degrees;
            visual.Properties.InsertVector3("Translation", translation);
            visual.Scale = new Vector3(scale, scale, 1);
            return;
        }

        var compositor = visual.Compositor;
        var rotate = compositor.CreateScalarKeyFrameAnimation();
        rotate.InsertKeyFrame(1, degrees, Decelerate(compositor));
        rotate.Duration = duration;
        visual.StartAnimation("RotationAngleInDegrees", rotate);

        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1, translation, Decelerate(compositor));
        move.Duration = duration;
        visual.StartAnimation("Translation", move);

        var grow = compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(1, new Vector3(scale, scale, 1), Decelerate(compositor));
        grow.Duration = duration;
        visual.StartAnimation("Scale", grow);
    }

    /// <summary>The staggered entrance: fade up 14 px. The element is hidden until its delay passes.</summary>
    public static void EnterFromBelow(UIElement element, TimeSpan delay, float distance = 14)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = GetVisual(element);
        var compositor = visual.Compositor;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, Decelerate(compositor));
        fade.Duration = Entrance;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", fade);

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, distance, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, Decelerate(compositor));
        rise.Duration = Entrance;
        rise.DelayTime = delay;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Translation", rise);
    }

    /// <summary>A slow, endless zoom (Ken Burns) toward a point of the artwork.</summary>
    public static void StartDrift(FrameworkElement element, float from, float to, TimeSpan period, Vector2 focus)
    {
        var visual = GetVisual(element);
        SetCenter(element, visual, focus);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, new Vector3(from, from, 1));
        animation.InsertKeyFrame(1, new Vector3(to, to, 1), easing);
        animation.Duration = period;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.Direction = AnimationDirection.Alternate;
        visual.StartAnimation("Scale", animation);
    }

    /// <summary>The play/pause pulse: grows from 0.7 to 1.35 while fading out.</summary>
    public static void Pulse(FrameworkElement element)
    {
        var visual = GetVisual(element);
        SetCenter(element, visual, new Vector2(0.5f, 0.5f));
        var compositor = visual.Compositor;
        var duration = TimeSpan.FromMilliseconds(600);

        var grow = compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(0.7f, 0.7f, 1));
        grow.InsertKeyFrame(1, new Vector3(1.35f, 1.35f, 1), Decelerate(compositor));
        grow.Duration = duration;
        visual.StartAnimation("Scale", grow);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.95f);
        fade.InsertKeyFrame(1, 0, Decelerate(compositor));
        fade.Duration = duration;
        visual.StartAnimation("Opacity", fade);
    }

    private static void SetCenter(FrameworkElement element, Visual visual, Vector2 fraction)
    {
        // Before the first layout pass ActualWidth is 0; fall back to an explicit size.
        var width = element.ActualWidth > 0 ? element.ActualWidth : double.IsNaN(element.Width) ? 0 : element.Width;
        var height = element.ActualHeight > 0 ? element.ActualHeight : double.IsNaN(element.Height) ? 0 : element.Height;
        visual.CenterPoint = new Vector3((float)width * fraction.X, (float)height * fraction.Y, 0);
    }
}

/// <summary>Pointer helpers for hover effects.</summary>
internal static class PointerHelper
{
    /// <summary>
    /// PointerExited bubbles up from child elements, so a container gets it while the pointer is still
    /// inside. This tells the two apart.
    /// </summary>
    public static bool IsInside(FrameworkElement element, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e, double edge = 0)
    {
        var point = e.GetCurrentPoint(element).Position;
        return point.X >= edge && point.Y >= edge
               && point.X < element.ActualWidth - edge && point.Y < element.ActualHeight - edge;
    }
}
