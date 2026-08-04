using System;
using Autodesk.Revit.UI;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;



namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        // Kept so the button can be re-labelled whenever the service starts or stops.
        private static PushButton _switchButton;
        private static BitmapImage _iconSmall;
        private static BitmapImage _iconLarge;

        public Result OnStartup(UIControlledApplication application)
        {
            RibbonPanel mcpPanel = application.CreateRibbonPanel("Revit MCP Plugin");

            PushButtonData pushButtonData = new PushButtonData("ID_EXCMD_TOGGLE_REVIT_MCP", "MCP Server\r\nOFF",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.MCPServiceConnection");
            _iconSmall = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-16.png", UriKind.RelativeOrAbsolute));
            _iconLarge = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-32.png", UriKind.RelativeOrAbsolute));
            pushButtonData.Image = _iconSmall;
            pushButtonData.LargeImage = _iconLarge;
            _switchButton = mcpPanel.AddItem(pushButtonData) as PushButton;

            // The socket service never auto-starts, so the button begins in the off state.
            SetServiceState(false);

            PushButtonData mcp_settings_pushButtonData = new PushButtonData("ID_EXCMD_MCP_SETTINGS", "Settings",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.Settings");
            mcp_settings_pushButtonData.ToolTip = "MCP Settings";
            mcp_settings_pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-16.png", UriKind.RelativeOrAbsolute));
            mcp_settings_pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(mcp_settings_pushButtonData);

            return Result.Succeeded;
        }

        /// <summary>
        /// Reflect the socket service's state on the ribbon button. The old button looked
        /// identical whether the server was running or not, and the only feedback was a
        /// dialog that vanished on the next click, so there was no way to tell at a glance.
        /// </summary>
        /// <param name="running">Whether the socket service is actually listening.</param>
        /// <param name="port">Port it is listening on; shown in the tooltip when running.</param>
        public static void SetServiceState(bool running, int port = 8080)
        {
            if (_switchButton == null) return;

            try
            {
                _switchButton.ItemText = running ? "MCP Server\r\nON" : "MCP Server\r\nOFF";

                _switchButton.ToolTip = running
                    ? $"MCP server is RUNNING on port {port}. Click to stop it."
                    : "MCP server is STOPPED. Click to start it, so an AI client can reach this Revit session.";

                _switchButton.LongDescription = running
                    ? "Commands sent by the AI client are being executed in this Revit session."
                    : "Nothing is listening. An AI client cannot reach Revit until the server is started.";

                _switchButton.Image = AddStatusBadge(_iconSmall, running);
                _switchButton.LargeImage = AddStatusBadge(_iconLarge, running);
            }
            catch
            {
                // The button is cosmetic: never let a UI update break starting or stopping.
            }
        }

        /// <summary>
        /// Draw the product icon with a status dot in the corner: green when running, grey
        /// when stopped. Composed at run time so no extra image files need shipping, and so
        /// the badge always matches whatever icon the button is using.
        /// </summary>
        private static ImageSource AddStatusBadge(BitmapImage baseImage, bool running)
        {
            if (baseImage == null) return null;

            int width = baseImage.PixelWidth;
            int height = baseImage.PixelHeight;
            if (width <= 0 || height <= 0) return baseImage;

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawImage(baseImage, new Rect(0, 0, width, height));

                double radius = Math.Max(3.0, width / 4.0);
                var centre = new Point(width - radius, height - radius);
                Brush fill = running
                    ? new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43))   // green: listening
                    : new SolidColorBrush(Color.FromRgb(0x8C, 0x93, 0x8D));  // grey: stopped
                var outline = new Pen(Brushes.White, Math.Max(1.0, radius / 3.0));

                dc.DrawEllipse(fill, outline, centre, radius, radius);
            }

            var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();
            return rendered;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch { }

            return Result.Succeeded;
        }
    }
}
