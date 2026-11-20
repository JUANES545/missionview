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
        private readonly List<MainWindow> views;
        private readonly ScatterInteraction scatterInteraction;
        private readonly WindowActivation windowActivation;
        private readonly DesktopToggle desktopToggle;
        private MainWindow? activeView;
        private MonitorHelper.MonitorBounds monitor = null!;
        private List<MonitorHelper.MonitorBounds> monitors = new();
        private readonly WindowTransfer windowTransfer = new();
        private readonly WindowMoveOperation moveOperation = new();
        private CancellationTokenSource? moveCancellation;
        private readonly WindowDragGesture drag = new();
        private Border? dragCapture;
        private MainWindow? dragSource;
        private WindowThumb? dragThumb;
        private double dragGrabX, dragGrabY;
        private IntPtr dragThumbnail;
        private bool movingWindow;
        private bool reportingMoveError;
        private int topologyCancellationGeneration = -1;
        private bool previewExpanded;
        private readonly HeldPreviewState previewKey;
        private double monitorPixelWidth;
        private double monitorPixelHeight;
        private POINT lastHoverPosition;
        private IntPtr selection;
        private IntPtr selectedWindowHandle { get => coordinator.selection; set => coordinator.selection = value; }

        private bool opening;
        private bool transitioning;
        private bool isScatterActive { get => coordinator.opening; set => coordinator.opening = value; }
        private bool isTransitioning { get => coordinator.transitioning; set => coordinator.transitioning = value; }
        private object transitionLock => coordinator;
        private bool AnyAnimating => views.Any(v => v.animationManager != null && v.animationManager.IsAnimating);
        private bool IsCoordinator => ReferenceEquals(this, coordinator);

        private IntPtr hookID = IntPtr.Zero;
        private LowLevelKeyboardProc hookCallback = null!;
        private HotkeyParser configuredHotkey;
        private readonly KeyboardModifierState modifierKeyState = new();

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
            views = parent?.views ?? new List<MainWindow> { this };
            scatterInteraction = parent?.scatterInteraction ?? new();
            windowActivation = parent?.windowActivation ?? new();
            desktopToggle = parent?.desktopToggle ?? new();
            previewKey = parent?.previewKey ?? new();
            InitializeComponent();
            AccessKeyManager.Register("S", DesktopButton);
            RenderCanvas.Children.Remove(ChromeCanvas);
            chromeWindow = new OverviewChromeWindow(this, ChromeCanvas);
            chromeWindow.PreviewKeyDown += Window_KeyDown;
            chromeWindow.PreviewKeyUp += Window_KeyUp;
            chromeWindow.Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!scatterInteraction.IsOwnWindow(GetForegroundWindow()))
                {
                    coordinator.CancelDrag();
                    foreach (var view in views) view.ResetPreview();
                }
            }));
            DesktopButton.GotKeyboardFocus += (_, _) => ReleasePreviewForDesktopFocus();
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            chromeWindow.Closed += (_, _) =>
            {
                chromeClosed = true;
                if (!isClosing) coordinator.Close();
            };
            LocationChanged += (_, _) => SyncChromeToHost();
            SizeChanged += (_, _) => SyncChromeToHost();
            IsVisibleChanged += (_, _) => { if (!IsVisible && !chromeClosed) chromeWindow.Hide(); };

            settings = parent?.settings ?? ((App)Application.Current).Settings;
            configuredHotkey = HotkeyParser.Parse(settings.Hotkey);

            // Covers the plain .exe release too: registry follows settings (#15).
            if (IsCoordinator)
            {
                settings.ApplyRunOnStartup();
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
            thumbnailManager = new ThumbnailManager(ScatterCanvas, windowThumbs, helper.Handle, OnWindowClicked, OnWindowHovered);
            thumbnailManager.ConfigureInput = ConfigureThumbnailInput;
            hostHandle = helper.Handle;
            chromeHandle = new WindowInteropHelper(chromeWindow).EnsureHandle();
            scatterInteraction.RegisterSurface(hostHandle);
            scatterInteraction.RegisterSurface(chromeHandle);
            windowEnumerator = new WindowEnumerator(scatterInteraction.IsOwnWindow);

            animationManager.OnScatterComplete = () =>
            {
                try { thumbnailManager.SyncClickBordersToThumbs(currentDpiScale); } catch { }
                if (scatterInteraction.IsOpen && !isTransitioning && !coordinator.drag.IsPressed)
                    thumbnailManager.SetSelectedWindow(selectedWindowHandle);
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
                int vkCode = Marshal.ReadInt32(lParam);

                if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                {
                    modifierKeyState.Update(vkCode, true);

                    if (vkCode == configuredHotkey.VirtualKeyCode)
                    {
                        bool modifiersMatch = modifierKeyState.Matches(configuredHotkey.ModifierKeys);
                        bool toggle = scatterInteraction.ActivationKeyDown(modifiersMatch);
                        if (modifiersMatch)
                        {
                            if ((configuredHotkey.ModifierKeys & 0x0A) != 0)
                                KeyboardMenuMask.TrySend(SendInput);
                            if (toggle)
                            {
                                this.Dispatcher.BeginInvoke(new Action(async () =>
                                {
                                    await ToggleScatterAsync();
                                }));
                            }
                            return (IntPtr)1;
                        }
                    }
                }
                else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                {
                    modifierKeyState.Update(vkCode, false);
                    if (vkCode == configuredHotkey.VirtualKeyCode)
                        scatterInteraction.ActivationKeyUp();
                }
            }

            return CallNextHookEx(hookID, nCode, wParam, lParam);
        }

        #endregion

        #region Event Handlers

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is MenuItem) return;
            bool escape = e.Key == Key.Escape || (e.Key == Key.System && e.SystemKey == Key.Escape);
            if (coordinator.drag.IsPressed)
            {
                if (escape && !e.IsRepeat) coordinator.CancelDrag();
                e.Handled = true;
                return;
            }
            if (DesktopButton.IsKeyboardFocusWithin &&
                (e.Key == Key.Space || e.Key == Key.Return))
                return;

            if (scatterInteraction.IsOpen && !isScatterActive && !AnyAnimating && !isTransitioning && !coordinator.movingWindow)
            {
                if (OverviewMoveShortcut.Matches(e.Key, e.SystemKey, Keyboard.Modifiers))
                {
                    coordinator.ShowMoveMenu();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Space)
                {
                    if (previewKey.Press(e.IsRepeat)) coordinator.activeView?.ExpandPreview();
                    e.Handled = true;
                    return;
                }
                if (HandleScatterNavigationKey(e.Key))
                {
                    e.Handled = true;
                    return;
                }
            }

            bool isEsc = e.Key == Key.Escape ||
                         (e.Key == Key.System && e.SystemKey == Key.Escape);

            if (isEsc && !e.IsRepeat)
            {
                e.Handled = true;
                RequestDismissScatter();
            }
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || DesktopButton.IsKeyboardFocusWithin) return;
            if (previewKey.Release() && scatterInteraction.IsOpen && !isTransitioning)
                foreach (var view in views.Where(v => v.previewExpanded || v.animationManager.IsPreviewAnimating))
                    view.AnimatePreview(IntPtr.Zero, default);
            e.Handled = true;
        }

        private void ScatterCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Thumbnail borders handle their own clicks. Only bare canvas hits
            // dismiss the overview, never a click meant to select a window.
            if (!ReferenceEquals(e.OriginalSource, ScatterCanvas)) return;
            e.Handled = true;
            RequestDismissScatter();
        }

        private bool HandleScatterNavigationKey(Key key)
        {
            return coordinator.Navigate(this, key);
        }

        protected override void OnClosed(EventArgs e)
        {
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
                UnhookWindowsHookEx(hookID);
                hookID = IntPtr.Zero;
            }

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
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                animationManager?.SetMotionEnabled(SystemParameters.ClientAreaAnimation);
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

        private async System.Threading.Tasks.Task ToggleScatterAsync()
        {
            if (!IsCoordinator) { await coordinator.ToggleScatterAsync(); return; }
            if (animationManager == null || isTransitioning || movingWindow) return;
            if (drag.IsPressed) { CancelDrag(); return; }
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
                await StartScatterAsync();
        }

        private void RequestDismissScatter()
        {
            if (coordinator.reportingMoveError) return;
            if (coordinator.movingWindow) { coordinator.CancelSessionImmediately(); return; }
            if (isTransitioning || (!isScatterActive && this.Visibility != Visibility.Visible)) return;
            scatterInteraction.RequestDismiss();
            TryDismissScatter();
        }

        private void TryDismissScatter()
        {
            bool busy = isScatterActive || AnyAnimating || isTransitioning || coordinator.movingWindow;
            if (scatterInteraction.TakeConfirmationRequest(busy) && this.Visibility == Visibility.Visible)
            {
                if (selectedWindowHandle != IntPtr.Zero)
                    OnWindowClicked(selectedWindowHandle);
                else
                    CleanupAndClose();
                return;
            }
            if (scatterInteraction.TakeDismissRequest(busy) && this.Visibility == Visibility.Visible)
                CleanupAndClose();
        }

        private async System.Threading.Tasks.Task StartScatterAsync()
        {
            if (!CanActivateScatter()) return;
            isScatterActive = true;
            int generation = 0;
            try
            {
                IntPtr previousWindow = GetForegroundWindow();
                scatterInteraction.BeginSession(windowActivation.Capture(previousWindow));
                generation = scatterInteraction.Generation;
                monitors = MonitorHelper.GetMonitors().OrderBy(m => m.Left).ThenBy(m => m.Top).ToList();
                var pointerMonitor = MonitorHelper.GetMonitorFromCursor();
                monitor = monitors.FirstOrDefault(m => m.Handle == pointerMonitor.Handle) ?? monitors[0];
                foreach (var other in monitors.Where(m => m.Handle != monitor.Handle))
                {
                    var view = new MainWindow(this) { monitor = other };
                    views.Add(view);
                    view.InitializeRendering();
                }
                if (!Win32Interop.GetCursorPos(out var pt))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not read the pointer position.");
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
                var windows = windowEnumerator.EnumerateByMonitor(monitors);
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
                SelectWindow(OverviewPreview.InitialSelection(previousWindow, AllThumbs().Select(t => t.WindowHandle)));
                activeView!.chromeWindow.Activate();
                activeView.chromeWindow.Focus();
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
            var thumb = coordinator.AllThumbs().FirstOrDefault(t => t.WindowHandle == windowHandle);
            if (thumb != null) coordinator.CloseAllViews(thumb.Identity);
        }

        private void OnWindowHovered(IntPtr windowHandle)
        {
            if (scatterInteraction.IsOpen && !AnyAnimating && !isTransitioning && !coordinator.drag.IsPressed && !coordinator.movingWindow)
            {
                if (!Win32Interop.GetCursorPos(out var cursor))
                {
                    System.Diagnostics.Trace.TraceWarning("Could not read the pointer position for thumbnail selection.");
                    return;
                }
                if (cursor.X == coordinator.lastHoverPosition.X && cursor.Y == coordinator.lastHoverPosition.Y) return;
                coordinator.lastHoverPosition = cursor;
                SelectWindow(windowHandle);
            }
        }

        private void SelectNextWindow(int direction)
        {
            var all = AllThumbs().ToList();
            if (all.Count == 0) return;

            int currentIndex = all.FindIndex(t => t.WindowHandle == selectedWindowHandle);
            if (currentIndex < 0) currentIndex = direction > 0 ? -1 : 0;

            int nextIndex = (currentIndex + direction + all.Count) % all.Count;
            SelectWindow(all[nextIndex].WindowHandle);
        }

        private void SelectWindowInDirection(int dx, int dy)
        {
            var targets = AllThumbs()
                .Select(t => new NavigationTarget(t.Identity, GetCenterX(t), GetCenterY(t))).ToList();
            if (targets.Count != 0)
                SelectWindow(OverviewNavigation.Select(targets, selectedWindowHandle, dx, dy));
        }

        private void SelectWindow(IntPtr windowHandle)
        {
            if (!IsCoordinator) { coordinator.SelectWindow(windowHandle); return; }
            if (selectedWindowHandle == windowHandle) return;
            selectedWindowHandle = windowHandle;
            activeView = ViewFor(windowHandle) ?? activeView ?? this;
            foreach (var view in views)
            {
                if (view != activeView && (view.previewExpanded || view.animationManager.IsPreviewAnimating))
                    view.AnimatePreview(IntPtr.Zero, default);
                view.thumbnailManager?.SetSelectedWindow(isScatterActive || view.animationManager.IsPreviewAnimating
                    ? IntPtr.Zero : windowHandle);
            }
            if (previewKey.IsHeld) activeView.ExpandPreview();
        }

        private void ExpandPreview()
        {
            var thumb = windowThumbs.FirstOrDefault(t => t.WindowHandle == selectedWindowHandle);
            if (thumb == null) return;
            animationManager.StopAllAnimations();
            var bounds = OverviewPreview.Fit(thumb.StartWidth, thumb.StartHeight, monitorPixelWidth,
                monitorPixelHeight, Math.Min(WindowLayoutCalculator.ToolbarHeight * currentDpiScale,
                    monitorPixelHeight * .4), currentDpiScale);
            if (!thumbnailManager.BringThumbnailToFront(thumb.WindowHandle, animationManager))
            {
                AbortOverview(new InvalidOperationException("DWM could not raise the live preview."));
                return;
            }
            previewExpanded = true;
            AnimatePreview(thumb.WindowHandle, new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }

        private void AnimatePreview(IntPtr selected, PreviewBounds bounds)
        {
            int generation = scatterInteraction.Generation;
            thumbnailManager.SetPreviewAnimating(true);
            thumbnailManager.BeginTransition(() => animationManager.StartPreviewAnimation(selected, bounds, success =>
            {
                if (isClosing || !scatterInteraction.IsOpen || generation != scatterInteraction.Generation) return;
                if (!success)
                {
                    AbortOverview(new InvalidOperationException("DWM could not animate the live preview."));
                    return;
                }
                if (isClosing || isTransitioning || !IsVisible || !scatterInteraction.IsOpen) return;
                previewExpanded = previewKey.IsHeld && ReferenceEquals(this, coordinator.activeView);
                if (selected == IntPtr.Zero)
                {
                    try { thumbnailManager.RestoreSourceStacking(animationManager); }
                    catch (Exception ex) { AbortOverview(ex); return; }
                }
                thumbnailManager.SyncClickBordersToThumbs(currentDpiScale);
                thumbnailManager.SetPreviewAnimating(false);
                thumbnailManager.SetSelectedWindow(selectedWindowHandle);
                animationManager.StartKeepAlive();
            }));
        }

        private void ResetPreview()
        {
            previewKey.Release();
            if (!previewExpanded && !animationManager.IsPreviewAnimating) return;
            animationManager.StopAllAnimations();
            previewExpanded = false;
            foreach (var thumb in windowThumbs)
            {
                thumb.CurrentX = thumb.TargetX;
                thumb.CurrentY = thumb.TargetY;
                thumb.CurrentWidth = thumb.TargetWidth;
                thumb.CurrentHeight = thumb.TargetHeight;
                if (!animationManager.UpdateThumbnailPosition(thumb))
                    System.Diagnostics.Trace.TraceError("DWM could not restore a thumbnail to its overview slot.");
            }
            thumbnailManager.SyncClickBordersToThumbs(currentDpiScale);
            if (!isTransitioning && scatterInteraction.IsOpen && this.Visibility == Visibility.Visible)
            {
                thumbnailManager.SetPreviewAnimating(false);
                thumbnailManager.SetSelectedWindow(selectedWindowHandle);
                animationManager.StartKeepAlive();
            }
        }

        private void ReleasePreviewForDesktopFocus()
        {
            previewKey.Release();
            if (!isTransitioning && scatterInteraction.IsOpen)
                foreach (var view in views.Where(v => v.previewExpanded || v.animationManager.IsPreviewAnimating))
                    view.AnimatePreview(IntPtr.Zero, default);
        }

        private double GetCenterX(WindowThumb thumb) => ViewFor(thumb.WindowHandle)!.monitor.Left + thumb.TargetX + thumb.TargetWidth / 2.0;
        private double GetCenterY(WindowThumb thumb) => ViewFor(thumb.WindowHandle)!.monitor.Top + thumb.TargetY + thumb.TargetHeight / 2.0;

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
        /// double-click and exposes Run-on-startup + Exit (#12, #15).
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

                var triggerItem = new System.Windows.Forms.ToolStripMenuItem(
                    "Scatter windows",
                    null,
                    async (s, e) =>
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            await ToggleScatterAsync();
                        });
                    });

                var startupItem = new System.Windows.Forms.ToolStripMenuItem("Run on startup")
                {
                    Checked = settings.RunOnStartup,
                    CheckOnClick = true
                };
                startupItem.Click += (s, e) =>
                {
                    settings.TrySetRunOnStartup(startupItem.Checked, settings.ApplyRunOnStartup, error =>
                    {
                        System.Diagnostics.Trace.TraceError($"Could not save settings: {error}");
                        MessageBox.Show($"MissionView could not save your settings. The previous startup preference is unchanged.\n\n{error.Message}",
                            "MissionView", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    startupItem.Checked = settings.RunOnStartup;
                };

                var exitItem = new System.Windows.Forms.ToolStripMenuItem(
                    "Exit", null, (s, e) => { Close(); });

                menu.Items.Add(triggerItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
                    "Show / restore desktop", null, (s, e) =>
                    {
                        if (isTransitioning || isScatterActive || animationManager.IsAnimating) return;
                        if (this.Visibility == Visibility.Visible) DesktopButton_Click(this, new RoutedEventArgs());
                        else RequestDesktopToggle();
                    }));
                menu.Items.Add(startupItem);
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(exitItem);
                trayIcon.ContextMenuStrip = menu;

                trayIcon.DoubleClick += async (s, e) =>
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await ToggleScatterAsync();
                    });
                };
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

            selectedWindowHandle = IntPtr.Zero;
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
