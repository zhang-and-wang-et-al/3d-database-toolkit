using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SceneKit;

namespace SceneInspector;

/// <summary>R3-C12: profiles in the preset box (after a separator: built-in, then user), the select hint, Save as profile,
/// Delete profile, Open profiles folder.</summary>
public partial class MainWindow
{
    /// <summary>The chosen profile while nothing was edited since; null once a toggle or field changed.</summary>
    Profile? activeProfile;
    /// <summary>The profile the editor state came from (kept after an edit, for the "Custom (from ...)" label).</summary>
    Profile? fromProfile;
    ComboBoxItem? profileLabelItem;
    Separator? profileSeparator;
    List<string> lastProfileWarnings = new();

    /// <summary>The profile the finding note refers to (R3-C11).</summary>
    Profile? ActiveCardsProfile => activeProfile;

    static bool IsPresetProfile(Profile p) => Optimizer.Presets.Keys.Any(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>(Re)builds the entries after the separator: built-in profiles (not Safe / Aggressive / LOD a second time), then user profiles.
    /// Load warnings go to the log. Keeps the current selection.</summary>
    void LoadProfileItems(bool logWarnings)
    {
        var (all, warnings) = Profiles.LoadAll(Profiles.UserDir);
        // warnings are logged only when they changed since the last time (the list is reloaded on every drop-down)
        if (logWarnings && !warnings.SequenceEqual(lastProfileWarnings)) foreach (var w in warnings) Log("profiles: " + w);
        if (logWarnings) lastProfileWarnings = warnings;
        bool was = applyingPreset;
        applyingPreset = true;
        try
        {
            var selected = PresetBox.SelectedItem;
            if (profileSeparator != null)
            {
                int at = PresetBox.Items.IndexOf(profileSeparator);
                while (PresetBox.Items.Count > at) PresetBox.Items.RemoveAt(PresetBox.Items.Count - 1);
            }
            profileSeparator = new Separator();
            PresetBox.Items.Add(profileSeparator);
            var shown = all.Where(p => !IsPresetProfile(p)).ToList();
            foreach (var p in shown.Where(p => p.BuiltIn).Concat(shown.Where(p => !p.BuiltIn)))
                PresetBox.Items.Add(new ComboBoxItem
                {
                    Content = p.Name, Tag = p,
                    ToolTip = string.IsNullOrWhiteSpace(p.Use) ? p.Description : p.Use + (p.Description.Length > 0 ? "\n" + p.Description : ""),
                });
            if (selected != null && PresetBox.Items.Contains(selected)) PresetBox.SelectedItem = selected;
            else if (selected is ComboBoxItem { Tag: Profile old } && PresetBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Profile q && q.Name == old.Name) is { } again)
                PresetBox.SelectedItem = again;
        }
        finally { applyingPreset = was; }
    }

    /// <summary>Writes the profile into the pass boxes, the generated fields and the simplify editor.</summary>
    void ApplyProfile(Profile p)
    {
        var passes = new HashSet<string>();
        var prms = new Dictionary<string, object>();
        var warnings = new List<string>();
        Profiles.Apply(p, passes, prms, warnings);
        foreach (var w in warnings) Log("profiles: " + w);
        foreach (var (k, cb) in passBoxes) cb.IsChecked = passes.Contains(k);
        bool wasSync = cardSync;
        cardSync = true;   // the rows are written as they are; the Layout row must not overwrite them with the named layout's rows
        try
        {
            foreach (var (key, (ctl, pp)) in paramControls)
            {
                if (!prms.TryGetValue(key, out var v)) continue;
                switch (ctl)
                {
                    case CheckBox b: b.IsChecked = Convert.ToBoolean(v, CultureInfo.InvariantCulture); break;
                    case ComboBox b: b.SelectedItem = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                    case TextBox b: b.Text = DefaultText(v); break;
                }
            }
            if (passes.Contains("replace_cards"))
            {
                string layout = prms.TryGetValue("replace_cards.layout", out var l) ? Convert.ToString(l, CultureInfo.InvariantCulture) ?? "custom" : "custom";
                cardBase = Array.IndexOf(CardLayout.Names, layout) >= 0 ? layout : "custom";
            }
        }
        finally { cardSync = wasSync; }
        if (passes.Contains("simplify")) SetSimplify(SimplifyParams.FromPassParams(prms), null);
        RefreshCardLayout();
        Validate();
    }

    void ShowProfileHint()
    {
        string hint = activeProfile?.SelectHint ?? "";
        ProfileHint.Text = hint;
        ProfileHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteProfileButton.IsEnabled = activeProfile is { BuiltIn: false };
    }

    /// <summary>Removes the display-only "Custom (from ...)" entry.</summary>
    void DropProfileLabel()
    {
        if (profileLabelItem != null && PresetBox.Items.Contains(profileLabelItem))
        {
            bool was = applyingPreset;
            applyingPreset = true;
            try { PresetBox.Items.Remove(profileLabelItem); } finally { applyingPreset = was; }
        }
    }

    /// <summary>A toggle or field changed after a profile was chosen: the profile no longer describes the editor.</summary>
    void OnProfileFieldEdited()
    {
        if (applyingPreset || activeProfile == null) return;
        fromProfile = activeProfile;
        activeProfile = null;
        ShowProfileHint();
        ShowProfileLabel();
    }

    /// <summary>Selects the display-only "Custom (from ...)" entry for the profile the state came from.</summary>
    void ShowProfileLabel()
    {
        bool was = applyingPreset;
        applyingPreset = true;
        try
        {
            string text = InspectorModel.ProfileBoxLabel(activeProfile, fromProfile);
            profileLabelItem ??= new ComboBoxItem { Tag = "profile-label" };
            profileLabelItem.Content = text;
            if (!PresetBox.Items.Contains(profileLabelItem))
                PresetBox.Items.Insert(profileSeparator != null ? PresetBox.Items.IndexOf(profileSeparator) : PresetBox.Items.Count, profileLabelItem);
            PresetBox.SelectedItem = profileLabelItem;
        }
        finally { applyingPreset = was; }
    }

    /// <summary>Selects a profile's item without applying it again.</summary>
    void SelectProfileItem(Profile p)
    {
        bool was = applyingPreset;
        applyingPreset = true;
        try { PresetBox.SelectedItem = PresetBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Profile q && q.Name == p.Name); }
        finally { applyingPreset = was; }
    }

    // ---- the buttons under the box

    internal void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var passes = CheckedPasses();
        if (passes.Count == 0) { Dialogs.Info(DialogOwner, "Save as profile", "Tick at least one pass first: a profile stores the ticked passes with all their settings."); return; }
        string? name = Dialogs.AskName(DialogOwner, "Save as profile", "Name of the profile. It stores every setting of the ticked passes.", (activeProfile ?? fromProfile)?.Name ?? "");
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0) { Dialogs.Warn(DialogOwner, "Profile not saved", "Enter a name for the profile."); return; }
        if (InspectorModel.IsReservedProfileName(name)) { Dialogs.Warn(DialogOwner, "Profile not saved", "Built-in profiles are read-only; save as a new name."); return; }
        var (all, _) = Profiles.LoadAll(Profiles.UserDir);
        var existing = all.FirstOrDefault(p => !p.BuiltIn && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !Dialogs.Confirm(DialogOwner, "Replace profile?", $"A profile named \"{existing.Name}\" exists. Replace it? Its version goes up to {existing.Version + 1}.", "Replace")) return;
        try
        {
            var p = Profiles.FromEditor(name, passes, CollectParams());
            if (existing != null) { p.Version = existing.Version + 1; p.Name = existing.Name; }
            string path = Profiles.Save(p);
            Log($"profile saved: {p.Tag} -> {path}");
            LoadProfileItems(false);
            activeProfile = PresetBox.Items.OfType<ComboBoxItem>().Select(i => i.Tag).OfType<Profile>().FirstOrDefault(q => q.Name == p.Name) ?? p;
            fromProfile = null;
            DropProfileLabel();
            SelectProfileItem(activeProfile);
            ShowProfileHint();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            Dialogs.Warn(DialogOwner, "Profile not saved", InspectorModel.Sentence(ex.Message));
        }
    }

    internal void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (activeProfile is not { BuiltIn: false } p) return;
        if (!Dialogs.Confirm(DialogOwner, "Delete profile?", $"Delete the profile \"{p.Name}\"? Its file is removed. History entries that name it keep their settings.", "Delete")) return;
        try { Profiles.Delete(p); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            Dialogs.Warn(DialogOwner, "Profile not deleted", ex.Message);
            return;
        }
        Log($"profile deleted: {p.Tag}");
        activeProfile = null;
        fromProfile = null;
        DropProfileLabel();
        LoadProfileItems(false);
        ShowProfileHint();
        SyncPresetBox();
    }

    internal void OnProfileMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuEntry("Open profiles folder", "", (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Profiles.UserDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Profiles.UserDir, UseShellExecute = true });
            }
            catch (Exception ex) { Dialogs.Warn(DialogOwner, "Folder not opened", ex.Message); }
        }, true));
        menu.IsOpen = true;
    }
}
