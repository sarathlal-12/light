using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DotLight.Knobs;

/// <summary>Notes knob: a short list of notes saved as you type; one can be pinned to the island.</summary>
public partial class NotesKnob : UserControl
{
    readonly DispatcherTimer saveSoon = new() { Interval = TimeSpan.FromMilliseconds(500) };
    Note? open;
    bool loading;

    /// <summary>Raised when the pinned note (or its text) changes, so the island can update.</summary>
    public event Action? PinnedChanged;

    public NotesKnob()
    {
        InitializeComponent();
        List.ItemsSource = NoteStore.All;
        saveSoon.Tick += (_, _) => { saveSoon.Stop(); NoteStore.Save(); };
        NoteStore.All.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && open != null) { Back_Click(this, new RoutedEventArgs()); e.Handled = true; }
        };
    }

    public bool Editing => open != null;

    void UpdateEmpty() => EmptyHint.Visibility = NoteStore.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The visible rows in order, for the ripple-in animation.</summary>
    public IEnumerable<FrameworkElement> RippleItems =>
        Enumerable.Range(0, NoteStore.All.Count)
            .Select(i => List.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)
            .Where(c => c != null)!;

    void New_Click(object sender, RoutedEventArgs e)
    {
        var note = new Note();
        NoteStore.All.Insert(0, note);
        Open(note);
    }

    void Note_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Note n) Open(n);
    }

    void Open(Note n)
    {
        open = n;
        loading = true;
        Editor.Text = n.Text;
        loading = false;
        PinBtn.IsChecked = n.Pinned;
        EditTime.Text = n.Text.Length == 0 ? "New note" : "Edited " + n.TimeLabel;
        Swap(ListView, EditView, forward: true);
        Window.GetWindow(this)?.Activate();
        Dispatcher.BeginInvoke(() => { Editor.Focus(); Editor.CaretIndex = Editor.Text.Length; }, DispatcherPriority.Input);
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (open != null && string.IsNullOrWhiteSpace(open.Text))
        {
            bool wasPinned = open.Pinned;
            NoteStore.All.Remove(open); // nothing written: don't keep an empty note
            if (wasPinned) PinnedChanged?.Invoke();
        }
        open = null;
        NoteStore.Save();
        Swap(EditView, ListView, forward: false);
        Motion.Ripple(RippleItems);
    }

    void Editor_Changed(object sender, TextChangedEventArgs e)
    {
        if (loading || open == null) return;
        open.Text = Editor.Text;
        EditTime.Text = "Saved";
        // newest first
        int i = NoteStore.All.IndexOf(open);
        if (i > 0) NoteStore.All.Move(i, 0);
        saveSoon.Stop();
        saveSoon.Start();
        if (open.Pinned) PinnedChanged?.Invoke();
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (open == null) return;
        bool pin = PinBtn.IsChecked == true;
        foreach (var n in NoteStore.All) n.Pinned = false; // one pinned note at a time
        open.Pinned = pin;
        NoteStore.Save();
        PinnedChanged?.Invoke();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (open == null) return;
        bool wasPinned = open.Pinned;
        NoteStore.All.Remove(open);
        open = null;
        NoteStore.Save();
        if (wasPinned) PinnedChanged?.Invoke();
        Swap(EditView, ListView, forward: false);
    }

    /// <summary>Slides one view out and the other in, like moving between pages.</summary>
    static void Swap(FrameworkElement from, FrameworkElement to, bool forward)
    {
        from.Visibility = Visibility.Collapsed;
        to.Visibility = Visibility.Visible;
        var shift = new TranslateTransform(forward ? 24 : -24, 0);
        to.RenderTransform = shift;
        to.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        shift.BeginAnimation(TranslateTransform.XProperty, Motion.Spring(0, 340, 0.25));
    }
}
