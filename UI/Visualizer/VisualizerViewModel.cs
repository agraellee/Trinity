﻿using System;
using Trinity.Framework;
using Trinity.Framework.Helpers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Web.UI.WebControls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Serialization;
using JetBrains.Annotations;
using Trinity.Components.Combat;
using Trinity.DbProvider;
using Trinity.Framework.Actors.ActorTypes;
using Trinity.Framework.Avoidance.Structures;
using Trinity.Framework.Objects;
using Trinity.Modules;
using Trinity.Settings;
using Trinity.UI.UIComponents;
using Trinity.UI.Visualizer.RadarCanvas;
using Zeta.Bot;
using Zeta.Bot.Navigation;
using Zeta.Common;
using Zeta.Common.Xml;
using Zeta.Game;
using Expression = System.Linq.Expressions.Expression;


namespace Trinity.UI.Visualizer
{
    [Zeta.XmlEngine.XmlElement("VisualizerViewModel")]
    public class VisualizerViewModel : XmlSettings, INotifyPropertyChanged
    {
        private ObservableCollection<TrinityActor> _objects;
        private static VisualizerViewModel _instance;
        private static Window _window;
        private int _windowWidth;
        private int _windowHeight;
        private int _zoom;
        private RadarVisibilityFlags _visibilityFlags;
        private List<GridColumnFlags> _allGridColumnFlags;
        private bool _showWeighted;
        private const int RefreshRateMs = 50;
        private DateTime _lastRefresh = DateTime.MinValue;
        private string _pauseButtonText = "Pause";
        private bool _isPaused;
        private TrinityActor _selectedObject;
        private ExpandingPanels _expandedPanel;
        private GridColumnFlags _selectedColumns;
        private GridColumnFlags _selectedSort;
        private bool _showPriority;
        private bool _showIgnored;
        private bool _showUncategorized;
        private TrinityActor _currentTarget;
        private TrinityActor _player;
        private SortDirection _sortDirection;

        public VisualizerViewModel() : base(Path.Combine(FileManager.SpecificSettingsPath, "Visualizer.xml"))
        {
            _instance = this;

            //Events.OnCacheUpdated += Cache_OnCacheUpdated;
            BotMain.OnStart += BotMain_OnStart;
            BotMain.OnStop += BotMain_OnStop;

            NotInCacheObjects = new List<TrinityActor>();

            if (BotMain.IsRunning)
                IsBotRunning = true;
        }

        private void OnCloseWindow(object sender, EventArgs e)
        {
            StopThread();
        }

        private void BotMain_OnStart(IBot bot)
        {
            StartThreadAllowed = true;
            IsBotRunning = true;
            StopThread();
        }

        private void BotMain_OnStop(IBot bot)
        {
            StartThreadAllowed = true;
            IsBotRunning = false;
            //StartThread();
            RemoveStatChangerListeners();
        }

        private void StartThread(bool force = false)
        {
            if ((Window != null && Window.IsVisible || force) && !Worker.IsRunning)
            {
                StartThreadAllowed = false;
                Core.Logger.Verbose("Starting Thread for Visualizer Updates");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Worker.Start(ThreadUpdateTask, RefreshRateMs);
                });
            }
        }

        private DateTime LastUpdatedNav = DateTime.MinValue;
        private DateTime LastUpdated = DateTime.MinValue;

        private bool ThreadUpdateTask()
        {
            if (BotMain.IsPausedForStateExecution)
                return false;

            if (DateTime.UtcNow.Subtract(LastUpdated).TotalMilliseconds < RefreshRateMs)
                return false;

            LastUpdated = DateTime.UtcNow;

            using (ZetaDia.Memory.AcquireFrame())
            {
                ZetaDia.Actors.Update();

                if (ZetaDia.Me == null || !ZetaDia.Me.IsValid || !ZetaDia.Service.Hero.IsValid)
                    return false;

                Core.Scenes.Update();
                Core.Update();
                TrinityCombat.Weighting.WeightActors(Core.Targets);
                UpdateVisualizer();
                return false;
            }
        }

        private void StopThread()
        {
            if (Worker.IsRunning)
                Core.Logger.Verbose("Stopping Thread for Visualizer Updates");

            Worker.Stop();
            StartThreadAllowed = true;
            RemoveStatChangerListeners();
        }

        public void UpdateVisualizer()
        {
            if (Window == null || !Window.IsVisible)
                return;

            if (!ZetaDia.IsInGame)
                return;

            using (new PerformanceLogger("Visualizer Update"))
            {
                if (DateTime.UtcNow.Subtract(_lastRefresh).TotalMilliseconds <= RefreshRateMs || IsPaused)
                    return;

                _lastRefresh = DateTime.UtcNow;

                var objects = Core.Targets.ToList();
                var queryableObjects = ApplySort(objects.AsQueryable());

                Objects = new ObservableCollection<TrinityActor>(queryableObjects);

                if (VisibilityFlags.HasFlag(RadarVisibilityFlags.NotInCache))
                {
                    if (DateTime.UtcNow.Subtract(LastUpdatedNotInCacheObjects).TotalMilliseconds > 150)
                    {
                        NotInCacheObjects = new List<TrinityActor>(Core.Targets.Ignored);
                        LastUpdatedNotInCacheObjects = DateTime.UtcNow;
                    }
                }
                else
                {
                    NotInCacheObjects.Clear();
                }

                //if (!IsMouseOverGrid)
                //{
                switch (SelectedTab)
                {
                    case Tab.Actors:
                        var allobjects = new List<TrinityActor>(Objects);
                        allobjects.AddRange(NotInCacheObjects);
                        AllObjects = new ObservableCollection<TrinityActor>(allobjects);
                        break;

                    case Tab.Markers:
                        AllMarkers = new ObservableCollection<TrinityMarker>(Core.Markers);
                        break;

                    case Tab.Minimap:
                        AllMinimap = new ObservableCollection<TrinityMinimapIcon>(Core.Minimap.MinimapIcons);
                        break;
                }
                //}
                //else
                //{
                //    Core.Logger.Verbose("Skipping grid update so grid items can be clicked properly");
                //}

                CurrentTarget = TrinityCombat.Targeting.CurrentTarget;
                Player = Core.Player.Actor;
                if (Player != null)
                {
                    PlayerPositionX = Player.Position.X;
                    PlayerPositionY = Player.Position.Y;
                    PlayerPositionZ = Player.Position.Z;
                    WorldSnoId = Core.Player.WorldSnoId;
                    LevelAreaSnoId = Core.Player.LevelAreaId;
                    PlayerRotation = MathUtil.RadianToDegree(Player.Rotation);
                    IsStuck = Navigator.StuckHandler.IsStuck;
                    IsBlocked = PlayerMover.IsBlocked;
                    OnPropertyChanged(nameof(Player));
                }

                if (!_listeningForStatChanges)
                    AddStatChangerListeners();

                OnPropertyChanged(nameof(CurrentTarget));
                //OnPropertyChanged(nameof(SelectedObject));
            }
        }

        public ObservableCollection<TrinityMinimapIcon> AllMinimap
        {
            get => _allMinimap;
            set => SetField(ref _allMinimap, value);
        }

        public ObservableCollection<TrinityMarker> AllMarkers
        {
            get => _allMarkers;
            set => SetField(ref _allMarkers, value);
        }

        public ObservableCollection<TrinityActor> AllObjects
        {
            get => _allObjects;
            set => SetField(ref _allObjects, value);
        }

        public DateTime LastUpdatedNotInCacheObjects = DateTime.MinValue;

        public List<TrinityActor> NotInCacheObjects
        {
            get => _notInCacheObjects;
            set => SetField(ref _notInCacheObjects, value);
        }

        private bool _listeningForStatChanges;

        public void AddStatChangerListeners()
        {
            Core.Avoidance.NearbyStats.PropertyChanged += NearbyStatsOnPropertyChanged;
            _listeningForStatChanges = true;
        }

        public void RemoveStatChangerListeners()
        {
            Core.Avoidance.NearbyStats.PropertyChanged -= NearbyStatsOnPropertyChanged;
            _listeningForStatChanges = false;
        }

        private void NearbyStatsOnPropertyChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
        {
            OnPropertyChanged(nameof(NearbyStats));
        }

        [XmlIgnore]
        public float PlayerPositionX
        {
            get => _playerPositionX;
            set => SetField(ref _playerPositionX, value);
        }

        [XmlIgnore]
        public float PlayerPositionY
        {
            get => _playerPositionY;
            set => SetField(ref _playerPositionY, value);
        }

        [XmlIgnore]
        public float PlayerPositionZ
        {
            get => _playerPositionZ;
            set => SetField(ref _playerPositionZ, value);
        }

        [XmlIgnore]
        public double PlayerRotation
        {
            get => _playerRotation;
            set => SetField(ref _playerRotation, value);
        }

        [XmlIgnore]
        public bool IsStuck
        {
            get => _isStuck;
            set => SetField(ref _isStuck, value);
        }

        [XmlIgnore]
        public bool IsBlocked
        {
            get => _isBlocked;
            set => SetField(ref _isBlocked, value);
        }

        [XmlIgnore]
        public bool IsAvoiding
        {
            get => _isAvoiding;
            set => SetField(ref _isAvoiding, value);
        }

        [XmlIgnore]
        public int WorldSnoId
        {
            get => _worldSnoId;
            set => SetField(ref _worldSnoId, value);
        }

        [XmlIgnore]
        public int LevelAreaSnoId
        {
            get => _levelAreaSnoId;
            set => SetField(ref _levelAreaSnoId, value);
        }

        [XmlIgnore]
        public TrinityActor Player
        {
            get => _player;
            set => SetField(ref _player, value);
        }

        private List<TrinityActor> ApplyFilter(IList<TrinityActor> objects)
        {
            var result = new List<TrinityActor>();
            //var playerGuid = ZetaDia.ActivePlayerACDId;

            foreach (var o in objects)
            {
                //if (!ShowIgnored && o.TargetingType == TargetingType.Ignore)
                //    continue;

                //if (!ShowPriority && o.TargetingType == TargetingType.Priority)
                //    continue;

                //if (!ShowWeighted && o.TargetingType == TargetingType.Weight)
                //    continue;

                //if (!ShowUncategorized && o.TargetingType == TargetingType.Unknown && o.AcdId != playerGuid)
                //    continue;

                result.Add(o);
            }

            return result;
        }

        [XmlIgnore]
        public static VisualizerViewModel Instance
        {
            get => _instance ?? (_instance = new VisualizerViewModel());
            set => _instance = value;
        }

        [XmlIgnore]
        public ObservableCollection<TrinityActor> Objects
        {
            get => _objects;
            set => SetField(ref _objects, value);
        }

        [XmlIgnore]
        public TrinityActor SelectedObject
        {
            get => _selectedObject;
            set
            {
                if (_selectedObject != value)
                    ExpandedPanel = ExpandingPanels.SelectedItem;

                if (!IsDragging)
                {
                    if (!IsPaused && value != null)
                        Pause();

                    if (IsPaused && value == null)
                        return;
                }
                else
                {
                    if (IsPaused && value == null)
                        TogglePause();
                }

                SetField(ref _selectedObject, value);

                //BlacklistButtonText = IsSelectedBlacklisted ? "Remove from Blacklist" : "Add to Blacklist";
            }
        }

        [XmlIgnore]
        public TrinityMarker SelectedMarker
        {
            get => _selectedMarker;
            set => SetField(ref _selectedMarker, value);
        }

        [XmlIgnore]
        public TrinityMinimapIcon SelectedIcon
        {
            get => _selectedIcon;
            set => SetField(ref _selectedIcon, value);
        }

        [Zeta.XmlEngine.XmlElement("WindowWidth")]
        [DefaultValue(400)]
        public int WindowWidth
        {
            get => _windowWidth;
            set => SetField(ref _windowWidth, value);
        }

        public TrinityStorage Storage => Core.Storage;

        [Zeta.XmlEngine.XmlElement("WindowHeight")]
        [DefaultValue(300)]
        public int WindowHeight
        {
            get => _windowHeight;
            set => SetField(ref _windowHeight, value);
        }

        [Zeta.XmlEngine.XmlElement("Zoom")]
        [DefaultValue(5)]
        public int Zoom
        {
            get => _zoom;
            set => SetField(ref _zoom, value);
        }

        [Zeta.XmlEngine.XmlElement("ShowWeighted")]
        [DefaultValue(true)]
        public bool ShowWeighted
        {
            get => _showWeighted;
            set => SetField(ref _showWeighted, value);
        }

        public bool IsMouseOverGrid
        {
            get => _isMouseOverGrid;
            set => SetField(ref _isMouseOverGrid, value);
        }

        //[Zeta.XmlEngine.XmlElement("ShowGrid")]
        //[DefaultValue(true)]
        //public bool ShowGrid
        //{
        //    get { return _showGrid; }
        //    set { SetField(ref _showGrid, value); }
        //}

        //public GridLength GridPanelHeight
        //{
        //    get
        //    {
        //        // : Invalid cast from 'System.String' to 'System.Windows.GridLength'. at line  (<GridPanelHeight>*</GridPanelHeight>) 
        //        if (StoredGridPanelHeight == "*")
        //        {
        //            return _starHeight;
        //        }

        //        int height;
        //        if (!int.TryParse(StoredGridPanelHeight, out height))
        //        {
        //            return _starHeight;
        //        };

        //        return new GridLength(height);
        //    }
        //    set
        //    {
        //        var val = value.ToString();
        //        if (val != StoredGridPanelHeight)
        //        {
        //            Core.Logger.Log("GridPanelHeight Changed from {0} to {1}", StoredGridPanelHeight, val);
        //            StoredGridPanelHeight = val;
        //            OnPropertyChanged();
        //        }
        //    }
        //}

        private GridLength _gridPanelHeight = new GridLength(0, GridUnitType.Auto);

        [Zeta.XmlEngine.XmlElement("GridPanelHeight")]
        public string StoredGridPanelHeight { get; set; }

        public GridLength GridPanelHeight
        {
            get
            {
                if (_gridPanelHeight.IsAuto)
                {
                    if (!string.IsNullOrEmpty(StoredGridPanelHeight))
                    {
                        var storedGridLength = GetGridLength(StoredGridPanelHeight);
                        _gridPanelHeight = storedGridLength;
                        IsGridPanelExpanded = Math.Abs(_gridPanelHeight.Value) > float.Epsilon;
                        return storedGridLength;
                    }
                    _gridPanelHeight = GetGridLength("*");
                    IsGridPanelExpanded = true;
                }
                return _gridPanelHeight;
            }
            set
            {
                if (_gridPanelHeight != value)
                {
                    _gridPanelHeight = value;
                    StoredGridPanelHeight = value.Value.ToString(CultureInfo.InvariantCulture);
                    OnPropertyChanged();
                    IsGridPanelExpanded = Math.Abs(value.Value) > float.Epsilon;
                }
            }
        }

        [DataMember]
        [DefaultValue(true)]
        public bool IsGridPanelExpanded
        {
            get => _isGridPanelExpanded;
            set => SetField(ref _isGridPanelExpanded, value);
        }

        [IgnoreDataMember]
        public Tab SelectedTab => (Tab)SelectedTabIndex;

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                SetField(ref _selectedTabIndex, value);
                OnPropertyChanged(nameof(SelectedTab));
            }
        }

        public enum Tab
        {
            Actors,
            Markers,
            Minimap
        }

        private GridLength _sidePanelWidth = new GridLength(0, GridUnitType.Auto);

        [Zeta.XmlEngine.XmlElement("GridPanelWidth")]
        public string StoredSidePanelWidth { get; set; }

        public GridLength SidePanelWidth
        {
            get
            {
                if (_sidePanelWidth.IsAuto)
                {
                    if (!string.IsNullOrEmpty(StoredSidePanelWidth))
                    {
                        var storedGridLength = GetGridLength(StoredSidePanelWidth);
                        _sidePanelWidth = storedGridLength;
                        IsSidePanelExpanded = Math.Abs(_sidePanelWidth.Value) > float.Epsilon;
                        return storedGridLength;
                    }
                    _sidePanelWidth = GetGridLength("*");
                    IsSidePanelExpanded = true;
                }
                return _sidePanelWidth;
            }
            set
            {
                if (_sidePanelWidth != value)
                {
                    _sidePanelWidth = value;
                    StoredSidePanelWidth = value.Value.ToString(CultureInfo.InvariantCulture);
                    OnPropertyChanged();
                    IsSidePanelExpanded = Math.Abs(value.Value) > float.Epsilon;
                }
            }
        }

        [DataMember]
        [DefaultValue(true)]
        public bool IsSidePanelExpanded
        {
            get => _isSidePanelExpanded || _sidePanelWidth.IsAuto || _sidePanelWidth.IsStar;
            set => SetField(ref _isSidePanelExpanded, value);
        }
        
        [IgnoreDataMember]
        public bool IsDragging
        {
            get => _isDragging;
            set => SetField(ref _isDragging, value);
        }

        readonly GridLengthConverter _gridLengthConverter = new GridLengthConverter();
        public GridLength GetGridLength<T>(T input)
        {
            var result = new GridLength(1, GridUnitType.Star);

            if (!_gridLengthConverter.CanConvertFrom(input.GetType()))
            {
                return result;
            }
            var convertedValue = _gridLengthConverter.ConvertFrom(input);
            if (convertedValue != null)
            {
                result = (GridLength)convertedValue;
            }
            return result;
        }




        [Zeta.XmlEngine.XmlElement("ShowPriority")]
        [DefaultValue(true)]
        public bool ShowPriority
        {
            get => _showPriority;
            set => SetField(ref _showPriority, value);
        }

        [Zeta.XmlEngine.XmlElement("ShowIgnored")]
        [DefaultValue(false)]
        public bool ShowIgnored
        {
            get => _showIgnored;
            set => SetField(ref _showIgnored, value);
        }

        [IgnoreDataMember]
        public AvoidanceAreaStats NearbyStats => Core.Avoidance.NearbyStats;

        [Zeta.XmlEngine.XmlElement("ShowUncategorized")]
        [DefaultValue(false)]
        public bool ShowUncategorized
        {
            get => _showUncategorized;
            set => SetField(ref _showUncategorized, value);
        }

        [Zeta.XmlEngine.XmlElement("VisibilityFlags1")]
        [Setting, DefaultValue(
            RadarVisibilityFlags.Terrain |
            RadarVisibilityFlags.CurrentPath |
            RadarVisibilityFlags.CurrentTarget |
            RadarVisibilityFlags.Monsters |
            RadarVisibilityFlags.ActivePlayer |
            RadarVisibilityFlags.WalkableNodes |
            RadarVisibilityFlags.SafeNodes |
            RadarVisibilityFlags.Avoidance |
            RadarVisibilityFlags.KiteFromNodes |
            RadarVisibilityFlags.NotInCache |
            RadarVisibilityFlags.RangeGuide)]
        [UIControl(UIControlType.FlagsCheckboxes, UIControlOptions.Inline | UIControlOptions.NoLabel)]
        public RadarVisibilityFlags VisibilityFlags
        {
            get => _visibilityFlags;
            set => SetField(ref _visibilityFlags, value);
        }

        [XmlIgnore]
        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                PauseButtonText = value ? "Unpause" : "Pause";
                SetField(ref _isPaused, value);
            }
        }

        [XmlIgnore]
        public string PauseButtonText
        {
            get => _pauseButtonText;
            set => SetField(ref _pauseButtonText, value);
        }

        [XmlIgnore]
        public string BlacklistButtonText
        {
            get => _pauseButtonText;
            set => SetField(ref _pauseButtonText, value);
        }

        [XmlIgnore]
        public bool IsSelectedBlacklisted => false;

        public GridViewColumnCollection GridViewColumns { get; set; }

        [Flags]
        public enum GridColumnFlags
        {
            None = 0,
            RActorGuid = 1 << 0,
            AcdId = 1 << 1,
            ActorSnoId = 1 << 2,
            InternalName = 1 << 3,
            GameBalanceId = 1 << 4,
            RadiusDistance = 1 << 5,
            Distance = 1 << 6,
            Weight = 1 << 7,
            TargetingType = 1 << 8,
            Type = 1 << 9,
            ActorType = 1 << 10,
            Radius = 1 << 11,
            AnimationState = 1 << 12,
            CurrentAnimation = 1 << 13,
            All = ~(-1 << 14)
        }

        private IEnumerable<TrinityActor> ApplySort(IQueryable<TrinityActor> collection)
        {
            var sortProp = SelectedSort.ToString();
            if (sortProp == "None" || sortProp == "All")
                sortProp = "Weight";

            return OrderByField(collection, sortProp, SortDirection == SortDirection.Ascending);
        }

        [Zeta.XmlEngine.XmlElement("SortDirection")]
        public SortDirection SortDirection
        {
            get => _sortDirection;
            set => SetField(ref _sortDirection, value);
        }

        public static IQueryable<T> OrderByField<T>(IQueryable<T> q, string SortField, bool Ascending)
        {
            var param = Expression.Parameter(typeof(T), "p");
            var prop = Expression.Property(param, SortField);
            var exp = Expression.Lambda(prop, param);
            var method = Ascending ? "OrderBy" : "OrderByDescending";
            var types = new[] { q.ElementType, exp.Body.Type };
            var mce = Expression.Call(typeof(Queryable), method, types, q.Expression, exp);
            return q.Provider.CreateQuery<T>(mce);
        }

        [XmlIgnore]
        public List<GridColumnFlags> AllGridColumnFlags => _allGridColumnFlags ?? (_allGridColumnFlags = Enum.GetValues(typeof(GridColumnFlags)).Cast<GridColumnFlags>().ToList());

        [Zeta.XmlEngine.XmlElement("SelectedSort")]
        public GridColumnFlags SelectedSort
        {
            get => _selectedSort;
            set => SetField(ref _selectedSort, value);
        }

        [Zeta.XmlEngine.XmlElement("SelectedColumnsA")]
        [Setting, DefaultValue(GridColumnFlags.ActorSnoId | GridColumnFlags.InternalName | GridColumnFlags.Weight | GridColumnFlags.Distance | GridColumnFlags.Radius)]
        [UIControl(UIControlType.FlagsCheckboxes, UIControlOptions.Inline | UIControlOptions.NoLabel)]
        public GridColumnFlags SelectedColumns
        {
            get => _selectedColumns;
            set => SetField(ref _selectedColumns, value);
        }

        public enum ExpandingPanels
        {
            SelectedItem = 0,
            CurrentTarget,
            RadarOptions,
            GridFilters,
            GridColumns,
            Avoidance
        }

        [Zeta.XmlEngine.XmlElement("ExpandedPanel")]
        public ExpandingPanels ExpandedPanel
        {
            get => _expandedPanel;
            set => SetField(ref _expandedPanel, value);
        }

        [XmlIgnore]
        public TrinityActor CurrentTarget
        {
            get => _currentTarget;
            set => SetField(ref _currentTarget, value);
        }

        [XmlIgnore]
        public ICommand PauseToggleCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        TogglePause();
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in PauseToggleCommand Command", ex);
                    }
                });
            }
        }

        [XmlIgnore]
        public ICommand SaveAvoidanceCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        Core.Storage.Save();
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in PauseToggleCommand Command", ex);
                    }
                });
            }
        }

        private void TogglePause()
        {
            IsPaused = !IsPaused;
            BotMain.IsPausedForStateExecution = IsPaused;
        }

        private void Pause()
        {
            IsPaused = true;
            BotMain.IsPausedForStateExecution = IsPaused;
        }

        [XmlIgnore]
        public ICommand BlacklistToggleCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        //Core.Logger.Log("Blacklist Toggle Command Fired!");

                        //var sno = SelectedObject.ActorSnoId;
                        //var name = SelectedObject.Name;

                        //if (!IsSelectedBlacklisted)
                        //{
                        //    var inputDialog = new InputDialog("Reason for blacklisting?");
                        //    if (inputDialog.ShowDialog() == true)
                        //    {                                
                        //        CustomBlacklist.Instance.Add(SelectedObject, inputDialog.Answer, BlacklistType.Permanent);
                        //        OnPropertyChanged("SelectedObject");
                        //    }
                        //}
                        //else
                        //{
                        //    CustomBlacklist.Instance.Remove(SelectedObject);
                        //    OnPropertyChanged("SelectedObject");
                        //}
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in BlacklistToggleCommand Command {0}", ex);
                    }

                });
            }
        }

        [XmlIgnore]
        public ICommand SortCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        var sort = (GridColumnFlags)Enum.Parse(typeof(GridColumnFlags), (string)param);

                        if (SelectedSort == sort)
                            SortDirection = 1 - SortDirection;

                        SelectedSort = sort;
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in SortCommand Command", ex);
                    }
                });
            }
        }


        [XmlIgnore]
        public ICommand RadarClickCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        //Pause();
                        //Thread.Sleep(500);

                        //var actor = param as IActor;
                        //if (actor != null)
                        //{
                        //    Core.Logger.Log("Actor: {0} was clicked on radar", actor.Name);
                        //    SelectedObject = actor;
                        //}      
                        SelectedObject = param as TrinityActor;
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in RadarClickCommand Command. {0}", ex);
                    }
                });
            }
        }

        public ICommand StartThreadCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        StartThread();
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in StartThreadCommand Command. {0}", ex);
                    }
                });
            }
        }

        public ICommand ToggleGridPanelCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        GridPanelHeight = IsGridPanelExpanded ? GetGridLength(0) : GetGridLength(200);
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in StartThreadCommand Command. {0}", ex);
                    }
                });
            }
        }

        public ICommand ToggleSidePanelCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        SidePanelWidth = IsSidePanelExpanded ? GetGridLength(0) : GetGridLength(200);
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in StartThreadCommand Command. {0}", ex);
                    }
                });
            }
        }

        public ICommand StopThreadCommand
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        StopThread();
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in StopThreadCommand Command. {0}", ex);
                    }
                });
            }
        }

        public ICommand CopySelected
        {
            get
            {
                return new RelayCommand(param =>
                {
                    try
                    {
                        var actor = param as TrinityActor;
                        if (actor != null)
                        {
                            var sb = new StringBuilder();
                            foreach (var prop in actor.GetType().GetProperties())
                            {
                                sb.AppendLine($"{prop.Name} => {prop.GetValue(actor)}");
                            }
                            Clipboard.SetText(sb.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        Core.Logger.Error("Exception in CopySelected Command. {0}", ex);
                    }
                });
            }
        }



        private bool _isBotRunning;
        public bool IsBotRunning
        {
            get => _isBotRunning;
            set => SetField(ref _isBotRunning, value);
        }

        private bool _startThreadAllowed = true;
        private bool _isGridPanelExpanded;
        private bool _isSidePanelExpanded;
        private int _worldSnoId;
        private int _levelAreaSnoId;
        private double _playerRotation;
        private bool _isStuck;
        private bool _isAvoiding;
        private bool _isBlocked;
        private float _playerPositionZ;
        private float _playerPositionY;
        private float _playerPositionX;
        private List<TrinityActor> _notInCacheObjects;
        private ObservableCollection<TrinityActor> _allObjects;
        private bool _isMouseOverGrid;
        private int _selectedTabIndex;
        private ObservableCollection<TrinityMarker> _allMarkers;
        private TrinityMarker _selectedMarker;
        private bool _isDragging;
        private ObservableCollection<TrinityMinimapIcon> _allMinimap;
        private TrinityMinimapIcon _selectedIcon;


        public bool StartThreadAllowed
        {
            get => _startThreadAllowed;
            set => SetField(ref _startThreadAllowed, value);
        }

        public Window Window
        {
            get => _window;
            set
            {
                if(_window != null)
                    _window.Closed -= OnCloseWindow;

                _window = value;
                _window.Closed += OnCloseWindow;
            }
        }

        public static bool IsWindowOpen { get; set; }
        public static Vector3 DebugPosition { get; set; }

        public new event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        public new virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected string GetLastMethodFullName(int frame = 1)
        {
            var stackTrace = new StackTrace();
            var methodBase = stackTrace.GetFrame(frame).GetMethod();
            if (methodBase.DeclaringType != null)
                return methodBase.DeclaringType.FullName + "." + methodBase.Name + "()";

            return methodBase.Name;
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

    }
}

