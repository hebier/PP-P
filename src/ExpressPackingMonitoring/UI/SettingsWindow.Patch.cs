#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// PP-PATCH: a self-contained "Patch / 补丁" tab added to the settings window at runtime.
    ///
    /// Why build the UI in code instead of editing SettingsWindow.xaml / .resx:
    ///   - injecting into a 4000-line XAML is fragile across upstream releases;
    ///   - editing .resx would generate satellite assemblies (zh-Hans / ja-JP resource dlls),
    ///     which breaks the "replace ONE dll" deployment promise;
    ///   - a code-built tab needs zero upstream XAML changes and zero localization resources.
    ///
    /// Labels are bilingual (Chinese + English) so they stay readable in any UI language.
    /// Settings are written ONLY to pp-patch.json, never to the app's config.json.
    /// </summary>
    public partial class SettingsWindow
    {
        private TextBlock? _ppStatus;
        private bool _ppLoading;

        /// <summary>Called once from the constructor hook, right after InitializeComponent().</summary>
        public void PpPatchInstallSettings()
        {
            try
            {
                var tabs = FindName("SettingsTabControl") as TabControl;
                if (tabs == null)
                {
                    RuntimeLog.Warn("PP-PATCH", "SettingsTabControl not found - patch settings tab skipped");
                    return;
                }

                _ppLoading = true;
                var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };

                panel.Children.Add(MakeTitle("补丁设置 / Patch settings"));
                panel.Children.Add(MakeNote(
                    "这里只控制补丁自带的行为，不改官方设置。选项保存在 pp-patch.json，不影响 config.json。\n" +
                    "Patch-only options. Saved to pp-patch.json; the app's config.json is never touched."));

                // ---- preview mode -------------------------------------------------
                panel.Children.Add(MakeSection("预览画质 / Preview quality"));
                panel.Children.Add(MakeNote(
                    "只影响预览窗口的清晰度与流畅度，录像始终是摄像头原始 2K30，不会变糊。\n" +
                    "Affects the preview window only. Recording stays full 2K30."));

                var preview = new ComboBox
                {
                    Width = 320,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 8, 0, 4)
                };
                AddItem(preview, "Fluid", "流畅  1280×720 · 30 fps  (默认 / default)");
                AddItem(preview, "Balanced", "均衡  1280×720 · 15 fps");
                AddItem(preview, "Eco", "省资源  640×360 · 15 fps");
                preview.SelectedIndex = IndexOfMode(PpPatchConfig.Current.PreviewMode);
                preview.SelectionChanged += (_, __) =>
                {
                    if (_ppLoading) return;
                    if (preview.SelectedItem is ComboBoxItem item && item.Tag is string mode)
                    {
                        PpPatchConfig.SetPreviewMode(mode);
                        RefreshStatus();
                    }
                };
                panel.Children.Add(preview);

                // ---- pipeline mode ------------------------------------------------
                panel.Children.Add(MakeSection("录像链路 / Recording pipeline"));
                panel.Children.Add(MakeNote(
                    "直采 = 不碰像素、2K30 不卡不丢完整；原版 = 退回官方 rawvideo 录制（用于对比或排障）。切换后需重启软件。\n" +
                    "Direct = stream copy, no pixel work. Legacy = original pipeline. Restart required."));

                var pipeline = new ComboBox
                {
                    Width = 320,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 8, 0, 4)
                };
                AddItem(pipeline, "Direct", "直采（推荐 / recommended）");
                AddItem(pipeline, "Legacy", "原版 / Legacy");
                pipeline.SelectedIndex = PpPatchConfig.Current.IsDirect ? 0 : 1;
                pipeline.SelectionChanged += (_, __) =>
                {
                    if (_ppLoading) return;
                    if (pipeline.SelectedItem is ComboBoxItem item && item.Tag is string mode)
                    {
                        PpPatchConfig.SetPipelineMode(mode);
                        RefreshStatus();
                    }
                };
                panel.Children.Add(pipeline);

                // ---- live status --------------------------------------------------
                panel.Children.Add(MakeSection("当前状态 / Status"));
                _ppStatus = MakeNote("");
                panel.Children.Add(_ppStatus);
                RefreshStatus();

                var tab = new TabItem
                {
                    Header = "补丁 / Patch",
                    Content = new ScrollViewer
                    {
                        Content = panel,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    }
                };
                tabs.Items.Add(tab);
                _ppLoading = false;
            }
            catch (Exception ex)
            {
                _ppLoading = false;
                RuntimeLog.Warn("PP-PATCH", "patch settings tab failed: " + ex.Message);
            }
        }

        private void RefreshStatus()
        {
            if (_ppStatus == null) return;
            var o = PpPatchConfig.Current;
            var p = o.GetPreviewProfile();
            string mode = o.IsDirect ? "直采 / Direct" : "原版 / Legacy";
            string restart = o.IsDirect ? "" : "  (原版链路 / legacy pipeline)";
            _ppStatus.Text =
                $"链路: {mode}{restart}\n" +
                $"预览: {p.Width}×{p.Height} @ {(p.TakeEvery > 1 ? 15 : 30)} fps\n" +
                $"录像: {o.Width}×{o.Height} @ {o.Fps} fps (MJPEG 直封 / stream copy)\n" +
                $"滚动段: {o.ResolveRawSegmentDir()}\n" +
                $"按单文件: {o.ResolveOrderClipDir()}\n" +
                $"生效哨兵: 日志里应有 PP-PATCH: Direct pipeline active";
        }

        private static void AddItem(ComboBox box, string tag, string label) =>
            box.Items.Add(new ComboBoxItem { Tag = tag, Content = label });

        private static int IndexOfMode(string? mode) =>
            string.Equals(mode, "Eco", StringComparison.OrdinalIgnoreCase) ? 2 :
            string.Equals(mode, "Balanced", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        private TextBlock MakeTitle(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            return tb;
        }

        private TextBlock MakeSection(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 22, 0, 8)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return tb;
        }

        private TextBlock MakeNote(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 19,
                Margin = new Thickness(0, 0, 0, 6),
                MaxWidth = 780,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return tb;
        }
    }
}
