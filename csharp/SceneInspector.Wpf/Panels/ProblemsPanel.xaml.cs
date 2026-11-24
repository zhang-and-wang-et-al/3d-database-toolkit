using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SceneInspector.Panels;

public partial class ProblemsPanel : UserControl
{
    internal MainWindow Host = null!;

    public ProblemsPanel() => InitializeComponent();

    void OnProblemsMode(object sender, RoutedEventArgs e) => Host?.OnProblemsMode(sender, e);
    void OnFindingFilter(object sender, SelectionChangedEventArgs e) => Host?.OnFindingFilter(sender, e);
    void OnFixFinding(object sender, RoutedEventArgs e) => Host?.OnFixFinding(sender, e);
    void OnFixAllSame(object sender, RoutedEventArgs e) => Host?.OnFixAllSame(sender, e);
    void OnShowFindingNodes(object sender, RoutedEventArgs e) => Host?.OnShowFindingNodes(sender, e);
    void OnFindingSelect(object sender, SelectionChangedEventArgs e) => Host?.OnFindingSelect(sender, e);
    void OnFindingKey(object sender, KeyEventArgs e) => Host?.OnFindingKey(sender, e);
    void OnHotGoto(object sender, MouseButtonEventArgs e) => Host?.OnHotGoto(sender, e);
    void OnHotKey(object sender, KeyEventArgs e) => Host?.OnHotKey(sender, e);
    void OnHotGeoMenuOpening(object sender, ContextMenuEventArgs e) => Host?.OnHotGeoMenuOpening(sender, e);
    void OnSimFind(object sender, RoutedEventArgs e) => Host?.OnSimFind(sender, e);
    void OnSimCriteria(object sender, RoutedEventArgs e) => Host?.OnSimCriteria(sender, e);
    void OnSimSelectAll(object sender, RoutedEventArgs e) => Host?.OnSimSelectAll(sender, e);
    void OnSimCards(object sender, RoutedEventArgs e) => Host?.OnSimCards(sender, e);
    void OnSimCardsLayout(object sender, RoutedEventArgs e) => Host?.OnSimCardsLayout(sender, e);
    void OnSimProxy(object sender, RoutedEventArgs e) => Host?.OnSimProxy(sender, e);
    void OnSimInstances(object sender, RoutedEventArgs e) => Host?.OnSimInstances(sender, e);
    void OnSimMemberGoto(object sender, MouseButtonEventArgs e) => Host?.OnSimMemberGoto(sender, e);
    void OnSimMemberKey(object sender, KeyEventArgs e) => Host?.OnSimMemberKey(sender, e);
    void OnSimSelect(object sender, SelectionChangedEventArgs e) => Host?.OnSimSelect(sender, e);
    void OnSimKey(object sender, KeyEventArgs e) => Host?.OnSimKey(sender, e);
    void OnSimHeaderClick(object sender, RoutedEventArgs e) => Host?.OnSimHeaderClick(sender, e);
    void OnSimMenuOpening(object sender, ContextMenuEventArgs e) => Host?.OnSimMenuOpening(sender, e);
}
