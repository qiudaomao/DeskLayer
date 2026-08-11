// A macOS-flavored WPF theme (dark or light) loaded as a ResourceDictionary.
// Applying it to the Manager gives every control a consistent rounded, spaced,
// accented look. The palette varies by mode; the control templates reference
// palette brushes by key, so they're written once. Implicit styles cover
// Button/TextBox/ListBox/CheckBox/ComboBox/TabControl; named styles
// (AccentButton, DangerButton, Card, SectionText, CaptionText) fill specific
// roles.

using System.Windows;
using System.Windows.Markup;

namespace DeskLayer.App;

public static class Theme
{
    public static ResourceDictionary Load(bool dark) =>
        (ResourceDictionary)XamlReader.Parse(Header + Palette(dark) + Styles + "</ResourceDictionary>");

    /// Follows the Windows "apps" theme (Settings → Personalization → Colors).
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }

    private const string Header =
        "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

    private static string Palette(bool dark) => dark
        ? """
          <SolidColorBrush x:Key="WindowBg" Color="#FF1B1B1F"/>
          <SolidColorBrush x:Key="CardBg" Color="#FF26262B"/>
          <SolidColorBrush x:Key="CardBorder" Color="#FF3A3A42"/>
          <SolidColorBrush x:Key="FieldBg" Color="#FF2E2E34"/>
          <SolidColorBrush x:Key="ButtonHover" Color="#FF3A3A42"/>
          <SolidColorBrush x:Key="Accent" Color="#FF0A84FF"/>
          <SolidColorBrush x:Key="AccentHover" Color="#FF3D9BFF"/>
          <SolidColorBrush x:Key="TextPrimary" Color="#FFF2F2F5"/>
          <SolidColorBrush x:Key="TextSecondary" Color="#FF9A9AA5"/>
          <SolidColorBrush x:Key="Danger" Color="#FFFF453A"/>
          <SolidColorBrush x:Key="Hover" Color="#22FFFFFF"/>
          <SolidColorBrush x:Key="SelectedBg" Color="#330A84FF"/>
          <SolidColorBrush x:Key="OverviewBg" Color="#FF14181F"/>
          """
        : """
          <SolidColorBrush x:Key="WindowBg" Color="#FFF2F3F5"/>
          <SolidColorBrush x:Key="CardBg" Color="#FFFFFFFF"/>
          <SolidColorBrush x:Key="CardBorder" Color="#FFE1E1E6"/>
          <SolidColorBrush x:Key="FieldBg" Color="#FFFFFFFF"/>
          <SolidColorBrush x:Key="ButtonHover" Color="#FFECECEF"/>
          <SolidColorBrush x:Key="Accent" Color="#FF0A84FF"/>
          <SolidColorBrush x:Key="AccentHover" Color="#FF3D9BFF"/>
          <SolidColorBrush x:Key="TextPrimary" Color="#FF1B1B1F"/>
          <SolidColorBrush x:Key="TextSecondary" Color="#FF6A6A73"/>
          <SolidColorBrush x:Key="Danger" Color="#FFE5342A"/>
          <SolidColorBrush x:Key="Hover" Color="#14000000"/>
          <SolidColorBrush x:Key="SelectedBg" Color="#220A84FF"/>
          <SolidColorBrush x:Key="OverviewBg" Color="#FFDDE1EA"/>
          """;

    private const string Styles = """
      <Style TargetType="TextBlock">
        <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="TextOptions.TextFormattingMode" Value="Ideal"/>
      </Style>
      <Style x:Key="SectionText" TargetType="TextBlock">
        <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Margin" Value="2,0,0,8"/>
      </Style>
      <Style x:Key="CaptionText" TargetType="TextBlock">
        <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="Margin" Value="2,10,0,3"/>
      </Style>

      <Style x:Key="Card" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource CardBg}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource CardBorder}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="10"/>
      </Style>

      <Style TargetType="Button">
        <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="Background" Value="{DynamicResource FieldBg}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Padding" Value="12,6"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="Button">
              <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="7"
                      BorderBrush="{DynamicResource CardBorder}" BorderThickness="1" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource ButtonHover}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.8"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.5"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
      <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
        <Setter Property="Background" Value="{DynamicResource Accent}"/>
        <Setter Property="Foreground" Value="White"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="Button">
              <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource AccentHover}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.85"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.5"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
      <Style x:Key="DangerButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
        <Setter Property="Foreground" Value="{DynamicResource Danger}"/>
      </Style>

      <Style TargetType="TextBox">
        <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="CaretBrush" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="Background" Value="{DynamicResource FieldBg}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource CardBorder}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Padding" Value="8,5"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="TextBox">
              <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="6" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1">
                <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                <!-- A read-only field (a fixed-size or content-sized axis)
                     must look unavailable, not merely refuse the keystroke. -->
                <Trigger Property="IsEnabled" Value="False">
                  <Setter TargetName="b" Property="Opacity" Value="0.5"/>
                  <Setter TargetName="b" Property="Background" Value="{DynamicResource ButtonHover}"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>

      <Style TargetType="CheckBox">
        <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="CheckBox">
              <StackPanel Orientation="Horizontal">
                <Border x:Name="box" Width="18" Height="18" CornerRadius="5" Background="{DynamicResource FieldBg}" BorderBrush="{DynamicResource CardBorder}" BorderThickness="1">
                  <TextBlock x:Name="check" Text="&#xE73E;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="12" Foreground="White" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
                </Border>
                <ContentPresenter Margin="8,0,0,0" VerticalAlignment="Center"/>
              </StackPanel>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="box" Property="Background" Value="{DynamicResource Accent}"/>
                  <Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                  <Setter TargetName="check" Property="Visibility" Value="Visible"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>

      <Style TargetType="ListBox">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
      </Style>
      <Style TargetType="ListBoxItem">
        <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Padding" Value="10,7"/>
        <Setter Property="Margin" Value="0,1"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="ListBoxItem">
              <Border x:Name="b" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                <Trigger Property="IsSelected" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SelectedBg}"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>

      <Style TargetType="ComboBox">
        <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
        <Setter Property="Background" Value="{DynamicResource FieldBg}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource CardBorder}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Padding" Value="8,5"/>
        <Setter Property="Cursor" Value="Hand"/>
      </Style>

      <Style TargetType="TabControl">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/>
      </Style>
      <Style TargetType="TabItem">
        <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
        <Setter Property="FontFamily" Value="Segoe UI Variable, Segoe UI"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="TabItem">
              <Border x:Name="b" CornerRadius="7" Padding="16,7" Margin="0,0,4,0" Background="Transparent">
                <ContentPresenter ContentSource="Header" HorizontalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                <Trigger Property="IsSelected" Value="True">
                  <Setter TargetName="b" Property="Background" Value="{DynamicResource CardBg}"/>
                  <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
    """;
}
