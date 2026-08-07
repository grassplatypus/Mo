using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mo.Helpers;

namespace Mo.Controls;

public sealed partial class ApplyConfirmationDialog : ContentDialog
{
    private readonly DispatcherTimer _timer;
    private readonly int _totalSeconds;
    private int _secondsRemaining;
    private bool _confirmed;

    public ApplyConfirmationDialog(int seconds = 15)
    {
        InitializeComponent();

        // Clamped: 0 would revert before the user can read the dialog, and an
        // unbounded value turns the safety net into a stuck window.
        _totalSeconds = Math.Clamp(seconds, 5, 120);
        _secondsRemaining = _totalSeconds;
        CountdownProgress.Maximum = _totalSeconds;
        CountdownProgress.Value = _totalSeconds;

        Title = ResourceHelper.GetString("ApplyConfirmTitle");
        PrimaryButtonText = ResourceHelper.GetString("KeepChanges");
        SecondaryButtonText = ResourceHelper.GetString("Revert");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;

        PrimaryButtonClick += OnPrimaryButtonClick;
        SecondaryButtonClick += OnSecondaryButtonClick;

        UpdateDisplay();
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
        _secondsRemaining = _totalSeconds;
        _confirmed = false;
        UpdateDisplay();
        _timer.Start();
    }

    private void ContentDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _timer.Stop();
    }

    private void Timer_Tick(object? sender, object e)
    {
        _secondsRemaining--;
        UpdateDisplay();

        if (_secondsRemaining <= 0)
        {
            _timer.Stop();
            _confirmed = false;
            Hide();
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _timer.Stop();
        _confirmed = true;
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _timer.Stop();
        _confirmed = false;
    }

    private void UpdateDisplay()
    {
        CountdownText.Text = ResourceHelper.GetString("ApplyConfirmCountdown", _secondsRemaining);
        CountdownProgress.Value = _secondsRemaining;
    }
}
