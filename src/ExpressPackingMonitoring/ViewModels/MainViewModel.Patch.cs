#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// PP-PATCH: preview wiring for the direct (-c copy) pipeline.
    ///
    /// The legacy pipeline publishes frames through AForge NewFrame -> Mat -> WriteableBitmap.
    /// In Direct mode AForge is deliberately NOT started (UVC allows only one process to open
    /// the camera), so without this file the preview control stays black even though recording
    /// works. DirectCaptureRecordingService emits complete JPEG bytes on a background channel;
    /// here we downscale-decode them with WIC and assign VideoFrame on the UI dispatcher.
    ///
    /// Cost control:
    ///   - DecodePixelWidth performs the downscale INSIDE the JPEG decoder (no full 2K frame
    ///     ever exists in managed memory), exactly like ImreadModes.ReducedColor2.
    ///   - Only one frame may be in flight at a time; if the UI is busy, new frames are DROPPED.
    ///     Nothing here can back-pressure the stdout reader, so recording is unaffected.
    ///   - The decoded BitmapImage is Frozen, so it can cross threads safely.
    ///
    /// This is a partial class: no upstream file is modified, and no upstream business type
    /// other than the already-public VideoFrame / SuppressVideoPreviewUpdates is referenced.
    /// </summary>
    public partial class MainViewModel
    {
        private int _ppPreviewBusy;
        private int _ppPreviewAttached;
        private int _ppPreviewErrors;

        /// <summary>Called once from the StartCamera() entry hook.</summary>
        public void PpPatchAttachPreview()
        {
            if (Interlocked.Exchange(ref _ppPreviewAttached, 1) != 0) return;
            PpPatchRuntime.PreviewFrame += OnPpPatchPreviewFrame;
            RuntimeLog.Info("PP-PATCH", "preview attached to VideoFrame");
        }

        private void OnPpPatchPreviewFrame(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length < 4) return;
            if (SuppressVideoPreviewUpdates) return;
            if (Interlocked.Exchange(ref _ppPreviewBusy, 1) != 0) return;  // drop, never queue

            _ = Task.Run(() =>
            {
                try
                {
                    var profile = PpPatchConfig.Current.GetPreviewProfile();
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.None;
                    image.DecodePixelWidth = Math.Max(64, profile.Width);
                    image.StreamSource = new MemoryStream(jpeg, writable: false);
                    image.EndInit();
                    image.Freeze();

                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null) { ReleasePreviewSlot(); return; }

                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { VideoFrame = image; }
                        catch (Exception ex) { LogPreviewError(ex); }
                        finally { ReleasePreviewSlot(); }
                    }), DispatcherPriority.Render);
                }
                catch (Exception ex)
                {
                    LogPreviewError(ex);
                    ReleasePreviewSlot();
                }
            });
        }

        private void ReleasePreviewSlot() => Interlocked.Exchange(ref _ppPreviewBusy, 0);

        private void LogPreviewError(Exception ex)
        {
            int n = Interlocked.Increment(ref _ppPreviewErrors);
            if (n <= 3 || n % 100 == 0)
                RuntimeLog.Warn("PP-PATCH", $"preview decode failed ({n}): {ex.Message}");
        }
    }
}
