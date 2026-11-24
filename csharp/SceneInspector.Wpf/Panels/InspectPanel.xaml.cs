using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SceneInspector.Panels;

public partial class InspectPanel : UserControl
{
    internal MainWindow Host = null!;

    public InspectPanel() => InitializeComponent();

    void OnCopyDetails(object sender, RoutedEventArgs e) => Host?.OnCopyDetails(sender, e);
    void OnUnitCardClick(object sender, MouseButtonEventArgs e) => Host?.OnUnitCardClick(sender, e);
}
