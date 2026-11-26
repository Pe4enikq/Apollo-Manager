using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.Controls;

/// <summary>
/// Completely decoupled flash-card Starting Guide overlay hosted in MainWindow's top Z-layer.
/// Zero direct dependencies on MainWindowViewModel or workspace views.
/// </summary>
public partial class OnboardingOverlayControl : UserControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<OnboardingOverlayControl, bool>(nameof(IsOpen));

    public static readonly StyledProperty<int> CurrentIndexProperty =
        AvaloniaProperty.Register<OnboardingOverlayControl, int>(nameof(CurrentIndex));

    public static readonly StyledProperty<bool> HasCompletedBeforeProperty =
        AvaloniaProperty.Register<OnboardingOverlayControl, bool>(nameof(HasCompletedBefore));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public int CurrentIndex
    {
        get => GetValue(CurrentIndexProperty);
        set => SetValue(CurrentIndexProperty, value);
    }

    public bool HasCompletedBefore
    {
        get => GetValue(HasCompletedBeforeProperty);
        set => SetValue(HasCompletedBeforeProperty, value);
    }

    public IReadOnlyList<OnboardingCard> Cards { get; }

    public event Action<bool>? DismissRequested;
    public event Action<string>? NavigateModuleRequested;

    static OnboardingOverlayControl()
    {
        IsOpenProperty.Changed.AddClassHandler<OnboardingOverlayControl>((c, _) => c.OnOpenChanged());
        CurrentIndexProperty.Changed.AddClassHandler<OnboardingOverlayControl>((c, _) => c.RenderActiveCard());
        HasCompletedBeforeProperty.Changed.AddClassHandler<OnboardingOverlayControl>((c, _) => c.SyncCheckBoxDefault());
    }

    public OnboardingOverlayControl()
    {
        Cards = OnboardingCardDeck.DefaultCards;
        InitializeComponent();
        BuildStepDots();
        RenderActiveCard();
    }

    private void OnOpenChanged()
    {
        IsVisible = IsOpen;
        IsHitTestVisible = IsOpen;
        if (IsOpen)
        {
            CurrentIndex = 0;
            SyncCheckBoxDefault();
            RenderActiveCard();
        }
    }

    private void SyncCheckBoxDefault()
    {
        // On genuine first-run launch, default to saving completion once user reads or dismisses.
        DontShowAgainCheckBox.IsChecked = !HasCompletedBefore || DontShowAgainCheckBox.IsChecked == true;
    }

    private void BuildStepDots()
    {
        DotsHost.Children.Clear();
        for (int i = 0; i < Cards.Count; i++)
        {
            int idx = i;
            var dotButton = new Button
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Cursor = AeroSkiaHelpers.HandCursor,
                Tag = idx
            };
            dotButton.Click += (_, _) => CurrentIndex = idx;
            DotsHost.Children.Add(dotButton);
        }
    }

    private void RenderActiveCard()
    {
        if (Cards.Count == 0)
        {
            return;
        }

        int clamped = Math.Clamp(CurrentIndex, 0, Cards.Count - 1);
        if (clamped != CurrentIndex)
        {
            CurrentIndex = clamped;
            return;
        }

        var card = Cards[clamped];
        StepBadgeText.Text = $"{card.StepBadge}  ·  {clamped + 1} / {Cards.Count}";
        CardTitleText.Text = card.Title;
        CardSubtitleText.Text = card.Subtitle;
        CardBodyText.Text = card.Body;
        CardTipText.Text = card.TipLabel;
        CardIconControl.Data = Geometry.Parse(card.IconGeometryData);

        bool hasAction = !string.IsNullOrWhiteSpace(card.ActionModuleId) && !string.IsNullOrWhiteSpace(card.ActionLabel);
        ActionButton.IsVisible = hasAction;
        ActionButtonText.Text = card.ActionLabel ?? string.Empty;

        PreviousButton.IsEnabled = clamped > 0;
        NextButton.Content = clamped >= Cards.Count - 1 ? "Get Started" : "Next →";

        for (int i = 0; i < DotsHost.Children.Count; i++)
        {
            if (DotsHost.Children[i] is Button dot)
            {
                bool active = i == clamped;
                dot.Width = active ? 22 : 9;
                dot.Background = active
                    ? (IBrush?)Application.Current?.FindResource("AeroSkyInkBrush") ?? Brushes.DeepSkyBlue
                    : (IBrush?)Application.Current?.FindResource("RowDividerBrush") ?? Brushes.SlateGray;
            }
        }
    }

    public void StepCard(int delta)
    {
        if (Cards.Count == 0) return;
        CurrentIndex = Math.Clamp(CurrentIndex + delta, 0, Cards.Count - 1);
    }

    private void OnPreviousClicked(object? sender, RoutedEventArgs e) => StepCard(-1);

    private void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        if (CurrentIndex >= Cards.Count - 1)
        {
            DismissRequested?.Invoke(DontShowAgainCheckBox.IsChecked == true);
            return;
        }

        StepCard(1);
    }

    private void OnSkipClicked(object? sender, RoutedEventArgs e) =>
        DismissRequested?.Invoke(DontShowAgainCheckBox.IsChecked == true);

    private void OnCloseClicked(object? sender, RoutedEventArgs e) =>
        DismissRequested?.Invoke(DontShowAgainCheckBox.IsChecked == true);

    private void OnActionClicked(object? sender, RoutedEventArgs e)
    {
        var card = Cards[Math.Clamp(CurrentIndex, 0, Cards.Count - 1)];
        if (!string.IsNullOrWhiteSpace(card.ActionModuleId))
        {
            NavigateModuleRequested?.Invoke(card.ActionModuleId);
        }
    }
}
