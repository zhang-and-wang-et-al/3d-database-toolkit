using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SceneInspector;

/// <summary>A top-level window that holds one popped-out panel (the same control instance that sat in the tab).
/// No owner, so it can live beside or behind the main window or on another monitor; closing it docks the panel back
/// (<see cref="PanelHost"/> handles <see cref="Window.Closed"/>).</summary>
sealed class PanelWindow : Window
{
    public PanelId Id { get; }

    public PanelWindow(PanelId id, string title, FrameworkElement content, MainWindow main)
    {
        Id = id;
        Title = $"{title} – Scene Inspector";
        Icon = main.Icon;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Font.Body");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        MinWidth = 320;
        MinHeight = 240;
        // the main window's shortcuts (Ctrl+Z, F5, F1, Ctrl+1..5 ...) work here too; the handlers stay the main window's
        foreach (InputBinding b in main.InputBindings)
            InputBindings.Add(new InputBinding(b.Command, b.Gesture) { CommandParameter = b.CommandParameter });
        foreach (CommandBinding b in main.CommandBindings) CommandBindings.Add(b);
        Dialogs.DarkTitleBar(this);
        Content = content;
    }
}
