using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DotLight.Knobs;

/// <summary>Snippets knob: saved prompts; click one to paste it into the app you were using.</summary>
public partial class SnippetsKnob : UserControl
{
    readonly DispatcherTimer saveSoon = new() { Interval = TimeSpan.FromMilliseconds(500) };
    Snippet? open;
    bool loading;

    /// <summary>A snippet was picked: paste this text into the previous app.</summary>
    public event Action<string>? PasteRequested;

    public SnippetsKnob()
    {
        InitializeComponent();
        List.ItemsSource = SnippetStore.All;
        saveSoon.Tick += (_, _) => { saveSoon.Stop(); SnippetStore.Save(); };
        SnippetStore.All.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && open != null) { Back_Click(this, new RoutedEventArgs()); e.Handled = true; }
        };
    }

    public bool Editing => open != null;

    void UpdateEmpty() => EmptyHint.Visibility = SnippetStore.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public IEnumerable<FrameworkElement> RippleItems =>
        Enumerable.Range(0, SnippetStore.All.Count)
            .Select(i => List.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)
            .Where(c => c != null)!;

    void Snippet_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return; // the edit button
        if (((FrameworkElement)sender).DataContext is Snippet s && !string.IsNullOrWhiteSpace(s.Text))
            PasteRequested?.Invoke(Body(s.Text));
    }

    /// <summary>The first line is the snippet's name; if there's more, only the rest is pasted.</summary>
    internal static string Body(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var rest = string.Join("\n", lines.Skip(1)).Trim();
        return rest.Length > 0 ? rest : text.Trim();
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Snippet s) Open(s);
        e.Handled = true;
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        var s = new Snippet();
        SnippetStore.All.Insert(0, s);
        Open(s);
    }

    void Open(Snippet s)
    {
        open = s;
        loading = true;
        Editor.Text = s.Text;
        loading = false;
        Swap(ListView, EditView, forward: true);
        Window.GetWindow(this)?.Activate();
        Dispatcher.BeginInvoke(() => { Editor.Focus(); Editor.CaretIndex = Editor.Text.Length; }, DispatcherPriority.Input);
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (open != null && string.IsNullOrWhiteSpace(open.Text)) SnippetStore.All.Remove(open);
        open = null;
        SnippetStore.Save();
        Swap(EditView, ListView, forward: false);
        Motion.Ripple(RippleItems);
    }

    void Editor_Changed(object sender, TextChangedEventArgs e)
    {
        if (loading || open == null) return;
        open.Text = Editor.Text;
        saveSoon.Stop();
        saveSoon.Start();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (open == null) return;
        SnippetStore.All.Remove(open);
        open = null;
        SnippetStore.Save();
        Swap(EditView, ListView, forward: false);
    }

    static void Swap(FrameworkElement from, FrameworkElement to, bool forward)
    {
        from.Visibility = Visibility.Collapsed;
        to.Visibility = Visibility.Visible;
        var shift = new TranslateTransform(forward ? 24 : -24, 0);
        to.RenderTransform = shift;
        to.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        shift.BeginAnimation(TranslateTransform.XProperty, Motion.Spring(0, 340, 0.25));
    }

    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
