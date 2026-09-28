param([string]$StatusPath,[string]$ReadyPath,[string]$Version)
$ErrorActionPreference='Stop'
try {
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
    [xml]$markup=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Title="ИТ-Сети | Обновление" Width="500" SizeToContent="Height"
        ResizeMode="NoResize" WindowStartupLocation="CenterScreen"
        Background="#F3F6F8" FontFamily="Segoe UI">
  <Border Background="White" BorderBrush="#DDE6EB" BorderThickness="1" Padding="26">
    <StackPanel>
      <TextBlock x:Name="Heading" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="Установка обновления" FontSize="22" FontWeight="SemiBold" Foreground="#143C87"/>
      <TextBlock x:Name="VersionLabel" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" FontSize="16" Foreground="#263C52" Margin="0,7,0,18"/>
      <ProgressBar x:Name="Progress" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Height="8" IsIndeterminate="True" Foreground="#326EFF" Background="#E6EDF2" BorderThickness="0"/>
      <TextBlock x:Name="StatusLabel" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Text="Ожидаем закрытия приложения…" FontSize="16" Foreground="#263C52" TextWrapping="Wrap" Margin="0,16,0,0"/>
      <TextBlock Text="После завершения запустите приложение снова." FontSize="14" Foreground="#52677A" TextWrapping="Wrap" Margin="0,10,0,0"/>
      <Button x:Name="CloseButton" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Content="Закрыть" Padding="18,8" HorizontalAlignment="Right" Margin="0,20,0,0" Visibility="Collapsed"/>
    </StackPanel>
  </Border>
</Window>
'@
    $reader=New-Object Xml.XmlNodeReader $markup
    $window=[Windows.Markup.XamlReader]::Load($reader)
    $heading=$window.FindName('Heading')
    $versionLabel=$window.FindName('VersionLabel')
    $progress=$window.FindName('Progress')
    $statusLabel=$window.FindName('StatusLabel')
    $closeButton=$window.FindName('CloseButton')
    $versionLabel.Text='Версия '+$Version
    $closeButton.Add_Click({$window.Close()})
    $timer=New-Object Windows.Threading.DispatcherTimer
    $timer.Interval=[TimeSpan]::FromMilliseconds(500)
    $timer.Add_Tick({
        try {
            if(!(Test-Path -LiteralPath $StatusPath -PathType Leaf)){return}
            $line=[IO.File]::ReadAllText($StatusPath,[Text.Encoding]::UTF8)
            $separator=$line.IndexOf('|')
            $message=if($separator -ge 0){$line.Substring($separator+1).Trim()}else{$line.Trim()}
            if(!$message -or $message -eq $script:lastMessage){return}
            $script:lastMessage=$message
            $statusLabel.Text=$message
            if($message -match 'установлена\. Запустите приложение снова\.'){
                $heading.Text='Обновление завершено'
                $progress.IsIndeterminate=$false
                $progress.Value=100
                $closeButton.Visibility='Visible'
                $timer.Stop()
            } elseif($message.StartsWith('Ошибка обновления:')) {
                $heading.Text='Обновление не выполнено'
                $progress.IsIndeterminate=$false
                $progress.Value=0
                $closeButton.Visibility='Visible'
                $timer.Stop()
            }
        } catch [IO.IOException] {}
    })
    $window.Add_Closed({$timer.Stop()})
    $window.Add_Loaded({
        [IO.File]::WriteAllText($ReadyPath,'ready',[Text.Encoding]::ASCII)
        $timer.Start()
    })
    [void]$window.ShowDialog()
} catch {
    try {[IO.File]::WriteAllText($ReadyPath,('error: '+$_.Exception.Message),[Text.Encoding]::UTF8)} catch {}
    exit 1
}
