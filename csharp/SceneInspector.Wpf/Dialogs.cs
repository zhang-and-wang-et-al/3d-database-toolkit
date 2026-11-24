using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SceneKit;

namespace SceneInspector;

/// <summary>Small themed dialogs with named buttons (instead of MessageBox Yes / No / Cancel), an error dialog with
/// a details expander and a list dialog (load warnings).</summary>
public static class Dialogs
{
    public sealed record Choice(string Key, string Label, bool Accent = false, bool Cancel = false);

    /// <summary>Shows the message with one button per choice; returns the key of the clicked one (null: closed / Esc).</summary>
    public static string? Choose(Window? owner, string title, string message, string icon, string iconBrush, params Choice[] choices)
    {
        string? result = null;
        var w = NewWindow(owner, title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var c in choices)
        {
            var b = new Button { Content = c.Label, Margin = new Thickness(8, 0, 0, 0), MinWidth = 88, IsCancel = c.Cancel, IsDefault = c.Accent };
            if (c.Accent) b.Style = (Style)Application.Current.FindResource("Button.Accent");
            b.Click += (_, _) => { result = c.Key; w.DialogResult = true; };
            buttons.Children.Add(b);
        }
        w.Content = Body(title, message, icon, iconBrush, null, buttons);
        w.ShowDialog();
        return result;
    }

    public static bool Confirm(Window? owner, string title, string message, string yes, string no = "Cancel") =>
        Choose(owner, title, message, "", "Accent", new Choice("yes", yes, Accent: true), new Choice("no", no, Cancel: true)) == "yes";

    public static void Info(Window? owner, string title, string message) =>
        Choose(owner, title, message, "", "Accent", new Choice("ok", "OK", Accent: true, Cancel: true));

    public static void Warn(Window? owner, string title, string message) =>
        Choose(owner, title, message, "", "Sev.Med", new Choice("ok", "OK", Accent: true, Cancel: true));

    /// <summary>Asks for one line of text (a name); Enter / OK return it trimmed and non-empty, null when cancelled.</summary>
    public static string? AskName(Window? owner, string title, string message, string initial)
    {
        string? result = null;
        var w = NewWindow(owner, title);
        var box = new TextBox { Text = initial, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Save", MinWidth = 88, IsDefault = true, Style = (Style)Application.Current.FindResource("Button.Accent"), IsEnabled = initial.Trim().Length > 0 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text.Trim().Length > 0;
        ok.Click += (_, _) => { result = box.Text.Trim(); w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        w.Content = Body(title, message, "", "Accent", box, buttons);
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    /// <summary>Asks for a width x height (whole numbers 1 .. the current size); "keep aspect" couples the two boxes.
    /// Null when cancelled.</summary>
    public static (int w, int h)? AskSize(Window? owner, string title, string message, int w0, int h0)
    {
        (int, int)? result = null;
        var w = NewWindow(owner, title);
        var wBox = new TextBox { Text = w0.ToString(), Width = 80 };
        var hBox = new TextBox { Text = h0.ToString(), Width = 80 };
        var keep = new CheckBox { Content = "Keep aspect ratio", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        var error = new TextBlock { Foreground = Ui.Brush("Sev.High"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Width", Foreground = Ui.Brush("TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(wBox);
        row.Children.Add(new TextBlock { Text = "Height", Foreground = Ui.Brush("TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
        row.Children.Add(hBox);
        row.Children.Add(new TextBlock { Text = "px", Foreground = Ui.Brush("TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        var extra = new StackPanel();
        extra.Children.Add(row);
        extra.Children.Add(keep);
        extra.Children.Add(error);

        var ok = new Button { Content = "Resize", MinWidth = 88, IsDefault = true, Style = (Style)Application.Current.FindResource("Button.Accent") };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        bool syncing = false;
        bool Read(TextBox b, int max, out int v) => int.TryParse(b.Text.Trim(), out v) && v >= 1 && v <= max;
        void Check()
        {
            bool okW = Read(wBox, w0, out _), okH = Read(hBox, h0, out _);
            Ui.SetInvalid(wBox, !okW);
            Ui.SetInvalid(hBox, !okH);
            error.Text = okW && okH ? "" : $"Enter whole numbers from 1 to {w0} x {h0} (textures are only made smaller).";
            error.Visibility = okW && okH ? Visibility.Collapsed : Visibility.Visible;
            ok.IsEnabled = okW && okH;
        }
        void Couple(TextBox from, TextBox to, int fromMax, int toMax)
        {
            if (syncing || keep.IsChecked != true || !int.TryParse(from.Text.Trim(), out var v) || fromMax == 0) { Check(); return; }
            syncing = true;
            to.Text = Math.Max(1, (int)Math.Round(v * (double)toMax / fromMax)).ToString();
            syncing = false;
            Check();
        }
        wBox.TextChanged += (_, _) => Couple(wBox, hBox, w0, h0);
        hBox.TextChanged += (_, _) => Couple(hBox, wBox, h0, w0);
        ok.Click += (_, _) =>
        {
            if (!Read(wBox, w0, out var rw) || !Read(hBox, h0, out var rh)) return;
            result = (rw, rh);
            w.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        w.Content = Body(title, message, "", "Accent", extra, buttons);
        w.Loaded += (_, _) => { wBox.Focus(); wBox.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    /// <summary>Save Copy to Folder: the target folder (with Browse…), the master file name and *Include unchanged files*.
    /// The folder must lie outside <paramref name="databaseFolder"/> (the same rule as <see cref="SaveCopy.ToFolder"/>);
    /// an existing non-empty folder only gets a note. Null when cancelled.</summary>
    public static (string folder, string masterName, bool includeUnchanged)? AskSaveCopy(Window? owner, string folder, string masterName,
        bool includeUnchanged, string databaseFolder)
    {
        (string, string, bool)? result = null;
        const string title = "Save Copy to Folder";
        var w = NewWindow(owner, title);
        TextBlock Label(string text, double top) => new() { Text = text, Foreground = Ui.Brush("TextMuted"), Margin = new Thickness(0, top, 0, 4) };
        var folderBox = new TextBox { Text = folder, Width = 420 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(folderBox, "SaveCopyFolder");
        System.Windows.Automation.AutomationProperties.SetName(folderBox, "Folder");
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), MinWidth = 88 };
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
        folderRow.Children.Add(folderBox);
        folderRow.Children.Add(browse);
        var notEmpty = new TextBlock
        {
            Text = "Folder is not empty: files in it are overwritten, other files stay.", Foreground = Ui.Brush("TextMuted"),
            FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
        };
        var nameBox = new TextBox { Text = masterName, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetAutomationId(nameBox, "SaveCopyMaster");
        System.Windows.Automation.AutomationProperties.SetName(nameBox, "Master file name");
        var include = new CheckBox { Content = "Include unchanged files", IsChecked = includeUnchanged, Margin = new Thickness(0, 14, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(include, "SaveCopyIncludeUnchanged");
        var includeHint = new TextBlock
        {
            Text = "Off: only changed files and the files that reference them; copy them over a copy of the original database.",
            Foreground = Ui.Brush("TextMuted"), FontSize = 12, Margin = new Thickness(22, 2, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 500,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var error = new TextBlock { Foreground = Ui.Brush("Sev.High"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        var extra = new StackPanel();
        extra.Children.Add(Label("Folder", 14));
        extra.Children.Add(folderRow);
        extra.Children.Add(notEmpty);
        extra.Children.Add(Label("Master file name", 12));
        extra.Children.Add(nameBox);
        extra.Children.Add(include);
        extra.Children.Add(includeHint);
        extra.Children.Add(error);

        var ok = new Button { Content = "Save Copy", MinWidth = 88, IsDefault = true, Style = (Style)Application.Current.FindResource("Button.Accent") };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };

        static string Dir(string p) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p));
        string? FolderProblem(string text)
        {
            if (text.Trim().Length == 0) return "Enter the folder to write the copy to.";
            string f;
            try
            {
                if (!System.IO.Path.IsPathFullyQualified(text.Trim())) return "Enter a full folder path (for example D:\\data\\tiles_optimized).";
                f = Dir(text.Trim());
            }
            catch (Exception) { return "This is not a valid folder path."; }
            string root = Dir(databaseFolder);
            if (string.Equals(f, root, StringComparison.OrdinalIgnoreCase)) return "This is the opened database folder. Choose a folder outside it.";
            if (f.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "This folder is inside the opened database folder. Choose a folder outside it.";
            if (root.StartsWith(System.IO.Path.EndsInDirectorySeparator(f) ? f : f + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "This folder contains the opened database folder. Choose a folder outside it.";
            if (System.IO.File.Exists(f)) return "A file with this name exists. Choose a folder.";
            return null;
        }
        string? NameProblem(string text)
        {
            string n = text.Trim();
            if (n.Length == 0) return "Enter the file name of the master file.";
            if (n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return "The master file name contains characters a file name cannot have.";
            if (System.IO.Path.GetExtension(n).Length == 0) return "Give the master file name an extension (for example .flt).";
            return null;
        }
        void Check()
        {
            string? fp = FolderProblem(folderBox.Text), np = NameProblem(nameBox.Text);
            Ui.SetInvalid(folderBox, fp != null);
            Ui.SetInvalid(nameBox, np != null);
            error.Text = fp ?? np ?? "";
            error.Visibility = fp != null || np != null ? Visibility.Visible : Visibility.Collapsed;
            bool nonEmpty = false;
            if (fp == null)
                try { nonEmpty = System.IO.Directory.Exists(folderBox.Text.Trim()) && System.IO.Directory.EnumerateFileSystemEntries(folderBox.Text.Trim()).Any(); }
                catch (Exception) { /* unreadable: the save reports it */ }
            notEmpty.Visibility = nonEmpty ? Visibility.Visible : Visibility.Collapsed;
            ok.IsEnabled = fp == null && np == null;
        }
        folderBox.TextChanged += (_, _) => Check();
        nameBox.TextChanged += (_, _) => Check();
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for the copy" };
            string cur = folderBox.Text.Trim();
            try
            {
                if (cur.Length > 0 && System.IO.Directory.Exists(cur)) dlg.InitialDirectory = cur;
                else if (cur.Length > 0 && System.IO.Path.GetDirectoryName(cur) is { } parent && System.IO.Directory.Exists(parent)) dlg.InitialDirectory = parent;
            }
            catch (Exception) { /* not a path: start where Windows likes */ }
            if (dlg.ShowDialog(w) == true) folderBox.Text = dlg.FolderName;
        };
        ok.Click += (_, _) =>
        {
            if (FolderProblem(folderBox.Text) != null || NameProblem(nameBox.Text) != null) { Check(); return; }
            result = (Dir(folderBox.Text.Trim()), nameBox.Text.Trim(), include.IsChecked == true);
            w.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        w.Content = Body(title, "Writes a complete copy of the database: the opened folder is not changed.", "\uE792", "Accent", extra, buttons);
        Check();
        w.Loaded += (_, _) => { folderBox.Focus(); folderBox.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    /// <summary>Texture sampling (TX6): wrap S / T, min / mag filter, max anisotropy. Null in and out = "default" (not in the
    /// file; OSG uses REPEAT, LINEAR_MIPMAP_LINEAR, LINEAR, 1). Returns null when cancelled.</summary>
    public static (string? wrapS, string? wrapT, string? min, string? mag, double? aniso)? AskSampling(Window? owner, string title, string message,
        string? wrapS, string? wrapT, string? min, string? mag, double? aniso)
    {
        (string?, string?, string?, string?, double?)? result = null;
        var w = NewWindow(owner, title);
        ComboBox Combo(string[] values, string? current, string dflt)
        {
            var c = new ComboBox { Width = 220 };
            c.Items.Add(new ComboBoxItem { Content = $"Default ({dflt})", Tag = null });
            foreach (var v in values) c.Items.Add(new ComboBoxItem { Content = v, Tag = v });
            if (current != null && !values.Contains(current)) c.Items.Add(new ComboBoxItem { Content = current, Tag = current });   // unknown value from the file
            c.SelectedIndex = current == null ? 0 : c.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string?)i.Tag == current);
            return c;
        }
        var cs = Combo(Texture.WrapModes, wrapS, "REPEAT");
        var ct = Combo(Texture.WrapModes, wrapT, "REPEAT");
        var cmin = Combo(Texture.MinFilters, min, "LINEAR_MIPMAP_LINEAR");
        var cmag = Combo(Texture.MagFilters, mag, "LINEAR");
        var aBox = new TextBox { Text = aniso?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "", Width = 80 };
        aBox.SetValue(Ui.PlaceholderProperty, "1");
        var error = new TextBlock { Foreground = Ui.Brush("Sev.High"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        (string label, FrameworkElement ctl, string tip)[] rows =
        {
            ("Wrap S (u)", cs, "What the texture does outside 0..1 across: REPEAT tiles, CLAMP_TO_EDGE stretches the edge texels, MIRROR tiles mirrored"),
            ("Wrap T (v)", ct, "The same along v"),
            ("Minification", cmin, "Filter when the texture is drawn smaller than its size (the *_MIPMAP_* ones use mipmaps)"),
            ("Magnification", cmag, "Filter when the texture is drawn larger than its size"),
            ("Max anisotropy", aBox, "1 = off; 4 to 16 keep textures sharp at grazing angles (empty = default 1)"),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = rows[i].label, Foreground = Ui.Brush("TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 8) };
            rows[i].ctl.Margin = new Thickness(0, 0, 0, 8);
            rows[i].ctl.HorizontalAlignment = HorizontalAlignment.Left;
            rows[i].ctl.ToolTip = rows[i].tip;
            Grid.SetRow(l, i); Grid.SetRow(rows[i].ctl, i); Grid.SetColumn(rows[i].ctl, 1);
            grid.Children.Add(l);
            grid.Children.Add(rows[i].ctl);
        }
        var extra = new StackPanel();
        extra.Children.Add(grid);
        extra.Children.Add(error);

        var ok = new Button { Content = "Apply", MinWidth = 88, IsDefault = true, Style = (Style)Application.Current.FindResource("Button.Accent") };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        bool ReadAniso(out double? v)
        {
            v = null;
            var s = aBox.Text.Trim();
            if (s.Length == 0) return true;
            if (!double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                || !double.IsFinite(d) || d < 1 || d > 64) return false;
            v = d;
            return true;
        }
        void Check()
        {
            bool good = ReadAniso(out _);
            Ui.SetInvalid(aBox, !good);
            error.Text = good ? "" : "Enter a number from 1 to 64, or leave it empty for the default.";
            error.Visibility = good ? Visibility.Collapsed : Visibility.Visible;
            ok.IsEnabled = good;
        }
        aBox.TextChanged += (_, _) => Check();
        static string? Tag(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string;
        ok.Click += (_, _) =>
        {
            if (!ReadAniso(out var a)) return;
            result = (Tag(cs), Tag(ct), Tag(cmin), Tag(cmag), a);
            w.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        w.Content = Body(title, message, "", "Accent", extra, buttons);
        w.Loaded += (_, _) => cs.Focus();
        w.ShowDialog();
        return result;
    }

    /// <summary>Error with the message up front and the technical details (stack trace) folded away.</summary>
    public static void Error(Window? owner, string title, Exception exc)
    {
        var w = NewWindow(owner, title);
        var details = new TextBox
        {
            Text = exc.ToString(), Style = (Style)Application.Current.FindResource("Mono"), Height = 220, Width = 560,
            Background = Ui.Brush("SurfaceAlt"),
        };
        var copy = new Button { Content = "Copy details", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        copy.SetValue(Ui.IconProperty, "");
        copy.Click += (_, _) => { try { Clipboard.SetText(exc.ToString()); } catch { /* clipboard busy */ } };
        var panel = new StackPanel();
        panel.Children.Add(details);
        panel.Children.Add(copy);
        var exp = new Expander { Header = "Technical details", Content = panel, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 88, IsDefault = true, IsCancel = true, Style = (Style)Application.Current.FindResource("Button.Accent") };
        ok.Click += (_, _) => w.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(ok);
        w.Content = Body(title, exc.Message, "", "Sev.High", exp, buttons);
        w.ShowDialog();
    }

    /// <summary>A scrollable, selectable list of lines (e.g. load warnings).</summary>
    public static void List(Window? owner, string title, string intro, IEnumerable<string> lines)
    {
        var w = NewWindow(owner, title);
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, lines), Style = (Style)Application.Current.FindResource("Mono"), Height = 300, Width = 620,
            TextWrapping = TextWrapping.Wrap, Background = Ui.Brush("SurfaceAlt"), Margin = new Thickness(0, 12, 0, 0),
        };
        var copy = new Button { Content = "Copy", Margin = new Thickness(8, 0, 0, 0), MinWidth = 88 };
        copy.Click += (_, _) => { try { Clipboard.SetText(box.Text); } catch { /* clipboard busy */ } };
        var ok = new Button { Content = "Close", MinWidth = 88, IsDefault = true, IsCancel = true, Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Button.Accent") };
        ok.Click += (_, _) => w.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(copy);
        buttons.Children.Add(ok);
        w.Content = Body(title, intro, "", "Sev.Med", box, buttons);
        w.ShowDialog();
    }

    internal static Window NewWindow(Window? owner, string title)
    {
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Ui.Brush("Surface"), Foreground = Ui.Brush("Text"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
            UseLayoutRounding = true,
        };
        if (owner is { IsLoaded: true }) w.Owner = owner;
        DarkTitleBar(w);
        return w;
    }

    static FrameworkElement Body(string title, string message, string icon, string iconBrush, FrameworkElement? extra, FrameworkElement buttons)
    {
        var grid = new Grid { Margin = new Thickness(22, 20, 22, 18), MaxWidth = 680 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = new TextBlock
        {
            Text = icon, FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"), FontSize = 24,
            Foreground = Ui.Brush(iconBrush), Margin = new Thickness(0, 2, 16, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        grid.Children.Add(glyph);
        var stack = new StackPanel { MinWidth = 340 };
        Grid.SetColumn(stack, 1);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = Ui.Brush("Text"), LineHeight = 19 });
        if (extra != null) stack.Children.Add(extra);
        stack.Children.Add(buttons);
        grid.Children.Add(stack);
        return grid;
    }

    // ---------------------------------------------------------------- Windows 10 / 11 dark title bar

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Asks Windows for a dark title bar (no effect on older systems).</summary>
    public static void DarkTitleBar(Window w)
    {
        void Apply()
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
            }
            catch (Exception) { /* not available */ }
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();
    }
}
