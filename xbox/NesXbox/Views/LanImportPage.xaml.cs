using System;
using NesXbox.Logic;
using NesXbox.Services;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace NesXbox.Views
{
    public sealed partial class LanImportPage : Page
    {
        LanRomImport lan;

        public LanImportPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            lan = new LanRomImport();
            lan.Imported += OnImported;
            string error = lan.Start();
            if (error != null)
            {
                UrlText.Text = "LAN import unavailable";
                CodeText.Text = "";
                StatusLine.Text = error;
                return;
            }
            UrlText.Text = lan.Url;
            CodeText.Text = "Code: " + lan.Code;
            StatusLine.Text = "Waiting for uploads…";
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (lan != null) { lan.Imported -= OnImported; lan.Dispose(); lan = null; }
            base.OnNavigatedFrom(e);
        }

        async void OnImported(ImportResult r) =>
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                string line = (r.Game?.Title ?? "File") + ": " + r.Message;
                Received.Items.Insert(0, line);
                StatusLine.Text = line;
            });
    }
}
