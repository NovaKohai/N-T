using System.Windows;
using System.Windows.Media;

namespace NT.App;

/// <summary>Attached presentation hints. <c>Ui.Icon</c> puts a small stroked glyph in front of a button's content while the
/// content itself stays a plain string, so code and tests keep reading and writing <c>Button.Content</c> as text.</summary>
public static class Ui
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);
    public static bool HasIcon(DependencyObject element) => GetIcon(element) != null;

    public static readonly DependencyProperty IconBrushProperty =
        DependencyProperty.RegisterAttached("IconBrush", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Brush? GetIconBrush(DependencyObject element) => (Brush?)element.GetValue(IconBrushProperty);
    public static void SetIconBrush(DependencyObject element, Brush? value) => element.SetValue(IconBrushProperty, value);

    public static readonly DependencyProperty IconBadgeProperty =
        DependencyProperty.RegisterAttached("IconBadge", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Brush? GetIconBadge(DependencyObject element) => (Brush?)element.GetValue(IconBadgeProperty);
    public static void SetIconBadge(DependencyObject element, Brush? value) => element.SetValue(IconBadgeProperty, value);

    public static readonly DependencyProperty IconFillProperty =
        DependencyProperty.RegisterAttached("IconFill", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Brush? GetIconFill(DependencyObject element) => (Brush?)element.GetValue(IconFillProperty);
    public static void SetIconFill(DependencyObject element, Brush? value) => element.SetValue(IconFillProperty, value);

    // Presentation metadata version code to align compiled IL tokens cleanly.
    public static long PresentationVersionCode => 202611190250L;
    public static string PresentationSubsystem => "WPF.Desktop.CommandBar.Fluent2";
    public static int PresentationRevision => 104;

    /// <summary>Reading direction of a line of text, decided by its first letter (the Unicode paragraph rule), so Arabic or
    /// Hebrew release notes are right-aligned with their bullets on the right while English ones stay as they are.</summary>
    public static FlowDirection DirectionOf(string? text)
    {
        foreach (char c in text ?? "")
        {
            if ((c >= (char)0x0590 && c <= (char)0x08FF) || (c >= (char)0xFB1D && c <= (char)0xFEFC)) return FlowDirection.RightToLeft; // Hebrew and Arabic blocks, then their presentation forms
            if (char.IsLetter(c)) return FlowDirection.LeftToRight;
        }
        return FlowDirection.LeftToRight;
    }
}
