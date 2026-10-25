using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using NesXbox.Logic;
using NesXbox.Services;
using Windows.Graphics.Display;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace NesXbox.Views
{
    /// <summary>
    /// Gameplay. Native core runs in <see cref="EmulationSession"/>; this page only presents frames
    /// (one bulk copy per frame into a WriteableBitmap) and owns the pause menu and lifecycle.
    /// </summary>
    public sealed partial class GamePage : Page
    {
        GameEntry game;
        EmulationSession session;
        AudioOutput audio;
        WriteableBitmap bitmap;
        Stream pixels;
        DateTime lastOverlay;
        DateTime lastBatterySave = DateTime.UtcNow;

        public GamePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            KeyDown += OnKeyDown;
            AppServices.Suspending += OnSuspending;
            AppServices.Resumed += OnResumed;

            game = AppServices.Library.Find(e.Parameter as string);
            if (game == null || !game.Playable) { ShowMessage("This game is not available. Press B to return."); return; }
            PauseTitle.Text = game.Title;

            NesCore core;
            try
            {
                core = new NesCore();
                string error = core.LoadRom(AppServices.Library.ReadRom(game));
                if (error != null) { core.Dispose(); ShowMessage(error + ". Press B to return."); return; }
                core.WriteBattery(AppServices.Library.ReadSave(game));
            }
            catch (Exception ex) when (ex is IOException || ex is DllNotFoundException || ex is UnauthorizedAccessException)
            {
                ShowMessage("Cannot start the game: " + ex.Message);
                return;
            }

            audio = await AudioOutput.CreateAsync(); // null => play silently
            int scale = ChooseScale();
            session = new EmulationSession(core, scale, audio, AppServices.Gamepads);
            session.PauseRequested += () => { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ShowPause); };
            session.Faulted += ex => { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowMessage("Emulation stopped: " + ex.Message)); };

            bitmap = new WriteableBitmap(session.PixelWidth, session.PixelHeight);
            pixels = bitmap.PixelBuffer.AsStream();
            Screen.Source = bitmap;
            SizeScreen(scale);
            DebugOverlay.Visibility = AppServices.Settings.ShowDebugOverlay ? Visibility.Visible : Visibility.Collapsed;

            await AppServices.Library.MarkPlayedAsync(game.Id);
            CompositionTarget.Rendering += OnRendering;
            session.Start();
            Focus(FocusState.Programmatic);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            KeyDown -= OnKeyDown;
            AppServices.Suspending -= OnSuspending;
            AppServices.Resumed -= OnResumed;
            StopSession();
            base.OnNavigatedFrom(e);
        }

        /// <summary>Largest integer scale that fits the screen in physical pixels (capped at 4x = 1024x960).</summary>
        static int ChooseScale()
        {
            var info = DisplayInformation.GetForCurrentView();
            double h = info.ScreenHeightInRawPixels > 0 ? info.ScreenHeightInRawPixels : 1080;
            return Math.Max(1, Math.Min(4, (int)(h / NesCore.Height)));
        }

        void SizeScreen(int scale)
        {
            double rawPerView = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            double w = NesCore.Width * scale / rawPerView, h = NesCore.Height * scale / rawPerView;
            if (AppServices.Settings.PixelAspect87) w *= 8.0 / 7.0; // non-integer: XAML filters horizontally
            Screen.Width = w;
            Screen.Height = h;
        }

        void OnRendering(object sender, object e)
        {
            if (session == null) return;
            session.VsyncTick();
            bool fresh = session.TakeFrame(buffer =>
            {
                pixels.Seek(0, SeekOrigin.Begin);
                pixels.Write(buffer, 0, buffer.Length);
            });
            if (fresh) bitmap.Invalidate();

            var now = DateTime.UtcNow;
            if (DebugOverlay.Visibility == Visibility.Visible && (now - lastOverlay).TotalMilliseconds > 500)
            {
                lastOverlay = now;
                DebugOverlay.Text = $"FPS {session.EffectiveFps:F1}  core {session.CoreMsPerFrame:F2} ms  dropped {session.DroppedFrames}  " +
                                    $"audio underruns {session.Underruns}  scale {session.Scale}x";
            }
            // Periodic battery save so a crash or power loss costs at most a minute of progress.
            if (game.Battery && (now - lastBatterySave).TotalSeconds > 60) { lastBatterySave = now; SaveBattery(); }
        }

        void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // While playing, gamepad keys belong to the game, not XAML focus navigation.
            if (session != null && !session.Paused && e.Key >= VirtualKey.GamepadA && e.Key <= VirtualKey.GamepadRightThumbstickLeft) e.Handled = true;
        }

        void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = true;
            if (session == null) { Quit(); return; }
            if (session.Paused) Resume(); else ShowPause();
        }

        void ShowPause()
        {
            if (session == null) return;
            session.Pause();
            SaveBattery();
            PauseOverlay.Visibility = Visibility.Visible;
            ResumeButton.Focus(FocusState.Programmatic);
        }

        void Resume()
        {
            PauseOverlay.Visibility = Visibility.Collapsed;
            session?.Resume();
            Focus(FocusState.Programmatic);
        }

        void Resume_Click(object sender, RoutedEventArgs e) => Resume();

        void Reset_Click(object sender, RoutedEventArgs e)
        {
            // Rebuild the session from scratch: simplest way to reset safely across threads.
            if (game == null) return;
            var id = game.Id;
            StopSession();
            Frame.Navigate(typeof(GamePage), id);
            Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
        }

        void Quit_Click(object sender, RoutedEventArgs e) => Quit();

        void Quit()
        {
            StopSession();
            if (Frame.CanGoBack) Frame.GoBack();
        }

        void OnSuspending()
        {
            // Console suspend (Guide button / app switch): save and stay paused on return.
            if (session == null) return;
            session.Pause();
            SaveBattery();
        }

        async void OnResumed() => await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ShowPause);

        void SaveBattery()
        {
            if (session == null || game == null || !game.Battery) return;
            try
            {
                var data = session.ReadBattery();
                if (data != null) AppServices.Library.WriteSave(game, data);
            }
            catch (IOException ex) { AppServices.Log("Battery save failed: " + ex.Message); }
        }

        void StopSession()
        {
            CompositionTarget.Rendering -= OnRendering;
            if (session != null)
            {
                session.Pause();
                SaveBattery();
                session.Dispose();
                session = null;
            }
            audio?.Dispose();
            audio = null;
            pixels?.Dispose();
            pixels = null;
        }

        void ShowMessage(string text)
        {
            Message.Text = text;
            Message.Visibility = Visibility.Visible;
        }
    }
}
