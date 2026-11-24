using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Shapes;

namespace SceneInspector;

public partial class MainWindow
{
    // Forwarders: the controls that moved into the panel UserControls keep their old names for the rest of MainWindow.

    // TreeView
    RadioButton LeftTree => TreeView.LeftTree;
    RadioButton LeftMap => TreeView.LeftMap;
    DockPanel TreeTools => TreeView.TreeTools;
    ToggleButton OnlyFindings => TreeView.OnlyFindings;
    CheckBox FoldChains => TreeView.FoldChains;
    TextBox FilterBox => TreeView.FilterBox;
    StackPanel MapTools => TreeView.MapTools;
    ComboBox MapMetricBox => TreeView.MapMetricBox;
    ListView Tree => TreeView.Tree;
    ContextMenu TreeMenu => TreeView.TreeMenu;
    GridViewColumnHeader CountHeader => TreeView.CountHeader;
    DockPanel MapPanel => TreeView.MapPanel;
    TextBlock MapPath => TreeView.MapPath;
    TextBlock MapInfo => TreeView.MapInfo;
    TreemapView Map => TreeView.Map;
    Expander SelectionViewExpander => TreeView.SelectionViewExpander;
    TextBlock SelectionCaption => TreeView.SelectionCaption;
    ToggleButton SelectionScene => TreeView.SelectionScene;
    PreviewPanel Locator => TreeView.Locator;

    // InspectView
    TextBlock DetailsTitle => InspectView.DetailsTitle;
    ScrollViewer UnitStrip => InspectView.UnitStrip;
    ItemsControl UnitCards => InspectView.UnitCards;
    TextBox DetailsBox => InspectView.DetailsBox;
    PreviewPanel Preview => InspectView.Preview;

    // ProblemsView
    RadioButton ProbFindings => ProblemsView.ProbFindings;
    RadioButton ProbHotspots => ProblemsView.ProbHotspots;
    RadioButton ProbSimilar => ProblemsView.ProbSimilar;
    DockPanel FindingsView => ProblemsView.FindingsView;
    TextBlock FindingCount => ProblemsView.FindingCount;
    ComboBox FindingSevBox => ProblemsView.FindingSevBox;
    ComboBox FindingTypeBox => ProblemsView.FindingTypeBox;
    Button FixButton => ProblemsView.FixButton;
    Button FixAllButton => ProblemsView.FixAllButton;
    Button ShowInTreeButton => ProblemsView.ShowInTreeButton;
    TextBox FindingText => ProblemsView.FindingText;
    ListView FindingList => ProblemsView.FindingList;
    Grid HotspotsView => ProblemsView.HotspotsView;
    ListView HotGeo => ProblemsView.HotGeo;
    ContextMenu HotGeoMenu => ProblemsView.HotGeoMenu;
    ListView HotTex => ProblemsView.HotTex;
    DockPanel SimilarView => ProblemsView.SimilarView;
    TextBlock SimSummary => ProblemsView.SimSummary;
    Button SimFindButton => ProblemsView.SimFindButton;
    CheckBox SimName => ProblemsView.SimName;
    CheckBox SimSize => ProblemsView.SimSize;
    CheckBox SimTopology => ProblemsView.SimTopology;
    CheckBox SimTexture => ProblemsView.SimTexture;
    Button SimSelectButton => ProblemsView.SimSelectButton;
    Button SimBillboardButton => ProblemsView.SimBillboardButton;
    Button SimCrossButton => ProblemsView.SimCrossButton;
    Button SimCardsLayoutButton => ProblemsView.SimCardsLayoutButton;
    Button SimProxyButton => ProblemsView.SimProxyButton;
    Button SimInstanceButton => ProblemsView.SimInstanceButton;
    TextBlock SimDetailTitle => ProblemsView.SimDetailTitle;
    TextBlock SimDetailText => ProblemsView.SimDetailText;
    ListView SimMembers => ProblemsView.SimMembers;
    ListView SimList => ProblemsView.SimList;
    ContextMenu SimMenu => ProblemsView.SimMenu;

    // TexturesView
    TextBlock TexSummary => TexturesView.TexSummary;
    ToggleButton TexProblemsOnly => TexturesView.TexProblemsOnly;
    TextBox TexFilterBox => TexturesView.TexFilterBox;
    Button TexResizeHalf => TexturesView.TexResizeHalf;
    Button TexResizeQuarter => TexturesView.TexResizeQuarter;
    Button TexResizeCustom => TexturesView.TexResizeCustom;
    CheckBox TexResizeClasses => TexturesView.TexResizeClasses;
    Button TexResetSize => TexturesView.TexResetSize;
    Button TexReplace => TexturesView.TexReplace;
    Button TexSampling => TexturesView.TexSampling;
    Button TexRemoveUnit => TexturesView.TexRemoveUnit;
    Button TexShowUsers => TexturesView.TexShowUsers;
    Grid TexPreview => TexturesView.TexPreview;
    TextBlock TexDetailTitle => TexturesView.TexDetailTitle;
    TextBlock TexDetailPath => TexturesView.TexDetailPath;
    TextBlock TexDetailInfo => TexturesView.TexDetailInfo;
    TextBlock TexDetailProblems => TexturesView.TexDetailProblems;
    ListView TexList => TexturesView.TexList;
    ContextMenu TexMenu => TexturesView.TexMenu;

    // OptimizeView
    ComboBox PresetBox => OptimizeView.PresetBox;
    TextBlock ProfileHint => OptimizeView.ProfileHint;
    Button DeleteProfileButton => OptimizeView.DeleteProfileButton;
    RadioButton ScopeScene => OptimizeView.ScopeScene;
    RadioButton ScopeSelection => OptimizeView.ScopeSelection;
    TextBlock ScopeLine => OptimizeView.ScopeLine;
    CheckBox OptExternals => OptimizeView.OptExternals;
    /// <summary>The Optimize tab's "Include externals" (the passes and the preview's Try follow it).</summary>
    internal bool IncludeExternals => OptExternals.IsChecked == true;
    TextBox CompanionBox => OptimizeView.CompanionBox;
    TextBlock CompanionHint => OptimizeView.CompanionHint;
    Button RunButton => OptimizeView.RunButton;
    TextBlock ValidationText => OptimizeView.ValidationText;
    StackPanel PassPanel => OptimizeView.PassPanel;

    // ReviewView
    TextBlock ReviewTitle => ReviewView.ReviewTitle;
    TextBlock ReviewSub => ReviewView.ReviewSub;
    WrapPanel ReviewDeltas => ReviewView.ReviewDeltas;
    Expander LogExpander => ReviewView.LogExpander;
    TextBox OptLog => ReviewView.OptLog;
    Expander HistoryExpander => ReviewView.HistoryExpander;
    Button ExportStatesButton => ReviewView.ExportStatesButton;
    TextBlock HistoryCap => ReviewView.HistoryCap;
    ListView HistoryList => ReviewView.HistoryList;
    ListView ChangeList => ReviewView.ChangeList;
}
