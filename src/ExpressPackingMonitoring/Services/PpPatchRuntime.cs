#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services
{
    /// <summary>
    /// PP-PATCH: global glue for the direct pipeline.
    ///
    /// Deliberately self-contained: it references NO MainViewModel / ScanRecord / VideoDatabase
    /// types. The three entry hooks only call Start / BeginOrder / EndOrder, and the UI / DB
    /// wiring is done by subscribing to the events below. That is why upstream refactors of the
    /// recording internals produce zero merge conflicts.
    ///
    /// The string "PP-PATCH: Direct pipeline active" in the runtime log is the second sentinel:
    /// if it is missing after swapping the dll, the app silently fell back to the legacy pipeline.
    /// </summary>
    public static class PpPatchRuntime
    {
        private static readonly object Gate = new object();
        private static readonly ConcurrentDictionary<string, DateTimeOffset> OpenOrders =
            new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        private static string? _lastOrderNo;
        private static AppConfig? _config;
        private static DirectCaptureRecordingService? _capture;
        private static ClipByOrderService? _clipper;
        private static MjpegArchiveService? _archive;
        private static Task? _cleanupTask;
        private static CancellationTokenSource? _cleanupCts;

        public static bool DirectEnabled => _config?.Patch?.IsDirect ?? false;
        public static PatchOptions Settings => _config?.Patch ?? new PatchOptions();
        public static DirectCaptureRecordingService? Capture => _capture;
        public static MjpegArchiveService? Archive => _archive;

        /// <summary>Fired when a per-order MKV is ready (1-3 s after scan-stop). Subscribe to update DB FilePath.</summary>
        public static event Action<OrderClipResult>? OrderClipReady;

        /// <summary>Fired with a complete JPEG frame for preview. Decode on a background thread, never here.</summary>
        public static event Action<byte[]>? PreviewFrame;

        /// <summary>
        /// Call from the StartCamera() hook:  PpPatchRuntime.Start(Config);
        /// Pass the app's own AppConfig instance so config.json keeps controlling the patch.
        /// </summary>
        public static void Start(AppConfig config)
        {
            lock (Gate)
            {
                if (_capture != null) return;                 // already running
                _config = config ?? throw new ArgumentNullException(nameof(config));
                var opt = config.Patch ?? new PatchOptions();

                if (!opt.IsDirect)
                {
                    RuntimeLog.Info("PP-PATCH", "Direct pipeline disabled (VideoPipelineMode=Legacy)");
                    return;
                }

                Directory.CreateDirectory(opt.ResolveRawSegmentDir());
                Directory.CreateDirectory(opt.ResolveOrderClipDir());

                _capture = new DirectCaptureRecordingService(opt);
                _clipper = new ClipByOrderService(opt);
                _archive = new MjpegArchiveService(opt);

                _capture.JpegFrame += OnPreviewFrame;
                _capture.Start();
                _archive.Start();
                StartCleanupLoop(opt);

                RuntimeLog.Info("PP-PATCH", "PP-PATCH: Direct pipeline active");
            }
        }

        /// <summary>Call from the scan-START hook with the scanned order number.</summary>
        public static void BeginOrder(string orderNo)
        {
            if (!DirectEnabled || string.IsNullOrWhiteSpace(orderNo)) return;
            OpenOrders[orderNo] = DateTimeOffset.UtcNow;
            _lastOrderNo = orderNo;
            _archive?.NotifyRecordingStarted();
            RuntimeLog.Info("PP-PATCH", $"BeginOrder {orderNo}");
        }

        /// <summary>
        /// Call from the scan-STOP hook. The order no. may be omitted - the last BeginOrder
        /// value is reused, so the hook does not need to know any upstream field name.
        /// Cutting runs off the UI thread.
        /// </summary>
        public static void EndOrder(string? orderNo = null)
        {
            if (!DirectEnabled) return;
            string key = string.IsNullOrWhiteSpace(orderNo) ? (_lastOrderNo ?? "") : orderNo!;
            if (string.IsNullOrWhiteSpace(key))
            {
                RuntimeLog.Warn("PP-PATCH", "EndOrder called with no order number");
                return;
            }
            if (!OpenOrders.TryRemove(key, out var startUtc))
            {
                RuntimeLog.Warn("PP-PATCH", $"EndOrder without BeginOrder: {key}");
                return;
            }

            _archive?.NotifyRecordingStopped();
            var endUtc = DateTimeOffset.UtcNow;
            var window = new OrderWindow(key, startUtc, endUtc);
            var clipper = _clipper;
            var capture = _capture;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (clipper == null || capture == null) return;
                    var result = await clipper.ClipAsync(window, capture, CancellationToken.None);

                    // Playable immediately; the DB subscriber decides how to persist FilePath.
                    OrderClipReady?.Invoke(result);

                    // Offline H.264 conversion for size + burned-in order no./time.
                    _archive?.Enqueue(result);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("PP-PATCH", $"clip failed for {key}", ex);
                }
            });
        }

        public static void Stop()
        {
            lock (Gate)
            {
                _cleanupCts?.Cancel();
                if (_capture != null) _capture.JpegFrame -= OnPreviewFrame;
                try { _archive?.Dispose(); } catch { }
                try { _capture?.Dispose(); } catch { }
                _capture = null;
                _clipper = null;
                _archive = null;
                _config = null;
                OpenOrders.Clear();
                RuntimeLog.Info("PP-PATCH", "Direct pipeline stopped");
            }
        }

        private static void OnPreviewFrame(byte[] jpeg)
        {
            try { PreviewFrame?.Invoke(jpeg); }
            catch (Exception ex) { RuntimeLog.Warn("PP-PATCH", $"preview subscriber failed: {ex.Message}"); }
        }

        /// <summary>
        /// Rolling segments are kept for >=48 h, which is far longer than any in-flight clip or
        /// the 24 h archive safety net. Deleting only past that window makes reference counting
        /// unnecessary while still guaranteeing the direct writer never runs out of disk.
        /// </summary>
        private static void StartCleanupLoop(PatchOptions opt)
        {
            _cleanupCts = new CancellationTokenSource();
            var ct = _cleanupCts.Token;
            _cleanupTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromHours(1), ct); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        var capture = _capture;
                        if (capture == null) continue;
                        int deleted = capture.CleanupOldSegments(null);
                        if (deleted > 0) RuntimeLog.Info("PP-PATCH", $"cleaned {deleted} old rolling segments");
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Warn("PP-PATCH", $"segment cleanup failed: {ex.Message}");
                    }
                }
            }, CancellationToken.None);
        }
    }
}
