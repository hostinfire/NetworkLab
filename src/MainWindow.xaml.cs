using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using Microsoft.Win32;
using SiscoNet.Core;

namespace SiscoNet;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly NetworkSimulator _simulator = new();
    private readonly ObservableCollection<SimulationEvent> _events = [];
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly HashSet<Guid> _selectedDeviceIds = [];
    private readonly Dictionary<Guid, Point> _dragOrigins = [];
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly DispatcherTimer _simulationTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DiscordPresenceService _discordPresence = new();
    private MultiplayerSession _multiplayer = new();
    private NetworkProject _project = new();
    private NetworkServiceEngine _serviceEngine = new(new NetworkProject());
    private CliSession _cli = null!;
    private DeviceModel? _selectedDevice;
    private NetworkInterfaceModel? _selectedInterface;
    private DeviceModel? _draggingDevice;
    private Point _dragStartPoint;
    private bool _dragUndoCaptured;
    private bool _dragUsesGlobalLock;
    private string? _pendingKind;
    private Guid? _connectStart;
    private string? _projectPath;
    private HelpCenterWindow? _helpCenter;
    private DiscordPresenceSettings _discordSettings = new();
    private int _simulationStartIndex;
    private int _simulationEndIndex = -1;
    private int _simulationCursor = -1;
    private Guid? _activeEventDeviceId;
    private bool _darkTheme;
    private bool _loadingInspector;
    private int _addressCounter = 10;

    public MainWindow()
    {
        InitializeComponent();
        _serviceEngine = new NetworkServiceEngine(_project);
        _cli = new CliSession(_project, _simulator, _serviceEngine);
        _autosaveTimer.Tick += (_, _) => SaveAutosave();
        _autosaveTimer.Start();
        _simulationTimer.Tick += (_, _) => AdvanceSimulation();
        _discordPresence.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() => DiscordStatusText.Text = status));
        WireMultiplayerEvents();
        Closed += async (_, _) => { SaveAutosave(); await _multiplayer.DisposeAsync(); _discordPresence.Dispose(); };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BuildPalette();
        RenderTopology();
        CliOutput.Text = "Northstar Network Lab CLI\r\nSelect a device in the topology to open its command session.\r\n";
        UpdatePrompt();
        var recoveryPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NorthstarNetworkLab", "autosave.nslab");
        if (File.Exists(recoveryPath) && MessageBox.Show(this, "A recovery topology is available. Restore it?", "Recover autosave", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try
            {
                _project = ProjectFileService.Load(recoveryPath);
                _serviceEngine = new NetworkServiceEngine(_project);
                RefreshOperationalState();
                _cli = new CliSession(_project, _simulator, _serviceEngine);
                SelectDevice(null);
                RenderTopology();
                StatusText.Text = "Recovered the last autosaved topology.";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not recover topology", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        _discordSettings = DiscordPresenceSettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(_discordSettings.ApplicationId))
        {
            try { _discordPresence.Configure(_discordSettings.ApplicationId); }
            catch (ArgumentException) { DiscordStatusText.Text = "Invalid Discord Application ID"; }
        }
        UpdateDiscordActivity();
    }

    private void BuildPalette(string filter = "")
    {
        PalettePanel.Children.Clear();
        var categories = new (string Name, string[] Kinds)[]
        {
            ("ENDPOINTS", new[] { "PC", "Laptop", "Server", "Smartphone", "Tablet", "Printer", "IP Phone" }),
            ("NETWORK", new[] { "Hub", "Switch", "Layer 3 Switch", "Router", "Wireless Router", "Access Point", "Firewall", "Modem", "Network Device" }),
            ("IoT", new[] { "Smart Light", "Fan", "Door", "Motion Sensor", "Temperature Sensor", "Camera", "Smart Appliance", "IoT Gateway", "Microcontroller", "Single-board Computer" })
        };
        foreach (var category in categories)
        {
            var kinds = category.Kinds.Where(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (kinds.Length == 0) continue;
            PalettePanel.Children.Add(new TextBlock
            {
                Text = category.Name, FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("MutedTextBrush"), Margin = new Thickness(5, 11, 4, 4)
            });
            foreach (var kind in kinds)
            {
                var button = new Button
                {
                    Content = $"+  {kind}", HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(1), Padding = new Thickness(9, 6, 5, 6), ToolTip = $"Add {kind} to the canvas"
                };
                button.Click += (_, _) =>
                {
                    _pendingKind = kind;
                    WorkspaceStatus.Text = $"Click the canvas to place a {kind}";
                    StatusText.Text = $"Placement mode: {kind}. Press Escape to cancel.";
                };
                PalettePanel.Children.Add(button);
            }
        }
    }

    private void PaletteSearch_TextChanged(object sender, TextChangedEventArgs e) => BuildPalette(PaletteSearch.Text.Trim());

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != TopologyCanvas) return;
        var point = e.GetPosition(TopologyCanvas);
        if (_pendingKind is not null)
        {
            AddDevice(_pendingKind, point.X, point.Y);
            _pendingKind = null;
            WorkspaceStatus.Text = "Device added";
            return;
        }
        SelectDevice(null);
    }

    private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != TopologyCanvas) return;
        var point = e.GetPosition(TopologyCanvas);
        var menu = new ContextMenu();
        foreach (var kind in new[] { "PC", "Switch", "Router", "Server" })
        {
            var item = new MenuItem { Header = $"Add {kind} here" };
            item.Click += (_, _) => AddDevice(kind, point.X, point.Y);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var annotation = new MenuItem { Header = "Add annotation here" };
        annotation.Click += (_, _) => AddAnnotation(point);
        menu.Items.Add(annotation);
        menu.IsOpen = true;
    }

    private void AddDevice(string kind, double x, double y)
    {
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        if (SnapCheckBox.IsChecked == true) { x = Snap(x); y = Snap(y); }
        var device = DeviceCatalog.Create(kind, _project.Devices.Count + 1, Math.Max(10, x - 68), Math.Max(10, y - 31));
        if (kind is "Router" or "Layer 3 Switch") device.Interfaces[0].Ipv4Address = "192.168.10.1";
        else if (kind is "PC" or "Laptop" or "Server" or "Smartphone" or "Tablet" or "Printer" or "IP Phone" or "Single-board Computer")
        {
            device.Interfaces[0].Ipv4Address = $"192.168.10.{_addressCounter++}";
            device.DefaultGateway = "192.168.10.1";
        }
        _project.Devices.Add(device);
        PublishGlobalChange();
        RenderTopology();
        SelectDevice(device);
        StatusText.Text = $"Added {device.Name}. Configure an address, then connect it to another device.";
    }

    private void RenderTopology()
    {
        TopologyCanvas.Children.Clear();
        foreach (var annotation in _project.Annotations)
        {
            var globallyLocked = _multiplayer.IsLockedByOther(Guid.Empty);
            var note = new Border
            {
                MaxWidth = 280,
                Padding = new Thickness(9, 6, 9, 6),
                Background = new SolidColorBrush(Color.FromRgb(255, 247, 210)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 195, 119)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Opacity = globallyLocked ? 0.48 : 1,
                IsHitTestVisible = !globallyLocked,
                Tag = annotation,
                Child = new TextBlock { Text = annotation.Text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(62, 56, 35)) },
                ToolTip = "Double-click to edit; right-click to remove"
            };
            Canvas.SetLeft(note, annotation.X);
            Canvas.SetTop(note, annotation.Y);
            note.MouseLeftButtonUp += Annotation_MouseLeftButtonUp;
            note.MouseRightButtonUp += Annotation_MouseRightButtonUp;
            TopologyCanvas.Children.Add(note);
        }
        foreach (var link in _project.Links)
        {
            var first = _project.Devices.FirstOrDefault(d => d.Id == link.ADeviceId);
            var second = _project.Devices.FirstOrDefault(d => d.Id == link.BDeviceId);
            if (first is null || second is null) continue;
            TopologyCanvas.Children.Add(new Line
            {
                X1 = first.X + 68, Y1 = first.Y + 31, X2 = second.X + 68, Y2 = second.Y + 31,
                Stroke = _multiplayer.IsLockedByOther(Guid.Empty) ? Brushes.Gray : link.IsUp ? new SolidColorBrush(Color.FromRgb(8, 126, 139)) : Brushes.IndianRed,
                StrokeThickness = 2,
                Opacity = _multiplayer.IsLockedByOther(Guid.Empty) ? 0.45 : 1,
                Tag = link,
                ToolTip = $"{first.Name}:{link.AInterface} <-> {second.Name}:{link.BInterface}"
            });
        }
        foreach (var device in _project.Devices)
        {
            var selected = _selectedDeviceIds.Contains(device.Id);
            var remotelyLocked = _multiplayer.IsLockedByOther(device.Id);
            var lockOwner = remotelyLocked ? _multiplayer.GetLockOwner(device.Id) : null;
            var border = new Border
            {
                Width = 136, Height = 62, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = selected || device.Id == _activeEventDeviceId ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(186, 198, 204)),
                Background = remotelyLocked ? new SolidColorBrush(Color.FromRgb(125, 132, 135)) : (Brush)FindResource("PanelBrush"),
                Opacity = remotelyLocked ? 0.48 : 1,
                Tag = device,
                Cursor = remotelyLocked ? Cursors.No : _connectStart is null ? Cursors.SizeAll : Cursors.Cross,
                ToolTip = $"{device.Name} - {device.Kind}\n{device.Interfaces.FirstOrDefault()?.Ipv4Address ?? "No IPv4 address"}{(device.Groups.Count > 0 ? $"\nGroups: {string.Join(", ", device.Groups)}" : "")}{(remotelyLocked ? $"\nIn use by {lockOwner}; currently unmodifiable" : "") }"
            };
            var panel = new DockPanel { Margin = new Thickness(7, 5, 5, 5) };
            var badge = new Border
            {
                Width = 36, Height = 36, Background = GetDeviceBrush(device.Kind), CornerRadius = new CornerRadius(3),
                Child = new TextBlock
                {
                    Text = DeviceBadge(device.Kind), Foreground = Brushes.White, FontSize = 11,
                    FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(badge, Dock.Left);
            panel.Children.Add(badge);
            var labels = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = device.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock
            {
                Text = $"{device.Kind}  |  {device.Interfaces.FirstOrDefault()?.Ipv4Address ?? "unassigned"}",
                FontSize = 10, Foreground = (Brush)FindResource("MutedTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis
            });
            panel.Children.Add(labels);
            border.Child = panel;
            Canvas.SetLeft(border, device.X);
            Canvas.SetTop(border, device.Y);
            border.MouseLeftButtonDown += Device_MouseLeftButtonDown;
            border.MouseMove += Device_MouseMove;
            border.MouseLeftButtonUp += Device_MouseLeftButtonUp;
            border.MouseRightButtonUp += Device_MouseRightButtonUp;
            TopologyCanvas.Children.Add(border);
        }
        ProjectNameText.Text = _project.Name;
    }

    private void Device_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: DeviceModel device } border) return;
        if (_multiplayer.IsLockedByOther(device.Id))
        {
            StatusText.Text = $"{device.Name} is currently in use by {_multiplayer.GetLockOwner(device.Id)}.";
            e.Handled = true;
            return;
        }
        if (_pendingKind is not null)
        {
            AddDevice(_pendingKind, device.X + 145, device.Y + 35);
            _pendingKind = null;
            e.Handled = true;
            return;
        }
        if (_connectStart is not null)
        {
            if (_connectStart == device.Id)
            {
                _connectStart = null;
                StatusText.Text = "Connection cancelled.";
                RenderTopology();
            }
            else CompleteConnection(device);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ToggleDeviceSelection(device);
        else if (!_selectedDeviceIds.Contains(device.Id)) SelectDevice(device);
        else if (!TryAcquireDeviceLock(device)) { e.Handled = true; return; }
        if (!_selectedDeviceIds.Contains(device.Id) || _multiplayer.IsLockedByOther(device.Id)) { e.Handled = true; return; }
        _dragUsesGlobalLock = _selectedDeviceIds.Count > 1;
        if (_dragUsesGlobalLock && !TryAcquireGlobalLock()) { _dragUsesGlobalLock = false; e.Handled = true; return; }
        if (e.ClickCount == 2) { e.Handled = true; return; }
        _draggingDevice = device;
        _dragUndoCaptured = false;
        _dragStartPoint = e.GetPosition(TopologyCanvas);
        _dragOrigins.Clear();
        foreach (var selectedId in _selectedDeviceIds)
            if (_project.Devices.FirstOrDefault(item => item.Id == selectedId) is { } selectedDevice)
                _dragOrigins[selectedId] = new Point(selectedDevice.X, selectedDevice.Y);
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Device_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingDevice is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(TopologyCanvas);
        var deltaX = point.X - _dragStartPoint.X;
        var deltaY = point.Y - _dragStartPoint.Y;
        if (!_dragUndoCaptured && (Math.Abs(deltaX) > 1 || Math.Abs(deltaY) > 1))
        {
            PushUndo();
            _dragUndoCaptured = true;
        }
        foreach (var selected in _project.Devices.Where(device => _dragOrigins.ContainsKey(device.Id)))
        {
            var origin = _dragOrigins[selected.Id];
            var x = Math.Max(0, origin.X + deltaX);
            var y = Math.Max(0, origin.Y + deltaY);
            selected.X = SnapCheckBox.IsChecked == true ? Snap(x) : x;
            selected.Y = SnapCheckBox.IsChecked == true ? Snap(y) : y;
            var visual = TopologyCanvas.Children.OfType<Border>().FirstOrDefault(border => border.Tag is DeviceModel visualDevice && visualDevice.Id == selected.Id);
            if (visual is not null) { Canvas.SetLeft(visual, selected.X); Canvas.SetTop(visual, selected.Y); }
        }
        RedrawLinks();
    }

    private void Device_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border) border.ReleaseMouseCapture();
        if (_draggingDevice is not null)
        {
            if (_dragUndoCaptured)
            {
                if (_dragUsesGlobalLock) PublishGlobalChange();
                else PublishDeviceChange(_draggingDevice);
            }
            if (_dragUsesGlobalLock) _multiplayer.ReleaseLock(Guid.Empty);
            _dragUsesGlobalLock = false;
            _draggingDevice = null;
            RenderTopology();
        }
    }

    private void Device_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: DeviceModel device }) return;
        if (_multiplayer.IsLockedByOther(device.Id))
        {
            StatusText.Text = $"{device.Name} is currently in use by {_multiplayer.GetLockOwner(device.Id)}.";
            e.Handled = true;
            return;
        }
        if (!_selectedDeviceIds.Contains(device.Id)) SelectDevice(device);
        var menu = new ContextMenu();
        var duplicate = new MenuItem { Header = "Duplicate device" };
        duplicate.Click += (_, _) => DuplicateSelected();
        var connect = new MenuItem { Header = "Connect to..." };
        connect.Click += (_, _) => BeginConnection();
        var delete = new MenuItem { Header = "Delete device" };
        delete.Click += (_, _) => DeleteSelected();
        menu.Items.Add(duplicate);
        menu.Items.Add(connect);
        var group = new MenuItem { Header = "Group selected..." };
        group.Click += (_, _) => GroupSelected_Click(this, new RoutedEventArgs());
        menu.Items.Add(group);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RedrawLinks()
    {
        foreach (var line in TopologyCanvas.Children.OfType<Line>())
        {
            if (line.Tag is not LinkModel link) continue;
            var first = _project.Devices.FirstOrDefault(d => d.Id == link.ADeviceId);
            var second = _project.Devices.FirstOrDefault(d => d.Id == link.BDeviceId);
            if (first is not null) { line.X1 = first.X + 68; line.Y1 = first.Y + 31; }
            if (second is not null) { line.X2 = second.X + 68; line.Y2 = second.Y + 31; }
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connectStart is null) BeginConnection();
        else
        {
            _connectStart = null;
            ConnectButton.Background = Brushes.White;
            StatusText.Text = "Connection tool cancelled.";
            RenderTopology();
        }
    }

    private void BeginConnection()
    {
        if (_selectedDevice is null) { StatusText.Text = "Select a starting device, then choose Connect."; return; }
        _connectStart = _selectedDevice.Id;
        ConnectButton.Background = new SolidColorBrush(Color.FromRgb(190, 231, 232));
        StatusText.Text = $"Choose a second device to connect to {_selectedDevice.Name}.";
        RenderTopology();
    }

    private void CompleteConnection(DeviceModel second)
    {
        var first = _project.Devices.FirstOrDefault(d => d.Id == _connectStart);
        if (first is null) { _connectStart = null; return; }
        if (!TryAcquireGlobalLock()) { _connectStart = null; RenderTopology(); return; }
        var firstPort = _selectedInterface is not null && first.Interfaces.Contains(_selectedInterface) && IsPortAvailable(first, _selectedInterface)
            ? _selectedInterface : FindAvailablePort(first);
        var secondPort = FindAvailablePort(second);
        if (firstPort is null || secondPort is null)
        {
            _multiplayer.ReleaseLock(Guid.Empty);
            StatusText.Text = "No unused interfaces are available on one of these devices.";
            _connectStart = null;
            RenderTopology();
            return;
        }
        PushUndo();
        var cableType = (CableTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ethernet straight-through";
        _project.Links.Add(new LinkModel { ADeviceId = first.Id, AInterface = firstPort.Name, BDeviceId = second.Id, BInterface = secondPort.Name, CableType = cableType });
        firstPort.LinkUp = true;
        secondPort.LinkUp = true;
        PublishGlobalChange();
        _connectStart = null;
        ConnectButton.Background = Brushes.White;
        StatusText.Text = $"Connected {first.Name}:{firstPort.Name} to {second.Name}:{secondPort.Name}.";
        RenderTopology();
    }

    private NetworkInterfaceModel? FindAvailablePort(DeviceModel device)
    {
        var occupied = _project.Links.SelectMany(l =>
            (l.ADeviceId == device.Id ? new[] { l.AInterface } : Array.Empty<string>())
            .Concat(l.BDeviceId == device.Id ? new[] { l.BInterface } : Array.Empty<string>())).ToHashSet();
        return device.Interfaces.FirstOrDefault(i => !occupied.Contains(i.Name));
    }

    private bool IsPortAvailable(DeviceModel device, NetworkInterfaceModel networkInterface) =>
        !_project.Links.Any(link => (link.ADeviceId == device.Id && link.AInterface == networkInterface.Name) ||
                                    (link.BDeviceId == device.Id && link.BInterface == networkInterface.Name));

    private void SelectDevice(DeviceModel? device)
    {
        var priorIds = _selectedDeviceIds.ToArray();
        if (device is not null && !_selectedDeviceIds.Contains(device.Id) && !TryAcquireDeviceLock(device)) return;
        _selectedDeviceIds.Clear();
        if (device is not null) _selectedDeviceIds.Add(device.Id);
        foreach (var priorId in priorIds.Where(id => id != device?.Id)) _multiplayer.ReleaseLock(priorId);
        SetInspectorDevice(device);
    }

    private void ToggleDeviceSelection(DeviceModel device)
    {
        if (_selectedDeviceIds.Contains(device.Id))
        {
            _selectedDeviceIds.Remove(device.Id);
            _multiplayer.ReleaseLock(device.Id);
        }
        else
        {
            if (!TryAcquireDeviceLock(device)) return;
            _selectedDeviceIds.Add(device.Id);
        }
        var primary = _selectedDeviceIds.Contains(device.Id) ? device : _project.Devices.FirstOrDefault(item => _selectedDeviceIds.Contains(item.Id));
        SetInspectorDevice(primary);
        SelectionHint.Text = _selectedDeviceIds.Count > 1 ? $"{_selectedDeviceIds.Count} devices selected; inspector shows {_selectedDevice?.Name}." :
            primary is null ? "Select a device to inspect its configuration." : $"{primary.Name} | {primary.Interfaces.Count} interface(s)";
    }

    private void SetInspectorDevice(DeviceModel? device)
    {
        _selectedDevice = device;
        _selectedInterface = device?.Interfaces.FirstOrDefault();
        _cli.SelectDevice(device);
        _loadingInspector = true;
        DeviceNameBox.Text = device?.Name ?? "";
        DeviceTypeText.Text = device?.Kind ?? "-";
        SelectionHint.Text = device is null ? "Select a device to inspect its configuration." : $"{device.Name} | {device.Interfaces.Count} interface(s)";
        InterfaceCombo.ItemsSource = device?.Interfaces;
        InterfaceCombo.DisplayMemberPath = nameof(NetworkInterfaceModel.Name);
        InterfaceCombo.SelectedItem = _selectedInterface;
        GatewayBox.Text = device?.DefaultGateway ?? "";
        LoadAdvancedConfiguration(device);
        LoadServiceConfiguration(device);
        LoadInterfaceToInspector();
        _loadingInspector = false;
        UpdatePrompt();
        UpdateDiscordActivity();
        UpdateInspectorLockState();
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var border in TopologyCanvas.Children.OfType<Border>().Where(b => b.Tag is DeviceModel))
        {
            var isSelected = _selectedDeviceIds.Contains(((DeviceModel)border.Tag).Id);
            border.BorderThickness = new Thickness(isSelected ? 2 : 1);
            var isActive = ((DeviceModel)border.Tag).Id == _activeEventDeviceId;
            var remotelyLocked = _multiplayer.IsLockedByOther(((DeviceModel)border.Tag).Id);
            border.Opacity = remotelyLocked ? 0.48 : 1;
            border.Background = remotelyLocked ? new SolidColorBrush(Color.FromRgb(125, 132, 135)) : (Brush)FindResource("PanelBrush");
            border.BorderBrush = remotelyLocked ? Brushes.Gray : isSelected || isActive ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(186, 198, 204));
        }
    }

    private void UpdateInspectorLockState() =>
        InspectorContentPanel.IsEnabled = _selectedDevice is not null && !_multiplayer.IsLockedByOther(_selectedDevice.Id);

    private void InterfaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInspector) return;
        _selectedInterface = InterfaceCombo.SelectedItem as NetworkInterfaceModel;
        LoadInterfaceToInspector();
        LoadAdvancedConfiguration(_selectedDevice);
    }

    private void LoadAdvancedConfiguration(DeviceModel? device)
    {
        var iface = _selectedInterface;
        Ipv6AddressBox.Text = iface?.Ipv6Address ?? "";
        Ipv6PrefixBox.Text = iface?.Ipv6PrefixLength.ToString() ?? "64";
        Ipv6GatewayBox.Text = device?.DefaultIpv6Gateway ?? "";
        PortModeCombo.SelectedIndex = iface?.PortMode.Equals("trunk", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
        AllowedVlansBox.Text = iface?.AllowedVlans ?? "1-4094";
        NativeVlanBox.Text = iface?.NativeVlan.ToString() ?? "1";
        MaximumMacBox.Text = iface?.MaximumMacAddresses.ToString() ?? "1";
        PortSecurityBox.IsChecked = iface?.PortSecurityEnabled ?? false;
        StpStateCombo.SelectedIndex = iface?.StpAutomatic == false
            ? iface.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase) ? 2 : 1
            : 0;
        BundleGroupBox.Text = iface?.BundleGroup ?? "";
        RipEnabledBox.IsChecked = device?.DynamicRouting.RipEnabled ?? false;
        OspfEnabledBox.IsChecked = device?.DynamicRouting.OspfEnabled ?? false;
        BgpEnabledBox.IsChecked = device?.DynamicRouting.BgpEnabled ?? false;
        BgpAsBox.Text = device?.DynamicRouting.AutonomousSystem.ToString() ?? "65001";
        AdvertisedNetworksBox.Text = string.Join(Environment.NewLine, device?.DynamicRouting.AdvertisedNetworks ?? []);
        BgpNeighborsBox.Text = string.Join(Environment.NewLine, device?.DynamicRouting.BgpNeighbors ?? []);
        WirelessEnabledBox.IsChecked = device?.Wireless.Enabled ?? false;
        WirelessSsidBox.Text = device?.Wireless.Ssid ?? "Northstar-Lab";
        WirelessSecurityBox.Text = device?.Wireless.SecurityMode ?? "WPA2-Personal";
        WirelessPassphraseBox.Password = device?.Wireless.Passphrase ?? "";
        WirelessRangeBox.Text = device?.Wireless.RangeMeters.ToString() ?? "30";
        NatEnabledBox.IsChecked = device?.Nat.Enabled ?? false;
        NatInsideNetworkBox.Text = device?.Nat.InsideNetwork ?? "";
        NatInsideMaskBox.Text = device?.Nat.InsideMask ?? "255.255.255.0";
        NatOutsideAddressBox.Text = device?.Nat.OutsideAddress ?? "";
        NatInsideInterfaceCombo.ItemsSource = device?.Interfaces;
        NatOutsideInterfaceCombo.ItemsSource = device?.Interfaces;
        NatInsideInterfaceCombo.DisplayMemberPath = nameof(NetworkInterfaceModel.Name);
        NatOutsideInterfaceCombo.DisplayMemberPath = nameof(NetworkInterfaceModel.Name);
        NatInsideInterfaceCombo.SelectedItem = device?.Interfaces.FirstOrDefault(item => item.Name == device.Nat.InsideInterface);
        NatOutsideInterfaceCombo.SelectedItem = device?.Interfaces.FirstOrDefault(item => item.Name == device.Nat.OutsideInterface);
        AccessRulesBox.Text = string.Join(Environment.NewLine, device?.AccessRules.Select(rule =>
            $"{(rule.Permit ? "permit" : "deny")}|{rule.Protocol}|{rule.SourceNetwork}|{rule.SourceMask}|{rule.DestinationNetwork}|{rule.DestinationMask}|{rule.DestinationPort?.ToString() ?? "*"}") ?? []);

        var enabled = device is not null;
        foreach (var control in new UIElement[] { Ipv6AddressBox, Ipv6PrefixBox, Ipv6GatewayBox, PortModeCombo, AllowedVlansBox, NativeVlanBox,
                     MaximumMacBox, PortSecurityBox, StpStateCombo, BundleGroupBox, RipEnabledBox, OspfEnabledBox, BgpEnabledBox, BgpAsBox,
                     AdvertisedNetworksBox, BgpNeighborsBox, WirelessEnabledBox, WirelessSsidBox, WirelessSecurityBox, WirelessPassphraseBox,
                     WirelessRangeBox, NatEnabledBox, NatInsideNetworkBox, NatInsideMaskBox, NatInsideInterfaceCombo, NatOutsideInterfaceCombo,
                     NatOutsideAddressBox, AccessRulesBox })
            control.IsEnabled = enabled;
    }

    private void ApplyAdvancedConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null || _selectedInterface is null) return;
        if (_multiplayer.IsLockedByOther(_selectedDevice.Id) || !TryAcquireDeviceLock(_selectedDevice)) return;
        var ipv6Address = Ipv6AddressBox.Text.Trim();
        var ipv6Gateway = Ipv6GatewayBox.Text.Trim();
        if ((!string.IsNullOrWhiteSpace(ipv6Address) && !IpAddressing.IsValidIpv6(ipv6Address)) ||
            (!string.IsNullOrWhiteSpace(ipv6Gateway) && !IpAddressing.IsValidIpv6(ipv6Gateway)) ||
            !int.TryParse(Ipv6PrefixBox.Text, out var ipv6Prefix) || ipv6Prefix is < 0 or > 128)
        {
            MessageBox.Show(this, "Enter valid IPv6 addresses and a prefix length from 0 to 128.", "Invalid IPv6 configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(VlanBox.Text, out var vlan) || vlan is < 1 or > 4094 ||
            !int.TryParse(NativeVlanBox.Text, out var nativeVlan) || nativeVlan is < 1 or > 4094 ||
            !int.TryParse(MaximumMacBox.Text, out var maximumMacs) || maximumMacs is < 1 or > 4096 ||
            !int.TryParse(BgpAsBox.Text, out var autonomousSystem) || autonomousSystem < 1 ||
            !int.TryParse(WirelessRangeBox.Text, out var wirelessRange) || wirelessRange < 1)
        {
            MessageBox.Show(this, "Check VLAN, port-security, BGP AS, and wireless range values.", "Invalid network settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var advertised = ParseLines(AdvertisedNetworksBox.Text);
        var neighbors = ParseLines(BgpNeighborsBox.Text);
        if (advertised.Any(network => !IpAddressing.IsValidIpv4(network)) || neighbors.Any(neighbor => !IpAddressing.IsValidIpv4(neighbor)))
        {
            MessageBox.Show(this, "Advertised networks and BGP neighbors must be IPv4 addresses (one per line).", "Invalid routing configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var accessRules = ParseAccessRules(AccessRulesBox.Text, out var rulesValid);
        if (!rulesValid)
        {
            MessageBox.Show(this, "ACL entries must be permit|protocol|source|mask|destination|mask|port (use * for any port).", "Invalid ACL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var portMode = ((ComboBoxItem)PortModeCombo.SelectedItem).Content.ToString() ?? "access";
        var portVlan = new NetworkInterfaceModel { PortMode = portMode, Vlan = vlan, NativeVlan = nativeVlan, AllowedVlans = AllowedVlansBox.Text.Trim() };
        if (portMode == "trunk" && (!SwitchingEngine.AllowsVlan(portVlan, vlan) || !SwitchingEngine.AllowsVlan(portVlan, nativeVlan)))
        {
            MessageBox.Show(this, "The trunk allowed-VLAN list must include this port's VLAN and native VLAN.", "Invalid trunk VLAN list", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var natEnabled = NatEnabledBox.IsChecked == true;
        var natInside = NatInsideInterfaceCombo.SelectedItem as NetworkInterfaceModel;
        var natOutside = NatOutsideInterfaceCombo.SelectedItem as NetworkInterfaceModel;
        if (natEnabled && (!IpAddressing.IsValidIpv4(NatInsideNetworkBox.Text.Trim()) || !IpAddressing.IsValidSubnetMask(NatInsideMaskBox.Text.Trim()) ||
            !IpAddressing.IsValidIpv4(NatOutsideAddressBox.Text.Trim()) || natInside is null || natOutside is null || natInside == natOutside))
        {
            MessageBox.Show(this, "Enabled NAT requires valid inside network/mask, outside IPv4 address, and distinct inside/outside interfaces.", "Invalid NAT configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PushUndo();
        _selectedInterface.Ipv6Address = ipv6Address;
        _selectedInterface.Ipv6PrefixLength = ipv6Prefix;
        _selectedInterface.Vlan = vlan;
        _selectedInterface.PortMode = portMode;
        _selectedInterface.AllowedVlans = AllowedVlansBox.Text.Trim();
        _selectedInterface.NativeVlan = nativeVlan;
        _selectedInterface.PortSecurityEnabled = PortSecurityBox.IsChecked == true;
        _selectedInterface.MaximumMacAddresses = maximumMacs;
        var stpSelection = ((ComboBoxItem)StpStateCombo.SelectedItem).Content.ToString() ?? "automatic";
        _selectedInterface.StpAutomatic = stpSelection == "automatic";
        _selectedInterface.StpState = _selectedInterface.StpAutomatic ? "forwarding" : stpSelection;
        _selectedInterface.BundleGroup = BundleGroupBox.Text.Trim();
        _selectedDevice.DefaultIpv6Gateway = ipv6Gateway;
        _selectedDevice.DynamicRouting.RipEnabled = RipEnabledBox.IsChecked == true;
        _selectedDevice.DynamicRouting.OspfEnabled = OspfEnabledBox.IsChecked == true;
        _selectedDevice.DynamicRouting.BgpEnabled = BgpEnabledBox.IsChecked == true;
        _selectedDevice.DynamicRouting.AutonomousSystem = autonomousSystem;
        _selectedDevice.DynamicRouting.AdvertisedNetworks = advertised;
        _selectedDevice.DynamicRouting.BgpNeighbors = neighbors;
        _selectedDevice.Wireless.Enabled = WirelessEnabledBox.IsChecked == true;
        _selectedDevice.Wireless.Ssid = WirelessSsidBox.Text.Trim();
        _selectedDevice.Wireless.SecurityMode = WirelessSecurityBox.Text.Trim();
        _selectedDevice.Wireless.Passphrase = WirelessPassphraseBox.Password;
        _selectedDevice.Wireless.RangeMeters = wirelessRange;
        _selectedDevice.AccessRules = accessRules;
        _selectedDevice.Nat.Enabled = natEnabled;
        _selectedDevice.Nat.InsideNetwork = NatInsideNetworkBox.Text.Trim();
        _selectedDevice.Nat.InsideMask = NatInsideMaskBox.Text.Trim();
        _selectedDevice.Nat.OutsideAddress = NatOutsideAddressBox.Text.Trim();
        _selectedDevice.Nat.InsideInterface = natInside?.Name ?? "";
        _selectedDevice.Nat.OutsideInterface = natOutside?.Name ?? "";
        LoadInterfaceToInspector();
        PublishDeviceChange(_selectedDevice);
        RenderTopology();
        StatusText.Text = $"IPv6, switching, routing, wireless, ACL, and NAT settings applied to {_selectedDevice.Name}.";
    }

    private static List<string> ParseLines(string text) => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<AccessControlRule> ParseAccessRules(string text, out bool valid)
    {
        valid = true;
        var rules = new List<AccessControlRule>();
        foreach (var line in ParseLines(text))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 7 || parts[0] is not ("permit" or "deny") ||
                !IpAddressing.IsValidIpv4(parts[2]) || !IpAddressing.IsValidSubnetMask(parts[3]) ||
                !IpAddressing.IsValidIpv4(parts[4]) || !IpAddressing.IsValidSubnetMask(parts[5]) ||
                (parts[6] != "*" && (!int.TryParse(parts[6], out var port) || port is < 1 or > 65535)))
            {
                valid = false;
                continue;
            }
            rules.Add(new AccessControlRule
            {
                Permit = parts[0] == "permit", Protocol = parts[1], SourceNetwork = parts[2], SourceMask = parts[3],
                DestinationNetwork = parts[4], DestinationMask = parts[5], DestinationPort = parts[6] == "*" ? null : int.Parse(parts[6])
            });
        }
        return rules;
    }

    private void LoadInterfaceToInspector()
    {
        _loadingInspector = true;
        IpAddressBox.Text = _selectedInterface?.Ipv4Address ?? "";
        SubnetMaskBox.Text = _selectedInterface?.SubnetMask ?? "255.255.255.0";
        VlanBox.Text = _selectedInterface?.Vlan.ToString() ?? "1";
        InterfaceEnabledBox.IsChecked = _selectedInterface?.AdminUp ?? false;
        InterfaceStateText.Text = _selectedInterface is null ? "No interface selected" :
            $"{_selectedInterface.Name} | MAC {_selectedInterface.MacAddress}\n{_selectedInterface.SpeedMbps} Mbps | {_selectedInterface.Duplex} duplex | {(_selectedInterface.AdminUp && _selectedInterface.LinkUp ? "up/up" : "down/down")}";
        _loadingInspector = false;
    }

    private void LoadServiceConfiguration(DeviceModel? device)
    {
        var services = device?.Services ?? new NetworkServices();
        DhcpServiceCheckBox.IsChecked = services.DhcpEnabled;
        DnsServiceCheckBox.IsChecked = services.DnsEnabled;
        HttpServiceCheckBox.IsChecked = services.HttpEnabled;
        HttpsServiceCheckBox.IsChecked = services.HttpsEnabled;
        FtpServiceCheckBox.IsChecked = services.FtpEnabled;
        DhcpPoolStartBox.Text = services.DhcpPoolStart;
        DhcpPoolEndBox.Text = services.DhcpPoolEnd;
        DhcpMaskBox.Text = services.DhcpSubnetMask;
        DhcpGatewayBox.Text = services.DhcpGateway;
        DhcpDnsBox.Text = services.DhcpDnsServer;
        HttpContentBox.Text = services.HttpContent;
        DnsRecordsBox.Text = string.Join(Environment.NewLine, services.DnsRecords.Select(entry => $"{entry.Key}={entry.Value}"));
        FtpFilesBox.Text = string.Join(Environment.NewLine, services.FtpFiles.Where(entry => !entry.Key.StartsWith("__", StringComparison.Ordinal)).Select(entry => $"{entry.Key}={entry.Value}"));
        var enabled = device is not null;
        foreach (var control in new UIElement[] { DhcpServiceCheckBox, DnsServiceCheckBox, HttpServiceCheckBox, HttpsServiceCheckBox, FtpServiceCheckBox,
                     DhcpPoolStartBox, DhcpPoolEndBox, DhcpMaskBox, DhcpGatewayBox, DhcpDnsBox, HttpContentBox, DnsRecordsBox, FtpFilesBox })
            control.IsEnabled = enabled;
    }

    private void ApplyConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null || _selectedInterface is null) return;
        if (_multiplayer.IsLockedByOther(_selectedDevice.Id) || !TryAcquireDeviceLock(_selectedDevice)) return;
        if (!string.IsNullOrWhiteSpace(IpAddressBox.Text) && !IpAddressing.IsValidIpv4(IpAddressBox.Text.Trim()))
        {
            MessageBox.Show(this, "Enter a valid IPv4 address or leave it blank.", "Invalid address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!IpAddressing.IsValidIpv4(SubnetMaskBox.Text.Trim()))
        {
            MessageBox.Show(this, "Enter a valid IPv4 subnet mask.", "Invalid subnet mask", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(VlanBox.Text, out var vlan) || vlan is < 1 or > 4094)
        {
            MessageBox.Show(this, "VLAN must be between 1 and 4094.", "Invalid VLAN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PushUndo();
        _selectedDevice.Name = string.IsNullOrWhiteSpace(DeviceNameBox.Text) ? _selectedDevice.Name : DeviceNameBox.Text.Trim();
        _selectedDevice.DefaultGateway = GatewayBox.Text.Trim();
        _selectedInterface.Ipv4Address = IpAddressBox.Text.Trim();
        _selectedInterface.SubnetMask = SubnetMaskBox.Text.Trim();
        _selectedInterface.Vlan = vlan;
        _selectedInterface.AdminUp = InterfaceEnabledBox.IsChecked == true;
        RenderTopology();
        LoadInterfaceToInspector();
        PublishDeviceChange(_selectedDevice);
        UpdatePrompt();
        StatusText.Text = $"Configuration applied to {_selectedDevice.Name}:{_selectedInterface.Name}.";
    }

    private void ApplyServices_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) return;
        if (_multiplayer.IsLockedByOther(_selectedDevice.Id) || !TryAcquireDeviceLock(_selectedDevice)) return;
        if (!IpAddressing.IsValidIpv4(DhcpPoolStartBox.Text.Trim()) || !IpAddressing.IsValidIpv4(DhcpPoolEndBox.Text.Trim()) ||
            !IpAddressing.IsValidSubnetMask(DhcpMaskBox.Text.Trim()) ||
            (!string.IsNullOrWhiteSpace(DhcpGatewayBox.Text) && !IpAddressing.IsValidIpv4(DhcpGatewayBox.Text.Trim())) ||
            (!string.IsNullOrWhiteSpace(DhcpDnsBox.Text) && !IpAddressing.IsValidIpv4(DhcpDnsBox.Text.Trim())))
        {
            MessageBox.Show(this, "Check the DHCP pool addresses, subnet mask, gateway, and DNS address.", "Invalid service configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dnsRecords = ParseKeyValueLines(DnsRecordsBox.Text, out var invalidDns);
        if (invalidDns || dnsRecords.Any(entry => !IpAddressing.IsValidIpv4(entry.Value)))
        {
            MessageBox.Show(this, "DNS records must use one hostname=IPv4 address per line.", "Invalid DNS records", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var ftpFiles = ParseKeyValueLines(FtpFilesBox.Text, out var invalidFiles);
        if (invalidFiles)
        {
            MessageBox.Show(this, "FTP files must use one filename=contents entry per line.", "Invalid FTP files", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PushUndo();
        var services = _selectedDevice.Services;
        services.DhcpEnabled = DhcpServiceCheckBox.IsChecked == true;
        services.DnsEnabled = DnsServiceCheckBox.IsChecked == true;
        services.HttpEnabled = HttpServiceCheckBox.IsChecked == true;
        services.HttpsEnabled = HttpsServiceCheckBox.IsChecked == true;
        services.FtpEnabled = FtpServiceCheckBox.IsChecked == true;
        services.DhcpPoolStart = DhcpPoolStartBox.Text.Trim();
        services.DhcpPoolEnd = DhcpPoolEndBox.Text.Trim();
        services.DhcpSubnetMask = DhcpMaskBox.Text.Trim();
        services.DhcpGateway = DhcpGatewayBox.Text.Trim();
        services.DhcpDnsServer = DhcpDnsBox.Text.Trim();
        services.HttpContent = HttpContentBox.Text;
        services.DnsRecords = new Dictionary<string, string>(dnsRecords, StringComparer.OrdinalIgnoreCase);
        services.FtpFiles = new Dictionary<string, string>(ftpFiles, StringComparer.OrdinalIgnoreCase);
        PublishDeviceChange(_selectedDevice);
        StatusText.Text = $"Service configuration applied to {_selectedDevice.Name}.";
    }

    private static Dictionary<string, string> ParseKeyValueLines(string text, out bool invalid)
    {
        invalid = false;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) { invalid = true; continue; }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!values.TryAdd(key, value)) invalid = true;
        }
        return values;
    }

    private void Ping_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) { StatusText.Text = "Select a source device before starting a ping."; return; }
        var destination = Prompt("Ping", "Destination IPv4 address:", "192.168.10.11");
        if (destination is not null) RunPing(_selectedDevice, destination.Trim());
    }

    private void PingIpv6_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) { StatusText.Text = "Select a source device before starting an IPv6 ping."; return; }
        var destination = Prompt("IPv6 ping", "Destination IPv6 address:", "2001:db8::20");
        if (destination is null) return;
        var result = _simulator.PingIpv6(_project, _selectedDevice, destination.Trim());
        ShowSimulationEvents(result.Summary, result.Events, result.Events.Count == 0 ? result.Summary : FormatPacket(result.Events[^1]));
    }

    private void RunPing(DeviceModel source, string destination)
    {
        var result = _simulator.Ping(_project, source, destination);
        _simulationTimer.Stop();
        _simulationStartIndex = _events.Count;
        foreach (var item in result.Events) _events.Add(item);
        _simulationEndIndex = _events.Count - 1;
        _simulationCursor = _simulationStartIndex - 1;
        _activeEventDeviceId = null;
        EventsList.ItemsSource = null;
        EventsList.ItemsSource = _events;
        SimulationPositionText.Text = result.Events.Count == 0 ? "No events" : $"Ready · {result.Events.Count} events";
        StatusText.Text = result.Summary;
        PacketInspectorText.Text = result.Events.Count == 0 ? result.Summary : FormatPacket(result.Events[^1]);
        UpdateDiscordActivity($"Project: {_project.Name}", result.Success ? $"Ping succeeded · {destination}" : "Ping failed");
    }

    private void Dhcp_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) { StatusText.Text = "Select a DHCP client device first."; return; }
        ShowServiceResult(_serviceEngine.RequestDhcp(_selectedDevice));
        RenderTopology();
        LoadServiceConfiguration(_selectedDevice);
        LoadInterfaceToInspector();
    }

    private void Dns_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) { StatusText.Text = "Select a DNS client device first."; return; }
        var hostName = Prompt("DNS query", "Host name:", "lab.example");
        if (hostName is not null) ShowServiceResult(_serviceEngine.ResolveDns(_selectedDevice, hostName.Trim()));
    }

    private void Http_Click(object sender, RoutedEventArgs e) => RequestHttp(false);
    private void Https_Click(object sender, RoutedEventArgs e) => RequestHttp(true);
    private void RequestHttp(bool secure)
    {
        if (_selectedDevice is null) { StatusText.Text = "Select a network client first."; return; }
        var address = Prompt(secure ? "HTTPS request" : "HTTP request", "Server IPv4 address:", "192.168.10.20");
        if (address is not null) ShowServiceResult(_serviceEngine.RequestHttp(_selectedDevice, address.Trim(), secure));
    }

    private void Ftp_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) { StatusText.Text = "Select an FTP client first."; return; }
        var address = Prompt("FTP file listing", "FTP server IPv4 address:", "192.168.10.20");
        if (address is not null) ShowServiceResult(_serviceEngine.RequestFtp(_selectedDevice, address.Trim()));
    }

    private void Udp_Click(object sender, RoutedEventArgs e) => RequestTransport(false);
    private void Tcp_Click(object sender, RoutedEventArgs e) => RequestTransport(true);
    private void RequestTransport(bool tcp)
    {
        if (_selectedDevice is null) { StatusText.Text = "Select a source device first."; return; }
        var address = Prompt(tcp ? "TCP request" : "UDP datagram", "Destination IPv4 address:", "192.168.10.20");
        if (address is null || !int.TryParse(Prompt("Destination port", "Port:", tcp ? "8080" : "9000"), out var port)) return;
        var payload = Prompt("Payload", "Application data:", "Northstar test payload");
        if (payload is null) return;
        ShowServiceResult(tcp ? _serviceEngine.SendTcp(_selectedDevice, address.Trim(), port, payload) : _serviceEngine.SendUdp(_selectedDevice, address.Trim(), port, payload));
    }

    private void ShowServiceResult(ServiceResult result)
    {
        ShowSimulationEvents(result.Summary, result.Events, string.IsNullOrWhiteSpace(result.Data) ? result.Summary : result.Data);
    }

    private void ShowSimulationEvents(string summary, IReadOnlyList<SimulationEvent> events, string detail)
    {
        _simulationTimer.Stop();
        _simulationStartIndex = _events.Count;
        foreach (var item in events) _events.Add(item);
        _simulationEndIndex = _events.Count - 1;
        _simulationCursor = _simulationStartIndex - 1;
        _activeEventDeviceId = null;
        EventsList.ItemsSource = null;
        EventsList.ItemsSource = _events;
        SimulationPositionText.Text = events.Count == 0 ? "No events" : $"Ready · {events.Count} events";
        StatusText.Text = summary;
        EventDetailText.Text = detail;
        if (events.Count > 0) PacketInspectorText.Text = FormatPacket(events[^1]);
        UpdateDiscordActivity($"Project: {_project.Name}", summary);
    }

    private void WirelessJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) { StatusText.Text = "Select a wireless client first."; return; }
        if (_multiplayer.IsLockedByOther(_selectedDevice.Id)) { StatusText.Text = $"{_selectedDevice.Name} is locked by {_multiplayer.GetLockOwner(_selectedDevice.Id)}."; return; }
        var accessPointName = Prompt("Wireless association", "Access point device name:", "AP1");
        if (string.IsNullOrWhiteSpace(accessPointName)) return;
        var accessPoint = _project.Devices.FirstOrDefault(device => device.Name.Equals(accessPointName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (accessPoint is null) { StatusText.Text = $"Access point '{accessPointName}' not found."; return; }
        var passphrase = accessPoint.Wireless.SecurityMode.Equals("Open", StringComparison.OrdinalIgnoreCase)
            ? "" : Prompt("Wireless authentication", $"Passphrase for {accessPoint.Wireless.Ssid}:", "");
        if (passphrase is null) return;
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        var result = WirelessEngine.Associate(_project, _selectedDevice, accessPoint, passphrase);
        if (!result.Success) _undo.Pop();
        RefreshOperationalState();
        if (result.Success) PublishGlobalChange();
        else _multiplayer.ReleaseLock(Guid.Empty);
        RenderTopology();
        StatusText.Text = result.Message;
    }

    private void WirelessDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) return;
        var wirelessLink = _project.Links.FirstOrDefault(link => link.CableType.StartsWith("Wireless", StringComparison.OrdinalIgnoreCase) &&
            (link.ADeviceId == _selectedDevice.Id || link.BDeviceId == _selectedDevice.Id));
        if (wirelessLink is null) { StatusText.Text = "Selected device is not associated to a wireless access point."; return; }
        var accessPointId = wirelessLink.ADeviceId == _selectedDevice.Id ? wirelessLink.BDeviceId : wirelessLink.ADeviceId;
        var accessPoint = _project.Devices.FirstOrDefault(device => device.Id == accessPointId);
        if (accessPoint is null) return;
        if (_multiplayer.IsLockedByOther(_selectedDevice.Id) || _multiplayer.IsLockedByOther(accessPoint.Id))
        {
            StatusText.Text = "The wireless client or access point is locked by another peer.";
            return;
        }
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        WirelessEngine.Disconnect(_project, _selectedDevice, accessPoint);
        RefreshOperationalState();
        PublishGlobalChange();
        RenderTopology();
        StatusText.Text = $"Disconnected {_selectedDevice.Name} from {accessPoint.Name}.";
    }

    private void SetIotSensor_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null || !_selectedDevice.Kind.Contains("Sensor", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Select a sensor device first.";
            return;
        }
        var property = _selectedDevice.Kind == "Temperature Sensor" ? "temperature" : "value";
        var valueText = Prompt("IoT sensor reading", $"New {property} value:", "32");
        if (!double.TryParse(valueText, out var value)) return;
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        var actions = IotEngine.SetSensorValue(_project, _selectedDevice.Id, property, value);
        var events = new List<SimulationEvent>
        {
            new(DateTime.Now, $"IOT-{Guid.NewGuid():N}"[..12], _selectedDevice.Name, "Sensor update", $"{property}={value}.",
                "", "", "IoT telemetry", "", "", "", "", 0, 0, ApplicationData: $"{property}={value}")
        };
        events.AddRange(actions.Select(action => new SimulationEvent(DateTime.Now, $"IOT-{Guid.NewGuid():N}"[..12],
            _project.Devices.First(device => device.Id == action.TargetDeviceId).Name,
            action.Applied ? "Automation action" : "Automation error", action.Detail, "", "", "IoT automation", "", "", "", "", 0, 0,
            ApplicationData: action.Detail)));
        PublishGlobalChange();
        ShowSimulationEvents($"Updated {_selectedDevice.Name}.{property} to {value}.", events, string.Join(Environment.NewLine, actions.Select(action => action.Detail)));
    }

    private void ToggleIotActuator_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null || !IotEngine.IsActuator(_selectedDevice.Kind)) { StatusText.Text = "Select a supported IoT actuator first."; return; }
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        var enabled = !_selectedDevice.ActuatorEnabled;
        IotEngine.SetActuator(_selectedDevice, enabled);
        PublishGlobalChange();
        var item = new SimulationEvent(DateTime.Now, $"IOT-{Guid.NewGuid():N}"[..12], _selectedDevice.Name, "Actuator control",
            $"Actuator changed to {(enabled ? "ON" : "OFF")}.", "", "", "IoT control", "", "", "", "", 0, 0, ApplicationData: enabled ? "ON" : "OFF");
        ShowSimulationEvents($"{_selectedDevice.Name} is {(enabled ? "ON" : "OFF")}.", [item], item.Detail);
    }

    private void AddIotRule_Click(object sender, RoutedEventArgs e)
    {
        var sensors = _project.Devices.Where(device => device.Kind.Contains("Sensor", StringComparison.OrdinalIgnoreCase)).ToArray();
        var actuators = _project.Devices.Where(device => IotEngine.IsActuator(device.Kind)).ToArray();
        if (sensors.Length == 0 || actuators.Length == 0) { StatusText.Text = "Add an IoT sensor and actuator before creating an automation rule."; return; }
        var sensorName = Prompt("IoT automation", $"Sensor name ({string.Join(", ", sensors.Select(device => device.Name))}):", sensors[0].Name);
        var sensor = sensors.FirstOrDefault(device => device.Name.Equals(sensorName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (sensor is null) { StatusText.Text = "Sensor device not found."; return; }
        var property = Prompt("IoT automation", "Sensor property:", sensor.Kind == "Temperature Sensor" ? "temperature" : "value");
        if (string.IsNullOrWhiteSpace(property)) return;
        var comparison = Prompt("IoT automation", "Comparison (>, >=, <, <=, ==, !=):", ">");
        if (comparison is not (">" or ">=" or "<" or "<=" or "==" or "!=")) { StatusText.Text = "Unsupported comparison."; return; }
        var thresholdText = Prompt("IoT automation", "Threshold value:", "30");
        if (!double.TryParse(thresholdText, out var threshold)) { StatusText.Text = "Threshold must be numeric."; return; }
        var targetName = Prompt("IoT automation", $"Actuator name ({string.Join(", ", actuators.Select(device => device.Name))}):", actuators[0].Name);
        var target = actuators.FirstOrDefault(device => device.Name.Equals(targetName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null) { StatusText.Text = "Actuator device not found."; return; }
        var action = Prompt("IoT automation", "Action (on/off):", "on");
        if (action is not ("on" or "off")) { StatusText.Text = "Action must be on or off."; return; }
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        _project.IotRules.Add(new IotAutomationRule
        {
            SensorDeviceId = sensor.Id, Property = property.Trim(), Comparison = comparison,
            Threshold = threshold, TargetDeviceId = target.Id, Action = action
        });
        PublishGlobalChange();
        StatusText.Text = $"Added rule: {sensor.Name}.{property} {comparison} {threshold} -> {target.Name} {action}.";
    }

    private void IotDashboard_Click(object sender, RoutedEventArgs e)
    {
        var rows = _project.Devices.Where(device => device.SensorValues.Count > 0 || IotEngine.IsActuator(device.Kind))
            .SelectMany(device => device.SensorValues.Select(value => $"{device.Name}  {value.Key}: {value.Value:0.##}" )
                .Append(IotEngine.IsActuator(device.Kind) ? $"{device.Name}  actuator: {(device.ActuatorEnabled ? "ON" : "OFF")}" : ""))
            .Where(row => !string.IsNullOrWhiteSpace(row)).ToArray();
        var rules = _project.IotRules.Select(rule =>
        {
            var sensor = _project.Devices.FirstOrDefault(device => device.Id == rule.SensorDeviceId)?.Name ?? "(missing sensor)";
            var target = _project.Devices.FirstOrDefault(device => device.Id == rule.TargetDeviceId)?.Name ?? "(missing actuator)";
            return $"Rule: {sensor}.{rule.Property} {rule.Comparison} {rule.Threshold} -> {target} {rule.Action} ({(rule.Enabled ? "enabled" : "disabled")})";
        });
        var dashboard = string.Join(Environment.NewLine, rows.Concat(rules));
        MessageBox.Show(this, string.IsNullOrWhiteSpace(dashboard) ? "No sensor telemetry, actuators, or automation rules are present." : dashboard,
            "IoT dashboard", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CreateObjective_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null || _selectedInterface is null) { StatusText.Text = "Select a device and interface for the learning objective."; return; }
        var title = Prompt("New lab objective", "Objective title:", $"Configure {_selectedDevice.Name}");
        if (string.IsNullOrWhiteSpace(title)) return;
        var expectedIp = Prompt("Objective address", "Required IPv4 address:", _selectedInterface.Ipv4Address);
        var expectedMask = Prompt("Objective mask", "Required subnet mask:", _selectedInterface.SubnetMask);
        var expectedGateway = Prompt("Objective gateway", "Required gateway (blank for none):", _selectedDevice.DefaultGateway);
        if (expectedIp is null || expectedMask is null || expectedGateway is null) return;
        if ((!string.IsNullOrWhiteSpace(expectedIp) && !IpAddressing.IsValidIpv4(expectedIp)) ||
            (!string.IsNullOrWhiteSpace(expectedMask) && !IpAddressing.IsValidSubnetMask(expectedMask)) ||
            (!string.IsNullOrWhiteSpace(expectedGateway) && !IpAddressing.IsValidIpv4(expectedGateway)))
        {
            StatusText.Text = "Objective IPv4 values are invalid.";
            return;
        }
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        _project.Objectives.Add(new LabObjective
        {
            Title = title.Trim(), DeviceName = _selectedDevice.Name, InterfaceName = _selectedInterface.Name,
            ExpectedIpv4Address = expectedIp.Trim(), ExpectedSubnetMask = expectedMask.Trim(), ExpectedGateway = expectedGateway.Trim(),
            Description = $"Configure {_selectedDevice.Name}:{_selectedInterface.Name}."
        });
        PublishGlobalChange();
        StatusText.Text = $"Lab objective '{title.Trim()}' created.";
    }

    private void EvaluateLab_Click(object sender, RoutedEventArgs e)
    {
        var result = LabEvaluationEngine.Evaluate(_project);
        var summary = result.Objectives.Count == 0 ? "No lab objectives are configured." :
            $"Score: {result.Score}/{result.MaximumScore}{Environment.NewLine}{string.Join(Environment.NewLine, result.Objectives.Select(objective => $"{(objective.Complete ? "[x]" : "[ ]")} {objective.Title} ({objective.PointsEarned}/{objective.PointsAvailable})"))}";
        StatusText.Text = result.Objectives.Count == 0 ? "No lab objectives are configured." : $"Lab score: {result.Score}/{result.MaximumScore}.";
        MessageBox.Show(this, summary, "Lab evaluation", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowHint_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Objectives.Count == 0) { StatusText.Text = "No objective hints are available."; return; }
        var title = Prompt("Objective hint", "Objective title:", _project.Objectives[0].Title);
        if (title is null) return;
        var objective = _project.Objectives.FirstOrDefault(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (objective is null) { StatusText.Text = "Objective not found."; return; }
        MessageBox.Show(this, LabEvaluationEngine.GetHint(_project, objective.Id), objective.Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CliInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_selectedDevice is not null && _multiplayer.IsLockedByOther(_selectedDevice.Id))
        {
            CliOutput.AppendText($"% {_selectedDevice.Name} is locked by {_multiplayer.GetLockOwner(_selectedDevice.Id)}.{Environment.NewLine}");
            CliInput.Clear();
            e.Handled = true;
            return;
        }
        var command = CliInput.Text;
        CliOutput.AppendText($"{_cli.Prompt}{command}{Environment.NewLine}");
        var output = _cli.Execute(command,
            result => ShowSimulationEvents(result.Summary, result.Events, result.Events.Count == 0 ? result.Summary : FormatPacket(result.Events[^1])),
            ShowServiceResult);
        if (!string.IsNullOrWhiteSpace(output)) CliOutput.AppendText(output + Environment.NewLine);
        CliInput.Clear();
        UpdatePrompt();
        CliOutput.ScrollToEnd();
        RenderTopology();
        if (output.StartsWith("Reply from ", StringComparison.Ordinal)) StatusText.Text = output;
        if (_selectedDevice is not null && _multiplayer.IsSharing && _selectedDeviceIds.Contains(_selectedDevice.Id))
            PublishDeviceChange(_selectedDevice);
        e.Handled = true;
    }

    private void EventsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventsList.SelectedItem is not SimulationEvent item) return;
        EventDetailText.Text = string.Join(Environment.NewLine,
            $"{item.Time:HH:mm:ss.fff}  {item.Packet}", $"{item.Device}: {item.Action}", "", item.Detail, "", FormatPacket(item));
        PacketInspectorText.Text = FormatPacket(item);
    }

    private void SimulationPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_simulationEndIndex < _simulationStartIndex) { StatusText.Text = "Run a ping to create a simulation event sequence."; return; }
        if (_simulationTimer.IsEnabled)
        {
            _simulationTimer.Stop();
            StatusText.Text = "Simulation paused.";
            return;
        }
        if (_simulationCursor >= _simulationEndIndex) _simulationCursor = _simulationStartIndex - 1;
        _simulationTimer.Start();
        AdvanceSimulation();
    }

    private void SimulationStep_Click(object sender, RoutedEventArgs e)
    {
        _simulationTimer.Stop();
        if (_simulationEndIndex < _simulationStartIndex) { StatusText.Text = "Run a ping to create a simulation event sequence."; return; }
        if (_simulationCursor >= _simulationEndIndex) _simulationCursor = _simulationStartIndex - 1;
        AdvanceSimulation();
    }

    private void SimulationRestart_Click(object sender, RoutedEventArgs e)
    {
        _simulationTimer.Stop();
        _simulationCursor = _simulationStartIndex - 1;
        _activeEventDeviceId = null;
        EventsList.SelectedItem = null;
        SimulationPositionText.Text = _simulationEndIndex < _simulationStartIndex ? "No events" : $"Ready · {_simulationEndIndex - _simulationStartIndex + 1} events";
        UpdateSelectionVisuals();
        StatusText.Text = "Simulation returned to the start of the current sequence.";
    }

    private void SimulationSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SimulationSpeedCombo?.SelectedItem is ComboBoxItem { Tag: string milliseconds } && int.TryParse(milliseconds, out var interval))
            _simulationTimer.Interval = TimeSpan.FromMilliseconds(interval);
    }

    private void AdvanceSimulation()
    {
        if (_simulationCursor >= _simulationEndIndex)
        {
            _simulationTimer.Stop();
            SimulationPositionText.Text = $"Complete · {_simulationEndIndex - _simulationStartIndex + 1} events";
            return;
        }
        _simulationCursor++;
        if (_simulationCursor < 0 || _simulationCursor >= _events.Count) return;
        var item = _events[_simulationCursor];
        EventsList.SelectedItem = item;
        EventsList.ScrollIntoView(item);
        _activeEventDeviceId = _project.Devices.FirstOrDefault(d => d.Name == item.Device)?.Id;
        UpdateSelectionVisuals();
        SimulationPositionText.Text = $"Event {_simulationCursor - _simulationStartIndex + 1} / {_simulationEndIndex - _simulationStartIndex + 1}";
        StatusText.Text = $"{item.Device}: {item.Action}";
        UpdateDiscordActivity($"Project: {_project.Name}", $"{item.Device} · {item.Action}");
    }

    private static string FormatPacket(SimulationEvent item)
    {
        var isIpv6 = item.Protocol.Contains("IPv6", StringComparison.OrdinalIgnoreCase);
        var transport = string.IsNullOrWhiteSpace(item.Transport) ? "Not modeled" : item.Transport;
        var layer3 = isIpv6 ? "IPv6" : "IPv4";
        var ports = item.SourcePort == 0 && item.DestinationPort == 0 ? "" : $"  Ports {item.SourcePort} -> {item.DestinationPort}";
        return string.Join(Environment.NewLine,
            $"PACKET  {item.Packet}", $"Current device: {item.Device}", $"Action: {item.Action}", $"Protocol: {item.Protocol}", "",
            "OSI LAYERS", $"Layer 7  {item.Protocol.Split('/').Last().Trim()}", $"Layer 4  {transport}{ports}",
            $"Layer 3  {layer3}  {item.SourceIp} -> {item.DestinationIp}  TTL/Hop limit {item.Ttl}",
            $"Layer 2  Ethernet  {item.SourceMac} -> {item.DestinationMac}  VLAN {item.Vlan}",
            $"Layer 1  {item.IncomingInterface} -> {item.OutgoingInterface}",
            string.IsNullOrWhiteSpace(item.ApplicationData) ? "" : $"Application data: {item.ApplicationData}");
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!TryAcquireGlobalLock()) return;
        if (_project.Devices.Count > 0 && MessageBox.Show(this, "Discard the current unsaved topology?", "New topology", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _multiplayer.ReleaseLock(Guid.Empty);
            return;
        }
        PushUndo();
        _project = new NetworkProject();
        _serviceEngine = new NetworkServiceEngine(_project);
        _cli = new CliSession(_project, _simulator, _serviceEngine);
        _projectPath = null;
        _addressCounter = 10;
        _events.Clear();
        EventsList.ItemsSource = null;
        SelectDevice(null);
        CliOutput.Clear();
        RenderTopology();
        PublishGlobalChange();
        StatusText.Text = "New topology created.";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Northstar topology (*.nslab)|*.nslab;*.json|JSON project (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (!TryAcquireGlobalLock()) return;
        try
        {
            PushUndo();
            _project = ProjectFileService.Load(dialog.FileName);
            _serviceEngine = new NetworkServiceEngine(_project);
            RefreshOperationalState();
            _cli = new CliSession(_project, _simulator, _serviceEngine);
            _projectPath = dialog.FileName;
            _events.Clear();
            EventsList.ItemsSource = null;
            SelectDevice(null);
            RenderTopology();
            PublishGlobalChange();
            StatusText.Text = $"Opened {System.IO.Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            _multiplayer.ReleaseLock(Guid.Empty);
            MessageBox.Show(this, ex.Message, "Could not open project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject(false);
    private void SaveAsProject_Click(object sender, RoutedEventArgs e) => SaveProject(true);

    private void SaveProject(bool saveAs)
    {
        if (saveAs || string.IsNullOrWhiteSpace(_projectPath))
        {
            var dialog = new SaveFileDialog { Filter = "Northstar topology (*.nslab)|*.nslab|JSON project (*.json)|*.json", DefaultExt = ".nslab", FileName = _project.Name };
            if (dialog.ShowDialog(this) != true) return;
            _projectPath = dialog.FileName;
        }
        try
        {
            ProjectFileService.Save(_project, _projectPath!);
            StatusText.Text = $"Saved {_projectPath}.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save project", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveAutosave()
    {
        try
        {
            if (_project.Devices.Count == 0) return;
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NorthstarNetworkLab");
            Directory.CreateDirectory(folder);
            ProjectFileService.Save(_project, System.IO.Path.Combine(folder, "autosave.nslab"));
        }
        catch { }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();
    private void DuplicateSelected()
    {
        var selectedDevices = _project.Devices.Where(device => _selectedDeviceIds.Contains(device.Id)).ToArray();
        if (selectedDevices.Length == 0) return;
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        var copies = new List<DeviceModel>();
        foreach (var selectedDevice in selectedDevices)
        {
            var copy = JsonSerializer.Deserialize<DeviceModel>(JsonSerializer.Serialize(selectedDevice, JsonOptions))!;
            copy.Id = Guid.NewGuid();
            copy.Name = $"{selectedDevice.Name}-copy";
            copy.X += 48;
            copy.Y += 48;
            copy.LearnedMacAddresses.Clear();
            foreach (var item in copy.Interfaces)
                item.MacAddress = DeviceCatalog.Create("Network Device", _project.Devices.Count + copies.Count + 1, 0, 0).Interfaces[0].MacAddress;
            _project.Devices.Add(copy);
            copies.Add(copy);
        }
        PublishGlobalChange();
        RenderTopology();
        SelectDevices(copies);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();
    private void DeleteSelected()
    {
        var selectedIds = _selectedDeviceIds.ToHashSet();
        if (selectedIds.Count == 0) return;
        if (!TryAcquireGlobalLock()) return;
        PushUndo();
        var deletedNames = _project.Devices.Where(device => selectedIds.Contains(device.Id)).Select(device => device.Name).ToArray();
        _project.Devices.RemoveAll(device => selectedIds.Contains(device.Id));
        _project.Links.RemoveAll(link => selectedIds.Contains(link.ADeviceId) || selectedIds.Contains(link.BDeviceId));
        RefreshOperationalState();
        PublishGlobalChange();
        foreach (var selectedId in selectedIds) _multiplayer.ReleaseLock(selectedId);
        SelectDevice(null);
        StatusText.Text = $"Deleted {deletedNames.Length} device(s) and their connected links.";
    }

    private void SelectDevices(IReadOnlyCollection<DeviceModel> devices)
    {
        var priorIds = _selectedDeviceIds.ToArray();
        foreach (var device in devices)
            if (!TryAcquireDeviceLock(device)) return;
        _selectedDeviceIds.Clear();
        foreach (var device in devices) _selectedDeviceIds.Add(device.Id);
        foreach (var priorId in priorIds.Except(_selectedDeviceIds)) _multiplayer.ReleaseLock(priorId);
        SetInspectorDevice(devices.LastOrDefault());
        SelectionHint.Text = devices.Count > 1 ? $"{devices.Count} devices selected; inspector shows {_selectedDevice?.Name}." : SelectionHint.Text;
        UpdateSelectionVisuals();
    }

    private void GroupSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _project.Devices.Where(device => _selectedDeviceIds.Contains(device.Id)).ToArray();
        if (selected.Length == 0) { StatusText.Text = "Select one or more devices to assign to a group."; return; }
        var groupName = Prompt("Device group", "Group name:", "Network segment");
        if (string.IsNullOrWhiteSpace(groupName)) return;
        if (!TryAcquireGlobalLock()) 
            return;
        PushUndo();
        foreach (var device in selected)
            if (!device.Groups.Contains(groupName.Trim(), StringComparer.OrdinalIgnoreCase)) device.Groups.Add(groupName.Trim());
        PublishGlobalChange();
        StatusText.Text = $"Assigned {selected.Length} device(s) to group '{groupName.Trim()}'.";
        RenderTopology();
    }

    private void AddAnnotation_Click(object sender, RoutedEventArgs e)
    {
        var center = new Point(CanvasScrollViewer.HorizontalOffset + CanvasScrollViewer.ViewportWidth / 2,
            CanvasScrollViewer.VerticalOffset + CanvasScrollViewer.ViewportHeight / 2);
        AddAnnotation(center);
    }

    private void AddAnnotation(Point position)
    {
        var text = Prompt("Canvas annotation", "Note text:", "Network segment");
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!TryAcquireGlobalLock()) 
            return;
        PushUndo();
        _project.Annotations.Add(new CanvasAnnotation { Text = text.Trim(), X = Snap(position.X), Y = Snap(position.Y) });
        PublishGlobalChange();
        RenderTopology();
        StatusText.Text = "Canvas annotation added.";
    }

    private void Annotation_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Border { Tag: CanvasAnnotation annotation }) return;
        EditAnnotation(annotation);
        e.Handled = true;
    }

    private void EditAnnotation(CanvasAnnotation annotation)
    {
        var updated = Prompt("Edit annotation", "Note text:", annotation.Text);
        if (string.IsNullOrWhiteSpace(updated)) return;
        if (!TryAcquireGlobalLock()) 
            return;
        PushUndo();
        annotation.Text = updated.Trim();
        PublishGlobalChange();
        RenderTopology();
    }

    private void Annotation_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: CanvasAnnotation annotation }) return;
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Edit annotation" };
        edit.Click += (_, _) => EditAnnotation(annotation);
        var remove = new MenuItem { Header = "Delete annotation" };
        remove.Click += (_, _) =>
        {
            if (!TryAcquireGlobalLock()) 
                return;
            PushUndo();
            _project.Annotations.Remove(annotation);
            PublishGlobalChange();
            RenderTopology();
        };
        menu.Items.Add(edit);
        menu.Items.Add(remove);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Undo()
    {
        if (_undo.Count == 0) return;
        if (!TryAcquireGlobalLock()) return;
        _redo.Push(SerializeProject());
        RestoreProject(_undo.Pop());
        PublishGlobalChange();
        StatusText.Text = "Undo completed.";
    }
    private void Redo()
    {
        if (_redo.Count == 0) return;
        if (!TryAcquireGlobalLock()) return;
        _undo.Push(SerializeProject());
        RestoreProject(_redo.Pop());
        PublishGlobalChange();
        StatusText.Text = "Redo completed.";
    }
    private void PushUndo()
    {
        _undo.Push(SerializeProject());
        if (_undo.Count > 80)
        {
            var keep = _undo.Take(80).Reverse().ToArray();
            _undo.Clear();
            foreach (var state in keep) _undo.Push(state);
        }
        _redo.Clear();
    }
    private string SerializeProject() => JsonSerializer.Serialize(_project, JsonOptions);
    private void RestoreProject(string state)
    {
        _project = JsonSerializer.Deserialize<NetworkProject>(state, JsonOptions) ?? new NetworkProject();
        _serviceEngine = new NetworkServiceEngine(_project);
        RefreshOperationalState();
        _cli = new CliSession(_project, _simulator, _serviceEngine);
        _events.Clear();
        EventsList.ItemsSource = null;
        SelectDevice(null);
        RenderTopology();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => Search();
    private void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Search(); e.Handled = true; } }
    private void Search()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) { StatusText.Text = "Enter a device name or address to search."; return; }
        var device = _project.Devices.FirstOrDefault(d => d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            d.Kind.Contains(query, StringComparison.OrdinalIgnoreCase) || d.Interfaces.Any(i => i.Ipv4Address.Contains(query, StringComparison.OrdinalIgnoreCase)));
        if (device is null) { StatusText.Text = $"No device found for '{query}'."; return; }
        SelectDevice(device);
        CanvasScrollViewer.ScrollToHorizontalOffset(Math.Max(0, device.X - 100));
        CanvasScrollViewer.ScrollToVerticalOffset(Math.Max(0, device.Y - 100));
        StatusText.Text = $"Found {device.Name}.";
    }

    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TopologyCanvas is not null) TopologyCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue);
    }
    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Devices.Count == 0) { ZoomSlider.Value = 1; CanvasScrollViewer.ScrollToHome(); return; }
        var width = _project.Devices.Max(d => d.X + 150) - Math.Min(0, _project.Devices.Min(d => d.X));
        var height = _project.Devices.Max(d => d.Y + 80) - Math.Min(0, _project.Devices.Min(d => d.Y));
        var scale = Math.Min((CanvasScrollViewer.ViewportWidth - 30) / Math.Max(width, 1), (CanvasScrollViewer.ViewportHeight - 30) / Math.Max(height, 1));
        ZoomSlider.Value = Math.Clamp(scale, ZoomSlider.Minimum, ZoomSlider.Maximum);
        CanvasScrollViewer.ScrollToHome();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _darkTheme = !_darkTheme;
        SetBrush("WorkspaceBrush", _darkTheme ? Color.FromRgb(31, 41, 46) : Color.FromRgb(242, 245, 246));
        SetBrush("PanelBrush", _darkTheme ? Color.FromRgb(39, 51, 57) : Colors.White);
        SetBrush("TextBrush", _darkTheme ? Color.FromRgb(230, 237, 239) : Color.FromRgb(32, 41, 47));
        SetBrush("MutedTextBrush", _darkTheme ? Color.FromRgb(166, 181, 187) : Color.FromRgb(99, 113, 122));
        SetBrush("LineBrush", _darkTheme ? Color.FromRgb(70, 85, 92) : Color.FromRgb(214, 221, 225));
        TopologyCanvas.Background = new SolidColorBrush(_darkTheme ? Color.FromRgb(34, 46, 51) : Color.FromRgb(248, 250, 251));
        RenderTopology();
    }

    private void SetBrush(string key, Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
        else Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private void ClearEvents_Click(object sender, RoutedEventArgs e)
    {
        _events.Clear();
        EventsList.ItemsSource = null;
        EventDetailText.Clear();
        PacketInspectorText.Clear();
    }

    private void WireMultiplayerEvents()
    {
        _multiplayer.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() =>
        {
            MultiplayerStatusText.Text = _multiplayer.IsHosting
                ? $"Sharing on {_multiplayer.Port} · Host IPs: {string.Join(", ", GetLocalIpv4Addresses())}"
                : status;
            UpdateMultiplayerControls();
        }));
        _multiplayer.PeersChanged += peers => Dispatcher.BeginInvoke(new Action(() => MultiplayerPeersList.ItemsSource = peers));
        _multiplayer.LocksChanged += _ => Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateSelectionVisuals();
            UpdateInspectorLockState();
            if (_selectedDevice is not null && _multiplayer.IsLockedByOther(_selectedDevice.Id))
                SelectionHint.Text = $"{_selectedDevice.Name} is locked by {_multiplayer.GetLockOwner(_selectedDevice.Id)}.";
        }));
        _multiplayer.ProjectReceived += project => Dispatcher.BeginInvoke(new Action(() => ReplaceSharedProject(project)));
    }

    private void UpdateMultiplayerControls()
    {
        var connected = _multiplayer.IsSharing;
        BeginShareButton.IsEnabled = !connected;
        MultiplayerConnectButton.IsEnabled = !connected;
        ReloadSharedButton.IsEnabled = connected;
        DisconnectButton.IsEnabled = connected;
    }

    private async void BeginSharing_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Begin sharing", "Your peer display name:", Environment.UserName);
        if (string.IsNullOrWhiteSpace(name)) return;
        var portText = Prompt("Begin sharing", "Listening TCP port:", MultiplayerSession.DefaultPort.ToString());
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = "Port must be from 1 to 65535.";
            return;
        }
        try
        {
            await _multiplayer.StartSharingAsync(_project, name, port);
            if (_selectedDevice is not null) TryAcquireDeviceLock(_selectedDevice);
            MultiplayerStatusText.Text = $"Sharing on port {port}. Host IPs: {string.Join(", ", GetLocalIpv4Addresses())}";
            UpdateMultiplayerControls();
        }
        catch (Exception ex)
        {
            MultiplayerStatusText.Text = "Could not start sharing.";
            MessageBox.Show(this, ex.Message, "Begin sharing", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MultiplayerConnect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MultiplayerConnectWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _multiplayer.ConnectAsync(dialog.HostAddress, dialog.Port, dialog.DisplayName);
            UpdateMultiplayerControls();
        }
        catch (Exception ex)
        {
            MultiplayerStatusText.Text = "Connection failed.";
            MessageBox.Show(this, ex.Message, "Connect to shared topology", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReloadShared_Click(object sender, RoutedEventArgs e)
    {
        if (!_multiplayer.IsSharing) return;
        _multiplayer.ReloadFromHost();
        if (_multiplayer.IsHosting) ReplaceSharedProject(_project);
    }

    private async void DisconnectMultiplayer_Click(object sender, RoutedEventArgs e)
    {
        foreach (var id in _selectedDeviceIds) _multiplayer.ReleaseLock(id);
        await _multiplayer.DisconnectAsync();
        _selectedDeviceIds.Clear();
        MultiplayerPeersList.ItemsSource = null;
        UpdateMultiplayerControls();
        UpdateSelectionVisuals();
    }

    private void RestartApplication_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Restart Northstar Network Lab? Save the shared topology first if needed.", "Restart application",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Application executable path is unavailable.");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restart application", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool TryAcquireDeviceLock(DeviceModel device)
    {
        if (!_multiplayer.IsSharing) return true;
        if (_multiplayer.TryAcquireLock(device.Id, out var reason)) return true;
        StatusText.Text = reason;
        return false;
    }

    private bool TryAcquireGlobalLock()
    {
        if (!_multiplayer.IsSharing) return true;
        if (_multiplayer.TryAcquireLock(Guid.Empty, out var reason)) return true;
        StatusText.Text = reason;
        return false;
    }

    private void PublishDeviceChange(DeviceModel device)
    {
        if (_multiplayer.IsSharing) _multiplayer.PublishProject(_project, device.Id);
    }

    private void PublishGlobalChange()
    {
        if (!_multiplayer.IsSharing) return;
        _multiplayer.PublishProject(_project, Guid.Empty);
        _multiplayer.ReleaseLock(Guid.Empty);
    }

    private void ReplaceSharedProject(NetworkProject project)
    {
        var selectedIds = _selectedDeviceIds.ToArray();
        _project = project;
        _serviceEngine = new NetworkServiceEngine(_project);
        _cli = new CliSession(_project, _simulator, _serviceEngine);
        RefreshOperationalState();
        var stillSelected = _project.Devices.Where(device => selectedIds.Contains(device.Id)).ToArray();
        _selectedDeviceIds.Clear();
        foreach (var device in stillSelected)
        {
            if (TryAcquireDeviceLock(device)) _selectedDeviceIds.Add(device.Id);
        }
        SetInspectorDevice(stillSelected.LastOrDefault());
        RenderTopology();
        StatusText.Text = "Shared topology synchronized.";
    }

    private static string[] GetLocalIpv4Addresses()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString()).Distinct().ToArray();
        }
        catch { return []; }
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Northstar Network Lab\nAn independent educational network topology and simulation workspace.\n\nCurrent engine: Ethernet link traversal, MAC learning, ARP/ICMP event inspection, and a shared device CLI/configuration model.",
        "About Northstar Network Lab", MessageBoxButton.OK, MessageBoxImage.Information);

    private void HelpCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_helpCenter is null)
        {
            _helpCenter = new HelpCenterWindow { Owner = this };
            _helpCenter.Closed += (_, _) => _helpCenter = null;
        }
        _helpCenter.Show();
        _helpCenter.Activate();
    }

    private void DiscordPresenceSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiscordPresenceSettingsWindow(_discordSettings.ApplicationId) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _discordSettings.ApplicationId = dialog.ApplicationId;
        try
        {
            DiscordPresenceSettingsStore.Save(_discordSettings);
            _discordPresence.Configure(_discordSettings.ApplicationId);
            UpdateDiscordActivity();
        }
        catch (Exception ex)
        {
            DiscordStatusText.Text = "Discord setup failed";
            MessageBox.Show(this, ex.Message, "Discord Rich Presence", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateDiscordActivity(string? details = null, string? state = null)
    {
        var projectDetails = details ?? $"Project: {_project.Name}";
        var selectionState = _selectedDevice is null
            ? $"{_project.Devices.Count} devices · Network lab"
            : $"{_selectedDevice.Name} · {_selectedDevice.Kind}";
        _discordPresence.Update(projectDetails, state ?? selectionState);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_simulationTimer.IsEnabled && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            SimulationPlay_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            _pendingKind = null;
            _connectStart = null;
            ConnectButton.Background = Brushes.White;
            StatusText.Text = "Tool cancelled.";
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N: NewProject_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.O: OpenProject_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.S: SaveProject(false); e.Handled = true; break;
                case Key.Z: Undo(); e.Handled = true; break;
                case Key.Y: Redo(); e.Handled = true; break;
                case Key.D: DuplicateSelected(); e.Handled = true; break;
                case Key.P: Ping_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            }
        }
        else if (e.Key == Key.Delete) DeleteSelected();
        else if (e.Key == Key.F1) { HelpCenter_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void UpdatePrompt() => CliPromptText.Text = _cli.Prompt;

    private void RefreshOperationalState()
    {
        foreach (var device in _project.Devices)
            foreach (var item in device.Interfaces)
                item.LinkUp = _project.Links.Any(link => link.IsUp &&
                    ((link.ADeviceId == device.Id && link.AInterface == item.Name) || (link.BDeviceId == device.Id && link.BInterface == item.Name)));
    }
    private static double Snap(double value) => Math.Round(value / 24) * 24;
    private static string DeviceBadge(string kind) => kind switch
    {
        "Layer 3 Switch" => "L3", "Wireless Router" => "WR", "Access Point" => "AP",
        "Temperature Sensor" => "C", "Single-board Computer" => "SBC",
        _ => new string(kind.Where(char.IsLetterOrDigit).Take(2).Select(char.ToUpperInvariant).ToArray())
    };
    private static Brush GetDeviceBrush(string kind) => kind switch
    {
        "Router" or "Layer 3 Switch" or "Firewall" => new SolidColorBrush(Color.FromRgb(8, 126, 139)),
        "Switch" or "Hub" or "Access Point" => new SolidColorBrush(Color.FromRgb(72, 113, 135)),
        "Server" => new SolidColorBrush(Color.FromRgb(77, 122, 94)),
        "Smart Light" or "Fan" or "Door" or "Motion Sensor" or "Temperature Sensor" or "Camera" or "Smart Appliance" => new SolidColorBrush(Color.FromRgb(176, 113, 50)),
        _ => new SolidColorBrush(Color.FromRgb(88, 101, 111))
    };

    private string? Prompt(string title, string label, string initialValue)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 380, Height = 155,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush")
        };
        var layout = new Grid { Margin = new Thickness(14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) };
        var input = new TextBox { Text = initialValue };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        string? answer = null;
        var cancel = new Button { Content = "Cancel", MinWidth = 72 };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var ok = new Button { Content = "Run", MinWidth = 72, IsDefault = true, Background = (Brush)FindResource("AccentBrush"), Foreground = Brushes.White };
        ok.Click += (_, _) => { answer = input.Text; dialog.DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(input, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(caption);
        layout.Children.Add(input);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? answer : null;
    }
}