using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Toastshot;

/// <summary>
/// Shows release notes written in simple Markdown (headings, "- " bullets, paragraphs) as WPF text. Links show their
/// text; **bold** and `code` marks are dropped. Enough for GitHub release notes without a Markdown library.
/// </summary>
public static partial class ReleaseNotes
{
    public static UIElement Render(string markdown)
    {
        var panel = new StackPanel();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                panel.Children.Add(Text(Clean(line.TrimStart('#', ' ')), heading: true));
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var dot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(4, 8, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                dot.SetResourceReference(Shape.FillProperty, "InkMutedBrush");
                DockPanel.SetDock(dot, Dock.Left);
                var row = new DockPanel { Margin = new Thickness(line.Length - trimmed.Length > 1 ? 14 : 0, 0, 0, 4) };
                row.Children.Add(dot);
                row.Children.Add(Text(Clean(trimmed[2..])));
                panel.Children.Add(row);
                continue;
            }

            panel.Children.Add(Text(Clean(line)));
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(Text("No notes for this version."));
        }

        return panel;
    }

    private static TextBlock Text(string text, bool heading = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = heading ? 13 : 12.5,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, heading ? 6 : 0, 0, 4)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, heading ? "InkBrush" : "InkMutedBrush");
        return block;
    }

    /// <summary>[text](url) → text; **bold**, __bold__ and `code` → plain.</summary>
    private static string Clean(string text) => Marks().Replace(Links().Replace(text, "$1"), "");

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Links();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex Marks();
}
