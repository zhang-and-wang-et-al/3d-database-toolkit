using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SceneInspector.Panels;

public partial class ReviewPanel : UserControl
{
    internal MainWindow Host = null!;

    public ReviewPanel() => InitializeComponent();

    void OnShowChange(object sender, RoutedEventArgs e) => Host?.OnShowChange(sender, e);
    void OnCheckSelectedChanges(object sender, RoutedEventArgs e) => Host?.OnCheckSelectedChanges(sender, e);
    void OnExportStates(object sender, RoutedEventArgs e) => Host?.OnExportStates(sender, e);
    void OnHistoryKey(object sender, KeyEventArgs e) => Host?.OnHistoryKey(sender, e);
    void OnHistoryClick(object sender, MouseButtonEventArgs e) => Host?.OnHistoryClick(sender, e);
}
