using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;

namespace ITSeti.Maintenance.App
{
    internal sealed class Win7ViewModel : ICustomTypeDescriptor, INotifyPropertyChanged
    {
        private static readonly string[] Names = (
            "AdminAccountAuditStatus AdminAnyDeskLabel AdminInventoryInput AdminRmsLabel ApplicationUpdateStatus " +
            "BenchmarkRead BenchmarkReadBrush BenchmarkState BenchmarkTitle BenchmarkWrite CanChangeWindowsUpdatePolicy " +
            "CanCheckApplicationUpdates CanCheckWindowsUpdates CanInstallApplicationUpdate CanInstallSetup CanLaunchDiskTools " +
            "CanLaunchTreeSize CanManageSetup CanOpenDebugDirectory CanOpenRepairLog CanRun CanRunCleanup CanRunFull " +
            "CanRunQuickFull CanRunRepair CanToggleWindowsDefender CheckProgressEntries CheckProgressIsRunning " +
            "CheckProgressPhase CleanupStatus Comparison ComputerName Cpu CpuDetail CpuStatusBrush DebugErrorLine " +
            "DebugFiles DebugSource DebugStage DebugSteps DiskCount DiskDetail DiskGroups DiskStatusBrush EventMessage " +
            "EventStatus FilteredEvents FullRunHint Gpu HasAnyDeskId HasInventoryNumber HasLowDiskSpace " +
            "HasMoreUserIssues HasRmsId History HistoryCount IsBusy Memory MemoryDetail MemoryStatusBrush MemoryType " +
            "MoreUserIssuesLabel NetworkAdapters OverviewCoverage OverviewIssues OverviewStatus OverviewUptimeLabel " +
            "Processes ProcessStatus ScheduleStatus Selected SelectedEvent SelectedHistorySummary SelectedTreeSizeVolume " +
            "SetupBundleStatus SetupBundleTooltip SetupComponents SetupSummary SetupWarning SnapshotDate SoftwareActionStatus " +
            "StandaloneRepairStatus Status SystemDiskSpeedSummary SystemDiskSummary SystemTools TreeSizeVolumes " +
            "UserAnyDeskLabel UserCoverage UserCpuDetail UserCpuLabel UserCpuNameAndTemperature UserCpuValue " +
            "UserDiskDetail UserDiskHealth UserDiskLabel UserDiskValue UserInventoryLabel UserIpLabel UserIpTooltip " +
            "UserIssueDetail UserIssueHeading UserLiveSummary UserMemoryDetail UserMemoryLabel UserMemoryValue " +
            "UserRmsLabel UserStatus UserUptimeLabel UserVisibleIssues WindowsDefenderButtonLabel " +
            "WindowsDefenderStatus WindowsUpdatePolicyStatus WindowsUpdateStatus").Split(' ');

        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly PropertyDescriptorCollection properties;

        public Win7ViewModel()
        {
            properties = new PropertyDescriptorCollection(Names.Select(x => (PropertyDescriptor)new ValueDescriptor(x)).ToArray(), true);
            foreach (var name in new[] { "DiskGroups", "NetworkAdapters", "Processes", "FilteredEvents", "SetupComponents", "History",
                "DebugSteps", "DebugFiles", "SystemTools", "TreeSizeVolumes", "OverviewIssues", "UserVisibleIssues", "CheckProgressEntries" })
                Set(name, new ObservableCollection<Win7Row>());
            foreach (var name in new[] { "CanRun", "CanRunFull", "CanRunQuickFull", "CanRunCleanup", "CanCheckWindowsUpdates" }) Set(name, true);
            foreach (var name in Names.Where(x => x.StartsWith("Can", StringComparison.Ordinal) && !values.ContainsKey(x))) Set(name, false);
            Set("UserCpuValue", 0d);
            Set("UserMemoryValue", 0d);
            Set("UserDiskValue", 0d);
            Set("ComputerName", Environment.MachineName);
            Set("SnapshotDate", "Сохранённых проверок пока нет");
            Set("UserIssueHeading", "Проверка ещё не выполнена");
            Set("UserStatus", "Загрузка сохранённой проверки...");
            Set("Status", "Готово к проверке");
            Set("BenchmarkReadBrush", Brushes.DarkSlateGray);
            Set("CpuStatusBrush", Brushes.DarkSlateGray);
            Set("MemoryStatusBrush", Brushes.DarkSlateGray);
            Set("DiskStatusBrush", Brushes.DarkSlateGray);
            Set("WindowsDefenderButtonLabel", "Недоступно в Windows 7");
            Set("SetupBundleStatus", "");
            Set("SetupWarning", "Версия для Windows 7 показывает установленное ПО без функции установки.");
            Set("ApplicationUpdateStatus", "Для Windows 7 используется отдельный установщик.");
            Set("FullRunHint", "Диагностика и тест скорости системного диска");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public object Get(string name) { object value; return values.TryGetValue(name, out value) ? value : null; }
        public T Get<T>(string name) where T : class { return Get(name) as T; }
        public void Set(string name, object value)
        {
            values[name] = value;
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        public AttributeCollection GetAttributes() { return AttributeCollection.Empty; }
        public string GetClassName() { return GetType().Name; }
        public string GetComponentName() { return null; }
        public TypeConverter GetConverter() { return new TypeConverter(); }
        public EventDescriptor GetDefaultEvent() { return null; }
        public PropertyDescriptor GetDefaultProperty() { return null; }
        public object GetEditor(Type editorBaseType) { return null; }
        public EventDescriptorCollection GetEvents() { return EventDescriptorCollection.Empty; }
        public EventDescriptorCollection GetEvents(Attribute[] attributes) { return EventDescriptorCollection.Empty; }
        public PropertyDescriptorCollection GetProperties() { return properties; }
        public PropertyDescriptorCollection GetProperties(Attribute[] attributes) { return properties; }
        public object GetPropertyOwner(PropertyDescriptor pd) { return this; }

        private sealed class ValueDescriptor : PropertyDescriptor
        {
            public ValueDescriptor(string name) : base(name, null) { }
            public override Type ComponentType { get { return typeof(Win7ViewModel); } }
            public override bool IsReadOnly { get { return false; } }
            public override Type PropertyType { get { return typeof(object); } }
            public override bool CanResetValue(object component) { return false; }
            public override object GetValue(object component) { return ((Win7ViewModel)component).Get(Name); }
            public override void ResetValue(object component) { }
            public override void SetValue(object component, object value) { ((Win7ViewModel)component).Set(Name, value); }
            public override bool ShouldSerializeValue(object component) { return false; }
        }
    }

    internal sealed class Win7Row
    {
        public override string ToString() { return Name ?? Title ?? Model ?? base.ToString(); }
        public string Name { get; set; }
        public string Model { get; set; }
        public string MediaType { get; set; }
        public string Health { get; set; }
        public Brush HealthBrush { get; set; }
        public string TransferMode { get; set; }
        public bool HasPowerOnHours { get; set; }
        public string LifetimeLabel { get; set; }
        public string LifetimeWarning { get; set; }
        public ObservableCollection<Win7Row> Partitions { get; set; }
        public string Capacity { get; set; }
        public string Usage { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }
        public string LinkSpeed { get; set; }
        public string Addresses { get; set; }
        public string Description { get; set; }
        public string Publisher { get; set; }
        public string Signature { get; set; }
        public string Time { get; set; }
        public string LevelLabel { get; set; }
        public string Provider { get; set; }
        public long Id { get; set; }
        public string Message { get; set; }
        public string Source { get; set; }
        public string State { get; set; }
        public string Detail { get; set; }
        public string ActionStatus { get; set; }
        public string Package { get; set; }
        public string InstallLabel { get; set; }
        public string InstallKey { get; set; }
        public bool CanInstall { get; set; }
        public string DateLabel { get; set; }
        public string KindLabel { get; set; }
        public string CpuLabel { get; set; }
        public string MemoryLabel { get; set; }
        public string HistoryDetailLabel { get; set; }
        public string Title { get; set; }
        public Brush Accent { get; set; }
        public string Path { get; set; }
        public string Key { get; set; }
        public string Icon { get; set; }
        public bool Available { get; set; }
        public string ToolTip { get; set; }
        public ITSeti.Maintenance.Win7.LegacySnapshot Snapshot { get; set; }
    }
}
