using System;
using NesXbox.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace NesXbox
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            // Xbox: no mouse-cursor emulation, gamepad drives XY focus navigation.
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            Suspending += OnSuspending;
            Resuming += (s, e) => AppServices.RaiseResumed();
            UnhandledException += (s, e) => AppServices.Log("Unhandled: " + e.Message);
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (!(Window.Current.Content is Frame root))
            {
                root = new Frame();
                Window.Current.Content = root;
                await AppServices.InitializeAsync();
            }
            if (root.Content == null) root.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();
        }

        // Opening a .nes file (the file association that removableStorage requires) imports it.
        protected override async void OnFileActivated(FileActivatedEventArgs args)
        {
            if (!(Window.Current.Content is Frame root))
            {
                root = new Frame();
                Window.Current.Content = root;
                await AppServices.InitializeAsync();
                root.Navigate(typeof(MainPage));
            }
            Window.Current.Activate();
            await AppServices.ImportActivatedFilesAsync(args.Files);
        }

        void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try { AppServices.RaiseSuspending(); }
            finally { deferral.Complete(); }
        }
    }
}
