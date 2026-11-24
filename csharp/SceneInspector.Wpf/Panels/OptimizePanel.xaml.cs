using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SceneInspector.Panels;

public partial class OptimizePanel : UserControl
{
    internal MainWindow Host = null!;

    public OptimizePanel() => InitializeComponent();

    void OnPreset(object sender, SelectionChangedEventArgs e) => Host?.OnPreset(sender, e);
    void OnSaveProfile(object sender, RoutedEventArgs e) => Host?.OnSaveProfile(sender, e);
    void OnDeleteProfile(object sender, RoutedEventArgs e) => Host?.OnDeleteProfile(sender, e);
    void OnProfileMore(object sender, RoutedEventArgs e) => Host?.OnProfileMore(sender, e);
    void OnScopeChanged(object sender, RoutedEventArgs e) => Host?.OnScopeChanged(sender, e);
    void OnCompanionCommit(object sender, RoutedEventArgs e) => Host?.OnCompanionCommit(sender, e);
    void OnCompanionKey(object sender, KeyEventArgs e) => Host?.OnCompanionKey(sender, e);
}
