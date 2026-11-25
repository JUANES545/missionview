using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    public partial class MainWindow : Window
    {
        private WindowAnimationManager animationManager = null!;
        private ThumbnailManager thumbnailManager = null!;
        private WindowEnumerator windowEnumerator = null!;
        private WindowSwitcher? windowSwitcher;
        private WallpaperManager wallpaperManager = null!;
        private HotCornerManager hotCornerManager = null!;
        private DesktopCaptureManager desktopCaptureManager = null!;
        private bool useDesktopCapture = true;

        private List<WindowThumb> windowThumbs = new List<WindowThumb>();
        private readonly MainWindow coordinator;
        private readonly ApplicationServices services;
        private OverviewSession session => services.Overview;
        private readonly List<MainWindow> views;
        private readonly ScatterInteraction scatterInteraction;
        private readonly WindowActivation windowActivation;
        private readonly DesktopToggle desktopToggle;
        private MainWindow? activeView;
        private MonitorHelper.MonitorBounds monitor = null!;
        private List<MonitorHelper.MonitorBounds> monitors = new();
        private bool showMinimizedWindows = true;
        private WindowTransfer windowTransfer => services.Transfer;
        private WindowMoveOperation moveOperation => services.Move;
        private CancellationTokenSource? moveCancellation;
        private readonly WindowDragGesture drag = new();
        private Border? dragCapture;
        private MainWindow? dragSource;
        private WindowThumb? dragThumb;
        private double dragGrabX, dragGrabY;
        private DwmDragPreview? dragPreview;
        private DragPreviewGeometry? dragGeometry;
        private bool movingWindow;
        private bool reportingMoveError;
        private int topologyCancellationGeneration = -1;
        private bool previewExpanded;
        private readonly HeldPreviewState previewKey;
        private double monitorPixelWidth;
        private double monitorPixelHeight;
        private POINT lastHoverPosition;
        private IntPtr selectedWindowHandle => session.Selection;
        private bool isScatterActive { get => session.Opening; set => session.Opening = value; }
        private bool isTransitioning { get => session.Transitioning; set => session.Transitioning = value; }
        private object transitionLock => coordinator;
        private bool AnyAnimating => views.Any(v => v.animationManager != null && v.animationManager.IsAnimating);
        private bool IsCoordinator => ReferenceEquals(this, coordinator);

        private IntPtr hookID = IntPtr.Zero;
        private LowLevelKeyboardProc hookCallback = null!;
        private readonly OverviewShortcutInput shortcutInput;

        private HwndSource? hwndSource;
        private IntPtr hostHandle, chromeHandle;
        private DispatcherTimer? wallpaperCheckTimer;
        private string? lastWallpaperPath;


        // While idle with no input, Windows power-throttles the process and a 250ms
        // animation finishes before the CPU ramps back up. Boost priority while the
        // overlay is open and restore the original class when it closes.
        private readonly System.Diagnostics.ProcessPriorityClass normalPriorityClass =
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass;

        private bool timerPeriodActive;

        // Working-set floor while the overlay is open (see ApplyWorkingSetFloor).
        private UIntPtr origWsMin;
        private UIntPtr origWsMax;
        private ulong wsFloorMinBytes;
        private bool wsFloorApplied;

        private AppSettings settings;

        // Effective DPI scale (dpi/96) of the monitor the overlay is currently on.
        // Layout/DWM coordinates are physical pixels; WPF children are DIPs (#10).
        private double currentDpiScale = 1.0;

        private System.Windows.Forms.NotifyIcon? trayIcon;
        private System.Drawing.Icon? trayImage;
        private OverviewChromeWindow chromeWindow = null!;
        private bool isClosing;
        private bool chromeClosed;

        public MainWindow() : this(null) { }

        private MainWindow(MainWindow? parent)
        {
            coordinator = parent ?? this;
            services = parent?.services ?? ((App)Application.Current).Services;
            views = parent?.views ?? new List<MainWindow> { this };
            scatterInteraction = session.Interaction;
            windowActivation = services.Activation;
            shortcutInput = services.Shortcut;
            desktopToggle = services.Desktop;
            previewKey = session.PreviewKey;
            InitializeComponent();
            ConfigureSearchInput();
            AccessKeyManager.Register("S", DesktopButton);
            RenderCanvas.Children.Remove(ChromeCanvas);
            chromeWindow = new OverviewChromeWindow(this, ChromeCanvas);
            ConfigureFileDragInput();
            chromeWindow.PreviewKeyDown += Window_KeyDown;
            chromeWindow.PreviewKeyUp += Window_KeyUp;
            chromeWindow.Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!scatterInteraction.IsOwnWindow(GetForegroundWindow()))
                {
                    coordinator.CancelDrag();
                    foreach (var view in views) view.ResetPreview();
                    coordinator.TryExposeCloseAttention();
                }
            }));
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            chromeWindow.Closed += (_, _) =>
            {
                chromeClosed = true;
                if (!isClosing) coordinator.Close();
            };
            LocationChanged += (_, _) => SyncChromeToHost();
            SizeChanged += (_, _) => SyncChromeToHost();
            IsVisibleChanged += (_, _) => { if (!IsVisible && !chromeClosed) chromeWindow.Hide(); };

            settings = services.Settings;

            // Covers the plain .exe release too: registry follows settings (#15).
            if (IsCoordinator)
            {
                try { StartupRegistration.Apply(settings.RunOnStartup); }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or InvalidOperationException)
                {
                    ReportSettingsError(error);
                }
                SetupTrayIcon();
            }

            this.Topmost = true;
            this.ShowActivated = true;
            this.Focusable = true;
            this.ShowInTaskbar = false;
            this.WindowStyle = WindowStyle.None;
            this.ResizeMode = ResizeMode.NoResize;
            this.AllowsTransparency = false;

            this.Width = SystemParameters.PrimaryScreenWidth;
            this.Height = SystemParameters.PrimaryScreenHeight - 1;
            this.Left = 0;
            this.Top = 0;

            desktopCaptureManager = new DesktopCaptureManager(BackgroundImage, this);
            wallpaperManager = new WallpaperManager(BackgroundImage, this);
            useDesktopCapture = parent?.useDesktopCapture ?? true;

            this.Loaded += OnWindowLoaded;

            if (IsCoordinator)
            {
                hookCallback = HookCallback;
                hookID = SetHook(hookCallback);
                hotCornerManager = new HotCornerManager(settings, async () =>
                {
                    await Dispatcher.InvokeAsync(async () => await ToggleScatterAsync());
                });
                hotCornerManager.Start();
            }
        }

        #region Initialization

        private void OnWindowLoaded(object? sender, RoutedEventArgs e)
        {
            InitializeRendering();
            if (!scatterInteraction.IsOpen) this.Visibility = Visibility.Hidden;
        }

        private void InitializeRendering()
        {
            if (animationManager != null) return;
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();

            animationManager = new WindowAnimationManager(windowThumbs);
            thumbnailManager = new ThumbnailManager(ScatterCanvas, windowThumbs, helper.Handle, OnWindowClicked,
                handle =>
                {
                    if (!BlocksThumbnailInput) OnWindowHovered(handle);
                },
                selectionBorderColor: () => settings.SelectionBorderColor, effectsEnabled: () => !settings.ReduceAnimations);
            RefreshOverviewPresentation(SystemParameters.HighContrast, SystemParameters.ClientAreaAnimation);
            thumbnailManager.ConfigureInput = ConfigureThumbnailInput;
            hostHandle = helper.Handle;
            chromeHandle = new WindowInteropHelper(chromeWindow).EnsureHandle();
            scatterInteraction.RegisterSurface(hostHandle);
            scatterInteraction.RegisterSurface(chromeHandle);
            windowEnumerator = new WindowEnumerator(scatterInteraction.IsOwnWindow);

            animationManager.OnScatterComplete = () =>
            {
                try
                {
                    if (scatterInteraction.IsOpen && !isTransitioning && !coordinator.drag.IsPressed)
                    {
                        thumbnailManager.CompleteTransition(currentDpiScale, selectedWindowHandle);
                        if (minimizedStrip != null)
                        {
                            minimizedStrip.Visibility = Visibility.Visible;
                            minimizedStrip.SetSelected(selectedWindowHandle);
                        }
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException)
                {
                    AbortOverview(error);
                    return;
                }
                // Let FinishAnimation complete its keep-alive setup before
                // starting a return animation requested while opening.
                Dispatcher.BeginInvoke(new Action(coordinator.TryDismissScatter));
            };

            if (!useDesktopCapture)
            {
                wallpaperManager.SetWallpaperBackground();
                SetupWallpaperMonitoring();
            }

        }

        #endregion

        #region Wallpaper Monitoring

        private void SetupWallpaperMonitoring()
        {
            wallpaperCheckTimer = new DispatcherTimer();
            wallpaperCheckTimer.Interval = TimeSpan.FromSeconds(2);
            wallpaperCheckTimer.Tick += (s, e) =>
            {
                string? currentPath = wallpaperManager.GetWallpaperPath();
                if (currentPath != lastWallpaperPath && !string.IsNullOrEmpty(currentPath))
                {
                    lastWallpaperPath = currentPath;
                    wallpaperManager.SetWallpaperBackground();
                }
            };
            wallpaperCheckTimer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            hwndSource?.AddHook(WndProc);

            var hwnd = new WindowInteropHelper(this).Handle;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
            InitializeOverviewHotkeys(hwnd);

            if (!useDesktopCapture)
                lastWallpaperPath = wallpaperManager.GetWallpaperPath();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((scatterInteraction.IsOpen || coordinator.IsVisible) && (msg == WM_DISPLAYCHANGE ||
                (msg == WM_DPICHANGED && !isScatterActive) ||
                (msg == WM_SETTINGCHANGE && wParam.ToInt64() == 0x002F)))
                coordinator.QueueTopologyCancellation();
            if (msg == WM_SETTINGCHANGE)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    string? newPath = wallpaperManager.GetWallpaperPath();
                    if (newPath != lastWallpaperPath)
                    {
                        lastWallpaperPath = newPath;
                        wallpaperManager.SetWallpaperBackground();
                    }
                }), DispatcherPriority.Background);
            }

            return IntPtr.Zero;
        }

        #endregion

        #region Keyboard Hook

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule?.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                bool down = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool up = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;
                if (down || up)
                {
                    int key = Marshal.ReadInt32(lParam);
                    bool injected = (Marshal.ReadInt32(lParam, 8) & 0x10) != 0;
                    bool recording = false;
                    QueryShortcutRecording(ref recording);
                    var input = shortcutInput.Process(key, down, recording, overviewHotkeys, injected);
                    if (recording && !injected)
                        RecordOverviewShortcutKey(key, input.Modifiers, down);
                    if (!injected && !recording && down && key == VK_ESCAPE && fileDrag is { Cancelled: false } canceled)
                    {
                        canceled.Cancel();
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (ReferenceEquals(fileDrag, canceled)) DismissFileDrag(null);
                        }));
                    }
                    if (input.Suppress)
                    {
                        var fileDragAtPress = input.AppExpose ? null : fileDrag;
                        bool primaryHeld = !input.AppExpose && input.Activate && !drag.IsPressed && IsFileDragButtonDown();
                        if (input.MaskMenus && !primaryHeld && fileDrag == null)
                            KeyboardMenuMask.TrySend(SendInput);
                        if (input.Activate)
                        {
                            int revision = overviewHotkeys?.Revision ?? -1;
                            int generation = scatterInteraction.Generation;
                            var hover = fileDragAtPress?.Current;
                            this.Dispatcher.BeginInvoke(new Action(async () =>
                            {
                                if (isClosing || overviewHotkeys?.Revision != revision) return;
                                if (input.AppExpose) await ToggleAppExposeAsync(input.Foreground, input.ApplicationWindow, input.CaptureError);
                                else if (drag.IsPressed) await ToggleScatterAsync();
                                else if (fileDragAtPress != null) ConfirmFileDrag(fileDragAtPress, hover, primaryHeld);
                                else if (primaryHeld)
                                {
                                    if (generation == scatterInteraction.Generation && !scatterInteraction.IsOpen &&
                                        !desktopToggle.RestorePending)
                                        await ToggleScatterAsync(openingWithFileDrag: true);
                                }
                                else if (fileDrag == null) await ToggleScatterAsync();
                            }));
                        }
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(hookID, nCode, wParam, lParam);
        }

        #endregion

        #region Event Handlers

        private void ScatterCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Thumbnail borders handle their own clicks. Only bare canvas hits
            // dismiss the overview, never a click meant to select a window.
            if (!ReferenceEquals(e.OriginalSource, ScatterCanvas)) return;
            e.Handled = true;
            RequestDismissScatter();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (IsCoordinator) StopCloseObservation();
            AccessKeyManager.Unregister("S", DesktopButton);
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            if (IsCoordinator)
            {
                scatterInteraction.TryBeginClose(null, out _);
                CancelDrag();
                CloseMoveMenus();
                FullCleanup();
            }
            else CleanupView();
            scatterInteraction.UnregisterSurface(hostHandle);
            scatterInteraction.UnregisterSurface(chromeHandle);
            if (!chromeClosed) chromeWindow.Close();
            if (!IsCoordinator)
            {
                hwndSource?.RemoveHook(WndProc);
                wallpaperCheckTimer?.Stop();
                base.OnClosed(e);
                return;
            }
            settingsWindow?.Close();
            updateController?.Dispose();
            trayIcon?.ContextMenuStrip?.Dispose();
            try { trayIcon?.Dispose(); } catch { }
            trayIcon = null;
            trayImage?.Dispose();
            trayImage = null;
            RestoreWorkingSet();
            hwndSource?.RemoveHook(WndProc);
            wallpaperCheckTimer?.Stop();
            if (timerPeriodActive)
            {
                timeEndPeriod(1);
                timerPeriodActive = false;
            }
            SetThreadExecutionState(ES_CONTINUOUS);
            hotCornerManager?.Stop();
            desktopCaptureManager?.Cleanup();

            if (hookID != IntPtr.Zero)
            {
                if (UnhookWindowsHookEx(hookID)) hookID = IntPtr.Zero;
                else System.Diagnostics.Trace.TraceError($"Windows could not release the overview keyboard handler: {Marshal.GetLastWin32Error()}.");
            }
            if (overviewHotkeys?.Release() == false)
                System.Diagnostics.Trace.TraceWarning("Windows could not release all overview shortcuts during shutdown.");

            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = normalPriorityClass;
            }
            catch { }

            base.OnClosed(e);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            isClosing = !e.Cancel;
        }

        private void SyncChromeToHost()
        {
            if (isClosing || chromeClosed || !chromeWindow.IsVisible) return;
            IntPtr host = new WindowInteropHelper(this).Handle;
            if (!GetWindowRect(host, out var bounds))
            {
                AbortOverview(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not read the thumbnail host bounds."));
                return;
            }
            double scale = MonitorHelper.GetDpiScaleAtPoint(
                bounds.Left + (bounds.Right - bounds.Left) / 2,
                bounds.Top + (bounds.Bottom - bounds.Top) / 2);
            try
            {
                chromeWindow.ShowAt(bounds.Left, bounds.Top, bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top, scale, activate: false);
                currentDpiScale = scale;
                thumbnailManager?.SyncClickBordersToThumbs(scale);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                AbortOverview(ex);
            }
        }

        private void AbortOverview(Exception error)
        {
            if (!IsCoordinator) { coordinator.AbortOverview(error); return; }
            System.Diagnostics.Trace.TraceError($"Overview closed after a composition error: {error}");
            layoutCalculators.Clear();
            CancelDrag();
            HideAllViews();
            scatterInteraction.TryBeginClose(null, out _);
            FullCleanup();
            isTransitioning = false;
            isScatterActive = false;
            if (!isClosing)
                MessageBox.Show(this, "Windows could not display the overview controls.", "MissionView",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
                RefreshOverviewPresentation(SystemParameters.HighContrast, SystemParameters.ClientAreaAnimation);
        }

        #endregion

        #region Core Scatter Logic

        private bool CanActivateScatter()
        {
            lock (transitionLock)
            {
                // The overlay being visible means a scatter session is already
                // open: flags alone can't tell, since they reset once the opening
                // animation finishes. Without this, a hot-corner dwell or hotkey
                // re-press while open re-ran the whole scatter on top of itself.
                return !isScatterActive &&
                       !animationManager.IsAnimating &&
                       !isTransitioning &&
                       this.Visibility != Visibility.Visible;
            }
        }

        private async System.Threading.Tasks.Task ToggleScatterAsync(bool openingWithFileDrag = false)
        {
            if (!IsCoordinator) { await coordinator.ToggleScatterAsync(openingWithFileDrag); return; }
            if (animationManager == null || isTransitioning || movingWindow) return;
            if (drag.IsPressed) { CancelDrag(); return; }
            if (fileDrag != null) { DismissFileDrag(null); return; }
            if (desktopToggle.RestorePending)
            {
                RequestDesktopToggle();
                return;
            }
            if (isScatterActive || this.Visibility == Visibility.Visible)
            {
                scatterInteraction.RequestConfirmation();
                TryDismissScatter();
            }
            else if (CanActivateScatter())
                await StartScatterAsync(openingWithFileDrag: openingWithFileDrag);
        }

        private void RequestDismissScatter()
        {
            if (coordinator.reportingMoveError) return;
            if (coordinator.windowClose.HasPending) { coordinator.ReturnToPendingClose(); return; }
            if (coordinator.movingWindow) { coordinator.CancelSessionImmediately(); return; }
            if (isTransitioning || (!isScatterActive && this.Visibility != Visibility.Visible)) return;
            scatterInteraction.RequestDismiss();
            TryDismissScatter();
        }

        private void TryDismissScatter()
        {
            bool busy = isScatterActive || AnyAnimating || isTransitioning || coordinator.movingWindow;
            switch (session.TakeCloseRequest(busy, Visibility == Visibility.Visible))
            {
                case OverviewCloseRequest.ConfirmSelection: OnWindowClicked(selectedWindowHandle); break;
                case OverviewCloseRequest.Cancel: CleanupAndClose(); break;
            }
        }

        private async System.Threading.Tasks.Task StartScatterAsync(AppExposeRequest? request = null,
            bool openingWithFileDrag = false)
        {
            if (!CanActivateScatter()) return;
            isScatterActive = true;
            int generation = 0;
            try
            {
                appExposeRequest = request;
                var previous = request?.Foreground ?? windowActivation.Capture(GetForegroundWindow());
                IntPtr previousWindow = previous.Handle;
                scatterInteraction.BeginSession(previous);
                generation = scatterInteraction.Generation;
                var pt = ReadPointer();
                var pointerMonitor = MonitorHelper.GetMonitorFromPoint(pt.X, pt.Y);
                InitializeOverviewViews(MonitorHelper.GetMonitors(), pointerMonitor.Handle);
                if (openingWithFileDrag) BeginFileDrag(pt);
                lastHoverPosition = pt;
                previewKey.Release();
                try
                {
                    System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                        System.Diagnostics.ProcessPriorityClass.AboveNormal;
                }
                catch { }

                if (!timerPeriodActive)
                {
                    timeBeginPeriod(1);
                    timerPeriodActive = true;
                }
                SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

                var sourceOrder = WindowStacking.Capture();
                var windows = EnumerateOverviewWindows();
                IntPtr desktopSource = views.Any(v => v.useDesktopCapture) ? DesktopCaptureManager.GetDesktopWindowHandle() : IntPtr.Zero;
                foreach (var view in views)
                {
                    await view.PrepareViewAsync(sourceOrder, desktopSource, windows[view.monitor.Handle]);
                    if (isClosing || !scatterInteraction.IsOpen || generation != scatterInteraction.Generation) return;
                }
                if (!MonitorHelper.SameTopology(monitors, MonitorHelper.GetMonitors()))
                {
                    CancelForTopologyChange();
                    return;
                }
                foreach (var view in views) view.ShowView();
                activeView = this;
                SelectWindow(fileDrag != null ? IntPtr.Zero :
                    OverviewPreview.InitialSelection(previousWindow, AllThumbs().Select(t => t.WindowHandle)));
                activeView!.FocusOverviewInput();
                ApplyWorkingSetFloor();
                foreach (var view in views)
                    view.thumbnailManager.BeginTransition(view.animationManager.StartScatterAnimation);
            }
            catch (Exception ex)
            {
                AbortOverview(ex);
            }
            finally
            {
                if (generation == scatterInteraction.Generation || !scatterInteraction.IsOpen)
                    isScatterActive = false;
                if (!isClosing && scatterInteraction.IsOpen && generation == scatterInteraction.Generation)
                    TryDismissScatter();
            }
        }

        #endregion

        #region Window Click Handling

        private void OnWindowClicked(IntPtr windowHandle)
        {
            if (BlocksThumbnailInput) return;
            var thumb = coordinator.AllThumbs().FirstOrDefault(t =>
                t.WindowHandle == windowHandle && !t.IsFiltered);
            if (thumb != null) coordinator.CloseAllViews(thumb.Identity);
        }

        private void OnSwitchComplete(WindowActivationTarget target, bool restore, bool wasMaximized)
        {
            if (isClosing) return;
            windowActivation.CloseOverviewAndActivate(target, restore, wasMaximized, scatterInteraction.Surfaces, HideAllViews);

            QueueOverviewCleanup();
        }

        private void QueueOverviewCleanup()
        {
            int generation = scatterInteraction.Generation;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (isClosing || generation != scatterInteraction.Generation) return;
                try
                {
                    FullCleanup();
                }
                finally
                {
                    lock (transitionLock)
                    {
                        isTransitioning = false;
                        isScatterActive = false;
                    }
                }
            }), DispatcherPriority.Background);
        }

        #endregion

        #region Tray Icon & Empty-State Toast

        private void RequestDesktopToggle()
        {
            if (!desktopToggle.TryToggle(DesktopToggle.ToggleShell))
                MessageBox.Show(this, "Windows could not toggle the desktop.", "MissionView",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void DesktopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsCoordinator) { coordinator.DesktopButton_Click(sender, e); return; }
            if (isClosing || isScatterActive || isTransitioning || AnyAnimating || movingWindow) return;
            if (!scatterInteraction.TryBeginClose(null, out _)) return;
            isTransitioning = true;
            try
            {
                if (!desktopToggle.CompleteOverviewExit(
                    () =>
                    {
                        CancelDrag();
                        foreach (var view in views)
                        {
                            view.animationManager.StopAllAnimations();
                            previewKey.Release();
                            view.previewExpanded = false;
                            view.thumbnailManager.BeginTransition(() =>
                            {
                                view.thumbnailManager.SetPreviewAnimating(true);
                                view.ChromeCanvas.Visibility = Visibility.Hidden;
                            });
                        }
                    }, HideAllViews, DesktopToggle.ToggleShell, QueueOverviewCleanup))
                    MessageBox.Show(this, "Windows could not toggle the desktop.", "MissionView",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AbortOverview(ex);
            }
        }

        /// <summary>
        /// Keeps the app recallable while hidden: tray icon re-triggers scatter on
        /// left double-click and exposes preferences and app actions.
        /// </summary>
        private void SetupTrayIcon()
        {
            try
            {
                using var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("MissionView.TrayIcon.ico")
                    ?? throw new InvalidOperationException("The MissionView tray icon resource is missing.");
                using var sourceIcon = new System.Drawing.Icon(iconStream);
                trayImage = (System.Drawing.Icon)sourceIcon.Clone();
                trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Text = "MissionView",
                    Icon = trayImage,
                    Visible = true
                };

                var menu = new System.Windows.Forms.ContextMenuStrip();
                var version = (typeof(MainWindow).Assembly.GetName().Version
                    ?? throw new InvalidOperationException("The MissionView application version is missing.")).ToString(3);
                menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem($"MissionView v{version}") { Enabled = false, Tag = "Version" });
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

                var triggerItem = new System.Windows.Forms.ToolStripMenuItem(
                    "Open overview",
                    null,
                    async (s, e) =>
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            await ToggleScatterAsync();
                        });
                    });

                overviewTrayItem = triggerItem;
                triggerItem.ShortcutKeyDisplayString = settings.Hotkey;
                var exitItem = new System.Windows.Forms.ToolStripMenuItem(
                    "Quit", null, (s, e) => { Close(); });

                menu.Items.Add(triggerItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
                    "Show / restore desktop", null, (s, e) =>
                    {
                        if (isTransitioning || isScatterActive || AnyAnimating) return;
                        if (this.Visibility == Visibility.Visible) DesktopButton_Click(this, new RoutedEventArgs());
                        else RequestDesktopToggle();
                    }));
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()));
                updatesTrayItem = new System.Windows.Forms.ToolStripMenuItem("Check for updates...", null, async (_, _) => await RunUpdateAction());
                menu.Items.Add(updatesTrayItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(exitItem);
                menu.Renderer = new MissionViewMenuRenderer();
                menu.BackColor = System.Drawing.Color.FromArgb(32, 37, 34);
                menu.ForeColor = System.Drawing.Color.FromArgb(243, 245, 244);
                menu.ShowImageMargin = false;
                menu.Padding = new System.Windows.Forms.Padding(4);
                foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
                    item.Padding = new System.Windows.Forms.Padding(8, 5, 8, 5);
                trayIcon.ContextMenuStrip = menu;

                BindTrayOverview(trayIcon, async () =>
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await ToggleScatterAsync();
                    });
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Could not create the MissionView tray icon: {ex}");
                trayIcon?.Dispose();
                trayIcon = null;
                trayImage?.Dispose();
                trayImage = null;
                MessageBox.Show("MissionView could not create its notification area icon. You can still use the configured shortcut.",
                    "MissionView", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Brief, non-interactive feedback when scatter is invoked with no
        /// scatterable windows, instead of an empty/confusing overlay (#14).
        /// </summary>
        private void ShowNoWindowsToast(MonitorHelper.MonitorBounds monitor)
        {
            try
            {
                double scale = MonitorHelper.GetDpiScaleForMonitor(monitor);
                if (scale <= 0) scale = 1.0;

                const double toastW = 280;
                const double toastH = 64;

                var label = new TextBlock
                {
                    Text = "No windows to show",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var toast = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ShowActivated = false,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Width = toastW,
                    Height = toastH,
                    Content = new Border
                    {
                        Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(230, 30, 30, 30)),
                        CornerRadius = new CornerRadius(10),
                        Child = label
                    }
                };

                toast.Left = (monitor.Left + monitor.Width / 2.0) / scale - toastW / 2.0;
                toast.Top = (monitor.Top + monitor.Height / 2.0) / scale - toastH / 2.0;
                toast.Show();

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try { toast.Close(); } catch { }
                };
                timer.Start();
            }
            catch { }
        }

        #endregion

        #region Cleanup

        #region Working Set Floor

        // Between the scatter and return animations the overlay sits fully idle:
        // no CPU, no page faults. Windows' working-set manager therefore ages our
        // pages out and trims us (the "cliff" in the memory graph), and the first
        // input afterwards faults thousands of pages back at once — a stall right
        // at the start of the return animation, worse with more windows (more DWM
        // thumbnail surfaces mapped). Pin a floor under the working set while the
        // overlay is open and release it once hidden.
        private void ApplyWorkingSetFloor()
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                if (!wsFloorApplied)
                {
                    GetProcessWorkingSetSize(proc.Handle, out origWsMin, out origWsMax);
                    wsFloorApplied = true;
                }
                proc.Refresh();
                RaiseFloor(proc);
            }
            catch { }
        }

        // Runs on the 1s keepalive once the scatter animation has settled, so the
        // floor tracks the true DWM thumbnail working-set peak.
        private void RaiseWorkingSetFloorIfNeeded()
        {
            if (!wsFloorApplied || animationManager == null || animationManager.IsAnimating)
                return;
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                proc.Refresh();
                RaiseFloor(proc);
            }
            catch { }
        }

        private void RaiseFloor(System.Diagnostics.Process proc)
        {
            const ulong MARGIN = 128UL * 1024 * 1024;
            const ulong HYST = 32UL * 1024 * 1024;
            const ulong CAP = 1024UL * 1024 * 1024;

            ulong desired = (ulong)proc.WorkingSet64 + MARGIN;
            if (desired > CAP) desired = CAP;
            if (desired <= wsFloorMinBytes + HYST) return;

            ulong max = origWsMax.ToUInt64();
            if (max < desired + MARGIN) max = desired + MARGIN;

            if (SetProcessWorkingSetSizeEx(proc.Handle, (UIntPtr)desired, (UIntPtr)max, QUOTA_LIMITS_HARDWS_MIN_ENABLE))
                wsFloorMinBytes = desired;
        }

        private void RestoreWorkingSet()
        {
            if (!wsFloorApplied) return;
            wsFloorApplied = false;
            wsFloorMinBytes = 0;
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                SetProcessWorkingSetSizeEx(proc.Handle, origWsMin, origWsMax, 0);
            }
            catch { }
        }

        #endregion

        private void FullCleanup()
        {
            ResetFileDrag();
            appExposeRequest = null;
            searchQuery = "";
            moveCancellation?.Cancel();
            CancelDrag();
            foreach (var view in views.ToArray()) view.CleanupView();
            foreach (var view in views.Where(v => !ReferenceEquals(v, this)).ToArray())
            {
                view.Close();
                views.Remove(view);
            }
            previewExpanded = false;
            previewKey.Release();
            scatterInteraction.ClearDismissRequest();
            RestoreWorkingSet();
            if (timerPeriodActive)
            {
                timeEndPeriod(1);
                timerPeriodActive = false;
            }
            SetThreadExecutionState(ES_CONTINUOUS);
            animationManager?.StopAllAnimations();
            thumbnailManager?.CleanupAllThumbnails();

            session.Select(IntPtr.Zero);
            activeView = null;

            desktopCaptureManager?.Cleanup();

            // Overlay is hidden by now, so reclaim scatter-time garbage off the
            // critical path with a non-blocking background collect.
            try { GC.Collect(2, GCCollectionMode.Optimized, blocking: false); } catch { }

            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = normalPriorityClass;
            }
            catch { }
        }

        private void CleanupAndClose()
        {
            coordinator.CloseAllViews(null);
        }

        #endregion
    }
}
