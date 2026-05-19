using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LogicReinc.BlendFarm.Client;
using LogicReinc.BlendFarm.Client.ImageTypes;
using LogicReinc.BlendFarm.Client.Tasks;
using LogicReinc.BlendFarm.Objects;
using LogicReinc.BlendFarm.Server;
using LogicReinc.BlendFarm.Shared;
using LogicReinc.BlendFarm.Shared.Communication.RenderNode;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Image = Avalonia.Controls.Image;

namespace LogicReinc.BlendFarm.Windows
{
    public class RenderWindow : Window
    {
        private static DirectProperty<RenderWindow, bool> IsRenderingProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, bool>(nameof(IsRendering), (x) => x.IsRendering);
        private static DirectProperty<RenderWindow, bool> IsLiveChangingProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, bool>(nameof(IsLiveChanging), (x) => x.IsLiveChanging);
        private static DirectProperty<RenderWindow, bool> IsQueueingProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, bool>(nameof(IsQueueing), (x) => x.IsQueueing);

        private static DirectProperty<RenderWindow, OpenBlenderProject> CurrentProjectProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, OpenBlenderProject>(nameof(CurrentProject), (x) => x.CurrentProject, (w, v) => w.CurrentProject = v);
        private static DirectProperty<RenderWindow, string> CurrentSessionProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, string>(nameof(CurrentProject), (x) => x.CurrentSessionID, (w, v) => { });

        private static DirectProperty<RenderWindow, int> TabScrollIndexProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, int>(nameof(TabScrollIndex), (x) => x.TabScrollIndex, (w, v) => w.TabScrollIndex = v);
        private static DirectProperty<RenderWindow, bool> CanTabScrollRightProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, bool>(nameof(CanTabScrollRight), (x) => x.CanTabScrollRight, (w, v) => { });
        private static DirectProperty<RenderWindow, bool> CanTabScrollLeftProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, bool>(nameof(CanTabScrollLeft), (x) => x.CanTabScrollLeft, (w, v) => { });
        private static DirectProperty<RenderWindow, string> QueueNameProperty =
            AvaloniaProperty.RegisterDirect<RenderWindow, string>(nameof(QueueName), (x) => x.QueueName, (w, v) => { });
        //public string File { get; set; }
        public BlenderVersion Version { get; set; }
        public RenderWindowOptions Options { get; private set; }

        public ObservableCollection<OpenBlenderProject> Projects { get; set; } = new ObservableCollection<OpenBlenderProject>();

        public ObservableCollection<QueueItem> Queue { get; set; } = new ObservableCollection<QueueItem>();

        public ObservableCollection<RenderActivityLogEntry> ActivityLog { get; set; } = new ObservableCollection<RenderActivityLogEntry>();

        public bool IsClientConnecting { get; set; }
        public string InputClientName { get; set; }
        public string InputClientAddress { get; set; }

        public bool UseAutomaticPerformance { get; set; } = true;
        public bool UseSyncCompression { get; set; } = false;
        public RenderType NewNodeRenderType { get; set; } = RenderType.OPTIX_GPUONLY;

        public OpenBlenderProject CurrentProject { get; set; } = null;

        public string CurrentSessionID => CurrentProject?.SessionID;

        public string OS { get; set; }
        public bool IsWindows => OS == SystemInfo.OS_WINDOWS64;
        public bool IsLinux => OS == SystemInfo.OS_LINUX64;
        public bool IsMacOS => OS == SystemInfo.OS_MACOS || OS == SystemInfo.OS_MACOSARM64;


        //State
        public bool IsLiveChanging { get; set; } = false;

        public bool IsQueueing { get; set; } = false;

        private int _queueCount = 0;
        public string QueueName => $"Queue ({_queueCount})";
        private bool HasActiveQueueItems()
        {
            lock (Queue)
                return Queue.Any(x => x.Active);
        }

        private void RefreshQueueName()
        {
            int queueCount;
            lock (Queue)
                queueCount = Queue.Count(x => x.Active);

            if (queueCount != _queueCount)
            {
                _queueCount = queueCount;
                Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(QueueNameProperty, null, QueueName));
            }
        }

        public ObservableCollection<RenderNode> Nodes { get; private set; } = new ObservableCollection<RenderNode>();
        public BlendFarmManager Manager { get; set; } = null;

        public bool IsRendering => CurrentTask != null;
        public RenderTask CurrentTask = null;

        private Thread _queueThread = null;

        //Options
        protected string[] DenoiserOptions { get; } = new string[] { "Inherit", "None", "NLM", "OPTIX", "OPENIMAGEDENOISE" };
        protected EngineType[] EngineOptions { get; } = (EngineType[])Enum.GetValues(typeof(EngineType));
        public RenderType[] RenderTypes { get; } = (RenderType[])Enum.GetValues(typeof(RenderType));

        protected string[] ImageFormats { get; } = Client.ImageTypes.ImageFormats.Formats;

        //Dialogs
        private string _lastAnimationDirectory = null;

        //UI
        public int TabScrollIndex { get; set; }
        public bool CanTabScrollRight => TabScrollIndex < Projects.Count - 1;
        public bool CanTabScrollLeft => TabScrollIndex > 0;


        //Views
        private Image _image = null;
        private ProgressBar _imageProgress = null;
        private TextBlock _lastRenderTime = null;
        private TextBlock _estimatedRenderTime = null;
        private Border _animationProgressPanel = null;
        private TextBlock _animationFramesRendered = null;
        private TextBlock _animationCurrentFrame = null;
        private TextBlock _animationEstimatedRemaining = null;
        private TextBlock _animationTimeline = null;
        private ProgressBar _animationTimelineProgress = null;
        private TextBlock _statsTotalSaved = null;
        private TextBlock _statsFramesToday = null;
        private TextBlock _statsFastestNode = null;
        private TextBlock _statsAverageFrame = null;
        private TextBlock _renderProgressSummary = null;
        private ListBox _activityLogList = null;
        private ComboBox _selectStrategy = null;
        private ComboBox _selectOrder = null;
        private ComboBox _selectOutputType = null;
        private TextBox _inputAnimationFileFormat = null;
        private ComboBox _sceneComboBox = null;
        private ComboBox _cameraComboBox = null;
        private bool _updatingSceneCameraControls = false;
        private RenderNode _draggedInspectorNode = null;
        private Point _dragStartPoint;
        private bool _isDraggingNode = false;
        private bool _loadingMeta = false;
        private readonly HashSet<RenderNode> _activityTrackedNodes = new HashSet<RenderNode>();
        private readonly HashSet<string> _loggedDiscoveryNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Action<string, string, int> _discoveredServerHandler = null;

        private DateTime _statsDate = DateTime.Today;
        private int _framesRenderedToday = 0;
        private TimeSpan _totalRenderTimeSaved = TimeSpan.Zero;
        private TimeSpan _totalFrameRenderTime = TimeSpan.Zero;
        private int _timedFrameCount = 0;


        //Debug data
        private ObservableCollection<RenderNode> _testNodes = new ObservableCollection<RenderNode>(new List<RenderNode>()
        {
            new RenderNode()
            {
                Name = "Local",
                Address = "Localhost"
            },
            new RenderNode()
            {
                Name = "WhateverPC",
                Address = "192.168.1.212"
            }
        });



        public RenderWindow()
        {
            Projects = new ObservableCollection<OpenBlenderProject>()
            {
                new OpenBlenderProject("C://some/blend/dir/Example Project.blend"){
                    UseNetworkedPath = true
                    },
                new OpenBlenderProject("C://some/blend/dir/Some other project.blend"),
                new OpenBlenderProject("C://some/blend/dir/asdf1234.blend"),
                new OpenBlenderProject("C://some/blend/dir/testing.blend"),
            };
            Queue = new ObservableCollection<QueueItem>()
            {
                new QueueItem(this, new OpenBlenderProject("C://whatever/testproject.blend"), new RenderManagerSettings()
                {

                }){
                        Task = new ChunkedTask(null, null, null, 0)
                        {
                            Progress = 0.43
                        }
                },
                new QueueItem(this, new OpenBlenderProject("C://whatever/asdfdsag.blend"), new RenderManagerSettings()
                {

                })
            };
            //File = "path/to/some/blendfile.blend";
            CurrentProject = LoadProject("path/to/some/blendfile.blend");
            Version = new Shared.BlenderVersion()
            {
                Name = "blender-2.9.2"
            };
            Init();
        }
        public RenderWindow(BlendFarmManager manager, BlenderVersion version, string blenderFile, string sessionID = null, RenderWindowOptions options = null)
        {
            options = options ?? new RenderWindowOptions();
            Manager = manager;
            //File = blenderFile;
            CurrentProject = LoadProject(blenderFile);
            Version = version;
            Options = options;

            using (Stream icoStream = Program.GetIconStream())
            {
                this.Icon = new WindowIcon(icoStream);
            }

            Init();
        }
        private void Init()
        {
            OS = SystemInfo.GetOSName();
            Console.WriteLine("OS: " + OS);
            if(Manager?.Nodes != null)
            {
                foreach(RenderNode node in Manager.Nodes.ToList())
                {
                    Nodes.Add(node);
                    AttachNodeActivity(node);
                }
                Manager.OnNodeAdded += (manager, node) => Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Nodes.Add(node);
                    AttachNodeActivity(node);
                    AddActivityLog($"{node.NodeTitle} added");
                    UpdateRenderStats();
                });
                Manager.OnNodeRemoved += (manager, node) => Dispatcher.UIThread.InvokeAsync(() =>
                {
                    DetachNodeActivity(node);
                    AddActivityLog($"{node.NodeTitle} removed", ActivityLogType.Warning);
                    Nodes.Remove(node);
                    UpdateRenderStats();
                });
            }
            else 
            {
                Nodes =  _testNodes;
                foreach (RenderNode node in Nodes)
                    AttachNodeActivity(node);
            }
            DataContext = this;

            this.Closed += (a, b) =>
            {
                if (_discoveredServerHandler != null)
                    LocalServer.OnDiscoveredServer -= _discoveredServerHandler;
                LocalServer.Stop();
                Manager.StopFileWatch();
                Manager.Cleanup();
            };
            Manager?.StartFileWatch();

            this.InitializeComponent();
            StartDiscoveryTracking();
            _ = StartAutomaticNodeSetup();
        }

        private void StartDiscoveryTracking()
        {
            if (Manager == null || _discoveredServerHandler != null)
                return;

            _discoveredServerHandler = (name, address, port) =>
            {
                try
                {
                    RenderNode node = Manager.TryAddDiscoveryNode(name, address, port);
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        string discoveryKey = GetDiscoveryLogKey(node.Address);
                        if (_loggedDiscoveryNodes.Add(discoveryKey))
                            AddActivityLog($"Discovered {node.NodeTitle} at {node.Address}", ActivityLogType.Success);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.InvokeAsync(() => AddActivityLog($"Discovery failed for {name} at {address}:{port}: {ex.Message}", ActivityLogType.Error));
                }
            };

            LocalServer.OnDiscoveredServer += _discoveredServerHandler;
        }

        private static string GetDiscoveryLogKey(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return "";

            address = address.Trim().ToLowerInvariant();
            if (address.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase))
                return "localhost:" + address.Substring("127.0.0.1:".Length);
            if (address.StartsWith("::1:", StringComparison.OrdinalIgnoreCase))
                return "localhost:" + address.Substring("::1:".Length);
            return address;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            MinHeight = 600;
            MinWidth = 500;
            Width = 1400;
            Height = 975;

            System.Version version = Assembly.GetExecutingAssembly().GetName().Version;
            this.Title = $"BlendFarm by LogicReinc [{version.Major}.{version.Minor}.{version.Build}]";

            _image = this.Find<Image>("render");
            _imageProgress = this.Find<ProgressBar>("renderProgress");
            _lastRenderTime = this.Find<TextBlock>("lastRenderTime");
            _estimatedRenderTime = this.Find<TextBlock>("estimatedRenderTime");
            _animationProgressPanel = this.Find<Border>("animationProgressPanel");
            _animationFramesRendered = this.Find<TextBlock>("animationFramesRendered");
            _animationCurrentFrame = this.Find<TextBlock>("animationCurrentFrame");
            _animationEstimatedRemaining = this.Find<TextBlock>("animationEstimatedRemaining");
            _animationTimeline = this.Find<TextBlock>("animationTimeline");
            _animationTimelineProgress = this.Find<ProgressBar>("animationTimelineProgress");
            _statsTotalSaved = this.Find<TextBlock>("statsTotalSaved");
            _statsFramesToday = this.Find<TextBlock>("statsFramesToday");
            _statsFastestNode = this.Find<TextBlock>("statsFastestNode");
            _statsAverageFrame = this.Find<TextBlock>("statsAverageFrame");
            _renderProgressSummary = this.Find<TextBlock>("renderProgressSummary");
            _activityLogList = this.Find<ListBox>("activityLogList");
            _selectStrategy = this.Find<ComboBox>("selectStrategy");
            _selectOrder = this.Find<ComboBox>("selectOrder");
            _selectOutputType = this.Find<ComboBox>("selectOutputType");
            _inputAnimationFileFormat = this.Find<TextBox>("inputAnimationFileFormat");
            _sceneComboBox = this.Find<ComboBox>("sceneComboBox");
            _cameraComboBox = this.Find<ComboBox>("cameraComboBox");
            UpdateRenderStats();

            _selectStrategy.Items = Enum.GetValues(typeof(RenderStrategy));
            _selectStrategy.SelectedIndex = 0;
            _selectOrder.Items = Enum.GetValues(typeof(TaskOrder));
            _selectOrder.SelectedIndex = 0;

            _image.KeyDown += async (a, b) =>
            {
                if (b.Key == Avalonia.Input.Key.Delete)
                {
                    CurrentProject.LastImage = new System.Drawing.Bitmap(1, 1).ToAvaloniaBitmap();
                    RefreshCurrentProject();
                    _lastRenderTime.Text = "";
                    if (_estimatedRenderTime != null)
                        _estimatedRenderTime.Text = "";
                }
            };

            _selectOutputType.SelectionChanged += (s, e) =>
            {
                string selected = _selectOutputType.SelectedItem?.ToString();
                string fileExtension = Client.ImageTypes.ImageFormats.GetExtension(selected);
                if(fileExtension != null)
                {
                    if(CurrentProject.AnimationFileFormat != null && 
                        Client.ImageTypes.ImageFormats.Extensions.Any(ext=>CurrentProject.AnimationFileFormat.ToLower().EndsWith("." + ext.ToLower())))
                    {
                        CurrentProject.AnimationFileFormat = CurrentProject.AnimationFileFormat.Substring(0,
                            CurrentProject.AnimationFileFormat.LastIndexOf(".")) + "." + fileExtension;
                        CurrentProject.TriggerPropertyChange(nameof(CurrentProject.AnimationFileFormat));
                    }
                }
            };

            _sceneComboBox.SelectionChanged += (s, e) =>
            {
                if (CurrentProject == null || _updatingSceneCameraControls)
                    return;

                string scene = _sceneComboBox.SelectedItem?.ToString();
                if (scene != null)
                    SetCurrentProjectScene(scene);
            };

            _cameraComboBox.SelectionChanged += (s, e) =>
            {
                if (CurrentProject == null || _updatingSceneCameraControls)
                    return;

                string camera = _cameraComboBox.SelectedItem?.ToString();
                if (camera != null)
                    SetCurrentProjectCamera(camera);
            };

            AddHandler(DragDrop.DragOverEvent, NodeInspectorDragOver);
            AddHandler(DragDrop.DropEvent, NodeInspectorDrop);

            AddActivityLog("Ready");
            UpdateCenterRenderProgress(0);
        }

        private void NodeInspectorPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is Control control && control.DataContext is RenderNode node)
            {
                _draggedInspectorNode = node;
                _dragStartPoint = e.GetPosition(this);
                _isDraggingNode = false;
            }
        }

        private async void NodeInspectorPointerMoved(object sender, PointerEventArgs e)
        {
            if (_draggedInspectorNode == null || _isDraggingNode)
                return;

            PointerPoint point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            Point current = e.GetPosition(this);
            if (Math.Abs(current.X - _dragStartPoint.X) < 8 && Math.Abs(current.Y - _dragStartPoint.Y) < 8)
                return;

            _isDraggingNode = true;
            _draggedInspectorNode.IsBeingDragged = true;

            try
            {
                DataObject data = new DataObject();
                data.Set(DataFormats.Text, _draggedInspectorNode.Name);
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
            finally
            {
                _draggedInspectorNode.IsBeingDragged = false;
                _draggedInspectorNode = null;
                _isDraggingNode = false;
            }
        }

        private void NodeInspectorDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.Contains(DataFormats.Text) && GetNodeFromEventSource(e.Source) != null)
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void NodeInspectorDrop(object sender, DragEventArgs e)
        {
            string sourceNodeName = e.Data.GetText();
            if (string.IsNullOrWhiteSpace(sourceNodeName))
                return;

            RenderNode sourceNode = Nodes.FirstOrDefault(x => x.Name == sourceNodeName);
            RenderNode targetNode = GetNodeFromEventSource(e.Source);
            if (targetNode != null)
            {
                MoveNodeInInspector(sourceNode, targetNode);
                e.Handled = true;
            }
        }

        private RenderNode GetNodeFromEventSource(object source)
        {
            if (source is Control control)
            {
                if (control.DataContext is RenderNode directNode)
                    return directNode;

                foreach (StyledElement ancestor in control.GetVisualAncestors().OfType<StyledElement>())
                {
                    if (ancestor.DataContext is RenderNode node)
                        return node;
                }
            }

            return null;
        }

        private void MoveNodeInInspector(RenderNode sourceNode, RenderNode targetNode)
        {
            if (sourceNode == null || targetNode == null || sourceNode == targetNode)
                return;

            int oldIndex = Nodes.IndexOf(sourceNode);
            int newIndex = Nodes.IndexOf(targetNode);
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
                return;

            Nodes.Move(oldIndex, newIndex);

            if (Manager?.Nodes != null)
            {
                Manager.Nodes.Remove(sourceNode);
                int managerIndex = Manager.Nodes.IndexOf(targetNode);
                if (managerIndex < 0 || managerIndex > Manager.Nodes.Count)
                    Manager.Nodes.Add(sourceNode);
                else
                    Manager.Nodes.Insert(managerIndex, sourceNode);
            }
        }


        public async Task OpenProjectDialog()
        {
            OpenFileDialog dialog = new OpenFileDialog()
            {
                Title = "Select a Blendfile",
                Filters = new List<FileDialogFilter>()
                {
                    new FileDialogFilter()
                    {
                        Name = "Blender File (.blend)",
                        Extensions = new List<string>()
                        {
                            "blend"
                        }
                    }
                }
            };

            string[] paths = await dialog.ShowAsync(this);
            paths = paths?.Select(x => Statics.SanitizePath(x)).ToArray();

            if (paths != null)
                foreach (string path in paths)
                {
                    if (!File.Exists(path))
                        await MessageWindow.Show(this, "Invalid Path", $"Path {path} does not exist, and is ignored.");
                    else
                        LoadProject(path);
                }
        }

        public OpenBlenderProject LoadProject(string blendFile)
        {
            string sessionID = Manager?.GetFileSessionID(blendFile) ?? Guid.NewGuid().ToString();
            OpenBlenderProject proj = new OpenBlenderProject(blendFile, sessionID);

            var projSettings = BlendFarmSettings.Instance.GetProjectSettings(blendFile);
            if(projSettings != null)
                proj.ApplyProjectSettings(projSettings);

            proj.OnBitmapChanged += async (proj, bitmap) =>
            {

                if (proj == CurrentProject)
                    await Dispatcher.UIThread.InvokeAsync(() => _image.Source = bitmap); ;
            };
            proj.OnNetworkedChanged += async (proj, networked) =>
            {
                Manager.IsNetworked = networked;
                foreach (var node in Nodes.Where(x => x.Connected))
                    node.UpdateSyncedStatus(proj.SessionID, false);
            };
            Projects.Add(proj);

            SwitchProject(proj);

            return proj;   
        }

        public async Task SwitchProject(OpenBlenderProject proj)
        {
            OpenBlenderProject oldProj = CurrentProject;
            CurrentProject = proj;
            Manager.SetSelectedSessionID(CurrentProject.SessionID);
            TabScrollIndex = Projects.IndexOf(proj);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RaisePropertyChanged(CurrentProjectProperty, oldProj, proj);
                RaisePropertyChanged(CurrentSessionProperty, null, CurrentSessionID);
                RaisePropertyChanged(TabScrollIndexProperty, -1, TabScrollIndex);
                RaisePropertyChanged(CanTabScrollLeftProperty, !CanTabScrollLeft, CanTabScrollLeft);
                RaisePropertyChanged(CanTabScrollRightProperty, !CanTabScrollRight, CanTabScrollRight);

                _image.Source = proj.LastImage;
                RefreshSceneCameraControls();
            });
        }

        public async void ConnectAll()
        {
            try
            {
                await ConnectAndPrepareNodes();
            }
            catch { }
        }

        private async Task StartAutomaticNodeSetup()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });

            if (Manager?.Nodes == null || !Manager.Nodes.Any())
                return;

            await ConnectAndPrepareNodes();

            if (!Manager.Nodes.Any(x => x.Connected))
                return;

            await SyncAll();

            if (Manager.Nodes.Any(x => x.Connected && x.IsSessionSynced(CurrentProject.SessionID)))
                await ImportSettings();
        }

        private async Task ConnectAndPrepareNodes()
        {
            if (Manager?.Nodes == null)
                return;

            await Task.WhenAll(Manager.Nodes.ToList().Select(async node =>
            {
                try
                {
                    if (!node.Connected)
                        await node.ConnectAndPrepare(Version.Name);
                    else if (!node.IsPrepared)
                        await node.PrepareVersion(Version.Name);
                }
                catch (Exception ex)
                {
                    node.UpdateException(ex.Message);
                }
            }));
        }
        public async Task SyncAll()
        {
            if (!CurrentProject.UseNetworkedPath)
                await Manager?.Sync(CurrentProject.BlendFile, UseSyncCompression);
            else
                await Manager?.Sync(CurrentProject.BlendFile, CurrentProject.NetworkPathWindows, CurrentProject.NetworkPathLinux, CurrentProject.NetworkPathMacOS);
        }

        public async void AddNewNode()
        {
            if (!string.IsNullOrEmpty(InputClientAddress) && !string.IsNullOrEmpty(InputClientName))
            {
                if (BlendFarmSettings.Instance.PastClients.Any(x => x.Key == InputClientName || x.Value.Address == InputClientAddress))
                {
                    MessageWindow.Show(this, "Node already exists", "Node already exists, use a different name and address");
                    return;
                }
                if(!Regex.IsMatch(InputClientAddress, "^([a-zA-Z0-9\\.]*?):[0-9][0-9]?[0-9]?[0-9]?[0-9]?$"))
                {
                    MessageWindow.Show(this, "Invalid Address", "The address provided seems to be invalid, expected format is {hostname}:{port} or {ip}{port}, eg. 192.168.1.123:15000");
                    return;
                }

                RenderNode node = Manager.AddNode(InputClientName, InputClientAddress, NewNodeRenderType);

                BlendFarmSettings.Instance.PastClients.Add(InputClientName, new BlendFarmSettings.HistoryClient()
                {
                    Address = InputClientAddress,
                    Name = InputClientName,
                    RenderType = NewNodeRenderType
                });
                BlendFarmSettings.Instance.Save();

                try
                {
                    await node.ConnectAndPrepare(Version.Name);
                    if (node.Connected)
                    {
                        await SyncAll();
                        if (node.IsSessionSynced(CurrentProject.SessionID))
                            await ImportSettings();
                    }
                }
                catch (Exception ex)
                {
                    node.UpdateException(ex.Message);
                }
            }
            else
                MessageWindow.Show(this, "No name or address", "A node requires both a name and an address");
        }

        public void DeleteNode(RenderNode node)
        {
            Manager.RemoveNode(node.Name);

            var nodeEntry = BlendFarmSettings.Instance.PastClients.FirstOrDefault(x => x.Key == node.Name).Key;
            if (nodeEntry != null)
            {
                BlendFarmSettings.Instance.PastClients.Remove(nodeEntry);
                BlendFarmSettings.Instance.Save();
            }
        }

        public void TerminalNode(RenderNode node)
        {
            DeviceLogWindow.Show(this, node);
        }

        public void ClearActivityLog()
        {
            ActivityLog.Clear();
            AddActivityLog("Log cleared");
        }

        private void AttachNodeActivity(RenderNode node)
        {
            if (node == null || _activityTrackedNodes.Contains(node))
                return;

            _activityTrackedNodes.Add(node);
            node.OnConnected += HandleNodeConnected;
            node.OnDisconnected += HandleNodeDisconnected;
            node.OnActivityChanged += HandleNodeActivityChanged;
            node.OnLog += HandleNodeLog;
        }

        private void DetachNodeActivity(RenderNode node)
        {
            if (node == null || !_activityTrackedNodes.Remove(node))
                return;

            node.OnConnected -= HandleNodeConnected;
            node.OnDisconnected -= HandleNodeDisconnected;
            node.OnActivityChanged -= HandleNodeActivityChanged;
            node.OnLog -= HandleNodeLog;
        }

        private void HandleNodeConnected(RenderNode node)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                AddActivityLog($"{node.NodeTitle} connected", ActivityLogType.Success);
                UpdateCenterRenderProgress(CurrentProject?.CurrentTask?.Progress ?? 0);
            });
        }

        private void HandleNodeDisconnected(RenderNode node)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                AddActivityLog($"{node.NodeTitle} disconnected", ActivityLogType.Warning);
                UpdateCenterRenderProgress(CurrentProject?.CurrentTask?.Progress ?? 0);
            });
        }

        private void HandleNodeActivityChanged(RenderNode node, string activity)
        {
            if (string.IsNullOrWhiteSpace(activity))
                return;

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                AddActivityLog($"{node.NodeTitle}: {activity}");
                UpdateCenterRenderProgress(CurrentProject?.CurrentTask?.Progress ?? 0);
            });
        }

        private void HandleNodeLog(RenderNode node, string log)
        {
            if (string.IsNullOrWhiteSpace(log))
                return;

            string message = log.Trim();
            if (message.Length > 180)
                message = message.Substring(0, 177) + "...";

            Dispatcher.UIThread.InvokeAsync(() => AddActivityLog($"{node.NodeTitle}: {message}"));
        }

        private void AddActivityLog(string message, ActivityLogType logType = ActivityLogType.Info)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            ActivityLog.Add(new RenderActivityLogEntry(message, logType));
            while (ActivityLog.Count > 150)
                ActivityLog.RemoveAt(0);

            _activityLogList?.ScrollIntoView(ActivityLog.LastOrDefault());
        }

        private void UpdateCenterRenderProgress(double progress)
        {
            if (_renderProgressSummary == null)
                return;

            progress = Math.Max(0, Math.Min(1, progress));
            int activeNodes = Nodes.Count(x => x.Connected && !string.IsNullOrWhiteSpace(x.Activity));
            if (CurrentProject?.CurrentTask != null)
                _renderProgressSummary.Text = $"{progress * 100:0}% complete";
            else if (activeNodes > 0)
                _renderProgressSummary.Text = $"{activeNodes} active node{(activeNodes == 1 ? "" : "s")}";
            else
                _renderProgressSummary.Text = "Idle";
        }

        public async void ConfigureNode(RenderNode node)
        {
            DeviceSettingsWindow.Show(this, node);
        }

        public void StartingRender(RenderTask task)
        {
            string scene = task.Settings.Scene;
            
            if(!CurrentProject.ScenesAvailable.Contains(scene))
            {
                CurrentProject.ScenesAvailable.Add(scene);
                RefreshSceneCameraControls();
            }

            BlendFarmSettings.Instance.ApplyProjectSettings(CurrentProject.BlendFile, CurrentProject.GetProjectSettings());
            AddActivityLog($"Render started for {CurrentProject.BlendFileDisplay}");
            AddActivityLog($"Settings: {task.Settings.OutputWidth}x{task.Settings.OutputHeight}, {task.Settings.Samples} samples, {task.Settings.Strategy}");
            UpdateCenterRenderProgress(0);
        }

        private void AttachRenderTaskActivityLogging(RenderTask task)
        {
            if (task == null)
                return;

            task.OnSubTaskStarted += RenderTaskSubTaskStarted;
            task.OnSubTaskFinished += RenderTaskSubTaskFinished;
        }

        private void DetachRenderTaskActivityLogging(RenderTask task)
        {
            if (task == null)
                return;

            task.OnSubTaskStarted -= RenderTaskSubTaskStarted;
            task.OnSubTaskFinished -= RenderTaskSubTaskFinished;
        }

        private void RenderTaskSubTaskStarted(RenderNode node, RenderSubTask task)
        {
            AddRenderSubTaskActivity(node, task, "started");
        }

        private void RenderTaskSubTaskFinished(RenderNode node, RenderSubTask task)
        {
            AddRenderSubTaskActivity(node, task, "finished");
        }

        private void AddRenderSubTaskActivity(RenderNode node, RenderSubTask task, string action)
        {
            if (node == null || task == null)
                return;

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                AddActivityLog($"{node.NodeTitle} {action} {FormatRenderSubTask(task)}");
            });
        }

        private static string FormatRenderSubTask(RenderSubTask task)
        {
            if (task == null)
                return "render task";

            return task.Crop
                ? $"tile for frame {task.Frame}"
                : $"frame {task.Frame}";
        }

        public async Task<BlenderPeekResponse> RequestPeek(OpenBlenderProject currentProject)
        {

            //Check if any unsynced nodes
            if (!Manager.Nodes.Any(x => x.Connected && x.IsSessionSynced(currentProject.SessionID)))//!x.IsSynced))
            {
                if (!Manager.Nodes.Any(x => x.Connected))
                {
                    MessageWindow.Show(this, "No Nodes", "Need at least one connected node to import");
                    return null;
                }

                if (await YesNoWindow.Show(this, "No Synced Node", "Require atleast one synced node to import settings, would you like to sync?"))
                {
                    if (!CurrentProject.UseNetworkedPath)
                        await Manager?.Sync(CurrentProject.BlendFile, UseSyncCompression);
                    else
                        await Manager?.Sync(CurrentProject.BlendFile, CurrentProject.NetworkPathWindows, CurrentProject.NetworkPathLinux, CurrentProject.NetworkPathMacOS);
                }
                else return null;
            }

            //Start rendering thread
            return await Task.Run<BlenderPeekResponse>(async () =>
            {
                try
                {
                    BlenderPeekResponse peekInfo = await Manager.Peek(CurrentProject.BlendFile);

                    if (!peekInfo.Success)
                        throw new Exception(peekInfo.Message);

                    return peekInfo;

                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        MessageWindow.Show(this, "Failed Peek", "Failed peek due to:" + ex.Message);
                    });
                    return null;
                }
            });
        }
        public async Task ImportSettings()
        {
            OpenBlenderProject currentProject = CurrentProject;
            BlenderPeekResponse peekInfo = await RequestPeek(currentProject);
            if(peekInfo != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    currentProject.RenderWidth = peekInfo.RenderWidth;
                    currentProject.RenderHeight = peekInfo.RenderHeight;
                    currentProject.FrameStart = peekInfo.FrameStart;
                    currentProject.FrameEnd = peekInfo.FrameEnd;
                    currentProject.Samples = peekInfo.Samples;
                    currentProject.TriggerPropertyChange(
                        nameof(currentProject.RenderWidth),
                        nameof(currentProject.RenderHeight),
                        nameof(currentProject.FrameStart),
                        nameof(currentProject.FrameEnd),
                        nameof(currentProject.Samples));
                    LoadMeta(currentProject, peekInfo);
                });
            }
        }
        public async Task ImportMeta()
        {
            OpenBlenderProject currentProject = CurrentProject;
            BlenderPeekResponse peekInfo = await RequestPeek(currentProject);
            if (peekInfo != null)
            {
                LoadMeta(currentProject, peekInfo);
            }
        }

        public void LoadMeta(OpenBlenderProject project, BlenderPeekResponse peekInfo)
        {
            _loadingMeta = true;
            project.CamerasAvailable.Clear();
            project.CamerasAvailable.AddRange(peekInfo.Cameras);
            project.Camera = peekInfo.SelectedCamera;
            project.ScenesAvailable.Clear();
            project.ScenesAvailable.AddRange(peekInfo.Scenes);
            project.Scene = peekInfo.SelectedScene;
            RefreshSceneCameraControls();
            project.TriggerPropertyChange(
                nameof(project.CamerasAvailable),
                nameof(project.Camera),
                nameof(project.ScenesAvailable),
                nameof(project.Scene));
            _loadingMeta = false;
        }

        private void RefreshSceneCameraControls()
        {
            if (_sceneComboBox == null || _cameraComboBox == null || CurrentProject == null)
                return;

            _updatingSceneCameraControls = true;
            try
            {
                _sceneComboBox.Items = null;
                _sceneComboBox.Items = CurrentProject.ScenesAvailable;
                _sceneComboBox.SelectedItem = CurrentProject.ScenesAvailable.Contains(CurrentProject.Scene)
                    ? CurrentProject.Scene
                    : null;

                _cameraComboBox.Items = null;
                _cameraComboBox.Items = CurrentProject.CamerasAvailable;
                _cameraComboBox.SelectedItem = CurrentProject.CamerasAvailable.Contains(CurrentProject.Camera)
                    ? CurrentProject.Camera
                    : null;
            }
            finally
            {
                _updatingSceneCameraControls = false;
            }
        }

        private void SetCurrentProjectScene(string scene)
        {
            bool changed = CurrentProject.Scene != scene;
            CurrentProject.Scene = scene;
            if (_sceneComboBox != null && !_updatingSceneCameraControls && !Equals(_sceneComboBox.SelectedItem?.ToString(), scene))
                _sceneComboBox.SelectedItem = scene;
            Console.WriteLine($"UI scene selected: '{CurrentProject.Scene}'");

            if (changed && !_loadingMeta)
                ClearCurrentProjectCameraOverride();

            CurrentProject.TriggerPropertyChange(nameof(CurrentProject.Scene));
        }

        public void SelectSceneFromList(string scene)
        {
            if (CurrentProject == null)
                return;

            SetCurrentProjectScene(scene ?? "");
        }

        private void SetCurrentProjectCamera(string camera)
        {
            CurrentProject.Camera = camera;
            if (_cameraComboBox != null && !_updatingSceneCameraControls && !Equals(_cameraComboBox.SelectedItem?.ToString(), camera))
                _cameraComboBox.SelectedItem = camera;
            Console.WriteLine($"UI camera selected: '{CurrentProject.Camera}'");
            CurrentProject.TriggerPropertyChange(nameof(CurrentProject.Camera));
        }

        public void SelectCameraFromList(string camera)
        {
            if (CurrentProject == null)
                return;

            SetCurrentProjectCamera(camera ?? "");
        }

        private void ClearCurrentProjectCameraOverride()
        {
            CurrentProject.Camera = "";
            if (_cameraComboBox != null && !_updatingSceneCameraControls)
                _cameraComboBox.SelectedItem = null;
            Console.WriteLine("UI camera override cleared after scene change");
            CurrentProject.TriggerPropertyChange(nameof(CurrentProject.Camera));
        }

        public async Task Test()
        {
            OpenBlenderProject currentProject = CurrentProject;
            try
            {
                LocalServer.Manager.ExtractDependencies(Version.Name, currentProject.BlendFile, Manager.GetOrCreateSession(currentProject.BlendFile).FileID);
            }
            catch(Exception ex)
            {
                MessageWindow.Show(this, "Test failed", ex.Message);
            }
        }

        private void ResetRenderTiming()
        {
            if (_lastRenderTime != null)
                _lastRenderTime.Text = "0:00";
            if (_estimatedRenderTime != null)
                _estimatedRenderTime.Text = "Calculating...";
            UpdateCenterRenderProgress(0);
        }

        private void UpdateRenderTiming(Stopwatch watch, double progress)
        {
            if (_lastRenderTime != null)
                _lastRenderTime.Text = FormatDuration(watch.Elapsed);
            if (_estimatedRenderTime != null)
                _estimatedRenderTime.Text = GetEstimatedRemainingText(watch.Elapsed, progress);
            UpdateCenterRenderProgress(progress);
        }

        private static string GetEstimatedRemainingText(TimeSpan elapsed, double progress)
        {
            if (progress >= 1)
                return "Done";
            if (progress <= 0.001 || elapsed.TotalSeconds < 1)
                return "Calculating...";

            double remainingSeconds = elapsed.TotalSeconds * ((1 - progress) / progress);
            if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds) || remainingSeconds < 0)
                return "Calculating...";

            return "~ " + FormatDuration(TimeSpan.FromSeconds(remainingSeconds));
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalDays >= 1)
                return $"{(int)value.TotalDays}d {value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}";
            if (value.TotalHours >= 1)
                return value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
            return value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        private void ResetAnimationProgress(int startFrame, int endFrame)
        {
            if (_animationProgressPanel != null)
                _animationProgressPanel.IsVisible = true;
            UpdateAnimationProgress(TimeSpan.Zero, startFrame, endFrame, 0, startFrame);
        }

        private void UpdateAnimationProgress(TimeSpan elapsed, int startFrame, int endFrame, int completedFrames, int currentFrame)
        {
            int totalFrames = Math.Max(1, endFrame - startFrame + 1);
            completedFrames = Math.Max(0, Math.Min(completedFrames, totalFrames));
            currentFrame = Math.Max(startFrame, Math.Min(currentFrame, endFrame));
            double progress = (double)completedFrames / totalFrames;

            if (_animationFramesRendered != null)
                _animationFramesRendered.Text = $"{completedFrames} / {totalFrames}";
            if (_animationCurrentFrame != null)
                _animationCurrentFrame.Text = completedFrames >= totalFrames ? "Done" : currentFrame.ToString(CultureInfo.InvariantCulture);
            if (_animationEstimatedRemaining != null)
                _animationEstimatedRemaining.Text = GetEstimatedRemainingText(elapsed, progress);
            if (_animationTimelineProgress != null)
                _animationTimelineProgress.Value = progress * 100;
            if (_animationTimeline != null)
                _animationTimeline.Text = BuildFrameTimeline(startFrame, endFrame, currentFrame, progress);
            UpdateCenterRenderProgress(progress);
        }

        private static string BuildFrameTimeline(int startFrame, int endFrame, int currentFrame, double progress)
        {
            int slots = 22;
            int marker = Math.Max(0, Math.Min(slots - 1, (int)Math.Round(progress * (slots - 1))));
            char[] line = Enumerable.Repeat('-', slots).ToArray();
            line[marker] = 'o';
            return $"{startFrame:000} {new string(line)} {endFrame:000}";
        }

        private void RecordRenderedFrames(int frames, TimeSpan elapsed)
        {
            if (DateTime.Today != _statsDate)
            {
                _statsDate = DateTime.Today;
                _framesRenderedToday = 0;
            }

            if (frames <= 0)
                return;

            _framesRenderedToday += frames;
            _totalFrameRenderTime += elapsed;
            _timedFrameCount += frames;
            RecordEstimatedTimeSaved(elapsed);
            UpdateRenderStats();
        }

        private void RecordEstimatedTimeSaved(TimeSpan elapsed)
        {
            int activeNodes = Manager?.Nodes?.Count(x => x.Connected) ?? 1;
            if (activeNodes > 1)
                _totalRenderTimeSaved += TimeSpan.FromSeconds(elapsed.TotalSeconds * (activeNodes - 1));
        }

        private void UpdateRenderStats()
        {
            if (DateTime.Today != _statsDate)
            {
                _statsDate = DateTime.Today;
                _framesRenderedToday = 0;
            }

            if (_statsTotalSaved != null)
                _statsTotalSaved.Text = FormatDuration(_totalRenderTimeSaved);
            if (_statsFramesToday != null)
                _statsFramesToday.Text = _framesRenderedToday.ToString(CultureInfo.InvariantCulture);
            if (_statsFastestNode != null)
            {
                RenderNode fastest = Manager?.Nodes?
                    .Where(x => x.PerformanceScorePP > 0)
                    .OrderByDescending(x => x.PerformanceScorePP)
                    .FirstOrDefault();
                _statsFastestNode.Text = fastest?.Name ?? "--";
            }
            if (_statsAverageFrame != null)
                _statsAverageFrame.Text = _timedFrameCount > 0
                    ? FormatDuration(TimeSpan.FromSeconds(_totalFrameRenderTime.TotalSeconds / _timedFrameCount))
                    : "--";
        }

        //Singular
        public async Task Render() => await Render(false, false);
        private async Task SyncProjectBeforeRender(OpenBlenderProject project)
        {
            if (project == null || Manager == null)
                return;

            Console.WriteLine($"Syncing project before render: '{project.Name}' scene='{project.Scene}' camera='{project.Camera}'");

            if (!project.UseNetworkedPath)
                await Manager.Sync(project.BlendFile, UseSyncCompression);
            else
                await Manager.Sync(project.BlendFile, project.NetworkPathWindows, project.NetworkPathLinux, project.NetworkPathMacOS);
        }

        public async Task Render(bool noSync, bool noExcep = false)
        {
            if (!noSync && HasActiveQueueItems())
            {
                StartQueueingProcess();
                return;
            }

            OpenBlenderProject currentProject = CurrentProject;

            if (currentProject.CurrentTask != null)
                return;

            //Show Progressbar
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                this._imageProgress.IsVisible = true;
                this._imageProgress.IsIndeterminate = true;
                ResetRenderTiming();
            });

            if (!noSync)
                await SyncProjectBeforeRender(currentProject);

            RenderManagerSettings settings = GetSettingsFromUI(currentProject);

            //Start rendering thread
            await Task.Run(async () =>
            {
                try
                {
                    Stopwatch watch = new Stopwatch();
                    watch.Start();


                    //Create Task

                    RenderTask task = Manager.GetImageTask(currentProject.BlendFile, settings, async (task, updated) =>
                    {
                        //Apply image to canvas
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            currentProject.LastImage = updated.ToAvaloniaBitmap();
                            if (CurrentProject == currentProject)
                                RaisePropertyChanged(CurrentProjectProperty, null, CurrentProject);

                            UpdateRenderTiming(watch, currentProject.CurrentTask?.Progress ?? 0);
                        });
                    });
                    currentProject.SetRenderTask(task);
                    AttachRenderTaskActivityLogging(task);

                    //Progress Updating
                    currentProject.CurrentTask.OnProgress += async (task, progress) =>
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            this._imageProgress.IsIndeterminate = false;
                            this._imageProgress.Value = progress * 100;
                            UpdateRenderTiming(watch, progress);
                            if (progress >= 1)
                                AddActivityLog("Render processing complete", ActivityLogType.Success);
                        });
                    };
                    Dispatcher.UIThread.InvokeAsync(async () => {
                        StartingRender(task);
                    });

                    //Update view
                    await Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(IsRenderingProperty, false, true));

                    //Render
                    await currentProject.CurrentTask.Render();
                    var finalImage = ((currentProject.CurrentTask is IImageTask) ? (IImageTask)currentProject.CurrentTask : null)?.FinalImage;

                    //Finalize
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (finalImage != null)
                        {
                            currentProject.LastImage = finalImage.ToAvaloniaBitmap();
                            if(currentProject == CurrentProject)
                                RaisePropertyChanged(CurrentProjectProperty, null, CurrentProject);

                            finalImage.Save("lastRender.png");
                        }
                        UpdateRenderTiming(watch, 1);
                        RecordRenderedFrames(1, watch.Elapsed);
                        this._imageProgress.IsVisible = false;
                        AddActivityLog($"Render completed in {FormatDuration(watch.Elapsed)}", ActivityLogType.Success);
                    });
                    watch.Stop();

                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        this._imageProgress.IsVisible = false;
                        if (_estimatedRenderTime != null)
                            _estimatedRenderTime.Text = "Failed";
                        AddActivityLog($"Render failed: {ex.Message}", ActivityLogType.Error);
                        if (_animationEstimatedRemaining != null && _animationProgressPanel?.IsVisible == true)
                            _animationEstimatedRemaining.Text = "Failed";
                    });

                    if(!noExcep)
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            MessageWindow.Show(this, "Failed Render", "Failed render due to:" + ex.Message);
                        });
                }
                finally
                {
                    Manager.ClearLastTask();
                    DetachRenderTaskActivityLogging(currentProject.CurrentTask);
                    currentProject.SetRenderTask(null);
                    Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(IsRenderingProperty, true, false));
                }
            });
        }
        public async void RenderAnimation()
        {
            if (CurrentTask != null)
                return;

            OpenBlenderProject currentProject = CurrentProject;

            //Validate provided fileformat
            if(!currentProject.AnimationFileFormat.Contains("#"))
            {
                await MessageWindow.Show(this, "Invalid file format", "File format should contain a '#' for frame number");
                return;
            }
            string validAnimationFileName = currentProject.AnimationFileFormat.Replace("#", "");
            if(Path.GetInvalidFileNameChars().Any(x=>validAnimationFileName.Contains(x)))
            {
                await MessageWindow.Show(this, "Invalid file format", "File name for animation frames contains illegal characters");
                return;
            }
            string animationFileFormat = currentProject.AnimationFileFormat;



            string outputDir = await OpenFolderDialog("Select a directory to save frames to");
            if (string.IsNullOrEmpty(outputDir))
                return;

            _lastAnimationDirectory = outputDir;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                this._imageProgress.IsVisible = true;
                this._imageProgress.IsIndeterminate = true;
                ResetRenderTiming();
                ResetAnimationProgress(currentProject.FrameStart, currentProject.FrameEnd);
            });

            await SyncProjectBeforeRender(currentProject);

            RenderManagerSettings settings = GetSettingsFromUI(currentProject);

            await Task.Run(async () =>
            {
                try
                {
                    Stopwatch watch = new Stopwatch();
                    watch.Start();
                    int completedFrames = 0;
                    int totalFrames = Math.Max(1, currentProject.FrameEnd - currentProject.FrameStart + 1);
                    DateTime lastFrameFinishedAt = DateTime.UtcNow;

                    
                    //Create Task
                    RenderTask rtask = Manager.GetAnimationTask(currentProject.BlendFile, currentProject.FrameStart, currentProject.FrameEnd, settings, async (task, frame) =>
                    {
                        int renderedFrame = task.Frame;
                        int renderedCount = Interlocked.Increment(ref completedFrames);
                        DateTime now = DateTime.UtcNow;
                        TimeSpan frameDuration = now - lastFrameFinishedAt;
                        lastFrameFinishedAt = now;

                        string fileName = Statics.FormatAnimationFrameFileName(animationFileFormat, task.Frame, currentProject.FrameStart, currentProject.FrameEnd);
                        string filePath = Path.Combine(outputDir, fileName);

                        try
                        {
                            File.WriteAllBytes(filePath, frame.Image);
                        }
                        catch (Exception ex)
                        {
                            await MessageWindow.ShowOnUIThread(this, "Frame Save Error", $"Animation frame {task.Frame} failed to save due to:" + ex.Message);
                            return;
                        }

                        //Apply image to canvas
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            try
                            {
                                using (System.Drawing.Image img = ImageConverter.Convert(frame.Image, task.Parent.Settings.RenderFormat))
                                {
                                    if (img != null)
                                        currentProject.LastImage = img.ToAvaloniaBitmap();
                                    else
                                        currentProject.LastImage = Statics.NoPreviewImage;
                                }
                                if (currentProject == CurrentProject)
                                    RaisePropertyChanged(CurrentProjectProperty, null, CurrentProject);
                            }
                            catch (Exception ex)
                            {
                                _ = MessageWindow.Show(this, "GUI Exception", "An error occured trying to load animation Bitmap in GUI.\n(Animation frame should still be saved)");
                            }
                            double progress = currentProject.CurrentTask?.Progress ?? ((double)renderedCount / totalFrames);
                            UpdateRenderTiming(watch, progress);
                            UpdateAnimationProgress(watch.Elapsed, currentProject.FrameStart, currentProject.FrameEnd, renderedCount, renderedFrame + 1);
                            RecordRenderedFrames(1, frameDuration);
                            AddActivityLog($"Saved frame {task.Frame} as {fileName} ({renderedCount}/{totalFrames})");
                        });
                    });
                    currentProject.SetRenderTask(rtask);
                    AttachRenderTaskActivityLogging(rtask);

                    //Progress Updating
                    currentProject.CurrentTask.OnProgress += async (task, progress) =>
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            this._imageProgress.IsIndeterminate = false;
                            this._imageProgress.Value = progress * 100;
                            UpdateRenderTiming(watch, progress);
                            UpdateAnimationProgress(watch.Elapsed, currentProject.FrameStart, currentProject.FrameEnd, completedFrames, currentProject.FrameStart + completedFrames);
                        });
                    };
                    Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        StartingRender(rtask);
                    });

                    await Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(IsRenderingProperty, false, true));

                    //Render
                    var success = await currentProject.CurrentTask.Render();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        UpdateRenderTiming(watch, success ? 1 : currentProject.CurrentTask.Progress);
                        UpdateAnimationProgress(watch.Elapsed, currentProject.FrameStart, currentProject.FrameEnd, completedFrames, success ? currentProject.FrameEnd : currentProject.FrameStart + completedFrames);
                        this._imageProgress.IsVisible = false;
                        if (success)
                            AddActivityLog($"Animation render completed in {FormatDuration(watch.Elapsed)}", ActivityLogType.Success);
                        else
                            AddActivityLog("Animation render stopped before completion", ActivityLogType.Warning);
                    });
                    if (success)
                        _ = MessageWindow.ShowOnUIThread(this, "Animation Rendered", $"Frames {currentProject.FrameStart} to {currentProject.FrameEnd} rendered.\nLocated at {outputDir}.");

                    watch.Stop();

                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        this._imageProgress.IsVisible = false;
                        if (_estimatedRenderTime != null)
                            _estimatedRenderTime.Text = "Failed";
                        AddActivityLog($"Animation render failed: {ex.Message}", ActivityLogType.Error);
                        if (_animationEstimatedRemaining != null && _animationProgressPanel?.IsVisible == true)
                            _animationEstimatedRemaining.Text = "Failed";
                    });

                    await MessageWindow.ShowOnUIThread(this, "Failed Render", "Failed render due to:" + ex.Message);
                }
                finally
                {
                    Manager.ClearLastTask();
                    DetachRenderTaskActivityLogging(currentProject.CurrentTask);
                    currentProject.SetRenderTask(null);
                    await Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(IsRenderingProperty, true, false));
                }
            });
        }

        public async Task CancelRender()
        {
            RenderTask task = CurrentProject.CurrentTask;
            if (task != null)
                await task.Cancel();
            DetachRenderTaskActivityLogging(task);
            CurrentProject.SetRenderTask(null);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                this._imageProgress.IsVisible = false;
                if (_estimatedRenderTime != null)
                    _estimatedRenderTime.Text = "Cancelled";
                if (_animationEstimatedRemaining != null && _animationProgressPanel?.IsVisible == true)
                    _animationEstimatedRemaining.Text = "Cancelled";
                AddActivityLog("Render cancelled", ActivityLogType.Warning);
                UpdateCenterRenderProgress(task?.Progress ?? 0);
            });
        }

        public void SaveAsDefault()
        {
            CurrentProject?.SaveAsDefault();
        }

        //Queue
        public void StartQueueingProcess()
        {
            if (_queueThread != null)
                return;

            bool oldQueueing = IsQueueing;
            IsQueueing = true;
            Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(IsQueueingProperty, oldQueueing, true));

            _queueThread = new Thread(async () =>
            {
                QueueItem currentItem = null;
                try
                {
                    while (IsQueueing)
                    {
                        Thread.Sleep(500);
                        if (currentItem == null)
                        {
                            QueueItem item = GetNextQueueItem();
                            if (item == null)
                            {
                                IsQueueing = false;
                                break;
                            }

                            currentItem = item;
                            await item.Execute(this, Manager);
                        }
                        else
                        {
                            if (!currentItem.Active)
                            {
                                currentItem = null;
                                Thread.Sleep(1500);
                            }
                        }

                        RefreshQueueName();
                    }
                }
                catch (Exception ex)
                {
                    if (!await YesNoWindow.Show(this, "Exception in Queue", $"Exception \"{ex.Message}\" occured in queue. Continue queue process?"))
                        IsQueueing = false;
                }
                finally
                {
                    RefreshQueueName();
                    _queueThread = null;
                    Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(IsQueueingProperty, true, false));
                }
            });
            _queueThread.Start();
        }
        
        public async Task AddToQueueReplace()
        {
            OpenBlenderProject proj = CurrentProject;

            QueueItem existing = GetProjectQueueItem(proj);

            if(existing != null)
            {
                if (existing.Active)
                    await existing.UpdateValues(this, Manager, GetSettingsFromUI(proj));
            }
            else
                await AddToQueueNew();
        }
        public async Task AddToQueueNew()
        {
            OpenBlenderProject proj = CurrentProject;

            RenderManagerSettings settings = GetSettingsFromUI(proj);

            string saveTo = null;
            if(await YesNoNeverWindow.Show(this, "Queue Save", "Would you like to save this render to a specific path when it finishes?", "saveQueue"))
            {
                saveTo = await OpenSaveFileDialog("Save BlendFarm queue result", "render.png");
            }

            QueueItem item = new QueueItem(this, proj, settings, saveTo);

            lock(Queue)
                Queue.Add(item);

            RefreshQueueName();

        }
        public async Task AddAnimationToQueueNew()
        {
            OpenBlenderProject proj = CurrentProject;

            RenderManagerSettings settings = GetSettingsFromUI(proj);

            string saveTo = await OpenFolderDialog("Directory to save frames to");

            QueueItem item = new QueueItem(this, proj, settings, saveTo, (proj.FrameEnd - proj.FrameStart) + 1)
            {
                FrameFormat = proj.AnimationFileFormat
            };

            lock (Queue)
                Queue.Add(item);
            RefreshQueueName();
        }
        public QueueItem GetNextQueueItem()
        {
            lock (Queue)
                return Queue.FirstOrDefault(x => x.Active);
        }
        public void RemoveQueueItem(QueueItem item)
        {
            lock(Queue)
                Queue.Remove(item);
            if (item.Task != null && !item.Completed && item.Active)
                item.CancelQueueItem();
            RefreshQueueName();
        }


        public async Task SaveImage()
        {
            string result = await OpenSaveFileDialog("Save current BlendFarm render", "render.png");
            if (result != null && CurrentProject.LastImage != null)
                CurrentProject.LastImage.Save(result);
        }


        public void StartLiveRender()
        {
            if (!IsLiveChanging)
            {
                IsLiveChanging = true;
                Manager.OnFileChanged += RenderOnFileChange;
                Manager.AlwaysUpdateFile = true;
                RaisePropertyChanged(IsLiveChangingProperty, false, true);
            }
        }
        public void StopLiveRender()
        {
            Manager.AlwaysUpdateFile = false;
            Manager.OnFileChanged -= RenderOnFileChange;
            IsLiveChanging = false;
            RaisePropertyChanged(IsLiveChangingProperty, true, false);
        }

        public async Task SelectNetworkWindowsPath()
        {
            string path = await OpenFileDialog("Select Network Path for Windows nodes", "Blend file (.blend)", "blend");
            if (path == null)
                return;

            CurrentProject?.SetWindowsNetworkPath(path);
        }
        public async Task SelectNetworkLinuxPath()
        {
            string path = await OpenFileDialog("Select Network Path for Linux nodes", "Blend file (.blend)", "blend");
            if (path == null)
                return;

            CurrentProject?.SetLinuxNetworkPath(path);
        }
        public async Task SelectNetworkMacOSPath()
        {
            string path = await OpenFileDialog("Select Network Path for MacOS nodes", "Blend file (.blend)", "blend");
            if (path == null)
                return;

            CurrentProject?.SetMacOSNetworkPath(path);
        }


        //Buttons Top
        public void Github()
        {
            OpenUrl("https://github.com/LogicReinc/LogicReinc.BlendFarm");
        }
        public void Patreon()
        {
            OpenUrl("https://www.patreon.com/LogicReinc");
        }
        public void Help()
        {
            OpenUrl("https://www.youtube.com/watch?v=EXdwD5t53wc");
        }
        private static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }

        //Dialogs
        public async Task<string> OpenSaveFileDialog(string title, string initialName)
        {
            SaveFileDialog dialog = new SaveFileDialog()
            {
                Title = title
            };
            dialog.InitialFileName = initialName;

            string result = await dialog.ShowAsync(this);
            return Statics.SanitizePath(result);
        }
        public async Task<string> OpenFolderDialog(string title)
        {
            string outputDir = null;

            //Request output directory and UI
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                outputDir = null;

                OpenFolderDialog dialog = new OpenFolderDialog()
                {
                    Title = title
                };

                if (!string.IsNullOrEmpty(_lastAnimationDirectory))
                    dialog.Directory = _lastAnimationDirectory;

                outputDir = await dialog.ShowAsync(this);
                outputDir = Statics.SanitizePath(outputDir);

                this._imageProgress.IsVisible = true;
                this._imageProgress.IsIndeterminate = true;
            });

            if (string.IsNullOrEmpty(outputDir))
                return outputDir;
            else
                return Path.GetFullPath(outputDir);
        }

        public async Task<string> OpenFileDialog(string title, string fileName, string fileExtension)
        {
            OpenFileDialog dialog = new OpenFileDialog()
            {
                Title = title,
                AllowMultiple = false,
                Filters = new List<FileDialogFilter>()
                {
                    new FileDialogFilter()
                    {
                        Name = fileName,
                        Extensions = new List<string>()
                        {
                            fileExtension
                        }
                    }
                }
            };
            string[] results = await dialog.ShowAsync(this);

            if (results.Length == 0)
                return null;
            return Statics.SanitizePath(results[0]);
        }


        //Buttons Tabs
        public void ScrollRight()
        {
            TabScrollIndex = Math.Min(Projects.Count - 1, TabScrollIndex + 1);
            RaisePropertyChanged(TabScrollIndexProperty, -1, TabScrollIndex);
            RaisePropertyChanged(CanTabScrollLeftProperty, !CanTabScrollLeft, CanTabScrollLeft);
            RaisePropertyChanged(CanTabScrollRightProperty, !CanTabScrollLeft, CanTabScrollRight);

            if(TabScrollIndex < Projects.Count && TabScrollIndex >= 0)
                SwitchProject(Projects[TabScrollIndex]);
        }
        public void ScrollLeft()
        {
            TabScrollIndex = Math.Max(0, TabScrollIndex - 1);
            RaisePropertyChanged(TabScrollIndexProperty, -1, TabScrollIndex);
            RaisePropertyChanged(CanTabScrollLeftProperty, !CanTabScrollLeft, CanTabScrollLeft);
            RaisePropertyChanged(CanTabScrollRightProperty, !CanTabScrollLeft, CanTabScrollRight);

            if (TabScrollIndex < Projects.Count && TabScrollIndex >= 0)
                SwitchProject(Projects[TabScrollIndex]);
        }

        //UI Properties
        public void RefreshCurrentProject()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                RaisePropertyChanged(CurrentProjectProperty, null, CurrentProject);
            });
        }

        //Util
        private QueueItem GetProjectQueueItem(OpenBlenderProject proj)
        {
            lock (Queue)
            {
                return Queue.FirstOrDefault(x => x.Project == proj);
            }
        }
        private RenderManagerSettings GetSettingsFromUI(OpenBlenderProject proj = null)
        {
            proj = proj ?? CurrentProject;
            SyncSceneCameraFromInputs(proj);

            int outputWidth = GetScaledRenderDimension(proj.RenderWidth, proj.RenderScale);
            int outputHeight = GetScaledRenderDimension(proj.RenderHeight, proj.RenderScale);
            RenderManagerSettings settings = new RenderManagerSettings()
            {
                Frame = proj.FrameStart,
                Scene = proj.Scene,
                Camera = proj.Camera,
                Strategy = (RenderStrategy)_selectStrategy.SelectedItem,
                Order = (TaskOrder)_selectOrder?.SelectedItem,
                OutputHeight = outputHeight,
                OutputWidth = outputWidth,
                ChunkHeight = ((decimal)proj.ChunkSize / outputHeight),
                ChunkWidth = ((decimal)proj.ChunkSize / outputWidth),
                Samples = proj.Samples,
                Engine = proj.Engine,
                RenderFormat = proj.RenderFormat,
                FPS = (proj.UseFPS) ? proj.FPS : 0,
                Denoiser = (proj.Denoiser == "Inherit") ? "" : proj.Denoiser ?? "",
                BlenderUpdateBugWorkaround = proj.UseWorkaround,
                UseAutoPerformance = UseAutomaticPerformance
            };
            Console.WriteLine($"Render settings payload scene='{settings.Scene}', camera='{settings.Camera}'");
            return settings;
        }

        private void SyncSceneCameraFromInputs(OpenBlenderProject proj)
        {
            if (proj == null || proj != CurrentProject)
                return;
            if (!Dispatcher.UIThread.CheckAccess())
                return;

            string scene = _sceneComboBox?.SelectedItem?.ToString();
            string camera = _cameraComboBox?.SelectedItem?.ToString();

            if (scene != null)
                proj.Scene = scene;
            if (camera != null)
                proj.Camera = camera;
        }

        private static int GetScaledRenderDimension(int dimension, int scale)
        {
            if (dimension <= 0)
                dimension = 1;
            if (scale <= 0)
                scale = 100;

            return Math.Max(1, (int)Math.Round(dimension * (scale / 100.0)));
        }

        private void RenderOnFileChange(BlendFarmManager manager)
        {
            if (CurrentTask?.Progress <= 0)
                return;
            Task.Run(async () =>
            {
                if (IsRendering)
                {
                    await CancelRender();
                }
                if (!IsRendering)
                {
                    await SyncAll();
                    await Render(true, true);
                }
            });
        }



        //Events
        public async void CheckUseQueue(object sender, RoutedEventArgs args)
        {
            ToggleSwitch sw = sender as ToggleSwitch;

            if(sw.IsChecked ?? false)
            {
                IsQueueing = true;
                await Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(IsQueueingProperty, false, true));
                StartQueueingProcess();
            }
            else
            {
                if(GetNextQueueItem() != null)
                {
                    sw.IsChecked = true;
                    IsQueueing = true;
                    await MessageWindow.Show(this, "Cannot disable Queue", "Your queue is not empty, and thus cannot be disabled");
                }
                else
                {
                    IsQueueing = false;
                    await Dispatcher.UIThread.InvokeAsync(() => RaisePropertyChanged(IsQueueingProperty, true, false));
                }
            }
        }

        public void ProjectTabChanged(object sender, SelectionChangedEventArgs args)
        {
            if(args.AddedItems.Count == 1 && Projects.Contains(args.AddedItems[0]))
            {
                SwitchProject(args.AddedItems[0] as OpenBlenderProject);
            }
        }


    }

    public class RenderWindowOptions
    {
        public bool WithAssetSync { get; set; }
        public bool ConnectLocal { get; set; }
        public bool ImportSettings { get; set; }
    }

    public enum ActivityLogType
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class RenderActivityLogEntry
    {
        public string Time { get; }
        public string Message { get; }
        public ActivityLogType LogType { get; }
        public string Icon { get; }

        public RenderActivityLogEntry(string message, ActivityLogType logType = ActivityLogType.Info)
        {
            Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Message = message;
            LogType = logType;
            Icon = logType switch
            {
                ActivityLogType.Success => "\u2713",
                ActivityLogType.Warning => "\u26A0",
                ActivityLogType.Error => "\u2717",
                _ => "\u2139"
            };
        }
    }
}
