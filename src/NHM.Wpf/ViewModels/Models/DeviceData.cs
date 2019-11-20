﻿using NHM.Common;
using NHM.Common.Enums;
using NHMCore;
using NHMCore.Mining;
using NHMCore.Mining.MiningStats;
using NHMCore.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace NHM.Wpf.ViewModels.Models
{
    /// <summary>
    /// Wrapper for <see cref="ComputeDevice"/> to convert for device status DataGrid
    /// </summary>
    public class DeviceData : NotifyChangedBase
    {
        

        public ComputeDevice Dev { get; }

        public DeviceMiningStats DeviceMiningStats { get; private set; } = null;
        public string DeviceMiningStatsProfitability { get; private set; } = "---";
        public string DeviceMiningStatsPluginAlgo { get; private set; } = "---";

        public ObservableCollection<AlgorithmContainer> AlgorithmSettingsCollection { get; private set; } = new ObservableCollection<AlgorithmContainer>();

        public bool Enabled
        {
            get => Dev.Enabled;
            set
            {
                ApplicationStateManager.SetDeviceEnabledState(this, (Dev.B64Uuid, value));
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
            }
        }

        public List<string> AlgoNames { get; private set; }

        // TODO Pending state and error states
        public bool CanStart => Dev.Enabled && Dev.State == DeviceState.Stopped;
        public bool CanStop => Dev.Enabled && (Dev.State == DeviceState.Benchmarking || Dev.State == DeviceState.Mining);

        public string AlgoOptions
        {
            get
            {
                var enabledAlgos = Dev.AlgorithmSettings.Count(a => a.Enabled);
                var benchedAlgos = Dev.AlgorithmSettings.Count(a => !a.BenchmarkNeeded);
                return $"{Dev.AlgorithmSettings.Count} / {enabledAlgos} / {benchedAlgos}";
            }
        }

        public string AlgosEnabled
        {
            get
            {
                var enabledAlgos = Dev.AlgorithmSettings.Count(a => a.Enabled);
                return $"{Dev.AlgorithmSettings.Count} / {enabledAlgos}";
            }
        }

        public string AlgosBenchmarked
        {
            get
            {
                var benchedAlgos = Dev.AlgorithmSettings.Count(a => !a.BenchmarkNeeded);
                return $"{Dev.AlgorithmSettings.Count} / {benchedAlgos}";
            }
        }

        public string ButtonLabel
        {
            get
            {
                // assume disabled
                var buttonLabel = "N/A";
                if (Dev.State == DeviceState.Stopped)
                {
                    buttonLabel = "Start";
                }
                else if (Dev.State == DeviceState.Mining || Dev.State == DeviceState.Benchmarking)
                {
                    buttonLabel = "Stop";
                }
                return Translations.Tr(buttonLabel);
            }
        }

        public ICommand StartStopCommand { get; }

        public DeviceData(ComputeDevice dev)
        {
            AlgoNames = dev.AlgorithmSettings.Select(a => a.AlgorithmName).ToList();
            Dev = dev;

            StartStopCommand = new BaseCommand(StartStopClick);

            Dev.PropertyChanged += DevOnPropertyChanged;

            foreach (var algo in Dev.AlgorithmSettings)
            {
                algo.PropertyChanged += AlgoOnPropertyChanged;
            }

            AlgorithmSettingsCollection = new ObservableCollection<AlgorithmContainer>(Dev.AlgorithmSettings);

            MiningDataStats.DevicesMiningStats.CollectionChanged += DevicesMiningStatsOnCollectionChanged;
        }

        private void DevicesMiningStatsOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            //Logger.Info("DEBUG", $"DevicesMiningStatsOnCollectionChanged {e.Action.ToString()}");
            if (e.NewItems == null)
            {
                DeviceMiningStats = null;
                DeviceMiningStatsProfitability = "---";
                DeviceMiningStatsPluginAlgo = "---";
            }
            else
            {
                var data = e.NewItems.OfType<DeviceMiningStats>().FirstOrDefault(d => d.DeviceUUID == Dev.Uuid);
                if (data != null)
                {
                    DeviceMiningStats = data;
                    // TODO this will not deduct power cost and no BTC scaling
                    DeviceMiningStatsProfitability = data.TotalPayingRate().ToString("0.00000000") + " BTC";
                    // CryptoDredge / Equihash: 1740.77 Sol/s
                    var algoName = string.Join("+", data.Speeds.Select(s => s.type.ToString()));
                    var speedStr = Helpers.FormatSpeedOutput(data.Speeds);
                    DeviceMiningStatsPluginAlgo = $"{data.MinerName} / {algoName}: {speedStr}";
                }
            }
            OnPropertyChanged(nameof(DeviceMiningStats));
            OnPropertyChanged(nameof(DeviceMiningStatsProfitability));
            OnPropertyChanged(nameof(DeviceMiningStatsPluginAlgo));
        }

        private void AlgoOnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AlgorithmContainer.Enabled) || e.PropertyName == nameof(AlgorithmContainer.BenchmarkNeeded))
            {
                OnPropertyChanged(nameof(AlgoOptions));
                OnPropertyChanged(nameof(AlgosEnabled));
                OnPropertyChanged(nameof(AlgosBenchmarked));
            }
        }

        private void DevOnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ComputeDevice.AlgorithmSettings):
                    AlgorithmSettingsCollection = new ObservableCollection<AlgorithmContainer>(Dev.AlgorithmSettings);
                    OrderAlgorithms();
                    OnPropertyChanged(nameof(AlgoOptions));
                    OnPropertyChanged(nameof(AlgosEnabled));
                    OnPropertyChanged(nameof(AlgosBenchmarked));
                    return;
                default:
                    break;
            }
            if (e.PropertyName == nameof(Dev.State))
            {
                OnPropertyChanged(nameof(ButtonLabel));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
            }
            else if (e.PropertyName == nameof(Dev.Enabled))
            {
                OnPropertyChanged(nameof(Enabled));
            }
        }

        public void RefreshDiag()
        {
            Dev.OnPropertyChanged(nameof(Dev.Load));
            Dev.OnPropertyChanged(nameof(Dev.Temp));
            Dev.OnPropertyChanged(nameof(Dev.FanSpeed));
        }

        private void StartStopClick(object param)
        {
            switch (Dev.State)
            {
                case DeviceState.Stopped:
                    ApplicationStateManager.StartSingleDevicePublic(Dev);
                    break;
                case DeviceState.Mining:
                case DeviceState.Benchmarking:
                    ApplicationStateManager.StopSingleDevicePublic(Dev);
                    break;
            }
        }
        #region AlgorithmSettingsCollection SORTING
        private enum SortColumn
        {
            ALGORITHM = 0,
            PLUGIN,
            SPEEDS,
            PAYING_RATE,
            STATUS,
            ENABLED
        }

        private SortColumn _sortColumn = SortColumn.PLUGIN;
        private bool _sortDescending = false; 

        public void OrderAlgorithmsByAlgorithm() => OrderAlgorithmsBy(SortColumn.ALGORITHM);
        public void OrderAlgorithmsByPlugin() => OrderAlgorithmsBy(SortColumn.PLUGIN);
        public void OrderAlgorithmsBySpeeds() => OrderAlgorithmsBy(SortColumn.SPEEDS);
        public void OrderAlgorithmsByPaying() => OrderAlgorithmsBy(SortColumn.PAYING_RATE);
        public void OrderAlgorithmsByStatus() => OrderAlgorithmsBy(SortColumn.STATUS);
        public void OrderAlgorithmsByEnabled() => OrderAlgorithmsBy(SortColumn.ENABLED);

        private void OrderAlgorithmsBy(SortColumn sortByColumn)
        {
            if (_sortColumn == sortByColumn)
            {
                _sortDescending = !_sortDescending;
            }
            else
            {
                _sortColumn = sortByColumn;
                _sortDescending = false;
            }
            OrderAlgorithms();
        }

        private void OrderAlgorithms()
        {
            List<Func<AlgorithmContainer, object>> orderedSortingFunctions = new List<Func<AlgorithmContainer, object>>
            {
                algo => algo.AlgorithmName,
                algo => algo.PluginName,
                algo => algo.BenchmarkSpeed, // FIRST SPEED FIX only
                algo => algo.CurrentEstimatedProfit,
                algo => algo.Status, // TODO STATUS doesn't exist yet
                algo => algo.Enabled,
            };
            // take the first one and order by that first then continue with the rest
            var firstOrder = orderedSortingFunctions[(int)_sortColumn];
            orderedSortingFunctions.RemoveAt((int)_sortColumn);
            IOrderedEnumerable<AlgorithmContainer> ordered;
            if (_sortDescending)
            {
                ordered = AlgorithmSettingsCollection.OrderByDescending(firstOrder);
            }
            else
            {
                ordered = AlgorithmSettingsCollection.OrderBy(firstOrder);
            }

            foreach (var nextOrderBy in orderedSortingFunctions)
            {
                ordered = ordered.ThenBy(nextOrderBy);
            }

            AlgorithmSettingsCollection = new ObservableCollection<AlgorithmContainer>(ordered);
            OnPropertyChanged(nameof(AlgorithmSettingsCollection));
        }


        #endregion AlgorithmSettingsCollection SORTING

        public static implicit operator DeviceData(ComputeDevice dev)
        {
            return new DeviceData(dev);
        }
    }
}
