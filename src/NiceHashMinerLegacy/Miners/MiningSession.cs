using NiceHashMiner.Configs;
using NiceHashMiner.Devices;
using NiceHashMiner.Interfaces;
using NiceHashMiner.Miners.Grouping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using NiceHashMiner.Algorithms;
using NiceHashMiner.Benchmarking;
using NiceHashMiner.Stats;
using NiceHashMiner.Switching;
using NiceHashMinerLegacy.Common.Enums;
using Timer = System.Timers.Timer;

namespace NiceHashMiner.Miners
{
    using GroupedDevices = SortedSet<string>;

    public class MiningSession
    {
        private const string Tag = "MiningSession";
        private const string DoubleFormat = "F12";

        // session varibles fixed

        string _username;

        private List<MiningDevice> _miningDevices;

        private readonly AlgorithmSwitchingManager _switchingManager;

        // session varibles changing
        // GroupDevices hash code doesn't work correctly use string instead
        //Dictionary<GroupedDevices, GroupMiners> _groupedDevicesMiners;
        private Dictionary<string, GroupMiner> _runningGroupMiners = new Dictionary<string, GroupMiner>();

        private bool _isProfitable;

        private bool _isConnectedToInternet;
        private readonly bool _isMiningRegardlesOfProfit;

        // timers 
        // check internet connection 
        private readonly Timer _internetCheckTimer;


        public bool IsMiningEnabled => _miningDevices.Count > 0;

        private bool IsCurrentlyIdle => !IsMiningEnabled || !_isConnectedToInternet || !_isProfitable;

        private readonly Dictionary<Algorithm, BenchChecker> _benchCheckers = new Dictionary<Algorithm, BenchChecker>();
        //private readonly Dictionary<DualAlgorithm, BenchChecker> _dualBenchCheckers = new Dictionary<DualAlgorithm, BenchChecker>();

        public List<int> ActiveDeviceIndexes
        {
            get
            {
                var minerIDs = new List<int>();
                if (!IsCurrentlyIdle)
                {
                    foreach (var miner in _runningGroupMiners.Values)
                    {
                        minerIDs.AddRange(miner.DevIndexes);
                    }
                }

                return minerIDs;
            }
        }

        public MiningSession(List<ComputeDevice> devices, string username)
        {
            _username = username;
            _switchingManager = new AlgorithmSwitchingManager();
            _switchingManager.SmaCheck += SwichMostProfitableGroupUpMethod;

            // initial settup
            SetUsedDevices(devices);

            // init timer stuff
            // set internet checking
            _internetCheckTimer = new Timer();
            _internetCheckTimer.Elapsed += InternetCheckTimer_Tick;
            _internetCheckTimer.Interval = 1 * 1000 * 60; // every minute

            // assume profitable
            _isProfitable = true;
            // assume we have internet
            _isConnectedToInternet = true;

            if (IsMiningEnabled)
            {
                _internetCheckTimer.Start();
            }

            _switchingManager.Start();

            _isMiningRegardlesOfProfit = ConfigManager.GeneralConfig.MinimumProfit == 0;
        }

        #region Timers stuff

        private void InternetCheckTimer_Tick(object sender, EventArgs e)
        {
            if (ConfigManager.GeneralConfig.IdleWhenNoInternetAccess)
            {
                _isConnectedToInternet = Helpers.IsConnectedToInternet();
            }
        }

        #endregion

        #region Start/Stop

        public void StopAllMiners(bool headless)
        {
            if (_runningGroupMiners != null)
            {
                foreach (var groupMiner in _runningGroupMiners.Values)
                {
                    groupMiner.End();
                }

                _runningGroupMiners = new Dictionary<string, GroupMiner>();
            }

            _switchingManager.Stop();

            ApplicationStateManager.ClearRatesAll();

            _internetCheckTimer.Stop();
            //Helpers.AllowMonitorPowerdownAndSleep();

            if (headless) return;

            foreach (var algo in _benchCheckers.Keys)
            {
                var info = _benchCheckers[algo].FinalizeIsDeviant(algo.BenchmarkSpeed, 0);
                if (!info.IsDeviant) continue;
                var result = MessageBox.Show(
                    Translations.Tr("Algorithm {0} was running at a hashrate of {1}, but was benchmarked at {2}. Would you like to take the new value?", algo.AlgorithmUUID, info.Deviation, algo.BenchmarkSpeed), 
                    Translations.Tr("Deviant Algorithm"),
                    MessageBoxButtons.YesNo);
                if (result == DialogResult.Yes)
                {
                    algo.BenchmarkSpeed = info.Deviation;
                }
            }

            //foreach (var algo in _dualBenchCheckers.Keys)
            //{
            //    var info = _dualBenchCheckers[algo].FinalizeIsDeviant(algo.SecondaryBenchmarkSpeed, 0);
            //    if (!info.IsDeviant) continue;
            //    var result = MessageBox.Show(
            //        Translations.Tr("Secondary speed for {0} was running at a hashrate of {1}, but was benchmarked at {2}. Would you like to take the new value?", algo.DualNiceHashID, info.Deviation, algo.SecondaryBenchmarkSpeed), 
            //        Translations.Tr("Deviant Algorithm"),
            //        MessageBoxButtons.YesNo);
            //    if (result == DialogResult.Yes)
            //    {
            //        algo.SecondaryBenchmarkSpeed = info.Deviation;
            //    }
            //}
        }

        public void StopAllMinersNonProfitable()
        {
            if (_runningGroupMiners != null)
            {
                foreach (var groupMiner in _runningGroupMiners.Values)
                {
                    groupMiner.End();
                }

                _runningGroupMiners = new Dictionary<string, GroupMiner>();
            }

            // one of these is redundant
            // THIS ONE PROBABLY MiningStats.ClearApiDataGroups();
            ApplicationStateManager.ClearRatesAll();
        }

        #endregion Start/Stop

        private static string CalcGroupedDevicesKey(GroupedDevices group)
        {
            return string.Join(", ", group);
        }

        public void UpdateUsedDevices(IEnumerable<ComputeDevice> devices)
        {
            _switchingManager.Stop();
            SetUsedDevices(devices);
            _switchingManager.Start();
        }

        private void RestartRunningGroupMiners()
        {
            foreach (var key in _runningGroupMiners.Keys)
            {
                _runningGroupMiners[key].Stop();
                var miningLocation = StratumService.SelectedServiceLocation;
                _runningGroupMiners[key].Start(miningLocation, _username);
            }
        }

        public void RestartMiners()
        {
            _switchingManager.Stop();
            RestartRunningGroupMiners();
            _switchingManager.Start();
        }

        private void SetUsedDevices(IEnumerable<ComputeDevice> devices)
        {
            _miningDevices = GroupSetupUtils.GetMiningDevices(devices, true);
            if (_miningDevices.Count > 0)
            {
                GroupSetupUtils.AvarageSpeeds(_miningDevices);
            }
        }

        // full of state
        private bool CheckIfProfitable(double currentProfit, bool log = true)
        {
            // TODO FOR NOW USD ONLY
            var currentProfitUsd = (currentProfit * ExchangeRateApi.GetUsdExchangeRate());
            _isProfitable =
                _isMiningRegardlesOfProfit
                || !_isMiningRegardlesOfProfit && currentProfitUsd >= ConfigManager.GeneralConfig.MinimumProfit;
            if (log)
            {
                Helpers.ConsolePrint(Tag, "Current Global profit: " + currentProfitUsd.ToString("F8") + " USD/Day");
                if (!_isProfitable)
                {
                    Helpers.ConsolePrint(Tag,
                        "Current Global profit: NOT PROFITABLE MinProfit " +
                        ConfigManager.GeneralConfig.MinimumProfit.ToString("F8") +
                        " USD/Day");
                }
                else
                {
                    var profitabilityInfo = _isMiningRegardlesOfProfit
                        ? "mine always regardless of profit"
                        : ConfigManager.GeneralConfig.MinimumProfit.ToString("F8") + " USD/Day";
                    Helpers.ConsolePrint(Tag, "Current Global profit: IS PROFITABLE MinProfit " + profitabilityInfo);
                }
            }

            return _isProfitable;
        }

        private bool CheckIfShouldMine(double currentProfit, bool log = true)
        {
            // if profitable and connected to internet mine
            var shouldMine = CheckIfProfitable(currentProfit, log) && _isConnectedToInternet;
            if (shouldMine)
            {
                ApplicationStateManager.SetProfitableState(true);
            }
            else
            {
                if (!_isConnectedToInternet)
                {
                    // change msg
                    if (log) Helpers.ConsolePrint(Tag, "NO INTERNET!!! Stopping mining.");
                    ApplicationStateManager.DisplayNoInternetConnection();
                }
                else
                {
                    ApplicationStateManager.SetProfitableState(false);
                }

                // return don't group
                StopAllMinersNonProfitable();
            }

            return shouldMine;
        }

        private void SwichMostProfitableGroupUpMethod(object sender, SmaUpdateEventArgs e)
        {
#if (SWITCH_TESTING)
            MiningDevice.SetNextTest();
#endif
            var profitableDevices = new List<MiningPair>();
            var currentProfit = 0.0d;
            var prevStateProfit = 0.0d;
            foreach (var device in _miningDevices)
            {
                // calculate profits
                device.CalculateProfits(e.NormalizedProfits);
                // check if device has profitable algo
                if (device.HasProfitableAlgo())
                {
                    profitableDevices.Add(device.GetMostProfitablePair());
                    currentProfit += device.GetCurrentMostProfitValue;
                    prevStateProfit += device.GetPrevMostProfitValue;
                }
            }
            var stringBuilderFull = new StringBuilder();
            stringBuilderFull.AppendLine("Current device profits:");
            foreach (var device in _miningDevices)
            {
                var stringBuilderDevice = new StringBuilder();
                stringBuilderDevice.AppendLine($"\tProfits for {device.Device.Uuid} ({device.Device.GetFullName()}):");
                foreach (var algo in device.Algorithms)
                {
                    stringBuilderDevice.AppendLine(
                        $"\t\tPROFIT = {algo.CurrentProfit.ToString(DoubleFormat)}" +
                        $"\t(SPEED = {algo.AvaragedSpeed:e5}" +
                        $"\t\t| NHSMA = {algo.CurNhmSmaDataVal:e5})" +
                        $"\t[{algo.AlgorithmStringID}]"
                    );
                    // TODO second paying ratio logging
                    //if (algo is PluginAlgorithm dualAlg && dualAlg.IsDual)
                    //{
                    //    stringBuilderDevice.AppendLine(
                    //        $"\t\t\t\t\t  Secondary:\t\t {dualAlg.SecondaryAveragedSpeed:e5}" +
                    //        $"\t\t\t\t  {dualAlg.SecondaryCurNhmSmaDataVal:e5}"
                    //    );
                    //}
                }
                // most profitable
                stringBuilderDevice.AppendLine(
                    $"\t\tMOST PROFITABLE ALGO: {device.GetMostProfitableString()}, PROFIT: {device.GetCurrentMostProfitValue.ToString(DoubleFormat)}");
                stringBuilderFull.AppendLine(stringBuilderDevice.ToString());
            }
            Helpers.ConsolePrint(Tag, stringBuilderFull.ToString());

            // check if should mine
            // Only check if profitable inside this method when getting SMA data, cheching during mining is not reliable
            if (CheckIfShouldMine(currentProfit) == false)
            {
                foreach (var device in _miningDevices)
                {
                    device.SetNotMining();
                }

                return;
            }

            // check profit threshold
            Helpers.ConsolePrint(Tag, $"PrevStateProfit {prevStateProfit}, CurrentProfit {currentProfit}");
            if (prevStateProfit > 0 && currentProfit > 0)
            {
                var a = Math.Max(prevStateProfit, currentProfit);
                var b = Math.Min(prevStateProfit, currentProfit);
                //double percDiff = Math.Abs((PrevStateProfit / CurrentProfit) - 1);
                var percDiff = ((a - b)) / b;
                if (percDiff < ConfigManager.GeneralConfig.SwitchProfitabilityThreshold)
                {
                    // don't switch
                    Helpers.ConsolePrint(Tag,
                        $"Will NOT switch profit diff is {percDiff}, current threshold {ConfigManager.GeneralConfig.SwitchProfitabilityThreshold}");
                    // RESTORE OLD PROFITS STATE
                    foreach (var device in _miningDevices)
                    {
                        device.RestoreOldProfitsState();
                    }

                    return;
                }

                Helpers.ConsolePrint(Tag,
                    $"Will SWITCH profit diff is {percDiff}, current threshold {ConfigManager.GeneralConfig.SwitchProfitabilityThreshold}");
            }

            // group new miners 
            var newGroupedMiningPairs = new Dictionary<string, List<MiningPair>>();
            // group devices with same supported algorithms
            {
                var currentGroupedDevices = new List<GroupedDevices>();
                for (var first = 0; first < profitableDevices.Count; ++first)
                {
                    var firstDev = profitableDevices[first].Device;
                    // check if is in group
                    var isInGroup = currentGroupedDevices.Any(groupedDevices => groupedDevices.Contains(firstDev.Uuid));
                    // if device is not in any group create new group and check if other device should group
                    if (isInGroup == false)
                    {
                        var newGroup = new GroupedDevices();
                        var miningPairs = new List<MiningPair>()
                        {
                            profitableDevices[first]
                        };
                        newGroup.Add(firstDev.Uuid);
                        for (var second = first + 1; second < profitableDevices.Count; ++second)
                        {
                            // check if we should group
                            var firstPair = profitableDevices[first];
                            var secondPair = profitableDevices[second];
                            if (GroupingLogic.ShouldGroup(firstPair, secondPair))
                            {
                                var secondDev = profitableDevices[second].Device;
                                newGroup.Add(secondDev.Uuid);
                                miningPairs.Add(profitableDevices[second]);
                            }
                        }

                        currentGroupedDevices.Add(newGroup);
                        newGroupedMiningPairs[CalcGroupedDevicesKey(newGroup)] = miningPairs;
                    }
                }
            }
            //bool IsMinerStatsCheckUpdate = false;
            {
                // check which groupMiners should be stopped and which ones should be started and which to keep running
                var toStopGroupMiners = new Dictionary<string, GroupMiner>();
                var toRunNewGroupMiners = new Dictionary<string, GroupMiner>();
                var noChangeGroupMiners = new Dictionary<string, GroupMiner>();
                // check what to stop/update
                foreach (var runningGroupKey in _runningGroupMiners.Keys)
                {
                    if (newGroupedMiningPairs.ContainsKey(runningGroupKey) == false)
                    {
                        // runningGroupKey not in new group definately needs to be stopped and removed from curently running
                        toStopGroupMiners[runningGroupKey] = _runningGroupMiners[runningGroupKey];
                    }
                    else
                    {
                        // runningGroupKey is contained but needs to check if mining algorithm is changed
                        var miningPairs = newGroupedMiningPairs[runningGroupKey];
                        var newAlgoType = GetMinerPairAlgorithmType(miningPairs);
                        if (newAlgoType != AlgorithmType.NONE && newAlgoType != AlgorithmType.INVALID)
                        {
                            // if algoType valid and different from currently running update
                            if (newAlgoType != _runningGroupMiners[runningGroupKey].AlgorithmUUID)
                            {
                                // remove current one and schedule to stop mining
                                toStopGroupMiners[runningGroupKey] = _runningGroupMiners[runningGroupKey];
                                toRunNewGroupMiners[runningGroupKey] = new GroupMiner(miningPairs, runningGroupKey);
                            }
                            else
                                noChangeGroupMiners[runningGroupKey] = _runningGroupMiners[runningGroupKey];
                        }
                    }
                }

                // check brand new
                foreach (var kvp in newGroupedMiningPairs)
                {
                    var key = kvp.Key;
                    var miningPairs = kvp.Value;
                    if (_runningGroupMiners.ContainsKey(key) == false)
                    {
                        var newGroupMiner = new GroupMiner(miningPairs, key);
                        toRunNewGroupMiners[key] = newGroupMiner;
                    }
                }

                if ((toStopGroupMiners.Values.Count > 0) || (toRunNewGroupMiners.Values.Count > 0))
                {
                    // There is a change in algorithms, change GUI
                    ApplicationStateManager.ClearRatesAll();

                    var stringBuilderPreviousAlgo = new StringBuilder();
                    var stringBuilderCurrentAlgo = new StringBuilder();
                    var stringBuilderNoChangeAlgo = new StringBuilder();

                    // stop old miners                   
                    foreach (var toStop in toStopGroupMiners.Values)
                    {
                        stringBuilderPreviousAlgo.Append($"{toStop.DevicesInfoString}: {toStop.AlgorithmUUID}, ");

                        toStop.Stop();
                        _runningGroupMiners.Remove(toStop.Key);
                        // Deviant checker works only for single Device so we skip if there are multiple devices, BUT NOW WE HAVE PER DEVICE SPEEDS AND WE SHOULD MOVE THIS CHECKER OUTSIDE
                        if (toStop.Miner.MiningPairs.Count != 1) continue;
                        var algo = toStop.Miner.MiningPairs.First().Algorithm;
                        if (_benchCheckers.TryGetValue(algo, out var checker))
                            checker.Stop();
                        //if (algo is DualAlgorithm dual && _dualBenchCheckers.TryGetValue(dual, out var sChecker))
                        //    sChecker.Stop();
                    }

                    // start new miners
                    var miningLocation = StratumService.SelectedServiceLocation;
                    foreach (var toStart in toRunNewGroupMiners.Values)
                    {
                        stringBuilderCurrentAlgo.Append($"{toStart.DevicesInfoString}: {toStart.AlgorithmUUID}, ");
                        toStart.Start(miningLocation, _username);
                        _runningGroupMiners[toStart.Key] = toStart;
                    }

                    // which miners dosen't change
                    foreach (var noChange in noChangeGroupMiners.Values)
                        stringBuilderNoChangeAlgo.Append($"{noChange.DevicesInfoString}: {noChange.AlgorithmUUID}, ");

                    if (stringBuilderPreviousAlgo.Length > 0)
                        Helpers.ConsolePrint(Tag, $"Stop Mining: {stringBuilderPreviousAlgo}");

                    if (stringBuilderCurrentAlgo.Length > 0)
                        Helpers.ConsolePrint(Tag, $"Now Mining : {stringBuilderCurrentAlgo}");

                    if (stringBuilderNoChangeAlgo.Length > 0)
                        Helpers.ConsolePrint(Tag, $"No change  : {stringBuilderNoChangeAlgo}");
                }
            }

            // stats quick fix code
            //if (_currentAllGroupedDevices.Count != _previousAllGroupedDevices.Count) {
            //await MinerStatsCheck();
            //}

            //ApplicationStateManager.ForceMinerStatsUpdate();
            // TODO not awaited, but we probably don't care
            MinerStatsCheck();
        }

        private static AlgorithmType GetMinerPairAlgorithmType(IEnumerable<MiningPair> miningPairs)
        {
            return miningPairs.FirstOrDefault()?.Algorithm?.AlgorithmUUID ?? AlgorithmType.NONE;
        }

        public async Task MinerStatsCheck()
        {
            //_ratesComunication.ClearRates(_runningGroupMiners.Count);
            var checks = new List<GroupMiner>(_runningGroupMiners.Values);
            try
            {
                foreach (var groupMiners in checks)
                {
                    var m = groupMiners.Miner;

                    // skip if not running or if await already in progress
                    if (!m.IsRunning || m.IsUpdatingApi) continue;

                    var ad = await m.GetSummaryAsync();
                    if (ad == null)
                    {
                        Helpers.ConsolePrint(m.MinerTag(), "GetSummary returned null..");
                    }

                    // BROKEN we have per device speeds in MiningStats we use those to check benchmark and mining speed deviation
                    //// Don't attempt unless card is mining alone
                    //if (m.MiningSetup.MiningPairs.Count == 1)
                    //{
                    //    var algo = m.MiningSetup.MiningPairs[0].Algorithm;
                    //    if (!_benchCheckers.TryGetValue(algo, out var checker))
                    //    {
                    //        checker = new BenchChecker();
                    //        _benchCheckers[algo] = checker;
                    //    }
                    //    checker.AppendSpeed(ad.Speed);

                    //    //if (algo is DualAlgorithm dual)
                    //    //{
                    //    //    if (!_dualBenchCheckers.TryGetValue(dual, out var sChecker)) {
                    //    //        sChecker = new BenchChecker();
                    //    //        _dualBenchCheckers[dual] = sChecker;
                    //    //    }
                    //    //    sChecker.AppendSpeed(ad.SecondarySpeed);
                    //    //}
                    //}
                }
                // Update GUI
                ApplicationStateManager.RefreshRates();
                // now we shoud have new global/total rate display it
                var kwhPriceInBtc = ExchangeRateApi.GetKwhPriceInBtc();
                ApplicationStateManager.DisplayTotalRate(MiningStats.GetProfit(kwhPriceInBtc));
            }
            catch (Exception e) { Helpers.ConsolePrint(Tag, e.Message); }
        }
    }
}