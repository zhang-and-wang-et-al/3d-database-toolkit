using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SceneKit;

namespace SceneInspector;

/// <summary>R3-C11: the Layout row of the replace_cards pass (plane diagram, count line, label) and Cards All…</summary>
public partial class MainWindow
{
    ComboBox? cardLayoutBox;
    CardDiagram? cardDiagram;
    TextBlock? cardCount;
    string cardBase = "custom";   // "custom" or the named layout the rows started from
    bool cardSync;


    static object? ControlValue(FrameworkElement ctl, PassParam p) => ctl switch
    {
        CheckBox cb => cb.IsChecked == true,
        ComboBox combo => combo.SelectedItem as string,
        TextBox tb => TryParam(p, tb.Text, out var v) ? v : null,
        _ => null,
    };

    /// <summary>The Layout row: the layout box, the diagram and the count line. Built once the generated fields exist.</summary>
    FrameworkElement CardLayoutRow()
    {
        cardLayoutBox = new ComboBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetAutomationId(cardLayoutBox, "CardLayoutBox");
        cardLayoutBox.ToolTip = "A named set of cards. Picking one fills in the fields below; changing a field makes it a custom layout.";
        cardDiagram = new CardDiagram { Margin = new Thickness(0, 0, 10, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(cardDiagram, "CardDiagram");
        cardCount = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("TextMuted"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 230 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(cardCount, "CardCountLine");
        var label = new TextBlock { Text = "Layout", Foreground = Ui.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 120 };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(label);
        top.Children.Add(cardLayoutBox);
        var picture = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        picture.Children.Add(cardDiagram);
        picture.Children.Add(cardCount);
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(top);
        stack.Children.Add(picture);
        cardLayoutBox.SelectionChanged += (_, _) =>
        {
            if (cardSync || cardLayoutBox.SelectedItem is not string s) return;
            if (s == "custom") { cardBase = "custom"; RefreshCardLayout(); }
            else if (Array.IndexOf(CardLayout.Names, s) >= 0) ChooseCardLayout(s);
            else RefreshCardLayout();   // the display-only "custom (from X)" item
        };
        return stack;
    }

    /// <summary>Hooks every row field (and the texture size) so an edit re-resolves the layout.</summary>
    void WireCardControls()
    {
        foreach (var k in CardLayout.RowKeys.Append("texture"))
            if (paramControls.TryGetValue("replace_cards." + k, out var c))
                switch (c.control)
                {
                    case CheckBox b: b.Checked += (_, _) => OnCardFieldEdited(); b.Unchecked += (_, _) => OnCardFieldEdited(); break;
                    case ComboBox b: b.SelectionChanged += (_, _) => OnCardFieldEdited(); break;
                    case TextBox b: b.TextChanged += (_, _) => OnCardFieldEdited(); break;
                }
        if (paramControls.TryGetValue("replace_cards.layout", out var lc) && lc.control is ComboBox hidden)
            hidden.SelectionChanged += (_, _) =>
            {   // set from outside (a preset): follow it
                if (cardSync || hidden.SelectedItem is not string s) return;
                if (Array.IndexOf(CardLayout.Names, s) >= 0) ChooseCardLayout(s);
                else { cardBase = "custom"; RefreshCardLayout(); }
            };
        RefreshCardLayout();
    }

    void OnCardFieldEdited()
    {
        if (!cardSync) RefreshCardLayout();
    }

    /// <summary>The layout the generated fields describe now.</summary>
    CardLayout CurrentCardLayout()
    {
        var rows = new Dictionary<string, object>();
        foreach (var k in CardLayout.RowKeys)
            if (paramControls.TryGetValue("replace_cards." + k, out var c) && ControlValue(c.control, c.p) is { } v) rows[k] = v;
        try { return CardLayout.Resolve(cardBase, rows); }
        catch (ArgumentException) { return CardLayout.Resolve("custom", rows); }
    }

    int CardTextureSize() =>
        paramControls.TryGetValue("replace_cards.texture", out var c) && ControlValue(c.control, c.p) is int n && n > 0 ? n : 512;

    void ChooseCardLayout(string name)
    {
        cardSync = true;
        try
        {
            foreach (var (k, v) in CardLayout.Named(name).Rows())
            {
                if (!paramControls.TryGetValue("replace_cards." + k, out var c)) continue;
                switch (c.control)
                {
                    case CheckBox b: b.IsChecked = Convert.ToBoolean(v, CultureInfo.InvariantCulture); break;
                    case ComboBox b: b.SelectedItem = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                    case TextBox b: b.Text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""; break;
                }
            }
            cardBase = name;
        }
        finally { cardSync = false; }
        RefreshCardLayout();
    }

    /// <summary>Re-resolves: the diagram, the count line, the label in the layout box and the hidden generated layout field.</summary>
    void RefreshCardLayout()
    {
        if (cardLayoutBox == null || cardDiagram == null || cardCount == null) return;
        var l = CurrentCardLayout();
        cardDiagram.Layout = l;
        cardCount.Text = l.CountLine(CardTextureSize());
        string label = l.Label;
        bool was = cardSync;
        cardSync = true;
        try
        {
            var items = new List<string> { "custom" };
            if (label.StartsWith("custom (from", StringComparison.Ordinal)) items.Add(label);
            items.AddRange(CardLayout.Names);
            cardLayoutBox.ItemsSource = items;
            cardLayoutBox.SelectedItem = label;
            if (paramControls.TryGetValue("replace_cards.layout", out var c) && c.control is ComboBox hidden)
                hidden.SelectedItem = l.MatchesNamed() ?? "custom";
        }
        finally { cardSync = was; }
    }

    /// <summary>Cards All…: pick a layout, then the confirmation names it.</summary>
    internal void OnSimCardsLayout(object sender, RoutedEventArgs e)
    {
        var pairs = SelectedPairs();
        if (pairs.Count == 0 || Scene == null) return;
        var l = AskCardLayout();
        if (l == null) return;
        int variants = pairs.Select(x => (x.g, x.m.Variant)).Distinct().Count(), atlases = Looks(pairs);
        string kind = $"{l.Label} card";
        if (!ConfirmSimRun("Cards all?",
                $"{Plural(pairs.Count, "object", "objects")}, {Plural(variants, "variant", "variants")} → {Plural(atlases, kind + " atlas", kind + " atlases")}.\n\n" +
                $"Every copy becomes {l.Label} cards. " +
                "Copies that look the same (same shape and textures) share one atlas and card; each copy keeps its place and size. Tilted copies get their own.\n\n" +
                ReplaceNote(pairs.Count, PairTris(pairs), InspectorModel.CardsAllWith(l)),
                "Cards All"))
            return;
        var prms = CollectParams();
        foreach (var (k, v) in l.Rows()) prms["replace_cards." + k] = v;
        prms["replace_cards.layout"] = l.MatchesNamed() ?? "custom";
        prms["replace_cards.share"] = true;
        RunPasses(new List<string> { "replace_cards" }, pairs.Select(x => x.m.Node).ToList(), prms);
    }

    /// <summary>The small popup: the layout list, a diagram and the count line. Null when cancelled.</summary>
    CardLayout? AskCardLayout()
    {
        CardLayout? result = null;
        var w = Dialogs.NewWindow(DialogOwner, "Cards All");
        System.Windows.Automation.AutomationProperties.SetAutomationId(w, "CardsAllPopup");
        var list = new ComboBox { Width = 170, ItemsSource = CardLayout.Names, SelectedItem = "ground", HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetAutomationId(list, "CardsAllLayoutBox");
        var diagram = new CardDiagram { Margin = new Thickness(0, 0, 10, 0) };
        var count = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("TextMuted"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260 };
        int size = CardTextureSize();
        void Show()
        {
            var l = CardLayout.Named((string)list.SelectedItem);
            diagram.Layout = l;
            count.Text = l.CountLine(size);
        }
        list.SelectionChanged += (_, _) => Show();
        Show();
        var ok = new Button { Content = "OK", MinWidth = 88, IsDefault = true, Style = (Style)Application.Current.FindResource("Button.Accent") };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => { result = CardLayout.Named((string)list.SelectedItem); w.DialogResult = true; };
        var picture = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        picture.Children.Add(diagram);
        picture.Children.Add(count);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        body.Children.Add(new TextBlock { Text = "Which cards replace every copy?", Foreground = Ui.Brush("Text"), Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(list);
        body.Children.Add(picture);
        body.Children.Add(buttons);
        w.Content = body;
        w.ShowDialog();
        return result;
    }
}
