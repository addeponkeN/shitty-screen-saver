using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Matriosk;

/// <summary>
/// Tuning panel: generated from <see cref="ControlSchema"/>, writes straight into
/// the live config and asks the window to rebuild whatever the edited key needs.
/// </summary>
public partial class DevPanel : Window
{
    private readonly MainWindow owner;
    private readonly List<Action> refreshers = [];
    private readonly DispatcherTimer statsTimer;
    private readonly DispatcherTimer debounceTimer;

    private Action? pendingApply;
    private bool syncing;
    private bool placed;

    public bool IsDirty { get; private set; }

    public DevPanel(MainWindow owner)
    {
        InitializeComponent();
        this.owner = owner;

        BuildPresets();
        BuildSections();

        statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        statsTimer.Tick += (_, _) => UpdateStats();
        statsTimer.Start();

        debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer.Stop();
            var apply = pendingApply;
            pendingApply = null;
            apply?.Invoke();
        };

        Refresh();
        UpdateStats();

        Closed += (_, _) => statsTimer.Stop();
    }

    private Config LiveConfig => owner.Pipeline.Config;

    public void ShowPanel()
    {
        if (!placed)
        {
            placed = true;
            Left = Math.Max(0, owner.Left + Math.Max(0, owner.ActualWidth) - Width - 40);
            Top = Math.Max(0, owner.Top + 40);
        }

        Show();
        Activate();
        Refresh();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key is Key.OemTilde or Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    public void Refresh()
    {
        foreach (var refresh in refreshers) refresh();
    }

    // ---------------------------------------------------------------- building

    private void BuildPresets()
    {
        foreach (var preset in Presets.All)
        {
            var button = new Button
            {
                Content = preset.Name,
                ToolTip = preset.Note,
                Margin = new Thickness(0, 0, 4, 4),
                Padding = new Thickness(7, 3, 7, 3),
                Background = Brush("#12202A"),
                Foreground = Brush("#9FD8EF"),
                BorderBrush = Brush("#2A4452"),
            };
            var captured = preset;
            button.Click += (_, _) => ApplyPreset(captured);
            PresetPanel.Children.Add(button);
        }
    }

    private void BuildSections()
    {
        foreach (var group in ControlSchema.Groups)
        {
            var content = new StackPanel { Margin = new Thickness(2, 2, 2, 8) };
            var expander = new Expander
            {
                Header = group,
                IsExpanded = group is "Text" or "Rain",
                Foreground = Brush("#EAF7FF"),
                Margin = new Thickness(0, 0, 0, 2),
                Content = content,
            };

            foreach (var spec in ControlSchema.Controls.Where(c => c.Group == group))
            {
                AddControl(content, spec);
                if (spec.Path == "charset") AddCustomCharset(content);
            }

            SectionPanel.Children.Add(expander);
        }
    }

    private void AddControl(StackPanel panel, ControlSpec spec)
    {
        if (spec.Kind == ControlKind.Range)
        {
            if (ControlSchema.IsRanged(LiveConfig, spec.Path))
            {
                var rangeRow = NewRangeRow();
                rangeRow.Children.Add(Label(spec.Label, spec.Hint));
                AddRange(rangeRow, spec);
                panel.Children.Add(rangeRow);
            }
            else
            {
                var sliderRow = NewNumericRow();
                sliderRow.Children.Add(Label(spec.Label, spec.Hint));
                AddSlider(sliderRow, spec);
                panel.Children.Add(sliderRow);
            }
            return;
        }

        if (spec.Kind == ControlKind.Number)
        {
            var numberRow = NewNumericRow();
            numberRow.Children.Add(Label(spec.Label, spec.Hint));
            AddNumber(numberRow, spec);
            panel.Children.Add(numberRow);
            return;
        }

        var row = NewRow();
        row.Children.Add(Label(spec.Label, spec.Hint));

        switch (spec.Kind)
        {
            case ControlKind.Text:
                AddText(row, spec);
                break;
            case ControlKind.Select:
                AddSelect(row, spec);
                break;
            case ControlKind.Color:
                AddColor(row, spec);
                break;
            case ControlKind.Toggle:
                AddToggle(row, spec);
                break;
        }

        panel.Children.Add(row);
    }

    private void AddRange(Grid row, ControlSpec spec)
    {
        var range = Tuning.For(spec.Path);
        var minLabel = BoundsLabel(HorizontalAlignment.Right, new Thickness(0, 0, 4, 0));
        var maxLabel = BoundsLabel(HorizontalAlignment.Left, new Thickness(4, 0, 4, 0));
        var slider = new RangeSlider();

        var lowBox = ValueBox();
        lowBox.Width = 38;
        var highBox = ValueBox();
        highBox.Width = 38;
        var ellipsis = new TextBlock
        {
            Text = "…",
            FontSize = 9,
            Foreground = Brush("#587A8C"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 3, 0),
        };

        Grid.SetColumn(minLabel, 1);
        Grid.SetColumn(slider, 2);
        Grid.SetColumn(maxLabel, 3);
        Grid.SetColumn(lowBox, 4);
        Grid.SetColumn(ellipsis, 5);
        Grid.SetColumn(highBox, 6);
        row.Children.Add(minLabel);
        row.Children.Add(slider);
        row.Children.Add(maxLabel);
        row.Children.Add(lowBox);
        row.Children.Add(ellipsis);
        row.Children.Add(highBox);

        void ApplyBounds(double low, double high)
        {
            var effLow = MathF.Min(range.Min, (float)Math.Min(low, high));
            var effHigh = MathF.Max(range.Max, (float)Math.Max(low, high));
            minLabel.Text = ControlSchema.Format(effLow, range.Step);
            maxLabel.Text = ControlSchema.Format(effHigh, range.Step);
            slider.SetRange(low, high, effLow, effHigh);
        }

        void ShowBoxes(double low, double high)
        {
            if (!lowBox.IsFocused) lowBox.Text = ControlSchema.Format(low, range.Step);
            if (!highBox.IsFocused) highBox.Text = ControlSchema.Format(high, range.Step);
        }

        slider.RangeChanged += (_, _) =>
        {
            if (syncing) return;
            var low = slider.Low;
            var high = slider.High;
            ApplyBounds(low, high);
            ControlSchema.SetRange(LiveConfig, spec.Path, low, high);
            ShowBoxes(low, high);
            MarkChanged(spec);
        };

        void Commit()
        {
            if (!double.TryParse(lowBox.Text, out var low)) low = slider.Low;
            if (!double.TryParse(highBox.Text, out var high)) high = slider.High;

            low = Math.Clamp(Tuning.Snap(spec.Path, (float)low), slider.Minimum, slider.Maximum);
            high = Math.Clamp(Tuning.Snap(spec.Path, (float)high), slider.Minimum, slider.Maximum);
            if (high < low) (low, high) = (high, low);

            ControlSchema.SetRange(LiveConfig, spec.Path, low, high);
            ApplyBounds(low, high);
            ShowBoxes(low, high);
            MarkChanged(spec);
        }

        lowBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        lowBox.LostFocus += (_, _) => Commit();
        highBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        highBox.LostFocus += (_, _) => Commit();

        refreshers.Add(() =>
        {
            syncing = true;
            var (low, high) = ControlSchema.GetRange(LiveConfig, spec.Path);
            ApplyBounds(low, high);
            ShowBoxes(low, high);
            syncing = false;
        });
    }

    private void AddSlider(Grid row, ControlSpec spec)
    {
        var range = Tuning.For(spec.Path);
        var minLabel = BoundsLabel(HorizontalAlignment.Right, new Thickness(0, 0, 4, 0));
        var maxLabel = BoundsLabel(HorizontalAlignment.Left, new Thickness(4, 0, 4, 0));

        var slider = new Slider
        {
            SmallChange = range.Step,
            LargeChange = Math.Max(range.Step, range.Step * 10),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = ValueBox();

        Grid.SetColumn(minLabel, 1);
        Grid.SetColumn(slider, 2);
        Grid.SetColumn(maxLabel, 3);
        Grid.SetColumn(box, 4);
        row.Children.Add(minLabel);
        row.Children.Add(slider);
        row.Children.Add(maxLabel);
        row.Children.Add(box);

        void ApplyBounds(double value)
        {
            var effective = Tuning.Effective(spec.Path, (float)value);
            slider.Minimum = effective.Min;
            slider.Maximum = effective.Max;
            minLabel.Text = ControlSchema.Format(effective.Min, range.Step);
            maxLabel.Text = ControlSchema.Format(effective.Max, range.Step);
        }

        void Show(double value)
        {
            if (!box.IsFocused) box.Text = ControlSchema.Format(value, range.Step);
        }

        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            ApplyBounds(e.NewValue);
            ControlSchema.SetNumber(LiveConfig, spec.Path, e.NewValue);
            Show(e.NewValue);
            MarkChanged(spec);
        };

        void Commit()
        {
            if (!double.TryParse(box.Text, out var parsed))
            {
                Show(slider.Value);
                return;
            }

            parsed = Math.Clamp(parsed, slider.Minimum, slider.Maximum);
            parsed = Tuning.Snap(spec.Path, (float)parsed);
            slider.Value = parsed;
        }

        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.LostFocus += (_, _) => Commit();

        refreshers.Add(() =>
        {
            syncing = true;
            var value = ControlSchema.GetNumber(LiveConfig, spec.Path);
            ApplyBounds(value);
            slider.Value = value;
            Show(value);
            syncing = false;
        });
    }

    private void AddNumber(Grid row, ControlSpec spec)
    {
        var range = Tuning.For(spec.Path);
        var minLabel = BoundsLabel(HorizontalAlignment.Right, new Thickness(0, 0, 4, 0));
        var maxLabel = BoundsLabel(HorizontalAlignment.Left, new Thickness(4, 0, 0, 0));

        var box = ValueBox();
        box.Width = 88;
        box.HorizontalAlignment = HorizontalAlignment.Left;

        Grid.SetColumn(minLabel, 1);
        Grid.SetColumn(box, 2);
        Grid.SetColumn(maxLabel, 3);
        row.Children.Add(minLabel);
        row.Children.Add(box);
        row.Children.Add(maxLabel);

        void ApplyBounds(double value)
        {
            var effective = Tuning.Effective(spec.Path, (float)value);
            minLabel.Text = ControlSchema.Format(effective.Min, range.Step);
            maxLabel.Text = ControlSchema.Format(effective.Max, range.Step);
        }

        void Commit()
        {
            if (!double.TryParse(box.Text, out var parsed)) return;

            var current = ControlSchema.GetNumber(LiveConfig, spec.Path);
            var effective = Tuning.Effective(spec.Path, (float)current);
            parsed = Math.Clamp(parsed, effective.Min, effective.Max);
            parsed = Tuning.Snap(spec.Path, (float)parsed);

            ControlSchema.SetNumber(LiveConfig, spec.Path, parsed);
            box.Text = ControlSchema.Format(parsed, range.Step);
            ApplyBounds(parsed);
            MarkChanged(spec);
        }

        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.LostFocus += (_, _) => Commit();

        refreshers.Add(() =>
        {
            syncing = true;
            var value = ControlSchema.GetNumber(LiveConfig, spec.Path);
            ApplyBounds(value);
            if (!box.IsFocused) box.Text = ControlSchema.Format(value, range.Step);
            syncing = false;
        });
    }

    private void AddText(Grid row, ControlSpec spec)
    {
        var box = ValueBox();
        box.Width = double.NaN;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(box, 1);
        Grid.SetColumnSpan(box, 2);
        row.Children.Add(box);

        box.TextChanged += (_, _) =>
        {
            if (syncing) return;
            pendingApply = () =>
            {
                ControlSchema.SetString(LiveConfig, spec.Path, box.Text);
                MarkChanged(spec);
            };
            debounceTimer.Stop();
            debounceTimer.Start();
        };

        refreshers.Add(() =>
        {
            syncing = true;
            if (!box.IsFocused) box.Text = ControlSchema.GetString(LiveConfig, spec.Path);
            syncing = false;
        });
    }

    private void AddToggle(Grid row, ControlSpec spec)
    {
        var box = new CheckBox
        {
            IsChecked = ControlSchema.GetBool(LiveConfig, spec.Path),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#BFE6F6"),
            ToolTip = spec.Hint,
        };
        Grid.SetColumn(box, 1);
        Grid.SetColumnSpan(box, 2);
        row.Children.Add(box);

        box.Checked += (_, _) => { if (!syncing) Apply(); };
        box.Unchecked += (_, _) => { if (!syncing) Apply(); };

        void Apply()
        {
            ControlSchema.SetBool(LiveConfig, spec.Path, box.IsChecked == true);
            MarkChanged(spec);
        }

        refreshers.Add(() =>
        {
            syncing = true;
            box.IsChecked = ControlSchema.GetBool(LiveConfig, spec.Path);
            syncing = false;
        });
    }

    private void AddSelect(Grid row, ControlSpec spec)    {
        var combo = new ComboBox
        {
            Background = Brush("#12202A"),
            Foreground = Brush("#EAF7FF"),
            BorderBrush = Brush("#2A4452"),
            Margin = new Thickness(0, 0, 4, 0),
            Style = (Style)FindResource("DarkCombo"),
        };

        // The default popup paints its items on a white system background, which made
        // the white text invisible.
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, Brush("#12202A")));
        itemStyle.Setters.Add(new Setter(ForegroundProperty, Brush("#EAF7FF")));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 3, 6, 3)));
        var highlighted = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        highlighted.Setters.Add(new Setter(BackgroundProperty, Brush("#1E3746")));
        highlighted.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
        itemStyle.Triggers.Add(highlighted);
        combo.ItemContainerStyle = itemStyle;

        foreach (var option in spec.Options ?? []) combo.Items.Add(option);
        combo.Items.Add("custom");

        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        row.Children.Add(combo);

        combo.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            if (combo.SelectedItem is not string chosen || chosen == "custom") return;
            ControlSchema.SetString(LiveConfig, spec.Path, chosen);
            MarkChanged(spec);
        };

        refreshers.Add(() =>
        {
            syncing = true;
            var current = ControlSchema.GetString(LiveConfig, spec.Path);
            var known = (spec.Options ?? []).Contains(current) ? current : "custom";
            combo.SelectedItem = known;
            syncing = false;
        });
    }

    private void AddColor(Grid row, ControlSpec spec)
    {
        var box = ValueBox();
        var swatch = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            BorderBrush = Brush("#2A4452"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.White,
        };

        var pick = new Button
        {
            Content = "pick",
            Width = 34,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brush("#12202A"),
            Foreground = Brush("#9FD8EF"),
            BorderBrush = Brush("#2A4452"),
            ToolTip = "pick a colour",
        };
        pick.Click += (_, _) => PickColor(spec);

        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 0);
        Grid.SetColumn(swatch, 1);
        Grid.SetColumn(pick, 2);
        box.Margin = new Thickness(0, 0, 6, 0);
        host.Children.Add(box);
        host.Children.Add(swatch);
        host.Children.Add(pick);

        Grid.SetColumn(host, 1);
        Grid.SetColumnSpan(host, 2);
        row.Children.Add(host);

        void Commit()
        {
            var text = box.Text.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            if (text.Length != 7) return;
            ControlSchema.SetString(LiveConfig, spec.Path, text.ToLowerInvariant());
            MarkChanged(spec);
            Refresh();
        }

        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.LostFocus += (_, _) => Commit();

        refreshers.Add(() =>
        {
            syncing = true;
            var value = ControlSchema.GetString(LiveConfig, spec.Path);
            if (!box.IsFocused) box.Text = value;
            swatch.Background = Brush(value);
            syncing = false;
        });
    }

    private void PickColor(ControlSpec spec)
    {
        var (r, g, b) = Config.ParseColor(ControlSchema.GetString(LiveConfig, spec.Path));
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(r, g, b),
        };

        var owner = new NativeOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;

        var hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}".ToLowerInvariant();
        ControlSchema.SetString(LiveConfig, spec.Path, hex);
        MarkChanged(spec);
        Refresh();
    }

    private sealed class NativeOwner(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }

    /// <summary>Free-text glyph set, replacing the charset preset.</summary>
    private void AddCustomCharset(StackPanel panel)
    {
        var row = NewRow();
        row.Children.Add(Label("glyphs", "literal glyph set; replaces the preset above"));

        var box = ValueBox();
        box.Width = double.NaN;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(box, 1);
        Grid.SetColumnSpan(box, 2);
        row.Children.Add(box);

        box.TextChanged += (_, _) =>
        {
            if (syncing) return;
            pendingApply = () =>
            {
                var literal = box.Text.Trim();
                if (literal.Length == 0) return;
                ControlSchema.SetString(LiveConfig, "charset", literal);
                IsDirty = true;
                owner.SetConfigDirty(true);
                SetStatus("edited");
                owner.RequestChange(ChangeFlags.Glyphs);
            };
            debounceTimer.Stop();
            debounceTimer.Start();
        };

        refreshers.Add(() =>
        {
            syncing = true;
            var value = ControlSchema.GetString(LiveConfig, "charset");
            var isPreset = ControlSchema.CharsetPresets.Contains(value);
            if (!box.IsFocused) box.Text = isPreset ? string.Empty : value;
            box.ToolTip = isPreset ? $"preset: {value} ({Matriosk.Config.ResolveCharset(value).Length} glyphs)" : "literal glyphs";
            syncing = false;
        });

        panel.Children.Add(row);
    }

    // ----------------------------------------------------------------- actions

    private void MarkChanged(ControlSpec spec)
    {
        IsDirty = true;
        owner.SetConfigDirty(true);
        SetStatus("edited");
        owner.RequestChange(ControlSchema.FlagsFor(spec.Path));
    }

    private void ApplyPreset(Preset preset)
    {
        var fresh = new Config();
        preset.Apply(fresh);
        fresh.Normalize();

        LiveConfig.CopyFrom(fresh);
        IsDirty = true;
        owner.SetConfigDirty(true);
        owner.RequestChange(ChangeFlags.Layout);
        Refresh();
        SetStatus($"preset: {preset.Name}");
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var fresh = new Config();
        fresh.Normalize();
        LiveConfig.CopyFrom(fresh);
        IsDirty = true;
        owner.SetConfigDirty(true);
        owner.RequestChange(ChangeFlags.Layout);
        Refresh();
        SetStatus("reset to defaults");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(App.ConfigPath, LiveConfig.ToJson());
            IsDirty = false;
            owner.SetConfigDirty(false);
            owner.SuppressReload();
            SetStatus("saved config.json");
        }
        catch (Exception ex)
        {
            SetStatus($"save failed: {ex.Message}");
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LiveConfig.ToJson());
            SetStatus("copied json");
        }
        catch
        {
            SetStatus("clipboard busy");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateStats()
    {
        if (!IsVisible) return;
        var pipeline = owner.Pipeline;
        var renderer = pipeline.Renderer;
        StatsText.Text =
            $"{pipeline.Config.Fps} fps · {pipeline.Cols}×{pipeline.Rows} · " +
            $"lit {renderer.Visible:N0} · hot {renderer.Hot:N0} · {pipeline.LastFrameMs:0.00} ms";
    }

    // ----------------------------------------------------------------- helpers

    private static Grid NewRow()
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return row;
    }

    private static Grid NewRangeRow()
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });   // label
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // min
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // slider
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // max
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // low box
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // ellipsis
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // high box
        return row;
    }

    private static Grid NewNumericRow()
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });   // label
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // min
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // slider
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // max
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // value box
        return row;
    }

    private static TextBlock BoundsLabel(HorizontalAlignment alignment, Thickness margin) => new()
    {
        FontSize = 9,
        Foreground = Brush("#587A8C"),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = alignment,
        TextAlignment = alignment == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left,
        MinWidth = 20,
        Margin = margin,
    };

    private static TextBlock Label(string text, string? hint)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brush("#7AA2B8"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = string.IsNullOrEmpty(hint) ? null : hint,
        };
        Grid.SetColumn(label, 0);
        return label;
    }

    private static TextBox ValueBox() => new()
    {
        Width = 56,
        Padding = new Thickness(3, 2, 3, 2),
        Background = Brush("#12202A"),
        Foreground = Brush("#EAF7FF"),
        BorderBrush = Brush("#2A4452"),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static Brush Brush(string hex) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
