using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Mo.Helpers;

namespace Mo.Controls;

public sealed partial class ApplyConfirmationDialog : ContentDialog
{
    // Below this the bar turns to the caution colour, so "nearly out of time" reads
    // without having to parse the number.
    private const int CautionSeconds = 5;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _elapsed = new();
    private readonly Storyboard _fill;
    private readonly int _totalSeconds;
    private int _secondsShown = -1;
    private bool _caution;
    private bool _confirmed;

    public ApplyConfirmationDialog(int seconds = 15)
    {
        InitializeComponent();

        // Clamped: 0 would revert before the user can read the dialog, and an
        // unbounded value turns the safety net into a stuck window.
        _totalSeconds = Math.Clamp(seconds, 5, 120);

        Title = ResourceHelper.GetString("ApplyConfirmTitle");
        PrimaryButtonText = ResourceHelper.GetString("KeepChanges");
        SecondaryButtonText = ResourceHelper.GetString("Revert");

        var grow = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromSeconds(_totalSeconds)),
            // Deliberately linear. Easing a deadline misreports how much time is left.
            EnableDependentAnimation = false,
        };
        Storyboard.SetTarget(grow, CountdownScale);
        Storyboard.SetTargetProperty(grow, "ScaleX");
        _fill = new Storyboard();
        _fill.Children.Add(grow);

        // The deadline is driven by the clock below, never by the storyboard: with
        // system animations turned off the bar would not run, and the revert has to
        // happen anyway. The bar is presentation only.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += Timer_Tick;

        PrimaryButtonClick += OnPrimaryButtonClick;
        SecondaryButtonClick += OnSecondaryButtonClick;

        ShowSecondsRemaining(_totalSeconds);
    }

    /// <summary>
    /// Shows the dialog and returns true if user confirmed (Keep Changes), false if reverted or timed out.
    /// </summary>
    public async Task<bool> ShowAndWaitAsync()
    {
        await ShowAsync();
        return _confirmed;
    }

    private void ContentDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _confirmed = false;
        _secondsShown = -1;
        _caution = false;
        SetFillBrush("AccentFillColorDefaultBrush");
        ShowSecondsRemaining(_totalSeconds);

        _elapsed.Restart();
        _fill.Begin();
        _timer.Start();
    }

    private void ContentDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        Stop();
    }

    private void Timer_Tick(object? sender, object e)
    {
        double left = _totalSeconds - _elapsed.Elapsed.TotalSeconds;
        if (left <= 0)
        {
            Stop();
            _confirmed = false;
            Hide();
            return;
        }

        ShowSecondsRemaining((int)Math.Ceiling(left));
    }

    private void ShowSecondsRemaining(int value)
    {
        // Ticking at 100 ms to keep the closing moment aligned with the filled bar, but
        // only touching the text when the number actually changes — otherwise a screen
        // reader announces the same string ten times a second.
        if (value == _secondsShown) return;
        _secondsShown = value;

        CountdownText.Text = ResourceHelper.GetString("ApplyConfirmCountdown", value);

        // Threshold, not equality: a dropped tick can skip the exact second.
        if (value <= CautionSeconds && !_caution)
        {
            _caution = true;
            SetFillBrush("SystemFillColorCautionBrush");
        }
    }

    private void SetFillBrush(string themeResourceKey)
    {
        if (Application.Current.Resources.TryGetValue(themeResourceKey, out var brush) && brush is Brush b)
            CountdownFill.Background = b;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Stop();
        _confirmed = true;
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Stop();
        _confirmed = false;
    }

    private void Stop()
    {
        _timer.Stop();
        _elapsed.Stop();
        _fill.Stop();
    }
}
