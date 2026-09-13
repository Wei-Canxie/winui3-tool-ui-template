using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using WinRT.Interop;

namespace UiTemplate;

/// <summary>
/// A page container that remembers which handlers were attached to it and detaches them
/// on <see cref="Dispose"/>.
///
/// Pages are thrown away and rebuilt constantly (navigation, Apply, theme change).
/// Without this, every rebuild leaves a live subscription behind and handlers keep firing
/// against controls nobody can see any more.
/// </summary>
internal sealed class DisposablePage : StackPanel, IDisposable
{
    private readonly List<Action> _unsubscribes = new();

    /// <summary>Queue a handler detachment to run when the page is disposed.</summary>
    internal void RegisterUnsubscribe(Action unsubscribe) => _unsubscribes.Add(unsubscribe);

    public void Dispose()
    {
        foreach (var unsubscribe in _unsubscribes)
        {
            try { unsubscribe(); }
            catch (Exception ex) { AppLog.Log($"Page unsubscribe failed: {ex.Message}"); }
        }

        _unsubscribes.Clear();
    }
}

/// <summary>
/// The tool shell: a 32px custom title bar over a <see cref="NavigationView"/>, with
/// settings pages built in C# and a floating Apply/Cancel card.
///
/// The window never owns the settings it edits.  It works on a draft (<c>_settings</c>)
/// and only writes to the live instance (<c>_liveSettings</c>) and to disk when the user
/// presses Apply; <c>_applied</c> is the snapshot that Cancel restores from.
/// </summary>
internal sealed class ShellWindow : Window
{
    /// <summary>All user-visible text, one block per page so it is easy to localise.</summary>
    private static class Strings
    {
        internal const string AppTitle = "UI Template";

        internal static class General
        {
            internal const string Header = "General";
            internal const string Intro =
                "The template's first page. This is the simplest setting shape: a switch that is "
                + "written to the draft and only committed when you press Apply.";
            internal const string SampleToggle = "Enable the sample feature";
            internal const string On = "On";
            internal const string Off = "Off";
            internal const string TrayHint = "The tray menu can toggle the same feature without opening this page.";
        }

        internal static class Appearance
        {
            internal const string Header = "Appearance";
            internal const string Intro =
                "Theme, window opacity and the background are applied to the window itself, so the "
                + "effect is visible before you commit it.";
            internal const string Theme = "Theme";
            internal const string ThemeFollowSystem = "Follow system";
            internal const string ThemeLight = "Light";
            internal const string ThemeDark = "Dark";
            internal const string WindowOpacity = "Window opacity";
            internal const string WindowOpacityLocked = "Window opacity: 100% (fixed while a backdrop is active)";
            internal const string Background = "Background";
            internal const string BackgroundImage = "Image";
            internal const string BackgroundMica = "Mica";
            internal const string BackgroundAcrylic = "Acrylic";
            internal const string BackgroundUnsupported =
                "This system does not provide the Mica/Acrylic backdrops; the image is used instead.";
            internal const string BlurRadius = "Blur radius";
            internal const string ChooseImage = "Choose image...";
            internal const string ClearImage = "Clear";
            internal const string NoImage = "(none)";
            internal const string ImageOpacity = "Image opacity";
        }

        internal static class Advanced
        {
            internal const string Header = "Advanced";
            internal const string Intro =
                "This is where tool-specific settings go. The row below is the template's numeric "
                + "control: drag the slider, type an exact value, or use the buttons.";
            internal const string SampleStrength = "Sample strength";
            internal const string HowToAddSettings =
                "To add a setting: add a property to AppSettings, show it here, and write it to the "
                + "draft in the handler. Apply takes care of the rest.";
        }

        internal static class ApplyCard
        {
            internal const string Apply = "Apply";
            internal const string Cancel = "Cancel changes";
        }
    }

    /// <summary>Height of the custom title bar row.</summary>
    private const int TitleBarHeight = 32;

    /// <summary>Sidebar collapse duration; the NavigationView template's own close takes the same time.</summary>
    private const int SidebarCloseMs = 120;

    /// <summary>The template's own easing curve, reused so both animations shrink together.</summary>
    private static readonly Windows.Foundation.Point SplineStart = new(0.1, 0.9);
    private static readonly Windows.Foundation.Point SplineEnd = new(0.2, 1.0);

    /// <summary>The settings the rest of the app uses. Only Apply writes to this.</summary>
    private readonly AppSettings _liveSettings;

    /// <summary>The draft the UI edits.</summary>
    private readonly AppSettings _settings;

    /// <summary>Snapshot of the last applied draft, used by Cancel changes.</summary>
    private AppSettings _applied;

    /// <summary>Scroll offset per page tag, so navigation does not jump back to the top.</summary>
    private readonly Dictionary<string, double> _scrollCache = new();

    private readonly Border _titleBar;
    private readonly TextBlock _titleBarText;
    private readonly NavigationView _nav;
    private readonly Border _applyCard;

    private FrameworkElement? _currentPage;
    private string? _currentTag;
    private bool _dirty;

    /// <summary>One clip per pane instance; re-clipping on a theme change would stack SizeChanged handlers.</summary>
    private FrameworkElement? _clippedPane;

    private bool _paneAnimating;
    private Storyboard? _paneAnimation;
    private FrameworkElement? _pinnedPane;

    /// <summary>Raised after the tray toggles the demo feature, so the tray label can follow.</summary>
    internal event Action<bool>? SampleEnabledChanged;

    /// <param name="liveSettings">The instance the app actually uses; it is adopted, not copied.</param>
    internal ShellWindow(AppSettings liveSettings)
    {
        _liveSettings = liveSettings;

        // The UI edits a clone. Applying rebuilds the page to re-colour the controls, and
        // doing that per slider tick would destroy the very slider being dragged.
        _settings = liveSettings.Clone();
        _applied = _settings.Clone();

        Title = Strings.AppTitle;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 680));

        // Tray tool: the close button hides the window instead of ending the process.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };

        ExtendsContentIntoTitleBar = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleBarText = new TextBlock
        {
            Text = Title,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetTitleBarForeground(),
        };
        _titleBar = new Border
        {
            Height = TitleBarHeight,
            Background = GetTitleBarBrush(),
            Child = _titleBarText,
        };
        Grid.SetRow(_titleBar, 0);

        _nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
            CompactPaneLength = 48,
            OpenPaneLength = 200,
            IsPaneOpen = false,
        };
        HookSidebarAnimation();

        _nav.MenuItems.Add(Item(Strings.General.Header, Symbol.Home, "general"));
        _nav.MenuItems.Add(Item(Strings.Appearance.Header, Symbol.View, "appearance"));
        _nav.MenuItems.Add(Item(Strings.Advanced.Header, Symbol.Setting, "advanced"));

        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is NavigationViewItem { Tag: string tag }) NavigateTo(tag);
        };

        _nav.Loaded += (_, _) =>
        {
            try { _nav.SelectedItem = _nav.MenuItems[0]; }
            catch (Exception ex) { AppLog.Log($"Selecting the first page failed: {ex.Message}"); }

            // The pane and the caption buttons only exist once the template is applied.
            ApplyAppearance(rebuildPage: false);
        };

        Grid.SetRow(_nav, 1);

        // The card floats over the navigation row so the sidebar still spans the full
        // window height; a real layout row would leave a band the sidebar and the
        // backdrop cannot cover.
        _applyCard = BuildApplyCard();
        Grid.SetRow(_applyCard, 1);

        root.Children.Add(_titleBar);
        root.Children.Add(_nav);
        root.Children.Add(_applyCard);
        Content = root;

        AppLog.Log("Shell window created");
    }

    // ------------------------------------------------------------------ shell API

    /// <summary>Re-show the window after the close button hid it.</summary>
    internal void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    /// <summary>
    /// Toggle the demo feature from outside the window (tray menu).  That is an external
    /// change like any other, so it goes straight to the live instance and the draft is
    /// only resynced while it holds no pending edits.
    /// </summary>
    internal void ToggleSampleFeature()
    {
        try
        {
            _liveSettings.SampleEnabled = !_liveSettings.SampleEnabled;
            _liveSettings.Save();

            if (!_dirty)
            {
                _settings.CopyFrom(_liveSettings);
                _applied = _settings.Clone();
            }

            RebuildCurrentPage();
            SampleEnabledChanged?.Invoke(_liveSettings.SampleEnabled);
            AppLog.Log($"Sample feature toggled from the tray: {_liveSettings.SampleEnabled}");
        }
        catch (Exception ex) { AppLog.Log($"ToggleSampleFeature failed: {ex.Message}"); }
    }

    private static NavigationViewItem Item(string text, Symbol icon, string tag) => new()
    {
        Content = text,
        Icon = new SymbolIcon(icon),
        Tag = tag,
    };

    // ----------------------------------------------------------------- title bar

    private bool IsDark => AppearanceManager.IsDark(_settings);

    private Brush GetTitleBarForeground() => new SolidColorBrush(IsDark ? Colors.White : Colors.Black);

    private Brush GetTitleBarBrush()
    {
        // The opacity slider is inert while a backdrop owns the surface, so the title bar
        // stays fully opaque there.
        var windowOpacity = _settings.BackgroundBlur == AppSettings.BlurMode.Default
            ? _settings.WindowOpacity
            : 1.0;

        // A 90%-opaque window would otherwise leave the title bar looking washed out.
        var effective = windowOpacity <= 0.9 ? Math.Clamp(windowOpacity + 0.1, 0, 1) : windowOpacity;

        return new SolidColorBrush(IsDark
            ? Color.FromArgb((byte)(effective * 255), 0x2D, 0x2D, 0x2D)
            : Color.FromArgb((byte)(effective * 255), 0xF3, 0xF3, 0xF3));
    }

    /// <summary>Translucent brush for the floating Apply/Cancel card.</summary>
    private Brush GetFloatingCardBrush() => IsDark
        ? new SolidColorBrush(Color.FromArgb(0xE6, 0x2D, 0x2D, 0x2D))
        : new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// The caption buttons follow the *system* theme by default, so light mode draws white
    /// glyphs on a light title bar.  They have to be recoloured by hand.
    /// </summary>
    private void ApplyCaptionButtonColors()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;

            var bar = AppWindow.TitleBar;
            var isDark = IsDark;

            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
            bar.ButtonInactiveForegroundColor = isDark
                ? Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A)
                : Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
            bar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = isDark
                ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
            bar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
            bar.ButtonPressedBackgroundColor = isDark
                ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
        }
        catch (Exception ex) { AppLog.Log($"ApplyCaptionButtonColors failed: {ex.Message}"); }
    }

    // -------------------------------------------------------------- apply/cancel

    /// <summary>The floating Apply/Cancel card, hidden until something is edited.</summary>
    private Border BuildApplyCard()
    {
        var apply = new Button
        {
            Content = Strings.ApplyCard.Apply,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        try
        {
            if (Application.Current.Resources["AccentButtonStyle"] is Style accent) apply.Style = accent;
        }
        catch (Exception ex) { AppLog.Log($"AccentButtonStyle unavailable: {ex.Message}"); }

        apply.Click += (_, _) => ApplyPendingChanges();

        var cancel = new Button
        {
            Content = Strings.ApplyCard.Cancel,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cancel.Click += (_, _) => CancelPendingChanges();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 16),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            Background = GetFloatingCardBrush(),
            Child = buttons,
            Visibility = Visibility.Collapsed,
        };
    }

    /// <summary>Commit the draft: live instance, window appearance, disk, snapshot.</summary>
    private void ApplyPendingChanges()
    {
        try
        {
            _liveSettings.CopyFrom(_settings);
            ApplyAppearance(rebuildPage: true);
            _settings.Save();
            _applied = _settings.Clone();
            HideApplyCard();
            SampleEnabledChanged?.Invoke(_liveSettings.SampleEnabled);
            AppLog.Log("Settings applied");
        }
        catch (Exception ex) { AppLog.Log($"Apply failed: {ex.Message}"); }
    }

    /// <summary>Roll the draft back to the last applied snapshot.</summary>
    private void CancelPendingChanges()
    {
        _settings.CopyFrom(_applied);
        HideApplyCard();

        // Every control has to be rebuilt to show the restored value again.
        RebuildCurrentPage();
        AppLog.Log("Pending changes discarded");
    }

    /// <summary>
    /// Mark the draft dirty.  Control handlers call this and nothing else: no apply, no
    /// save, no rebuild.  Anything more would fight the control the user is using.
    /// </summary>
    private void MarkDirty()
    {
        if (_dirty) return;

        _dirty = true;
        _applyCard.Visibility = Visibility.Visible;
    }

    private void HideApplyCard()
    {
        _dirty = false;
        _applyCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>Re-apply the appearance from the draft to the window.</summary>
    private void ApplyAppearance(bool rebuildPage)
    {
        AppearanceManager.ApplyAll(this, _settings);

        _titleBar.Background = GetTitleBarBrush();
        _titleBarText.Foreground = GetTitleBarForeground();
        _applyCard.Background = GetFloatingCardBrush();

        SyncSidebar();
        ApplyCaptionButtonColors();

        if (rebuildPage) RebuildCurrentPage();
    }

    // ------------------------------------------------------------------- sidebar

    /// <summary>
    /// Give the sidebar one background brush for the whole column, rounded 12px on the
    /// content side and square against the window edge, then flatten the insets the
    /// NavigationView template adds around the pane.
    /// </summary>
    private void SyncSidebar()
    {
        try
        {
            var splitView = FindSplitView(_nav);
            if (splitView?.Pane is not FrameworkElement pane)
            {
                AppLog.Log("SyncSidebar: the pane is not realised yet");
                return;
            }

            var background = new SolidColorBrush(IsDark
                ? Color.FromArgb(0xFF, 0x2D, 0x2D, 0x2D)
                : Colors.White);

            switch (pane)
            {
                case Panel panel: panel.Background = background; break;
                case Border border: border.Background = background; break;
                default: AppLog.Log($"SyncSidebar: unexpected pane type {pane.GetType().Name}"); break;
            }

            // The template insets the pane (a 3px margin plus a 1px bordered host), which
            // leaves hairline slits above and below the sidebar. Flatten the pane and its
            // pane-side ancestors; the 1px host border inset is kept on purpose.
            pane.Margin = new Thickness(0);
            FlattenPaneAncestors(pane, splitView);

            ApplyRoundedPaneClip(pane);
        }
        catch (Exception ex) { AppLog.Log($"SyncSidebar failed: {ex.Message}"); }
    }

    /// <summary>
    /// Strip margin / padding / border / background from every element between the pane
    /// and the SplitView, so the sidebar background reaches the title bar and the bottom.
    /// </summary>
    private static void FlattenPaneAncestors(DependencyObject pane, DependencyObject? stopAt)
    {
        try
        {
            var parent = VisualTreeHelper.GetParent(pane);
            while (parent is not null && parent != stopAt)
            {
                switch (parent)
                {
                    case Border border:
                        border.Margin = new Thickness(0);
                        border.Padding = new Thickness(0);
                        border.BorderThickness = new Thickness(0);
                        border.Background = new SolidColorBrush(Colors.Transparent);
                        break;
                    case Panel panel:
                        panel.Margin = new Thickness(0);
                        panel.Background = new SolidColorBrush(Colors.Transparent);
                        break;
                    case ContentPresenter presenter:
                        presenter.Margin = new Thickness(0);
                        break;
                }

                parent = VisualTreeHelper.GetParent(parent);
            }
        }
        catch (Exception ex) { AppLog.Log($"FlattenPaneAncestors failed: {ex.Message}"); }
    }

    /// <summary>
    /// Round the pane's outer corner.  XAML's RectangleGeometry has no CornerRadius, so
    /// the clip has to come from the Composition layer.
    /// </summary>
    private void ApplyRoundedPaneClip(FrameworkElement pane)
    {
        if (ReferenceEquals(_clippedPane, pane)) return;

        try
        {
            _clippedPane = pane;

            var visual = ElementCompositionPreview.GetElementVisual(pane);
            var clip = visual.Compositor.CreateRectangleClip();
            clip.TopLeftRadius = Vector2.Zero;
            clip.BottomLeftRadius = Vector2.Zero;
            clip.TopRightRadius = new Vector2(12, 12);
            clip.BottomRightRadius = new Vector2(12, 12);

            SyncClipBounds(clip, pane);
            visual.Clip = clip;

            pane.SizeChanged += (_, _) =>
            {
                try { SyncClipBounds(clip, pane); }
                catch (Exception ex) { AppLog.Log($"Pane clip resize failed: {ex.Message}"); }
            };
        }
        catch (Exception ex) { AppLog.Log($"ApplyRoundedPaneClip failed: {ex.Message}"); }
    }

    /// <summary>
    /// Keep the clip on the pane's real bounds.  A clip with a zero-sized box hides the
    /// sidebar completely, which shows up as a few frames of nothing while the visual
    /// states swap, so non-positive sizes are skipped and the last good bounds are kept.
    /// </summary>
    private static void SyncClipBounds(Microsoft.UI.Composition.RectangleClip clip, FrameworkElement pane)
    {
        double width = pane.ActualWidth, height = pane.ActualHeight;
        if (!(width > 0) || !(height > 0)) return;

        clip.Left = 0f;
        clip.Top = 0f;
        clip.Right = (float)width;
        clip.Bottom = (float)height;
    }

    private static SplitView? FindSplitView(DependencyObject? parent)
    {
        if (parent is null) return null;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is SplitView splitView) return splitView;

            var nested = FindSplitView(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    /// <summary>
    /// Watch the pane opening and closing.
    ///
    /// Opening only has to drop the width the collapse pinned.  Closing has to be animated
    /// here: the template's own close shrinks the pane inside the same 120ms as its slide,
    /// so the slide is invisible, and fighting that animation frame by frame is what makes
    /// the pane flicker.  Animating the pane width with the template's own duration and
    /// curve means the two move together.
    /// </summary>
    private void HookSidebarAnimation()
    {
        _nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
        {
            if (_nav.IsPaneOpen) ResetSidebarWidth();
            else AnimateSidebarCollapse();
        });
    }

    /// <summary>Drop the width the collapse pinned, so the pane can widen again.</summary>
    private void ResetSidebarWidth()
    {
        _paneAnimating = false;

        try { _paneAnimation?.Stop(); }
        catch (Exception ex) { AppLog.Log($"Stopping the sidebar animation failed: {ex.Message}"); }

        _paneAnimation = null;

        if (_pinnedPane is not null)
        {
            try { _pinnedPane.ClearValue(FrameworkElement.WidthProperty); }
            catch (Exception ex) { AppLog.Log($"Clearing the pinned pane width failed: {ex.Message}"); }

            _pinnedPane = null;
        }
    }

    private void AnimateSidebarCollapse()
    {
        if (_paneAnimating) return;

        _paneAnimating = true;

        try
        {
            var splitView = FindSplitView(_nav);
            if (splitView?.Pane is not FrameworkElement pane || splitView.CompactPaneLength <= 0)
            {
                _paneAnimating = false;
                return;
            }

            ResetSidebarWidth();

            double startWidth = pane.ActualWidth > splitView.CompactPaneLength
                ? pane.ActualWidth
                : splitView.OpenPaneLength;

            // Pin the width first: the SplitView arranges the pane down to the compact width
            // the moment the close lands, and only an explicit width, which the animation
            // then drives, keeps it wide enough to be seen shrinking.
            pane.Width = startWidth;
            _pinnedPane = pane;

            _paneAnimation = BuildSidebarAnimation(pane, startWidth, splitView.CompactPaneLength);
            _paneAnimation.Completed += (_, _) =>
            {
                try { _paneAnimation?.Stop(); }
                catch (Exception ex) { AppLog.Log($"Stopping the sidebar animation failed: {ex.Message}"); }

                _paneAnimation = null;
                _paneAnimating = false;
            };

            _paneAnimation.Begin();
        }
        catch (Exception ex)
        {
            AppLog.Log($"Sidebar collapse failed: {ex.Message}");
            _paneAnimating = false;
        }
    }

    private static Storyboard BuildSidebarAnimation(DependencyObject target, double from, double to)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            // Animating a layout property is only allowed with this flag.
            EnableDependentAnimation = true,
        };

        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(SidebarCloseMs),
            KeySpline = new KeySpline { ControlPoint1 = SplineStart, ControlPoint2 = SplineEnd },
            Value = to,
        });

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Width");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return storyboard;
    }

    // --------------------------------------------------------------------- pages

    private void NavigateTo(string tag)
    {
        // Detach the outgoing page's handlers before dropping it.
        if (_currentPage is IDisposable disposable) disposable.Dispose();

        CacheScrollPosition();

        _currentTag = tag;
        _currentPage = BuildPage(tag);
        _nav.Content = _currentPage;

        RestoreScrollPosition(tag, _currentPage);
    }

    private FrameworkElement BuildPage(string tag)
    {
        DisposablePage page = tag switch
        {
            "appearance" => BuildAppearancePage(),
            "advanced" => BuildAdvancedPage(),
            _ => BuildGeneralPage(),
        };

        var viewer = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        viewer.ViewChanged += (_, _) =>
        {
            if (_currentTag is not null) _scrollCache[_currentTag] = viewer.VerticalOffset;
        };

        return viewer;
    }

    private void RebuildCurrentPage()
    {
        if (_currentTag is not null && _currentPage is not null) NavigateTo(_currentTag);
    }

    private void CacheScrollPosition()
    {
        if (_currentPage is ScrollViewer viewer && _currentTag is not null)
        {
            _scrollCache[_currentTag] = viewer.VerticalOffset;
        }
    }

    private void RestoreScrollPosition(string tag, FrameworkElement page)
    {
        if (page is ScrollViewer viewer && _scrollCache.TryGetValue(tag, out var offset))
        {
            viewer.ScrollToVerticalOffset(offset);
        }
    }

    private static DisposablePage NewPage() => new()
    {
        Spacing = 12,
        Padding = new Thickness(24, 16, 24, 16),
    };

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
    };

    private static TextBlock Description(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Opacity = 0.7,
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 0),
    };

    /// <summary>General: a switch and a note. The smallest possible page.</summary>
    private DisposablePage BuildGeneralPage()
    {
        var page = NewPage();
        page.Children.Add(Header(Strings.General.Header));
        page.Children.Add(Description(Strings.General.Intro));

        var toggle = new ToggleSwitch
        {
            Header = Strings.General.SampleToggle,
            OnContent = Strings.General.On,
            OffContent = Strings.General.Off,
            IsOn = _settings.SampleEnabled,
        };

        RoutedEventHandler handler = (_, _) =>
        {
            // Draft only: the feature is not switched until Apply.
            _settings.SampleEnabled = toggle.IsOn;
            MarkDirty();
        };

        toggle.Toggled += handler;
        page.RegisterUnsubscribe(() => toggle.Toggled -= handler);
        page.Children.Add(toggle);

        page.Children.Add(Description(Strings.General.TrayHint));
        return page;
    }

    /// <summary>Appearance: theme, opacity and background, all applied to the window.</summary>
    private DisposablePage BuildAppearancePage()
    {
        var page = NewPage();
        page.Children.Add(Header(Strings.Appearance.Header));
        page.Children.Add(Description(Strings.Appearance.Intro));

        // ---- theme ----
        page.Children.Add(Section(Strings.Appearance.Theme));

        var themeFollow = new RadioButton { Content = Strings.Appearance.ThemeFollowSystem };
        var themeLight = new RadioButton { Content = Strings.Appearance.ThemeLight };
        var themeDark = new RadioButton { Content = Strings.Appearance.ThemeDark };

        switch (_settings.Theme)
        {
            case AppSettings.ThemeMode.Light: themeLight.IsChecked = true; break;
            case AppSettings.ThemeMode.Dark: themeDark.IsChecked = true; break;
            default: themeFollow.IsChecked = true; break;
        }

        // Each handler is kept in a local so the page can detach it on Dispose; an inline
        // lambda is unattachable and would keep firing after the page was thrown away.
        RoutedEventHandler followHandler = (_, _) => { _settings.Theme = AppSettings.ThemeMode.FollowSystem; MarkDirty(); };
        RoutedEventHandler lightHandler = (_, _) => { _settings.Theme = AppSettings.ThemeMode.Light; MarkDirty(); };
        RoutedEventHandler darkHandler = (_, _) => { _settings.Theme = AppSettings.ThemeMode.Dark; MarkDirty(); };

        themeFollow.Checked += followHandler;
        themeLight.Checked += lightHandler;
        themeDark.Checked += darkHandler;

        page.RegisterUnsubscribe(() => themeFollow.Checked -= followHandler);
        page.RegisterUnsubscribe(() => themeLight.Checked -= lightHandler);
        page.RegisterUnsubscribe(() => themeDark.Checked -= darkHandler);

        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        themeRow.Children.Add(themeFollow);
        themeRow.Children.Add(themeLight);
        themeRow.Children.Add(themeDark);
        page.Children.Add(themeRow);

        // ---- window opacity ----
        var opacityStatus = new TextBlock
        {
            Text = $"{Strings.Appearance.WindowOpacity}: {_settings.WindowOpacity:P0}",
            FontWeight = FontWeights.SemiBold,
        };
        page.Children.Add(opacityStatus);

        var opacityRow = BuildSliderWithTextBox(
            Strings.Appearance.WindowOpacity,
            _settings.WindowOpacity,
            0.3,
            1.0,
            value =>
            {
                _settings.WindowOpacity = value;
                opacityStatus.Text = $"{Strings.Appearance.WindowOpacity}: {value:P0}";
                MarkDirty();
            },
            step: 0.05);
        page.Children.Add(opacityRow);

        // ---- background ----
        page.Children.Add(Section(Strings.Appearance.Background));

        var backgroundImage = new RadioButton { Content = Strings.Appearance.BackgroundImage };
        var backgroundMica = new RadioButton { Content = Strings.Appearance.BackgroundMica };
        var backgroundAcrylic = new RadioButton { Content = Strings.Appearance.BackgroundAcrylic };

        switch (_settings.BackgroundBlur)
        {
            case AppSettings.BlurMode.Mica: backgroundMica.IsChecked = true; break;
            case AppSettings.BlurMode.Acrylic: backgroundAcrylic.IsChecked = true; break;
            default: backgroundImage.IsChecked = true; break;
        }

        // A backdrop owns the window surface, so the opacity slider is meaningless there:
        // disable the whole row and say why, instead of leaving a control that looks
        // draggable but does nothing.
        void UpdateOpacityRow()
        {
            var locked = _settings.BackgroundBlur != AppSettings.BlurMode.Default;

            SetRowEnabled(opacityRow, !locked);
            opacityStatus.Text = locked
                ? Strings.Appearance.WindowOpacityLocked
                : $"{Strings.Appearance.WindowOpacity}: {_settings.WindowOpacity:P0}";
        }

        RoutedEventHandler imageHandler = (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Default; UpdateOpacityRow(); MarkDirty(); };
        RoutedEventHandler micaHandler = (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Mica; UpdateOpacityRow(); MarkDirty(); };
        RoutedEventHandler acrylicHandler = (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Acrylic; UpdateOpacityRow(); MarkDirty(); };

        backgroundImage.Checked += imageHandler;
        backgroundMica.Checked += micaHandler;
        backgroundAcrylic.Checked += acrylicHandler;

        page.RegisterUnsubscribe(() => backgroundImage.Checked -= imageHandler);
        page.RegisterUnsubscribe(() => backgroundMica.Checked -= micaHandler);
        page.RegisterUnsubscribe(() => backgroundAcrylic.Checked -= acrylicHandler);

        var backgroundRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        backgroundRow.Children.Add(backgroundImage);
        backgroundRow.Children.Add(backgroundMica);
        backgroundRow.Children.Add(backgroundAcrylic);
        page.Children.Add(backgroundRow);

        UpdateOpacityRow();

        if (!IsBackdropSupported()) page.Children.Add(Description(Strings.Appearance.BackgroundUnsupported));

        // ---- background image ----
        var blurStatus = new TextBlock
        {
            Text = $"{Strings.Appearance.BlurRadius}: {_settings.BackgroundBlurRadius}px",
            FontWeight = FontWeights.SemiBold,
        };
        page.Children.Add(blurStatus);

        page.Children.Add(BuildSliderWithTextBox(
            Strings.Appearance.BlurRadius,
            _settings.BackgroundBlurRadius,
            0,
            255,
            value =>
            {
                _settings.BackgroundBlurRadius = (int)value;
                blurStatus.Text = $"{Strings.Appearance.BlurRadius}: {(int)value}px";
                MarkDirty();
            },
            step: 1,
            format: "0",
            textMax: 1024));

        var pathLabel = new TextBlock
        {
            Text = DescribeImage(_settings.BackgroundImagePath),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var chooseButton = new Button { Content = Strings.Appearance.ChooseImage };
        RoutedEventHandler chooseHandler = (_, _) =>
        {
            var path = ShowImagePicker();
            if (!string.IsNullOrEmpty(path))
            {
                _settings.BackgroundImagePath = path;
                pathLabel.Text = DescribeImage(path);
                MarkDirty();
            }
        };
        chooseButton.Click += chooseHandler;
        page.RegisterUnsubscribe(() => chooseButton.Click -= chooseHandler);

        var clearButton = new Button { Content = Strings.Appearance.ClearImage };
        RoutedEventHandler clearHandler = (_, _) =>
        {
            _settings.BackgroundImagePath = "";
            pathLabel.Text = DescribeImage("");
            MarkDirty();
        };
        clearButton.Click += clearHandler;
        page.RegisterUnsubscribe(() => clearButton.Click -= clearHandler);

        var imageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        imageRow.Children.Add(pathLabel);
        imageRow.Children.Add(chooseButton);
        imageRow.Children.Add(clearButton);
        page.Children.Add(imageRow);

        var imageOpacityStatus = new TextBlock
        {
            Text = $"{Strings.Appearance.ImageOpacity}: {_settings.BackgroundImageOpacity:P0}",
            FontWeight = FontWeights.SemiBold,
        };
        page.Children.Add(imageOpacityStatus);

        page.Children.Add(BuildSliderWithTextBox(
            Strings.Appearance.ImageOpacity,
            _settings.BackgroundImageOpacity,
            0.0,
            1.0,
            value =>
            {
                _settings.BackgroundImageOpacity = value;
                imageOpacityStatus.Text = $"{Strings.Appearance.ImageOpacity}: {value:P0}";
                MarkDirty();
            },
            step: 0.05));

        return page;
    }

    /// <summary>Advanced: where tool-specific settings go.</summary>
    private DisposablePage BuildAdvancedPage()
    {
        var page = NewPage();
        page.Children.Add(Header(Strings.Advanced.Header));
        page.Children.Add(Description(Strings.Advanced.Intro));

        page.Children.Add(BuildSliderWithTextBox(
            Strings.Advanced.SampleStrength,
            _settings.SampleStrength,
            0,
            100,
            value =>
            {
                _settings.SampleStrength = value;
                MarkDirty();
            },
            step: 1,
            format: "0"));

        page.Children.Add(Description(Strings.Advanced.HowToAddSettings));
        return page;
    }

    /// <summary>
    /// The template's numeric row: label | slider | text box | − / + buttons.
    ///
    /// The slider covers the practical range while the text box accepts values outside it
    /// (up to <paramref name="textMin"/>/<paramref name="textMax"/>), which is what makes a
    /// 0..255 slider usable for a value the user wants at 1024.
    /// </summary>
    private Grid BuildSliderWithTextBox(
        string label,
        double value,
        double sliderMin,
        double sliderMax,
        Action<double> apply,
        double step = 1.0,
        string format = "0.##",
        double? textMin = null,
        double? textMax = null)
    {
        double minimum = textMin ?? sliderMin;
        double maximum = textMax ?? sliderMax;

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110, GridUnitType.Pixel) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70, GridUnitType.Pixel) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var slider = new Slider
        {
            Minimum = sliderMin,
            Maximum = sliderMax,
            Value = Math.Clamp(value, sliderMin, sliderMax),
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            SmallChange = step,
            LargeChange = step * 10,
            StepFrequency = step,
        };

        var valueBox = new TextBox
        {
            Text = value.ToString(format),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // U+2212 MINUS SIGN, not a hyphen: it is a glyph in the font's numeric range, so it
        // lines up with the "+" button.
        var minusButton = NumberButton("\u2212", new Thickness(2, 0, 1, 0));
        var plusButton = NumberButton("+", new Thickness(1, 0, 2, 0));

        // The handlers are attached after the initial Value is set, so building the row does
        // not fire any of them.
        slider.ValueChanged += (_, _) =>
        {
            var current = Math.Clamp(slider.Value, sliderMin, sliderMax);
            valueBox.Text = current.ToString(format);
            apply(current);
        };

        void ApplyFromText()
        {
            if (double.TryParse(valueBox.Text, out var parsed))
            {
                parsed = Math.Clamp(parsed, minimum, maximum);
                if (parsed >= sliderMin && parsed <= sliderMax) slider.Value = parsed;

                valueBox.Text = parsed.ToString(format);
                apply(parsed);
            }
            else
            {
                valueBox.Text = slider.Value.ToString(format);
            }
        }

        valueBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ApplyFromText();
                e.Handled = true;
            }
        };
        valueBox.LostFocus += (_, _) => ApplyFromText();

        minusButton.Click += (_, _) =>
        {
            var current = Math.Max(sliderMin, slider.Value - step);
            slider.Value = current;
            valueBox.Text = current.ToString(format);
            apply(current);
        };
        plusButton.Click += (_, _) =>
        {
            var current = Math.Min(sliderMax, slider.Value + step);
            slider.Value = current;
            valueBox.Text = current.ToString(format);
            apply(current);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(minusButton);
        buttons.Children.Add(plusButton);

        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueBox, 2);
        Grid.SetColumn(buttons, 3);
        row.Children.Add(labelText);
        row.Children.Add(slider);
        row.Children.Add(valueBox);
        row.Children.Add(buttons);

        return row;
    }

    private static Button NumberButton(string glyph, Thickness margin) => new()
    {
        Content = new TextBlock
        {
            Text = glyph,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
        Width = 32,
        Height = 32,
        Padding = new Thickness(0),
        Margin = margin,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// Enable or disable everything in a generated row.  <c>IsEnabled</c> lives on Control,
    /// not on UIElement, so the panel holding the buttons has to be walked explicitly.
    /// </summary>
    private static void SetRowEnabled(DependencyObject element, bool enabled)
    {
        switch (element)
        {
            case Control control:
                control.IsEnabled = enabled;
                break;
            case Panel panel:
                foreach (var child in panel.Children) SetRowEnabled(child, enabled);
                break;
        }
    }

    private static string DescribeImage(string path) =>
        string.IsNullOrEmpty(path) ? Strings.Appearance.NoImage : Path.GetFileName(path);

    private static bool IsBackdropSupported()
    {
        try
        {
            return Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()
                || Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
        }
        catch { return false; }
    }

    /// <summary>
    /// Ask for a background image through the Win32 common dialog.  WinUI 3's
    /// FileOpenPicker and the WinForms one both need extra plumbing in an unpackaged app;
    /// <c>GetOpenFileName</c> is the shortest reliable path.
    /// </summary>
    private string? ShowImagePicker()
    {
        try
        {
            return ShowOpenFileDialog(
                WindowNative.GetWindowHandle(this),
                Strings.Appearance.ChooseImage,
                "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*");
        }
        catch (Exception ex)
        {
            AppLog.Log($"Image picker failed: {ex.Message}");
            return null;
        }
    }

    private static string? ShowOpenFileDialog(IntPtr owner, string title, string filter)
    {
        var dialog = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            hwndOwner = owner,
            lpstrTitle = title,
            lpstrFilter = filter.Replace('|', '\0') + "\0\0",
            nFilterIndex = 1,
            lpstrFile = new string('\0', 260),
            nMaxFile = 260,
            Flags = 0x00080000 /* OFN_EXPLORER */ | 0x00001000 /* OFN_FILEMUSTEXIST */,
        };

        return GetOpenFileName(ref dialog) ? dialog.lpstrFile : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME dialog);
}
