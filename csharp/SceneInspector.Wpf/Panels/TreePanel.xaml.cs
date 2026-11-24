using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SceneInspector.Panels;

public partial class TreePanel : UserControl
{
    internal MainWindow Host = null!;

    public TreePanel() => InitializeComponent();

    void OnLeftMode(object sender, RoutedEventArgs e) => Host?.OnLeftMode(sender, e);
    void OnExpandBiggest(object sender, RoutedEventArgs e) => Host?.OnExpandBiggest(sender, e);
    void OnFilterChanged(object sender, RoutedEventArgs e) => Host?.OnFilterChanged(sender, e);
    void OnFoldChains(object sender, RoutedEventArgs e) => Host?.OnFoldChains(sender, e);
    void OnFilterText(object sender, TextChangedEventArgs e) => Host?.OnFilterText(sender, e);
    void OnFilterKey(object sender, KeyEventArgs e) => Host?.OnFilterKey(sender, e);
    void OnMapMetric(object sender, SelectionChangedEventArgs e) => Host?.OnMapMetric(sender, e);
    void OnMapUp(object sender, RoutedEventArgs e) => Host?.OnMapUp(sender, e);
    void OnMapTop(object sender, RoutedEventArgs e) => Host?.OnMapTop(sender, e);
    void OnTreeSelect(object sender, SelectionChangedEventArgs e) => Host?.OnTreeSelect(sender, e);
    void OnTreeKey(object sender, KeyEventArgs e) => Host?.OnTreeKey(sender, e);
    void OnHeaderClick(object sender, RoutedEventArgs e) => Host?.OnHeaderClick(sender, e);
    void OnTreeMenuOpening(object sender, ContextMenuEventArgs e) => Host?.OnTreeMenuOpening(sender, e);
    void OnTreeDoubleClick(object sender, MouseButtonEventArgs e) => Host?.OnTreeDoubleClick(sender, e);
    void OnGlyphClick(object sender, MouseButtonEventArgs e) => Host?.OnGlyphClick(sender, e);
    void OnCountColumn(object sender, RoutedEventArgs e) => Host?.OnCountColumn(sender, e);
    void OnPopOut(object sender, RoutedEventArgs e) => Host?.OnPopOutButton(sender, e);
    void OnSelectionViewToggled(object sender, RoutedEventArgs e) => Host?.OnSelectionViewToggled(sender, e);
    void OnSelectionScene(object sender, RoutedEventArgs e) => Host?.OnSelectionScene(sender, e);
}
