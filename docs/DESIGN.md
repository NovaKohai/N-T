# N&T (NeoDM) Design Specification & Technical Architecture

> **Complete Visual Design System, Functional Logic, and Implementation Guide**  
> Target Platforms: Windows 11 / Windows 10 (x64) | Framework: .NET 8 WPF | Extension: Chromium MV3  
> License: MIT | Identity: N&T (NeoDM)

---

## Table of Contents
1. [Executive Design Philosophy & Core Principles](#1-executive-design-philosophy--core-principles)
2. [Color Architecture & The 9 Official Themes](#2-color-architecture--the-9-official-themes)
3. [Typography, Spatial Geometry & Accessibility](#3-typography-spatial-geometry--accessibility)
4. [Iconography & The 36px / 2.8px Adaptive Toolbar System](#4-iconography--the-36px--28px-adaptive-toolbar-system)
5. [Main Window Workspace Architecture & Data Flow](#5-main-window-workspace-architecture--data-flow)
6. [Dialogs & Modeless Windows: UI & Functional Logic](#6-dialogs--modeless-windows-ui--functional-logic)
7. [Core Download Engine Mechanics & Segmentation](#7-core-download-engine-mechanics--segmentation)
8. [Native Messaging Bridge & IPC Protocol](#8-native-messaging-bridge--ipc-protocol)
9. [Chromium MV3 Extension UI & Sniffer Specification](#9-chromium-mv3-extension-ui--sniffer-specification)
10. [State Persistence, Recovery & Process Isolation](#10-state-persistence-recovery--process-isolation)
11. [Verification Suites & Build Quality Gates](#11-verification-suites--build-quality-gates)

---

## 1. Executive Design Philosophy & Core Principles

### 1.1 The Workspace Mental Model
N&T is structured around a single foundational premise: **The queue is the workspace, not a marketing dashboard.**
- **Zero Decorative Fluff**: There are no artificial hero banners, promotional carousels, synthetic analytics, or mock demo jobs. Every pixel directly serves real download operations.
- **Proximity of Action**: High-frequency operational controls (Add, Resume, Stop, Delete, Scheduler, Queue Controls) are located immediately adjacent to real-time transfer telemetry.
- **Modeless Non-Blocking Windows**: Detailed progress viewers and download completion notifications operate modelessly without seizing keyboard focus or freezing queue orchestration.

### 1.2 "No AI Slop" & Mathematical Vector Precision
All iconography, layout boundaries, and controls are constructed with rigorous geometric precision:
- **Zero Generative Distortion**: No blurry raster bitmaps, irregular bezier fluctuations, or AI-generated visual clutter.
- **Pure Geometric Primitives**: All vector glyphs are defined mathematically in XAML and SVG with clean coordinate grids, uniform stroke thicknesses, and round caps (`StrokeLineCap="Round"`, `StrokeLineJoin="Round"`).
- **DPI-Agnostic Infinite Scalability**: Rendered natively via WPF `<Path>` geometries and vector mini-languages, guaranteeing pixel-perfect sharpness across 100%, 125%, 150%, 175%, and 200% display scaling factors.

### 1.3 Windows 11 Native Integration & Accessibility
- **Fluent 2 Principles**: Subtle control borders, smooth corner radii (6–8 DIP for inputs/buttons, 8–12 DIP for cards/windows), and native window borders that seamlessly integrate with Windows Snap layouts.
- **WCAG 2.1 AA Compliance**: All typography and actionable controls enforce a minimum contrast ratio of **4.5:1** across all 9 official themes.
- **Bidirectional Inclusivity (RTL / LTR)**: Layout dynamically evaluates text (e.g., release notes, filenames, and changelogs) via `Ui.DirectionOf` to render right-to-left scripts (Arabic/Hebrew) natively without layout corruption.

---

## 2. Color Architecture & The 9 Official Themes

N&T features a tokenized dynamic resource architecture. Swapping themes replaces the active resource dictionary at runtime, repainting all open windows instantly without requiring a process restart.

### 2.1 Dynamic Resource Token Dictionary

| Resource Key | Type | Semantic Role | Light Mode Value | Dark / Charcoal Value |
| :--- | :--- | :--- | :--- | :--- |
| `CanvasBrush` | `SolidColorBrush` | Base window background | `#F8FAFC` | `#1E1E1E` |
| `SidebarBrush` | `SolidColorBrush` | Navigation rail / sidebar surface | `#F1F5F9` | `#252526` |
| `SurfaceBrush` | `SolidColorBrush` | Cards, dialog backdrops, inputs | `#FFFFFF` | `#2D2D30` |
| `SurfaceHoverBrush` | `SolidColorBrush` | Hover state for buttons / rows | `rgba(0,0,0,0.06)` | `rgba(255,255,255,0.08)` |
| `SurfacePressedBrush`| `SolidColorBrush` | Active pressed state for buttons | `rgba(0,0,0,0.12)` | `rgba(255,255,255,0.14)` |
| `BorderBrush` | `SolidColorBrush` | Subtle grid splitters & card outlines | `#E2E8F0` | `#38383D` |
| `ControlBorderBrush`| `SolidColorBrush` | Input field borders & buttons | `#CBD5E1` | `#444448` |
| `InkBrush` | `SolidColorBrush` | Primary high-contrast typography | `#0F172A` | `#F3F4F6` |
| `MutedBrush` | `SolidColorBrush` | Secondary subtitles, timestamps, ETAs | `#64748B` | `#9CA3AF` |
| `DisabledInkBrush` | `SolidColorBrush` | Disabled controls (35% opacity) | `#94A3B8` | `#6B7280` |
| `AccentBrush` | `SolidColorBrush` | Focus outlines, active highlights | Theme Accent | Vibrant Theme Accent |
| `AccentFillBrush` | `Brush` | Primary action buttons (`IsDefault`) | Solid or Gradient | Solid or Gradient |
| `AccentSoftBrush` | `SolidColorBrush` | Subtle badge and selection tints | 12% Accent Opacity | 16% Accent Opacity |
| `SuccessBrush` | `SolidColorBrush` | Completed status, verified hashes | `#10B981` | `#34D399` |
| `WarningBrush` | `SolidColorBrush` | Scheduled tasks, duplicate warnings | `#F59E0B` | `#FBBF24` |
| `ErrorBrush` | `SolidColorBrush` | Failed downloads, validation errors | `#EF4444` | `#F87171` |
| `InfoBrush` | `SolidColorBrush` | Queued state, ambient notifications | `#0EA5E9` | `#38BDF8` |

---

### 2.2 Mathematical Contrast & Accent Adaptation Formula

In `src/NT.App/Theming.cs`, N&T programmatically derives accessible accent colors to guarantee WCAG 4.5:1 compliance:

```csharp
// From src/NT.App/Theming.cs
public static class ThemeManager
{
    private static double Channel(byte c)
    {
        double s = c / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    public static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    public static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));

    // Nudges custom accents in 40 fine steps toward white or black until contrast >= 4.5:1
    public static Color EnsureContrast(Color candidate, Color surface, double minRatio = 4.5)
    {
        if (Contrast(candidate, surface) >= minRatio) return candidate;
        bool lighten = Luminance(surface) < 0.5;
        Color target = lighten ? Colors.White : Colors.Black;
        for (int i = 1; i <= 40; i++)
        {
            var mixed = Mix(candidate, target, i / 40.0);
            if (Contrast(mixed, surface) >= minRatio) return mixed;
        }
        return target;
    }
}
```

---

### 2.3 The 9 Concrete Themes Palette Matrix

1. **Light**: Clean enterprise daylight palette (`CanvasBrush`: `#F8FAFC`, `SidebarBrush`: `#F1F5F9`, `AccentBrush`: `#0F766E`).
2. **Dark**: Professional graphite dark palette (`CanvasBrush`: `#1E1E1E`, `SidebarBrush`: `#252526`, `AccentBrush`: `#38BDF8`).
3. **System**: Dynamic bridge that tracks Windows Light/Dark personalization settings at runtime.
4. **Midnight**: Deep OLED cosmic blue (`CanvasBrush`: `#0F141C`, `SidebarBrush`: `#161E2E`, `AccentBrush`: `#818CF8`).
5. **Graphite**: Titanium industrial monochrome (`CanvasBrush`: `#202023`, `SidebarBrush`: `#28282D`, `AccentBrush`: `#A1A1AA`).
6. **Ocean**: Rich deep-sea teal (`CanvasBrush`: `#112533`, `SidebarBrush`: `#173447`, `AccentBrush`: `#2DD4BF`).
7. **Forest**: Emerald organic foliage (`CanvasBrush`: `#132B20`, `SidebarBrush`: `#1A3B2C`, `AccentBrush`: `#34D399`).
8. **Rose**: Subtle burgundy slate (`CanvasBrush`: `#2C1720`, `SidebarBrush`: `#3D202D`, `AccentBrush`: `#FB7185`).
9. **Paper**: Natural warm book-stock parchment (`CanvasBrush`: `#F7F2EA`, `SidebarBrush`: `#EFE8DA`, `AccentBrush`: `#A78BFA`, `InkBrush`: `#382F24`).

---

## 3. Typography, Spatial Geometry & Accessibility

### 3.1 Type Scale Specification
The application utilizes `Segoe UI Variable` (Windows 11) with fallback to `Segoe UI` (Windows 10) and `Cairo` for Arabic text:

- **Window Heading**: `20 DIP`, SemiBold (`FontWeight="600"`), LineHeight `26 DIP`.
- **Dialog Heading**: `18 DIP`, SemiBold (`FontWeight="600"`), LineHeight `24 DIP`.
- **Section Heading**: `14 DIP`, SemiBold (`FontWeight="600"`), LineHeight `20 DIP`.
- **Primary Body / Queue Rows**: `13 DIP`, Regular / Medium, LineHeight `18 DIP`.
- **Toolbar Command Labels**: `11.5 DIP`, Medium (`FontWeight="500"`), TextAlignment `Center`.
- **Secondary / Telemetry**: `11.5–12 DIP`, Regular (`FontWeight="400"`), LineHeight `16 DIP`.
- **Badge / Counter Tokens**: `10.5–11 DIP`, Bold (`FontWeight="700"`), LineHeight `14 DIP`.

### 3.2 Spatial Metrics & Corner Radii Tokens
- **Grid Increment**: Strict 4 DIP base grid (`4`, `8`, `12`, `16`, `20`, `24`, `32`).
- **Corner Radii Tokens**:
  - `ControlRadius`: `6 DIP` (Buttons, TextBoxes, Progress Bars).
  - `CardRadius`: `8–10 DIP` (Dialog containers, Duplicate Alert cards).
  - `PillRadius`: `999 DIP` (Status rail counter badges, category tags).
- **Window Size Bounds**:
  - Main Window: Default `1180 × 720 DIP`, Minimum `900 × 480 DIP`.
  - Add Download Window: Default `580 × 490 DIP`, Minimum `520 × 420 DIP` (tuned height prevents vertical scrollbars when "Save to" expands).
  - Torrent Add Window: Default `680 × 640 DIP`, Minimum `560 × 520 DIP`.
  - Progress Viewer: Default `560 × 460 DIP`, Minimum `480 × 380 DIP`.
  - Settings Window: Default `660 × 620 DIP`, Minimum `580 × 500 DIP`.

---

## 4. Iconography & The 36px / 2.8px Adaptive Toolbar System

The primary application toolbar is built with a mathematically precise, quiet monochrome design inspired by classic power-user download utilities, rendered natively in modern WPF vector geometry.

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│  [+]    (↓)    (■)    (✋)    (🗑)    (🗑✓)   │   (⚙)      (⏰)    │   [🗂↓▾]   [🗂■▾]   │   (🌐)     (👍)    │
│Add URL Resume  Stop  Stop All Delete Delete Co│ Options  Scheduler │ Start Qu.. Stop Qu..│ Grabber Tell a Friend│
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 4.1 Toolbar Design Principles
1. **Large 36px Geometries**: All primary command glyphs are bound to `Width="36" Height="36"`, ensuring immediate visual affordance and comfortable mouse targeting.
2. **2.8px Stroke Thickness**: Configured with `StrokeThickness="2.8"`, `StrokeStartLineCap="Round"`, `StrokeEndLineCap="Round"`, and `StrokeLineJoin="Round"` for crisp, high-definition rendering.
3. **Quiet Monochrome (`{DynamicResource InkBrush}`)**: Glyphs and text inherit the active theme's ink color directly without noisy colored badge backdrops.
4. **Theme-Adaptive Canvas (`{DynamicResource CanvasBrush}`)**: The toolbar container border seamlessly blends with whichever theme canvas is selected (`#F7F2EA` in Paper, `#1E1E1E` in Dark, `#0F141C` in Midnight, etc.).
5. **No AI Slop**: Every curve is a mathematically verified Cartesian point or Bezier arc, without distorted geometry.

---

### 4.2 XAML Vector Definitions (`src/NT.App/Themes/Controls.xaml`)

```xml
<!-- Classic Toolbar Geometries (Authentic 36px / 2.8px Outline Set) -->

<!-- 1. Add URL (Hollow Outline Rounded Plus) -->
<Geometry x:Key="IconAddUrlClassic">
  M 13.5,5 C 13.5,4.17 14.17,3.5 15,3.5 L 17,3.5 C 17.83,3.5 18.5,4.17 18.5,5 L 18.5,13.5 L 27,13.5 C 27.83,13.5 28.5,14.17 28.5,15 L 28.5,17 C 28.5,17.83 27.83,18.5 27,18.5 L 18.5,18.5 L 18.5,27 C 18.5,27.83 17.83,28.5 17,28.5 L 15,28.5 C 14.17,28.5 13.5,27.83 13.5,27 L 13.5,18.5 L 5,18.5 C 4.17,18.5 3.5,17.83 3.5,17 L 3.5,15 C 3.5,14.17 4.17,13.5 5,13.5 L 13.5,13.5 Z
</Geometry>

<!-- 2. Resume (Circle with Down Arrow) -->
<Geometry x:Key="IconResumeCircle">
  M 16,4.5 A 11.5,11.5 0 1 0 16.01,4.5 M 16,10 L 16,21 M 11.5,16.5 L 16,21 L 20.5,16.5
</Geometry>

<!-- 3. Stop (Circle with Rounded Square) -->
<Geometry x:Key="IconStopCircle">
  M 16,4.5 A 11.5,11.5 0 1 0 16.01,4.5 M 13.5,12 H 18.5 C 19.33,12 20,12.67 20,13.5 V 18.5 C 20,19.33 19.33,20 18.5,20 H 13.5 C 12.67,20 12,19.33 12,18.5 V 13.5 C 12,12.67 12.67,12 13.5,12 Z
</Geometry>

<!-- 4. Stop All (Raised 4-Finger Palm Hand) -->
<Geometry x:Key="IconStopAllHand">
  M 10,25.5 L 10,17 C 10,15.8 8.8,15 7.8,15.5 C 7,15.9 6.5,16.8 6.5,17.5 L 6.5,18 C 6.5,22.5 10,26 14.5,26 C 19,26 22.5,22.5 22.5,18 L 22.5,12 C 22.5,11.2 21.8,10.5 21,10.5 C 20.2,10.5 19.5,11.2 19.5,12 L 19.5,9 C 19.5,8.2 18.8,7.5 18,7.5 C 17.2,7.5 16.5,8.2 16.5,9 L 16.5,7 C 16.5,6.2 15.8,5.5 15,5.5 C 14.2,5.5 13.5,6.2 13.5,7 L 13.5,10 C 13.5,9.2 12.8,8.5 12,8.5 C 11.2,8.5 10.5,9.2 10.5,10 L 10.5,25.5
</Geometry>

<!-- 5. Delete (Fluted Trash Can with Lid & Handle) -->
<Geometry x:Key="IconDeleteTrash">
  M 7.5,9 H 24.5 M 13,9 V 6.5 C 13,5.7 13.7,5 14.5,5 H 17.5 C 18.3,5 19,5.7 19,6.5 V 9 M 9.5,11 L 11,24.5 C 11.1,25.4 11.8,26 12.7,26 H 19.3 C 20.2,26 20.9,25.4 21,24.5 L 22.5,11 M 14,14 V 21 M 18,14 V 21
</Geometry>

<!-- 6. Delete Completed (Trash Can with Centered Checkmark) -->
<Geometry x:Key="IconDeleteCompletedTrash">
  M 7.5,9 H 24.5 M 13,9 V 6.5 C 13,5.7 13.7,5 14.5,5 H 17.5 C 18.3,5 19,5.7 19,6.5 V 9 M 9.5,11 L 11,24.5 C 11.1,25.4 11.8,26 12.7,26 H 19.3 C 20.2,26 20.9,25.4 21,24.5 L 22.5,11 M 12.5,17.5 L 15,20.5 L 19.5,14.5
</Geometry>

<!-- 7. Options (10-Tooth Scalloped Cogwheel Gear) -->
<Geometry x:Key="IconGearClassic">
  M 16,5 C 17.3,5 17.7,6.3 19,6.7 C 20.3,7.1 21.2,6.1 22.3,6.7 C 23.4,7.3 23.3,8.7 24.2,9.5 C 25.1,10.3 26.5,10.2 27.1,11.3 C 27.7,12.4 26.7,13.3 27.1,14.6 C 27.5,15.9 28.5,16.5 28.5,17.8 C 28.5,19.1 27.5,19.7 27.1,21 C 26.7,22.3 27.7,23.2 27.1,24.3 C 26.5,25.4 25.1,25.3 24.2,26.1 C 23.3,26.9 23.4,28.3 22.3,28.9 C 21.2,29.5 20.3,28.5 19,28.9 C 17.7,29.3 17.3,30.6 16,30.6 C 14.7,30.6 14.3,29.3 13,28.9 C 11.7,28.5 10.8,29.5 9.7,28.9 C 8.6,28.3 8.7,26.9 7.8,26.1 C 6.9,25.3 5.5,25.4 4.9,24.3 C 4.3,23.2 5.3,22.3 4.9,21 C 4.5,19.7 3.5,19.1 3.5,17.8 C 3.5,16.5 4.5,15.9 4.9,14.6 C 5.3,13.3 4.3,12.4 4.9,11.3 C 5.5,10.2 6.9,10.3 7.8,9.5 C 8.7,8.7 8.6,7.3 9.7,6.7 C 10.8,6.1 11.7,7.1 13,6.7 C 14.3,6.3 14.7,5 16,5 Z M 16,13.6 A 4.2,4.2 0 1 0 16.01,13.6
</Geometry>

<!-- 8. Scheduler (Dual-Bell Alarm Clock with Clock Hands) -->
<Geometry x:Key="IconSchedulerClock">
  M 16,7.5 A 9.5,9.5 0 1 0 16.01,7.5 M 16,11.5 V 17 L 19.5,19 M 7.5,8.5 C 8.5,7 10,7.5 10.5,8.5 M 24.5,8.5 C 23.5,7 22,7.5 21.5,8.5 M 10,25.5 L 8,28 M 22,25.5 L 24,28
</Geometry>

<!-- 9. Start Queue (Stacked Cards + Down Arrow + Chevron) -->
<Geometry x:Key="IconQueueStartClassic">
  M 8,8 H 19 C 20.1,8 21,8.9 21,10 V 21 C 21,22.1 20.1,23 19,23 M 5,11 H 15 C 16.1,11 17,11.9 17,13 V 24 C 17,25.1 16.1,26 15,26 H 5 C 3.9,26 3,25.1 3,24 V 13 C 3,11.9 3.9,11 5,11 Z M 10,14.5 V 22.5 M 7,19.5 L 10,22.5 L 13,19.5 M 23.5,17.5 L 26.5,21 L 29.5,17.5
</Geometry>

<!-- 10. Stop Queue (Stacked Cards + Stop Square + Chevron) -->
<Geometry x:Key="IconQueueStopClassic">
  M 8,8 H 19 C 20.1,8 21,8.9 21,10 V 21 C 21,22.1 20.1,23 19,23 M 5,11 H 15 C 16.1,11 17,11.9 17,13 V 24 C 17,25.1 16.1,26 15,26 H 5 C 3.9,26 3,25.1 3,24 V 13 C 3,11.9 3.9,11 5,11 Z M 8,16 H 12 C 12.6,16 13,16.4 13,17 V 21 C 13,21.6 12.6,22 12,22 H 8 C 7.4,22 7,21.6 7,21 V 17 C 7,16.4 7.4,16 8,16 Z M 23.5,17.5 L 26.5,21 L 29.5,17.5
</Geometry>

<!-- 11. Grabber (Globe with Network Meridian & Step Nodes) -->
<Geometry x:Key="IconGrabberClassic">
  M 17,4 A 7.5,7.5 0 1 0 17.01,4 M 17,4 A 3.5,7.5 0 1 0 17.01,4 M 9.5,11.5 H 24.5 M 7,24 L 11,19.5 L 15,24 M 14,24 L 18,19.5 L 22,24
</Geometry>

<!-- 12. Tell a Friend (Contoured Thumbs Up Outline) -->
<Geometry x:Key="IconThumbsUpClassic">
  M 9.5,14 V 24.5 M 9.5,15.5 H 6.5 C 5.4,15.5 4.5,16.4 4.5,17.5 V 22.5 C 4.5,23.6 5.4,24.5 6.5,24.5 H 9.5 M 9.5,24.5 H 18.5 C 20.2,24.5 21.6,23.3 22,21.7 L 23.3,15.8 C 23.7,14.1 22.4,12.5 20.7,12.5 H 16.5 L 17.8,7.5 C 18.1,6.5 17.3,5.5 16.2,5.5 C 15.5,5.5 14.8,5.9 14.4,6.6 L 11,13.5 L 9.5,14
</Geometry>
```

---

### 4.3 Control Template & XAML Button Architecture (`MainWindow.xaml`)

```xml
<!-- Vertical Stacked Command Style for the 36px Toolbar -->
<Style x:Key="Command" TargetType="Button" BasedOn="{StaticResource ToolButton}">
  <Setter Property="Margin" Value="1,1,2,1"/>
  <Setter Property="Padding" Value="6,4,6,4"/>
  <Setter Property="MinHeight" Value="58"/>
  <Setter Property="MinWidth" Value="62"/>
  <Setter Property="HorizontalContentAlignment" Value="Center"/>
  <Setter Property="VerticalContentAlignment" Value="Center"/>
  <Setter Property="FontSize" Value="11.5"/>
  <Setter Property="FontWeight" Value="Medium"/>
  <Setter Property="Cursor" Value="Hand"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="Button">
        <Grid SnapsToDevicePixels="True">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" 
                  BorderBrush="{TemplateBinding BorderBrush}" 
                  BorderThickness="{TemplateBinding BorderThickness}" 
                  CornerRadius="{DynamicResource ControlRadius}"/>
          <Border x:Name="HoverOverlay" Background="{DynamicResource SurfaceHoverBrush}" 
                  CornerRadius="{DynamicResource ControlRadius}" Opacity="0" IsHitTestVisible="False"/>
          <StackPanel Orientation="Vertical" Margin="{TemplateBinding Padding}" 
                      HorizontalAlignment="Center" VerticalAlignment="Center">
            <Grid x:Name="GlyphContainer" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="0,0,0,3">
              <Path x:Name="Glyph" 
                    Data="{Binding Path=(local:Ui.Icon), RelativeSource={RelativeSource TemplatedParent}}" 
                    Stroke="{DynamicResource InkBrush}" 
                    Fill="Transparent" 
                    StrokeThickness="2.8" 
                    StrokeStartLineCap="Round" 
                    StrokeEndLineCap="Round" 
                    StrokeLineJoin="Round" 
                    Width="36" Height="36" 
                    Stretch="Uniform" 
                    HorizontalAlignment="Center" VerticalAlignment="Center" 
                    SnapsToDevicePixels="False"/>
            </Grid>
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True">
              <ContentPresenter.Resources>
                <Style TargetType="TextBlock">
                  <Setter Property="Foreground" Value="{DynamicResource InkBrush}"/>
                  <Setter Property="FontSize" Value="11.5"/>
                  <Setter Property="FontWeight" Value="Medium"/>
                  <Setter Property="TextAlignment" Value="Center"/>
                </Style>
              </ContentPresenter.Resources>
            </ContentPresenter>
          </StackPanel>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property="local:Ui.Icon" Value="{x:Null}">
            <Setter TargetName="GlyphContainer" Property="Visibility" Value="Collapsed"/>
          </Trigger>
          <Trigger Property="IsMouseOver" Value="True">
            <Setter TargetName="HoverOverlay" Property="Opacity" Value="1"/>
          </Trigger>
          <Trigger Property="IsPressed" Value="True">
            <Setter TargetName="Chrome" Property="Opacity" Value="0.75"/>
          </Trigger>
          <Trigger Property="IsEnabled" Value="False">
            <Setter TargetName="Chrome" Property="Opacity" Value="0.35"/>
            <Setter Property="Foreground" Value="{DynamicResource DisabledInkBrush}"/>
            <Setter TargetName="GlyphContainer" Property="Opacity" Value="0.35"/>
            <Setter Property="Cursor" Value="Arrow"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>

<!-- Hairline Separator between Logical Button Groups -->
<Style x:Key="CommandSeparator" TargetType="Border">
  <Setter Property="Width" Value="1"/>
  <Setter Property="Height" Value="42"/>
  <Setter Property="Margin" Value="4,0,4,0"/>
  <Setter Property="Background" Value="{DynamicResource BorderBrush}"/>
  <Setter Property="VerticalAlignment" Value="Center"/>
</Style>
```

---

## 5. Main Window Workspace Architecture & Data Flow

### 5.1 Top Command Toolbar XAML Layout
The top toolbar is embedded directly under the main menu bar, with an adaptive canvas background and non-overflowing wrap panel:

```xml
<Border DockPanel.Dock="Top" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,1" Padding="6,4" Background="{DynamicResource CanvasBrush}">
  <WrapPanel VerticalAlignment="Center">
    <Button Content="Add URL" Click="Add_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconAddUrlClassic}" AutomationProperties.Name="Add URL" ToolTip="Add a download from a link (Ctrl+N)"/>
    <Button x:Name="ResumeButton" Content="Resume" Click="Resume_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconResumeCircle}" AutomationProperties.Name="Resume" ToolTip="Resume the selected download"/>
    <Button x:Name="StopButton" Content="Stop" Click="Stop_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconStopCircle}" AutomationProperties.Name="Stop" ToolTip="Pause the selected download; it can be resumed later"/>
    <Button x:Name="TopStopAllButton" Content="Stop All" Click="PauseAll_Click" Style="{StaticResource Command}" IsEnabled="{Binding IsEnabled, ElementName=PauseAllButton}" local:Ui.Icon="{StaticResource IconStopAllHand}" AutomationProperties.Name="Stop All" ToolTip="Pause every active, queued and scheduled download"/>
    <Button x:Name="RemoveButton" Content="Delete" Click="Remove_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconDeleteTrash}" AutomationProperties.Name="Remove" ToolTip="Remove the selected download (Delete)"/>
    <Button x:Name="TopRemoveCompletedButton" Content="Delete Co..." Click="RemoveCompleted_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconDeleteCompletedTrash}" AutomationProperties.Name="Delete Completed" ToolTip="Remove finished downloads from the list"/>
    <Border Style="{StaticResource CommandSeparator}"/>
    <Button x:Name="Options" Content="Options" Click="Settings_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconGearClassic}" AutomationProperties.Name="Options" ToolTip="Application options and settings"/>
    <Button x:Name="ScheduleButton" Content="Scheduler" Click="Schedule_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconSchedulerClock}" AutomationProperties.Name="Scheduler" ToolTip="Schedule the selected download for later"/>
    <Border Style="{StaticResource CommandSeparator}"/>
    <Button x:Name="StartQueueButton" Content="Start Qu..." Click="StartQueue_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconQueueStartClassic}" AutomationProperties.Name="Start queue" ToolTip="Start downloading queued items"/>
    <Button x:Name="StopQueueButton" Content="Stop Qu..." Click="StopQueue_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconQueueStopClassic}" AutomationProperties.Name="Stop queue" ToolTip="Stop the queue and pause its active downloads"/>
    <Border Style="{StaticResource CommandSeparator}"/>
    <Button x:Name="GrabberButton" Content="Grabber" Click="Bulk_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconGrabberClassic}" AutomationProperties.Name="Grabber" ToolTip="Batch import / grab links"/>
    <Button x:Name="TellFriendButton" Content="Tell a Friend" Click="About_Click" Style="{StaticResource Command}" local:Ui.Icon="{StaticResource IconThumbsUpClassic}" AutomationProperties.Name="Tell a Friend" ToolTip="About N&amp;T"/>
  </WrapPanel>
</Border>
```

---

### 5.2 Decoupled UI Dispatcher & Event Buffering Architecture

To eliminate UI freezing during high-throughput downloads (e.g., 100+ MB/s over 32 concurrent connections), N&T implements a decoupled snapshot polling discipline:

```csharp
// Architecture from MainWindow.xaml.cs
// Engine Changed events arrive on thread-pool threads; MainWindow buffers them
// and commits UI mutations on a 500 ms DispatcherTimer.
private void SetupEngineEventBuffering()
{
    _updateTimer = new DispatcherTimer(DispatcherPriority.Normal)
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    _updateTimer.Tick += (s, e) => ApplyBufferedQueueUpdates();
    _updateTimer.Start();

    _manager.Changed += (jobId) =>
    {
        // Signal flag; do not invoke Dispatcher on every single network packet
        Interlocked.Exchange(ref _hasPendingChanges, 1);
    };
}

private void ApplyBufferedQueueUpdates()
{
    if (Interlocked.Exchange(ref _hasPendingChanges, 0) == 0) return;

    var snapshots = _manager.Jobs; // Detached immutable snapshots
    foreach (var snapshot in snapshots)
    {
        if (_rowLookup.TryGetValue(snapshot.Id, out var row))
        {
            // DownloadRow.Update only triggers PropertyChanged if observable values change
            row.Update(snapshot);
        }
        else
        {
            var newRow = new DownloadRow(snapshot);
            _rows.Add(newRow);
            _rowLookup[snapshot.Id] = newRow;
        }
    }
}
```

---

### 5.3 SmoothProgressBar Architecture (`src/NT.App/SmoothProgressBar.cs`)

Raw network chunk arrivals cause standard progress bars to jump erratically. `SmoothProgressBar` animates values over 250 ms using a quadratic ease-out curve:

```csharp
// From src/NT.App/SmoothProgressBar.cs
public static class SmoothProgressBar
{
    public static readonly DependencyProperty SmoothValueProperty =
        DependencyProperty.RegisterAttached(
            "SmoothValue",
            typeof(double),
            typeof(SmoothProgressBar),
            new PropertyMetadata(0.0, OnSmoothValueChanged));

    public static double GetSmoothValue(ProgressBar bar) => (double)bar.GetValue(SmoothValueProperty);
    public static void SetSmoothValue(ProgressBar bar, double value) => bar.SetValue(SmoothValueProperty, value);

    private static void OnSmoothValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar bar) return;
        double target = (double)e.NewValue;
        if (!double.IsFinite(target)) target = 0;
        target = Math.Clamp(target, bar.Minimum, bar.Maximum);

        if (bar.IsIndeterminate) return;

        double current = bar.Value;
        if (Math.Abs(current - target) < 0.1 || !SystemParameters.ClientAreaAnimation)
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null);
            bar.Value = target;
            return;
        }

        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(RangeBase.ValueProperty, anim);
    }
}
```

---

## 6. Dialogs & Modeless Windows: UI & Functional Logic

### 6.1 Add Download Dialog (`AddDownloadWindow.xaml.cs`)

The Add Download dialog handles URL ingestion, smart category routing, duplicate file checks, and directory browsing:

```csharp
// From src/NT.App/AddDownloadWindow.xaml.cs
private void UpdateDefaultPath()
{
    if (_pathEdited || _programmatic) return;
    try
    {
        _programmatic = true;
        string rawUrl = UrlInput.Text.Trim();
        string candidateName = NameInput.Text.Trim();
        
        if (string.IsNullOrEmpty(candidateName) && !string.IsNullOrEmpty(rawUrl))
        {
            if (rawUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                rawUrl.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                candidateName = "video.mp4"; // Auto-route YouTube links to Video
            }
            else
            {
                var uri = new Uri(rawUrl);
                candidateName = Path.GetFileName(uri.LocalPath);
            }
        }
        
        string folder = _pathResolver != null && !string.IsNullOrEmpty(candidateName)
            ? _pathResolver(candidateName)
            : _defaultDownloadDirectory;

        PathInput.Text = folder;
    }
    finally { _programmatic = false; }
}

private void Browse_Click(object sender, RoutedEventArgs e)
{
    string initial = !string.IsNullOrWhiteSpace(PathInput.Text) && Directory.Exists(PathInput.Text.Trim())
        ? PathInput.Text.Trim()
        : _defaultDownloadDirectory;

    var dialog = new Microsoft.Win32.OpenFolderDialog
    {
        Title = "Select download folder",
        InitialDirectory = initial
    };
    if (dialog.ShowDialog(this) == true)
    {
        PathInput.Text = dialog.FolderName;
        _pathEdited = true;
    }
}
```

---

### 6.2 Smart Category Helper (`src/NT.App/CategoryHelper.cs`)

Downloads are automatically sorted into dedicated subdirectories based on MIME types and file extensions:

```csharp
// From src/NT.App/CategoryHelper.cs
public static class CategoryHelper
{
    public enum Category { Programs, Compressed, Music, Video, Documents, General }

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".dmg", ".pkg", ".deb", ".rpm" };
    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz" };
    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt" };

    public static Category GetCategory(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return Category.General;
        if (ProgramExtensions.Contains(ext)) return Category.Programs;
        if (CompressedExtensions.Contains(ext)) return Category.Compressed;
        if (MusicExtensions.Contains(ext)) return Category.Music;
        if (VideoExtensions.Contains(ext)) return Category.Video;
        if (DocumentExtensions.Contains(ext)) return Category.Documents;
        return Category.General;
    }

    public static string GetDownloadPath(string fileName, AppSettings settings)
    {
        var cat = GetCategory(fileName);
        return GetCategoryFolder(cat, settings);
    }
}
```

---

## 7. Core Download Engine Mechanics & Segmentation

### 7.1 Multi-Connection Dynamic Segmentation Architecture

N&T achieves maximum network saturation through parallel byte-range requests (`bytes=X-Y`). The segment coordinator dynamically partitions pending byte intervals:

```csharp
// From src/NT.Core/SegmentScheduler.cs
private async Task DownloadSegmentsAsync(Work w, Probe probe, CancellationToken token)
{
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
    var running = new Dictionary<Segment, SegmentRunner>();

    while (true)
    {
        token.ThrowIfCancellationRequested();

        // 1. Clean up completed segment workers
        foreach (var pair in running.Where(p => p.Value.Task.IsCompleted).ToArray())
        {
            await pair.Value.Task.ConfigureAwait(false);
            running.Remove(pair.Key);
            pair.Value.Cancellation.Dispose();
        }

        // 2. Spawn workers for pending unstarted segments up to MaxSegments
        Segment[] pending;
        lock (gate)
        {
            pending = w.Segments.Where(s => s.Done < s.Length && !running.ContainsKey(s))
                               .Take(options.MaxSegments - running.Count)
                               .ToArray();
        }

        foreach (var s in pending)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            lock (gate) s.Active = true;
            var task = RunSegmentOwnedAsync(w, s, probe, cancellation.Token);
            running.Add(s, new SegmentRunner(task, cancellation));
        }

        if (running.Count == 0) return; // Download completed

        // 3. Dynamic Sub-segmentation: Split the largest remaining active segment
        Segment? candidate = null;
        lock (gate)
        {
            if (options.EnableDynamicSegmentation && options.MaxSegments > 1 && 
                running.Count < options.MaxSegments && w.Segments.Count < MaximumShards)
            {
                candidate = w.Segments
                    .Where(s => s.Active && (s.Length - s.Done) >= options.MinimumSegmentSize * 2)
                    .OrderByDescending(s => s.Length - s.Done)
                    .FirstOrDefault();
            }
        }

        if (candidate != null)
        {
            SplitSegment(w, candidate);
        }

        await Task.Delay(200, linked.Token).ConfigureAwait(false);
    }
}
```

---

## 8. Native Messaging Bridge & IPC Protocol

The browser extension communicates with the desktop app via an elevated Chromium native messaging executable (`NT.NativeHost.exe`) communicating over a local Windows named pipe:

```
┌─────────────────────────┐    Stdio Wire Frame     ┌─────────────────────────┐
│ Chromium Browser (MV3)  │ <=====================> │ NT.NativeHost.exe       │
└─────────────────────────┘  4-byte Length + JSON   └─────────────────────────┘
                                                                 ▲
                                                                 │ Named Pipe:
                                                                 │ NT.Downloads
                                                                 ▼
                                                    ┌─────────────────────────┐
                                                    │ NT.App (WPF Desktop)    │
                                                    └─────────────────────────┘
```

### 8.1 Wire Protocol Frame Format
- **Browser-to-Host**: 4-byte little-endian integer prefix representing frame length in bytes, followed by UTF-8 encoded JSON. Buffer capped at 64 KiB.
- **Pipe-to-App**: `NT.Downloads` (`CurrentUserOnly`). Single-line JSON terminated with `\n`.
- **Heartbeat & Keep-Alive Discipline**: While the user is deciding in `AddDownloadWindow`, `NT.NativeHost` emits `{"pending": true}` every 15 seconds to prevent browser process timeouts.

---

## 9. Chromium MV3 Extension UI & Sniffer Specification

### 9.1 CSS Design Tokens & Media Sniffer UI
Defined in `extension/popup.css`, the extension matches the desktop app's aesthetic:

```css
/* Ambient breathing connection dot animation from extension/popup.css */
.status-indicator.online .status-dot {
  background: #10b981;
  box-shadow: 0 0 5px rgba(16, 185, 129, 0.65);
  animation: ndm-pulse 2.5s infinite;
}

@keyframes ndm-pulse {
  0%, 100% { opacity: 1; transform: scale(1); }
  50% { opacity: 0.6; transform: scale(1.15); }
}
```

### 9.2 In-Page Floating Media Ribbon (`extension/content.js`)
The media ribbon attaches directly to HTML5 video elements (YouTube, Vimeo, etc.) within a closed Shadow DOM:

```javascript
// From extension/content.js
// Mounts inside a closed shadow root to isolate styles completely from host website CSS
const shadow = host.attachShadow({ mode: 'closed' });
shadow.innerHTML = `
  <style>
    .nt-ribbon {
      position: absolute;
      z-index: 2147483647;
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 12px;
      background: rgba(19, 25, 32, 0.94);
      border: 1px solid rgba(40, 51, 62, 0.85);
      border-radius: 6px;
      backdrop-filter: blur(8px);
      box-shadow: 0 4px 16px rgba(0,0,0,0.4);
      color: #f1f5f9;
      font: 12px/1.4 'Segoe UI', system-ui, sans-serif;
      cursor: move;
    }
    .nt-btn {
      background: #0f766e;
      color: #ffffff;
      border: none;
      padding: 4px 10px;
      border-radius: 4px;
      font-weight: 600;
      cursor: pointer;
    }
  </style>
  <div class="nt-ribbon" id="ribbonBar">
    <span class="nt-title">Download Video</span>
    <select id="qualitySelect"></select>
    <button class="nt-btn" id="dlBtn">Download</button>
  </div>
`;
```

---

## 10. State Persistence, Recovery & Process Isolation

### 10.1 Atomic JSON State Checkpoints
Queue mutations are persisted atomically to prevent file corruption during sudden system shutdowns:
1. State is serialized to a temporary buffer file (`queue.json.tmp`).
2. An exclusive lock (`queue.lock`) is acquired.
3. Win32 `File.Replace` atomically swaps `queue.json.tmp` into `queue.json`.
4. If deserialization fails upon restart, the engine fails closed and creates a backup (`queue.corrupt.json`), refusing to overwrite user state.

### 10.2 Environment Isolation via `NDM_STATE_DIR`
Setting `NDM_STATE_DIR=<directory>` isolates all instance state:
- Relocates settings, queue, and logs to the targeted directory.
- Derives a distinct single-instance mutex (`Local\NT.App.<hash12>`).
- Creates an isolated named pipe (`NT.Downloads.<hash12>`), enabling test runners and secondary instances to operate side-by-side with production without data collisions.

---

## 11. Verification Suites & Build Quality Gates

Every build is validated locally through `build.ps1` before publishing:

```powershell
# 1. Execute full verification gate and compile
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

# 2. Package standalone MSI installer
powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1
```

### 11.1 The Forbidden-Name Gate
The final step of `build.ps1` runs an exhaustive scan across all text files, markdown documents, source files, and first-party binaries. The build **immediately aborts** if any legacy third-party trademark names are detected. External software must always be referred to generically as *"legacy download managers"* or *"third-party torrent clients"*.
