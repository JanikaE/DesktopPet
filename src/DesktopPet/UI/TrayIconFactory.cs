using DesktopPet;
using Forms = System.Windows.Forms;

namespace DesktopPet.UI;

public static class TrayIconFactory
{
    public static Forms.NotifyIcon Create(MainWindow petWindow, Action toggleVisibility, Action openSettings, Action exitApplication)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => petWindow.Dispatcher.Invoke(toggleVisibility));
        var topmostItem = (Forms.ToolStripMenuItem)menu.Items.Add("始终置顶", null,
            (_, _) => petWindow.Dispatcher.Invoke(petWindow.TogglePetTopmost));
        menu.Items.Add("设置", null, (_, _) => petWindow.Dispatcher.Invoke(openSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitApplication());
        menu.Opening += (_, _) => topmostItem.Checked = petWindow.Dispatcher.Invoke(() => petWindow.IsPetTopmost);

        var trayIcon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico")),
            Text = "DesktopPet",
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) petWindow.Dispatcher.Invoke(toggleVisibility);
        };
        return trayIcon;
    }
}
