using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ITSeti.Maintenance.App
{
    public partial class MainWindow
    {
        private static readonly Dictionary<string, string> Icons = new Dictionary<string, string>
        {
            ["\uE8C8"] = "▣", ["\uE72E"] = "⚙", ["\uE9D9"] = "✓", ["\uE74D"] = "✦",
            ["\uE8F2"] = "✉", ["\uE8EF"] = "✓", ["\uE768"] = "▶", ["\uE74E"] = "✓",
            ["\uE72C"] = "↻", ["\uE8A5"] = "▣", ["\uE890"] = "◉"
        };

        private static void ApplyLegacyIconFallback(DependencyObject root)
        {
            var block = root as TextBlock;
            if (block != null && block.FontFamily != null &&
                block.FontFamily.Source.IndexOf("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string replacement;
                block.Text = Icons.TryGetValue(block.Text, out replacement) ? replacement : "•";
                block.FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI");
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                ApplyLegacyIconFallback(VisualTreeHelper.GetChild(root, index));
        }
    }
}
