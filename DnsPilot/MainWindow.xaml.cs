using System;
using System.ComponentModel;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;

using DnsPilot.Models;
using DnsPilot.Ui;

using Forms = System.Windows.Forms;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace DnsPilot;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private Forms.NotifyIcon? _tray;
    private System.Windows.Controls.ContextMenu? _trayMenu;
    private System.Windows.Controls.MenuItem? _trayShow;
    private System.Windows.Controls.MenuItem? _trayExit;

    private bool _exiting;
    private bool _toldAboutTray;
    private bool _addingToNetworkChange;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        CreateTray();

        Lang.Changed += OnLanguageChanged;
        OnLanguageChanged();

        // A laptop that switches from wired to Wi-Fi gets new adapters and loses old ones. Without
        // this the list is whatever was true when the window opened, and applying to an adapter
        // that has since gone away fails with a message that does not explain why.
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        App.Log($"window opened, {System.Linq.Enumerable.Count(_vm.Adapters)} adapters");
    }

    // --- tray -------------------------------------------------------------------------------

    private void CreateTray()
    {
        try
        {
            _trayShow = new System.Windows.Controls.MenuItem { Header = Lang.T("tray_show") };
            _trayShow.Click += (_, _) => ShowFromTray();

            _trayExit = new System.Windows.Controls.MenuItem { Header = Lang.T("tray_exit") };
            _trayExit.Click += (_, _) => Exit();

            _trayMenu = new System.Windows.Controls.ContextMenu
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
                Style = (Style)System.Windows.Application.Current.FindResource("Menu.Tray"),
            };
            _trayShow.Style = (Style)System.Windows.Application.Current.FindResource("Menu.TrayItem");
            _trayExit.Style = (Style)System.Windows.Application.Current.FindResource("Menu.TrayItem");
            var traySep = new System.Windows.Controls.Separator
            {
                Style = (Style)System.Windows.Application.Current.FindResource("Menu.TraySep"),
            };
            _trayMenu.Items.Add(_trayShow);
            _trayMenu.Items.Add(traySep);
            _trayMenu.Items.Add(_trayExit);

            _tray = new Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = Lang.T("tray_tip"),
                Visible = true,
            };

            // Both clicks are handled here rather than by assigning ContextMenuStrip, which is a
            // WinForms type: giving it the WinForms menu would render that menu in system colours
            // beside a themed window, and it would not follow the language either. The WPF menu is
            // opened by hand at the cursor, styled explicitly with Menu.Tray above (explicit,
            // because a ContextMenu with no logical parent is not guaranteed to pick up
            // Application-level implicit styles), and given its direction on every language change.
            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) { ShowFromTray(); return; }
                if (e.Button == Forms.MouseButtons.Right) OpenTrayMenu();
            };

            _tray.BalloonTipClicked += (_, _) => ShowFromTray();
            App.Log("tray icon created");
        }
        catch (Exception ex)
        {
            // The tray is a convenience. If Windows declines the icon the app must still work, so
            // the failure is recorded and the window keeps behaving normally.
            App.Log("tray unavailable: " + ex.Message);
            _tray = null;
        }
    }

    private void OpenTrayMenu()
    {
        if (_trayMenu is null) return;
        _trayMenu.IsOpen = true;
    }

    // Reads the .ico that the csproj compiled into this assembly as a Resource, and returns a
    // copy of the frame that best matches the tray's own pixel size. The copy matters:
    // System.Drawing.Icon(Stream) keeps using the stream it was given, and the stream here is
    // gone by the time the tray is painted - constructing from the stream and then cloning at a
    // fixed size is what makes the returned handle independent of it.
    private static System.Drawing.Icon LoadAppIcon()
    {
        var want = TrayIconSize();

        var info = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/dns-pilot.ico"));
        if (info?.Stream is null)
        {
            // Not reachable in a build that shipped the icon, but a missing resource must not
            // take the tray with it.
            App.Log("app icon resource missing");
            return SystemIcons.Application;
        }

        using (info.Stream)
        using (var full = new System.Drawing.Icon(info.Stream))
        {
            return new System.Drawing.Icon(full, want.Width, want.Height);
        }
    }

    // The size the notification area actually draws at, read straight from the metric rather
    // than derived from a formula that then has to be trusted: SystemInformation.SmallIconSize is
    // SM_CXSMICON/SM_CYSMICON. Asking the .ico for that frame lets Windows place it without
    // blurring a larger one down to the tray.
    private static System.Drawing.Size TrayIconSize()
    {
        var want = System.Windows.Forms.SystemInformation.SmallIconSize;

        // The frames the .ico actually carries, in the order the generator writes them.
        int[] have = { 16, 24, 32, 48, 64, 128, 256 };
        var best = have[0];
        foreach (var s in have)
        {
            if (Math.Abs(s - want.Width) < Math.Abs(best - want.Width)) best = s;
        }
        return new System.Drawing.Size(best, best);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;   // nudges the window above whatever has focus, without pinning it
        Focus();
    }

    private void Exit()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            // Closing hides rather than quits, which is the point of having a tray icon at all:
            // the app sits between the user and a privileged setting, and reopening it every time
            // it is needed would make the tray worthless. Exit lives in the tray menu.
            e.Cancel = true;
            Hide();

            if (!_toldAboutTray && _tray is not null)
            {
                _toldAboutTray = true;
                try
                {
                    _tray.ShowBalloonTip(3000, Lang.T("app_name"), Lang.T("tray_tip"), Forms.ToolTipIcon.Info);
                }
                catch
                {
                    // Balloons are frequently disabled system-wide; failing to show one is not a
                    // reason to stop the app from hiding to the tray.
                }
            }

            App.Log("window hidden to tray");
            return;
        }

        base.OnClosing(e);
        TearDown();
    }

    private void TearDown()
    {
        Lang.Changed -= OnLanguageChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _vm.Dispose();
        App.Log("exited");
    }

    // --- language ---------------------------------------------------------------------------

    private void OnLanguageChanged()
    {
        // Right-to-left is a property of the whole content area, not of individual labels: with
        // only the text mirrored, an icon column would sit on the wrong side of its label.
        //
        // Applied to the root grid rather than to this window. On a Window, WPF sets
        // WS_EX_LAYOUTRTL on the handle, and Windows mirrors the title bar caption with it -
        // "DnsPilot" would read backwards in the frame, outside the reach of anything in the
        // client area. The grid mirrors identically and leaves the caption alone.
        //
        // Qualified: inside the setter, an unqualified FlowDirection resolves to this window's
        // instance property, and the enum members cannot be read through an instance.
        Root.FlowDirection = Lang.IsRtl ? System.Windows.FlowDirection.RightToLeft
                                       : System.Windows.FlowDirection.LeftToRight;
        Title = Lang.T("app_name");

        // The tray menu has no parent in the tree to inherit direction from, so it is set here
        // with everything else that is re-worded - the menu itself is never rebuilt.
        if (_trayMenu is not null)
            _trayMenu.FlowDirection = Lang.IsRtl ? System.Windows.FlowDirection.RightToLeft
                                                 : System.Windows.FlowDirection.LeftToRight;

        if (_trayShow is not null) _trayShow.Header = Lang.T("tray_show");
        if (_trayExit is not null) _trayExit.Header = Lang.T("tray_exit");
        if (_tray is not null) _tray.Text = Lang.T("tray_tip");
    }

    // --- network ----------------------------------------------------------------------------

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_addingToNetworkChange) return;
        _addingToNetworkChange = true;

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _addingToNetworkChange = false;
                if (_exiting) return;
                _vm.RefreshAdapters();
                App.Log("network changed, adapters reloaded");
            }));
        }
        catch
        {
            _addingToNetworkChange = false;
        }
    }

    // --- add / remove -----------------------------------------------------------------------

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        AddName.Text = string.Empty;
        AddPrimary.Text = string.Empty;
        AddSecondary.Text = string.Empty;
        AddError.Visibility = Visibility.Collapsed;
        AddError.Text = string.Empty;

        // The family offered is the one being looked at, because that is the list the new server
        // will land in; a user on the IPv6 tab adding an IPv6 address should not have to say so.
        AddV4.IsChecked = _vm.Family == DnsFamily.IPv4;
        AddV6.IsChecked = _vm.Family == DnsFamily.IPv6;

        AddOverlay.Visibility = Visibility.Visible;
        AddPrimary.Focus();
    }

    private void OnAddCancel(object sender, RoutedEventArgs e)
        => AddOverlay.Visibility = Visibility.Collapsed;

    private void OnAddSave(object sender, RoutedEventArgs e)
    {
        var family = AddV6.IsChecked == true ? DnsFamily.IPv6 : DnsFamily.IPv4;

        var problem = _vm.TryAddCustom(AddName.Text, AddPrimary.Text, AddSecondary.Text, family);
        if (problem is null)
        {
            AddOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // Shown inline and left up. A message box here would be dismissable and the user would be
        // back at an empty field with no memory of what was wrong with the address they typed.
        AddError.Text = Lang.T(problem);
        AddError.Visibility = Visibility.Visible;
        AddPrimary.Focus();
        AddPrimary.SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && AddOverlay.Visibility == Visibility.Visible)
        {
            OnAddCancel(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+Enter applies the highlighted server - the keyboard half of "choose and go",
        // which matters when the list is being compared by arrow keys after a test.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _vm.ApplyCommand.CanExecute(null))
        {
            _vm.ApplyCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // --- failure detail ---------------------------------------------------------------------

    private void OnDetailClick(object sender, RoutedEventArgs e)
    {
        var detail = _vm.StatusDetail;
        if (string.IsNullOrEmpty(detail)) return;

        MessageBox.Show(detail, Lang.T("error_detail"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
