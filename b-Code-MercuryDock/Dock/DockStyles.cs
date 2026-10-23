using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace Mercury;

/// <summary>
/// 桌面坞与「+」项目列表的控件样式：搜索框、滚动条、列表选中。照 OneHistory 视觉识别
/// （<c>2026-031-OneHistorySite/z-OneHistoryID</c>）与 Aurora 的同名控件做：
/// <list type="bullet">
///   <item>输入框：比卡片深一级的底、<c>hairline-2</c> 细边、圆角 8；悬停边线加深，聚焦时边线变墨色、底色回到 canvas；</item>
///   <item>滚动条：没有箭头和轨道，只有一根细圆角滑块，平时半透明、悬停加深（同 Aurora）；</item>
///   <item>列表行：悬停 <c>surface-hover</c>，选中是墨色圆角块、字反白；不画虚线焦点框。</item>
/// </list>
/// </summary>
/// <remarks>
/// 模板用运行时解析的 XAML 文本写：用 FrameworkElementFactory 拼 Track / Thumb 很绕，而本模块不允许编译进 BAML
/// （Smoke 拦着）。颜色从 <see cref="DockTheme"/> 填进来，不另写第二份色值。坞只有深色，不跟前端主题。
/// </remarks>
internal static class DockStyles
{
    public const string SearchBoxKey = "Mercury.SearchBox";

    public const string ItemKey = "Mercury.ListItem";

    /// <summary>新建一份样式字典；每个窗口各要一份（字典里的模板不能跨线程共用，坞和列表虽同线程也各拿各的）。</summary>
    public static ResourceDictionary Create()
        => (ResourceDictionary)XamlReader.Parse(Xaml());

    private static string Hex(SolidColorBrush brush)
        => $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";

    private static string Xaml() => $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

          <Style x:Key="Mercury.ScrollThumb" TargetType="Thumb">
            <Setter Property="OverridesDefaultStyle" Value="True" />
            <Setter Property="IsTabStop" Value="False" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                  <Border x:Name="Bd" Margin="3" Background="{{Hex(DockTheme.TextDisabled)}}" CornerRadius="3" Opacity="0.55" />
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Opacity" Value="0.85" />
                    </Trigger>
                    <Trigger Property="IsDragging" Value="True">
                      <Setter TargetName="Bd" Property="Opacity" Value="0.85" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="ScrollBar">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="Width" Value="12" />
            <Setter Property="MinWidth" Value="12" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                  <Grid Background="{TemplateBinding Background}">
                    <Track x:Name="PART_Track" IsDirectionReversed="True">
                      <Track.Thumb>
                        <Thumb Style="{StaticResource Mercury.ScrollThumb}" />
                      </Track.Thumb>
                      <Track.IncreaseRepeatButton>
                        <RepeatButton Command="ScrollBar.PageDownCommand" Focusable="False" Opacity="0" />
                      </Track.IncreaseRepeatButton>
                      <Track.DecreaseRepeatButton>
                        <RepeatButton Command="ScrollBar.PageUpCommand" Focusable="False" Opacity="0" />
                      </Track.DecreaseRepeatButton>
                    </Track>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="Orientation" Value="Horizontal">
                      <Setter Property="Width" Value="Auto" />
                      <Setter Property="MinWidth" Value="0" />
                      <Setter Property="Height" Value="12" />
                      <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="{{SearchBoxKey}}" TargetType="TextBox">
            <Setter Property="Foreground" Value="{{Hex(DockTheme.Ink)}}" />
            <Setter Property="Background" Value="{{Hex(DockTheme.SurfaceAlt)}}" />
            <Setter Property="BorderBrush" Value="{{Hex(DockTheme.PanelBorder)}}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="Padding" Value="9,0" />
            <Setter Property="MinHeight" Value="34" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
            <Setter Property="CaretBrush" Value="{{Hex(DockTheme.Ink)}}" />
            <Setter Property="SelectionBrush" Value="{{Hex(DockTheme.Accent)}}" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                  <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8" SnapsToDevicePixels="True">
                    <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"
                                  VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                  HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="BorderBrush" Value="{{Hex(DockTheme.TextDisabled)}}" />
                    </Trigger>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True">
                      <Setter TargetName="Bd" Property="BorderBrush" Value="{{Hex(DockTheme.Ink)}}" />
                      <Setter TargetName="Bd" Property="Background" Value="{{Hex(DockTheme.Canvas)}}" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="{{ItemKey}}" TargetType="ListBoxItem">
            <Setter Property="Foreground" Value="{{Hex(DockTheme.Ink)}}" />
            <Setter Property="Padding" Value="8,6" />
            <Setter Property="Margin" Value="0,1" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="SnapsToDevicePixels" Value="True" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                  <Border x:Name="Bd" Background="Transparent" CornerRadius="8" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="{{Hex(DockTheme.Hover)}}" />
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="{{Hex(DockTheme.Ink)}}" />
                      <Setter Property="Foreground" Value="{{Hex(DockTheme.OnInk)}}" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;
}
