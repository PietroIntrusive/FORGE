using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ForgeSentinel;

static class ToastHelper
{
    const string AppId = "ForgeSentinel.Sentinel";

    // Registers the app AUMID so Windows accepts toast notifications from this unpackaged process.
    // Must be called before any Show() call.
    public static void RegisterAumid()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\AppUserModelId\" + AppId);
        key.SetValue("DisplayName", "Forge Sentinel");
    }

    public static void Show(string title, string body)
    {
        var xml = new XmlDocument();
        xml.LoadXml($"""
            <toast>
              <visual>
                <binding template="ToastGeneric">
                  <text>{Escape(title)}</text>
                  <text>{Escape(body)}</text>
                </binding>
              </visual>
            </toast>
            """);

        try
        {
            var toast = new ToastNotification(xml);
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch
        {
            // Toast failed silently — detection still logged to console
        }
    }

    static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
