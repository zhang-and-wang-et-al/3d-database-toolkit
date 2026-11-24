using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SceneInspector.Panels;

public partial class TexturesPanel : UserControl
{
    internal MainWindow Host = null!;

    public TexturesPanel() => InitializeComponent();

    void OnTexFilter(object sender, RoutedEventArgs e) => Host?.OnTexFilter(sender, e);
    void OnTexFilterKey(object sender, KeyEventArgs e) => Host?.OnTexFilterKey(sender, e);
    void OnTexResize(object sender, RoutedEventArgs e) => Host?.OnTexResize(sender, e);
    void OnTexResizeCustom(object sender, RoutedEventArgs e) => Host?.OnTexResizeCustom(sender, e);
    void OnTexResetSize(object sender, RoutedEventArgs e) => Host?.OnTexResetSize(sender, e);
    void OnTexReplace(object sender, RoutedEventArgs e) => Host?.OnTexReplace(sender, e);
    void OnTexSampling(object sender, RoutedEventArgs e) => Host?.OnTexSampling(sender, e);
    void OnTexRemoveUnit(object sender, RoutedEventArgs e) => Host?.OnTexRemoveUnit(sender, e);
    void OnTexShowUsers(object sender, RoutedEventArgs e) => Host?.OnTexShowUsers(sender, e);
    void OnTexSelect(object sender, SelectionChangedEventArgs e) => Host?.OnTexSelect(sender, e);
    void OnTexKey(object sender, KeyEventArgs e) => Host?.OnTexKey(sender, e);
    void OnTexDoubleClick(object sender, MouseButtonEventArgs e) => Host?.OnTexDoubleClick(sender, e);
    void OnTexHeaderClick(object sender, RoutedEventArgs e) => Host?.OnTexHeaderClick(sender, e);
    void OnTexMenuOpening(object sender, ContextMenuEventArgs e) => Host?.OnTexMenuOpening(sender, e);
}
