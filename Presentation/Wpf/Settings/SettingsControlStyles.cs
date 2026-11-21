using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace WindowScatter;

internal static class SettingsControlStyles
{
    internal static void Apply(ResourceDictionary resources, bool highContrast)
    {
        resources["FieldBackground"] = highContrast ? SystemColors.WindowBrush : SharedBrushes.Raised;
        resources["FieldForeground"] = highContrast ? SystemColors.WindowTextBrush : SharedBrushes.Text;
        resources["PreferenceSurface"] = highContrast ? SystemColors.WindowBrush : SharedBrushes.Surface;
        resources["PreferenceEdge"] = highContrast ? SystemColors.WindowTextBrush : Brushes.Transparent;
        resources["SecondaryText"] = highContrast ? SystemColors.WindowTextBrush : SharedBrushes.Secondary;
        resources["KeycapSurface"] = highContrast ? SystemColors.WindowBrush : SharedBrushes.Graphite;
        resources["ToggleFill"] = highContrast ? SystemColors.HighlightBrush : SharedBrushes.Mint;
        resources["ToggleInk"] = highContrast ? SystemColors.HighlightTextBrush : SharedBrushes.Graphite;
        resources["FieldBorder"] = highContrast ? SystemColors.WindowTextBrush : Brush("#87998C");
        resources["FieldHover"] = highContrast ? SystemColors.HighlightBrush : Brush("#BCC5BE");
        resources["FieldFocus"] = highContrast ? SystemColors.HighlightBrush : SharedBrushes.Mint;
        resources["FieldDisabled"] = highContrast ? SystemColors.GrayTextBrush : Brush("#BCC5BE");
        resources["FieldSelection"] = highContrast ? SystemColors.HighlightBrush : Brush("#465B4C");
        resources["FieldSelectionText"] = highContrast ? SystemColors.HighlightTextBrush : Brushes.White;
        resources["FieldError"] = highContrast ? SystemColors.WindowTextBrush : Brush("#FFB4AB");
        if (resources.Contains(typeof(TextBox))) return;
        var styles = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="CheckBox">
                <Setter Property="Foreground" Value="{DynamicResource FieldForeground}"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="CheckBox">
                      <Border x:Name="Focus" BorderThickness="1" BorderBrush="Transparent" CornerRadius="4" Padding="2">
                        <DockPanel Background="Transparent">
                          <Border x:Name="Box" Width="18" Height="18" CornerRadius="4" BorderThickness="1"
                                  BorderBrush="{DynamicResource FieldBorder}" Background="{DynamicResource KeycapSurface}"
                                  Margin="0,0,10,0" VerticalAlignment="Center">
                            <Path x:Name="Check" Data="M 3 8 L 7 12 L 14 4" Stroke="{DynamicResource ToggleInk}"
                                  StrokeThickness="2" Visibility="Hidden"/>
                          </Border>
                          <ContentPresenter VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
                        </DockPanel>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource FieldHover}"/>
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                          <Setter TargetName="Box" Property="Background" Value="{DynamicResource ToggleFill}"/>
                          <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource ToggleFill}"/>
                          <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                          <Setter TargetName="Focus" Property="BorderBrush" Value="{DynamicResource FieldFocus}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                          <Setter Property="Foreground" Value="{DynamicResource FieldDisabled}"/>
                          <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource FieldDisabled}"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="TextBox">
                <Setter Property="Background" Value="{DynamicResource FieldBackground}"/>
                <Setter Property="Foreground" Value="{DynamicResource FieldForeground}"/>
                <Setter Property="CaretBrush" Value="{DynamicResource FieldForeground}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource FieldBorder}"/>
                <Setter Property="Padding" Value="10,6"/>
                <Setter Property="MinHeight" Value="36"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="SelectionBrush" Value="{DynamicResource FieldSelection}"/>
                <Setter Property="SelectionTextBrush" Value="{DynamicResource FieldSelectionText}"/>
                <Setter Property="SelectionOpacity" Value="1"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="TextBox">
                      <Border x:Name="Frame" Background="{TemplateBinding Background}"
                              BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6">
                        <ScrollViewer x:Name="PART_ContentHost"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FieldHover}"/>
                        </Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FieldFocus}"/>
                        </Trigger>
                        <Trigger Property="Tag" Value="Invalid">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FieldError}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                          <Setter Property="Foreground" Value="{DynamicResource FieldDisabled}"/>
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FieldDisabled}"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="ComboBox">
                <Setter Property="Background" Value="{DynamicResource FieldBackground}"/>
                <Setter Property="Foreground" Value="{DynamicResource FieldForeground}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource FieldBorder}"/>
                <Setter Property="Padding" Value="10,6"/>
                <Setter Property="MinHeight" Value="36"/>
                <Setter Property="HorizontalContentAlignment" Value="Left"/>
                <Setter Property="ScrollViewer.CanContentScroll" Value="True"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ComboBox">
                      <Grid>
                        <Border x:Name="Frame" Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6">
                          <Grid>
                            <ToggleButton Focusable="False" ClickMode="Press"
                                IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                              <ToggleButton.Template>
                                <ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"/></ControlTemplate>
                              </ToggleButton.Template>
                            </ToggleButton>
                            <Grid IsHitTestVisible="False" Margin="{TemplateBinding Padding}">
                              <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                              <ContentPresenter Content="{TemplateBinding SelectionBoxItem}"
                                  ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                  ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"
                                  VerticalAlignment="Center" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
                              <Path Grid.Column="1" Data="M 0 0 L 4 4 L 8 0" Stroke="{TemplateBinding Foreground}"
                                    StrokeThickness="1.5" Width="8" Height="4" Margin="12,0,0,0" VerticalAlignment="Center"/>
                            </Grid>
                          </Grid>
                        </Border>
                        <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom"
                               AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                          <Border Background="{DynamicResource FieldBackground}" BorderBrush="{DynamicResource FieldBorder}"
                                  BorderThickness="1" CornerRadius="6" Padding="4"
                                  MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                            <ScrollViewer MaxHeight="{TemplateBinding MaxDropDownHeight}" CanContentScroll="True">
                              <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                            </ScrollViewer>
                          </Border>
                        </Popup>
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FieldHover}"/>
                        </Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FieldFocus}"/>
                        </Trigger>
                        <Trigger Property="IsDropDownOpen" Value="True">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FieldFocus}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                          <Setter Property="Foreground" Value="{DynamicResource FieldDisabled}"/>
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FieldDisabled}"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="ComboBoxItem">
                <Setter Property="Foreground" Value="{DynamicResource FieldForeground}"/>
                <Setter Property="Padding" Value="10,8"/>
                <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ComboBoxItem">
                      <Border x:Name="Frame" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}">
                        <ContentPresenter/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True">
                          <Setter TargetName="Frame" Property="Background" Value="{DynamicResource FieldSelection}"/>
                          <Setter Property="Foreground" Value="{DynamicResource FieldSelectionText}"/>
                        </Trigger>
                        <Trigger Property="IsHighlighted" Value="True">
                          <Setter TargetName="Frame" Property="Background" Value="{DynamicResource FieldSelection}"/>
                          <Setter Property="Foreground" Value="{DynamicResource FieldSelectionText}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                          <Setter Property="Foreground" Value="{DynamicResource FieldDisabled}"/>
                          <Setter TargetName="Frame" Property="Background" Value="{DynamicResource FieldBackground}"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </ResourceDictionary>
            """);
        foreach (object key in styles.Keys) resources[key] = styles[key];
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
