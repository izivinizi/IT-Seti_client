using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ITSeti.Maintenance.App;

internal sealed class ScheduledCheckPrompt : Window
{
    public ScheduledCheckPrompt()
    {
        Title = "Плановая проверка · ИТ-Сети";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        MinHeight = 280;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/favicon.ico"));

        var content = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        content.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")),
            Width = 132,
            Height = 66,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(new TextBlock
        {
            Text = "Плановая проверка",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(20, 60, 135))
        });
        content.Children.Add(new TextBlock
        {
            Text = "Проверить состояние компьютера сейчас? Проверка не должна помешать вашей работе.",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(82, 106, 124)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 9, 0, 20)
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var run = new Button
        {
            Content = "Выполнить сейчас",
            IsDefault = true,
            Padding = new Thickness(13, 9, 13, 9),
            Background = new SolidColorBrush(Color.FromRgb(50, 110, 255)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 9, 0)
        };
        run.Click += (_, _) => DialogResult = true;
        var later = new Button { Content = "Напомнить завтра", IsCancel = true, Padding = new Thickness(13, 9, 13, 9) };
        later.Click += (_, _) => DialogResult = false;
        actions.Children.Add(run);
        actions.Children.Add(later);
        content.Children.Add(actions);
        Content = content;
    }
}
