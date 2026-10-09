using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;

// Aliased because System.Drawing.Icon is the only type needed from that namespace.
using Icon = System.Drawing.Icon;

namespace KVMate.App.Tray;

/// <summary>The notification-area icon and its menu, which is the whole of the app most of the time.</summary>
/// <remarks>
/// <para>
/// Built in code and created with <c>ForceCreate</c>, because the app starts with no window to host
/// it. The menu is in <see cref="ContextMenuMode.PopupMenu"/> mode, a native popup menu built from
/// the flyout each time it opens, which needs no XAML root and so works with no window open.
/// </para>
/// <para>
/// The status line is a disabled first item rather than only the tooltip, so it can be read without
/// hovering and waiting.
/// </para>
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _control;
    private readonly MenuFlyoutItem _status;

    /// <param name="iconPath">The .ico to show. A missing file leaves the default icon.</param>
    /// <param name="connectNow">Runs on the UI thread when Connect now is picked.</param>
    /// <param name="disconnectNow">Runs when Disconnect now is picked.</param>
    /// <param name="openSettings">Runs when Settings… is picked or the icon is double-clicked.</param>
    /// <param name="exit">Runs when Exit is picked.</param>
    public TrayIcon(string iconPath, Action connectNow, Action disconnectNow, Action openSettings, Action exit)
    {
        _status = new MenuFlyoutItem { Text = "Starting…", IsEnabled = false };

        var menu = new MenuFlyout();
        menu.Items.Add(_status);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Connect now", Command = new RelayCommand(connectNow) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Disconnect now", Command = new RelayCommand(disconnectNow) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Settings…", Command = new RelayCommand(openSettings) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = new RelayCommand(exit) });

        _control = new TaskbarIcon
        {
            ToolTipText = "KVMate",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            DoubleClickCommand = new RelayCommand(openSettings),
            NoLeftClickDelay = true,
        };

        try
        {
            if (File.Exists(iconPath))
            {
                _control.Icon = new Icon(iconPath);
            }
        }
        catch (Exception)
        {
            // An icon is decoration. The menu still works with the default one.
        }
    }

    /// <summary>Put the icon in the notification area.</summary>
    /// <returns>False if that failed, in which case the caller must show a window instead.</returns>
    /// <remarks>
    /// Efficiency mode is declined: Windows would then deprioritise the process, and the whole point
    /// is reacting to a switch within a few seconds.
    /// </remarks>
    public bool TryCreate()
    {
        try
        {
            _control.ForceCreate(enablesEfficiencyMode: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Show <paramref name="status"/> in the menu's first line and in the tooltip.</summary>
    public void ShowStatus(string status)
    {
        _status.Text = status;
        _control.ToolTipText = $"KVMate: {status}";
    }

    /// <summary>A balloon notification. Failure to show one is ignored.</summary>
    public void Notify(string title, string message)
    {
        try
        {
            _control.ShowNotification(title, message, NotificationIcon.Warning);
        }
        catch (Exception)
        {
            // The status line still says it.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _control.Dispose();
}
