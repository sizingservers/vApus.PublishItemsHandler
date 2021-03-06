﻿/*
 * 2016 Sizing Servers Lab, affiliated with IT bachelor degree NMCT
 * University College of West-Flanders, Department GKG (www.sizingservers.be, www.nmct.be, www.howest.be/en)
 * 
 * Author(s):
 *    Dieter Vandroemme
 */
using MySql.Data.MySqlClient;
using RandomUtils;
using RandomUtils.Log;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text;
using System.Threading;
using vApus.Publish;
using vApus.Util;

namespace vApus.PublishItemsHandler {
    internal static class PublishItemHandler {
        private static readonly object _lock = new object();

        private static ConcurrentDictionary<string, string> _connectionStrings = new ConcurrentDictionary<string, string>(); //result set ids, connection string;
        private static ConcurrentDictionary<string, HandleObject> _handleObjects = new ConcurrentDictionary<string, HandleObject>();

        private static string _passwordGUID = "{51E6A7AC-06C2-466F-B7E8-4B0A00F6A21F}";

        private static readonly byte[] _salt = { 0x49, 0x16, 0x49, 0x2e, 0x11, 0x1e, 0x45, 0x24, 0x86, 0x05, 0x01, 0x03, 0x62 };

        private static string _host, _user, _password;
        private static int _port;


        /// <summary>
        /// This must be set correctly before anything else.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="user"></param>
        /// <param name="password"></param>
        public static void Init(string host, int port, string user, string password) {
            var databaseActions = new DatabaseActions() { ConnectionString = string.Format("Server={0};Port={1};Uid={2};Pwd={3};table cache = true;", host, port, user, password) };
            if (!databaseActions.CanConnect())
                throw new Exception("The credentials are not correct.");

            _host = host;
            _port = port;
            _user = user;
            _password = password;
        }

        public static void Handle(object[] items) {
            lock (_lock) {
                foreach (PublishItem item in items)
                    if (!(item is Poll) && item.ResultSetId != null) {
                        if (!_connectionStrings.ContainsKey(item.ResultSetId))
                            _connectionStrings.TryAdd(item.ResultSetId, Schema.Build(_host, _port, _user, _password));

                        string id = item.ResultSetId + item.vApusHost + item.vApusPort;

                        if (!_handleObjects.ContainsKey(id))
                            _handleObjects.TryAdd(id, new HandleObject(id, _connectionStrings[item.ResultSetId]));


                        _handleObjects[id].Handle(item);
                    }

                CleanupIdleHandleObjects();
            }
        }

        /// <summary>
        /// Handle objects that are idle for more than an hour will be disposed.
        /// </summary>
        private static void CleanupIdleHandleObjects() {
            var now = DateTime.Now;
            var newHandleObjects = new ConcurrentDictionary<string, HandleObject>();
            foreach (string id in _handleObjects.Keys) {
                var handleObject = _handleObjects[id];
                if (now - handleObject.LastActivity > TimeSpan.FromHours(1))
                    handleObject.Dispose(); //_connectionStrings will not be cleaned, because of other handle objects can depend on it.             
                else
                    newHandleObjects.TryAdd(id, handleObject);
            }

            _handleObjects = newHandleObjects;
        }

        private class HandleObject : IDisposable {
            private delegate void HandleDel(PublishItem item);
            private HandleDel _handleDel;

            private BackgroundWorkQueue _workQueue = new BackgroundWorkQueue();

            private string _id;
            private DatabaseActions _databaseActions;
            private int _vApusInstanceId = -1, _testId = -1, _stressTestResultId = -1, _concurrencyResultId = -1, _runResultId = -1, _run = -1;
            private ulong _totalRequestCount = 0;

            private HashSet<string> _monitorsMissingHeaders = new HashSet<string>();
            private ConcurrentDictionary<string, ulong> _monitorsWithIds = new ConcurrentDictionary<string, ulong>();

            public DateTime LastActivity { get; private set; }

            public HandleObject(string id, string connectionString) {
                _id = id;
                _databaseActions = new DatabaseActions() { ConnectionString = connectionString };
                _handleDel = AsyncHandle;
            }

            public void Handle(PublishItem item) {
                LastActivity = DateTime.Now;
                _workQueue.EnqueueWorkItem(_handleDel, item);
            }

            private void AsyncHandle(PublishItem item) {
                //Try 10 times.
                int i = 0, tries = 10;
                while (true)
                    try {
                        LastActivity = DateTime.Now;

                        ++i;
                        switch (item.PublishItemType) {
                            case "DistributedTestConfiguration":
                                HandleDistributedTestConfiguration(item);
                                break;
                            case "StressTestConfiguration":
                                HandleStressTestConfiguration(item);
                                break;
                            case "TileStressTestConfiguration":
                                HandleTileStressTestConfiguration(item);
                                break;
                            case "FastConcurrencyResults":
                                break;
                            case "FastRunResults":
                                break;
                            case "TestEvent":
                                HandleTestEvent(item);
                                break;
                            case "RequestResults":
                                HandleRequestResults(item);
                                break;
                            case "ClientMonitorMetrics":
                                break;
                            case "ApplicationLogEntry":
                                break;
                            case "MonitorConfiguration":
                                HandleMonitorConfiguration(item);
                                break;
                            case "MonitorEvent":
                                HandleMonitorEvent(item);
                                break;
                            case "MonitorMetrics":
                                HandleMonitorMetrics(item);
                                break;
                        }
                        break;
                    }
                    catch (Exception ex) {
                        if (i == tries) {
                            Loggers.Log(Level.Error, "Error handling item.", ex, new object[] { "id " + _id, "item " + item });
                            break;
                        }
                        else {
                            Thread.Sleep(i * 10);
                        }
                    }

                LastActivity = DateTime.Now;
            }

            private void HandleDistributedTestConfiguration(PublishItem item) {
                var pi = item as DistributedTestConfiguration;
                _testId = 1; //Monitor workaround for distributed tests.
                SetvApusInstance(pi.vApusHost, pi.vApusHost, pi.vApusPort, pi.vApusVersion, pi.vApusChannel, pi.vApusIsMaster);
                SetDescriptionAndTags(pi.Description, pi.Tags);
            }
            private void HandleStressTestConfiguration(PublishItem item) {
                var pi = item as StressTestConfiguration;
                SetvApusInstance(pi.vApusHost, pi.vApusHost, pi.vApusPort, pi.vApusVersion, pi.vApusChannel, pi.vApusIsMaster);
                SetDescriptionAndTags(pi.Description, pi.Tags);
                SetStressTest(pi.StressTest, "None", pi.Connection, pi.ConnectionProxy, "", pi.ScenariosAndWeights, pi.ScenarioRuleSet,
                    pi.Concurrencies, pi.Runs, pi.InitialMinimumDelayInMilliseconds, pi.InitialMaximumDelayInMilliseconds, pi.MinimumDelayInMilliseconds, pi.MaximumDelayInMilliseconds, pi.Shuffle, pi.ActionDistribution, pi.MaximumNumberOfUserActions,
                    pi.MonitorBeforeInMinutes, pi.MonitorAfterInMinutes, pi.UseParallelExecutionOfRequests, pi.MaximumPersistentConnections, pi.PersistentConnectionsPerHostname);

            }
            private void HandleTileStressTestConfiguration(PublishItem item) {
                var pi = item as TileStressTestConfiguration;
                SetvApusInstance(pi.vApusHost, pi.vApusHost, pi.vApusPort, pi.vApusVersion, pi.vApusChannel, pi.vApusIsMaster);
                SetStressTest(pi.TileStressTest, pi.RunSynchronization, pi.Connection, pi.ConnectionProxy, "", pi.ScenariosAndWeights, pi.ScenarioRuleSet,
                    pi.Concurrencies, pi.Runs, pi.InitialMinimumDelayInMilliseconds, pi.InitialMaximumDelayInMilliseconds, pi.MinimumDelayInMilliseconds, pi.MaximumDelayInMilliseconds, pi.Shuffle, pi.ActionDistribution, pi.MaximumNumberOfUserActions,
                    pi.MonitorBeforeInMinutes, pi.MonitorAfterInMinutes, pi.UseParallelExecutionOfRequests, pi.MaximumPersistentConnections, pi.PersistentConnectionsPerHostname);
            }
            private void HandleTestEvent(PublishItem item) {
                var pi = item as TestEvent;
                switch ((TestEventType)pi.TestEventType) {
                    case TestEventType.TestMessage:
                        AddMessage(item as TestEvent);
                        break;
                    case TestEventType.TestValue: break;
                    case TestEventType.TestInitialized: break;
                    case TestEventType.TestStarted:
                        SetStressTestStarted(GetUtcDateTime(pi.AtInMillisecondsSinceEpochUtc).ToLocalTime());
                        break;
                    case TestEventType.ConcurrencyStarted:
                        //int concurrencyId = int.Parse(GetValues(pi.Parameters, "ConcurrencyId")[0]);
                        SetConcurrencyStarted(int.Parse(GetValues(pi.Parameters, "Concurrency")[0]), GetUtcDateTime(pi.AtInMillisecondsSinceEpochUtc).ToLocalTime());
                        break;
                    case TestEventType.RunInitializedFirstTime:
                        _run = int.Parse(GetValues(pi.Parameters, "Run")[0]);
                        break;
                    case TestEventType.RunStarted:
                        SetRunStarted(_run, GetUtcDateTime(pi.AtInMillisecondsSinceEpochUtc).ToLocalTime());
                        break;
                    case TestEventType.RunDoneOnce: break;
                    case TestEventType.RerunDone: break;
                    case TestEventType.RunStopped:
                        SetRunStopped(GetUtcDateTime(pi.AtInMillisecondsSinceEpochUtc).ToLocalTime());
                        break;
                    case TestEventType.ConcurrencyStopped:
                        SetConcurrencyStopped(GetUtcDateTime(pi.AtInMillisecondsSinceEpochUtc).ToLocalTime());
                        break;
                    case TestEventType.TestStopped:
                        SetStressTestStopped(item.vApusIsMaster, GetUtcDateTime(pi.AtInMillisecondsSinceEpochUtc).ToLocalTime(), pi.Parameters);
                        break;
                    case TestEventType.MasterListeningError: break;
                }
            }
            private void HandleRequestResults(PublishItem item) {
                ++_totalRequestCount;

                if (_databaseActions != null && _runResultId != -1) {
                    var requestResults = item as RequestResults;

                    var sb = new StringBuilder();

                    if (requestResults.VirtualUser != null) {
                        sb.Append("('");
                        sb.Append(_runResultId);
                        sb.Append("', '");
                        sb.Append(requestResults.VirtualUser);
                        sb.Append("', '");
                        sb.Append(MySQLEscapeString(requestResults.UserAction));
                        sb.Append("', '");
                        sb.Append(requestResults.RequestIndex);
                        sb.Append("', '");
                        sb.Append(requestResults.SameAsRequestIndex);
                        sb.Append("', '");
                        sb.Append(MySQLEscapeString(requestResults.Request));
                        sb.Append("', '");
                        sb.Append(requestResults.InParallelWithPrevious ? 1 : 0);
                        sb.Append("', '");
                        sb.Append(requestResults.SentAtInTicksSinceEpochUtc); //No local time here!
                        sb.Append("', '");
                        sb.Append(requestResults.TimeToLastByteInTicks);
                        sb.Append("', '");
                        sb.Append(MySQLEscapeString(requestResults.Meta));
                        sb.Append("', '");
                        sb.Append(requestResults.DelayInMilliseconds);
                        sb.Append("', '");
                        sb.Append(MySQLEscapeString(requestResults.Error));
                        sb.Append("', '");
                        sb.Append(requestResults.Rerun);
                        sb.Append("')");

                        _databaseActions.ExecuteSQL(
                            string.Format("INSERT INTO requestresults(RunResultId, VirtualUser, UserAction, RequestIndex, SameAsRequestIndex, Request, InParallelWithPrevious, SentAtInTicksSinceEpochUtc, TimeToLastByteInTicks, Meta, DelayInMilliseconds, Error, Rerun) VALUES {0};",
                            sb.ToString()));
                    }
                }
            }

            private void HandleMonitorConfiguration(PublishItem item) {
                if (_testId != -1) {
                    var pi = item as MonitorConfiguration;
                    ulong id = SetMonitor(_testId, pi.Monitor, pi.MonitorSource, "", pi.HardwareConfiguration);
                    _monitorsMissingHeaders.Add(pi.Monitor);
                    _monitorsWithIds.TryAdd(pi.Monitor, id);
                }
            }
            private void HandleMonitorEvent(PublishItem item) {
                var pi = item as MonitorEvent;
                switch ((MonitorEventType)pi.MonitorEventType) {
                    case MonitorEventType.MonitorInitialized:
                        break;
                    case MonitorEventType.MonitorStarted:
                        break;
                    case MonitorEventType.MonitorBeforeTestStarted:
                        break;
                    case MonitorEventType.MonitorBeforeTestDone:
                        break;
                    case MonitorEventType.MonitorAfterTestStarted:
                        _databaseActions.ExecuteSQL("DELETE from resultsreadystate;");
                        _databaseActions.ExecuteSQL("INSERT INTO resultsreadystate(State) VALUES('Not ready');");

                        break;
                    case MonitorEventType.MonitorAfterTestDone:
                        _databaseActions.ExecuteSQL("DELETE from resultsreadystate;");
                        _databaseActions.ExecuteSQL("INSERT INTO resultsreadystate(State) VALUES('Ready');");

                        break;
                    case MonitorEventType.MonitorStopped:
                        break;
                }
            }
            private void HandleMonitorMetrics(PublishItem item) {
                if (_testId != -1 && _databaseActions != null) {
                    var pi = item as Publish.MonitorMetrics;
                    ulong monitorId = _monitorsWithIds[pi.Monitor];
                    if (_monitorsMissingHeaders.Contains(pi.Monitor)) {
                        _monitorsMissingHeaders.Remove(pi.Monitor);
                        string[] headers = new string[pi.Headers.Length + 1];
                        headers[0] = string.Empty;
                        Array.Copy(pi.Headers, 0, headers, 1, pi.Headers.Length);
                        SetMonitor(monitorId, headers);
                    }

                    //Store monitor values with a ',' for decimal seperator.
                    CultureInfo prevCulture = Thread.CurrentThread.CurrentCulture;
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("nl-BE");
                    var value = new List<string>();
                    for (int i = 0; i != pi.Values.Length; i++) {
                        object o = pi.Values[i];
                        if (o == null) o = 0;
                        value.Add((o is double) ? StringUtil.DoubleToLongString((double)o) : o.ToString());
                    }

                    var sb = new StringBuilder("('");
                    sb.Append(_monitorsWithIds[pi.Monitor]);
                    sb.Append("', '");
                    sb.Append(Parse(GetUtcDateTime(pi.AtInMillisecondsSinceEpochUtc).ToLocalTime()));
                    sb.Append("', '");
                    sb.Append(MySQLEscapeString(MySQLEscapeString(value.Combine("; "))));
                    sb.Append("')");

                    _databaseActions.ExecuteSQL(string.Format("INSERT INTO monitorresults(MonitorId, TimeStamp, Value) VALUES {0};", sb.ToString()));

                    Thread.CurrentThread.CurrentCulture = prevCulture;
                }
            }

            public void AddMessage(TestEvent item) {
                if (_databaseActions != null && _vApusInstanceId != -1) {
                    _databaseActions.ExecuteSQL(string.Format("INSERT INTO messages(vApusInstanceId, Timestamp, Level, Message) VALUES ({0}, '{1}', {2}, '{3}');", _vApusInstanceId,
                        Parse(GetUtcDateTime(item.PublishItemTimestampInMillisecondsSinceEpochUtc).ToLocalTime()), GetValues(item.Parameters, "Level")[0], MySQLEscapeString(GetValues(item.Parameters, "Message")[0])));
                }
            }
            private void SetDescriptionAndTags(string description, string[] tags) {
                _databaseActions.ExecuteSQL("DELETE FROM description");
                _databaseActions.ExecuteSQL("DELETE FROM tags");

                if (description.Length != 0)
                    _databaseActions.ExecuteSQL("INSERT INTO description(Description) VALUES('" + MySQLEscapeString(description) + "')");

                if (tags.Length != 0) {
                    var rowsToInsert = new List<string>(); //Insert multiple values at once.
                    foreach (string tag in tags) {
                        var sb = new StringBuilder("('");
                        sb.Append(MySQLEscapeString(tag));
                        sb.Append("')");
                        rowsToInsert.Add(sb.ToString());
                    }
                    _databaseActions.ExecuteSQL(string.Format("INSERT INTO tags(Tag) VALUES {0};", rowsToInsert.Combine(", ")));
                }
            }
            public int SetvApusInstance(string hostName, string ip, int port, string version, string channel, bool isMaster) {
                if (_databaseActions != null) {
                    _databaseActions.ExecuteSQL(
                        string.Format("INSERT INTO vapusinstances(HostName, IP, Port, Version, Channel, IsMaster) VALUES('{0}', '{1}', '{2}', '{3}', '{4}', '{5}')",
                        hostName, ip, port, version, channel, isMaster ? 1 : 0)
                        );
                    _vApusInstanceId = (int)_databaseActions.GetLastInsertId();
                    return _vApusInstanceId;
                }
                return 0;
            }
            private int SetStressTest(string stressTest, string runSynchronization, string connection, string connectionProxy, string connectionString, KeyValuePair<string, uint>[] scenariosAndWeights, string scenarioRuleSet, int[] concurrencies, int runs,
                                             int initialMinimumDelayInMilliseconds, int initialMaximumDelayInMilliseconds, int minimumDelayInMilliseconds, int maximumDelayInMilliseconds, bool shuffle, bool actionDistribution,
                int maximumNumberOfUserActions, int monitorBeforeInMinutes, int monitorAfterInMinutes, bool useParallelExecutionOfRequests, int maximumPersistentConnections, int persistentConnectionsPerHostname) {
                if (_databaseActions != null && _vApusInstanceId != -1) {
                    _databaseActions.ExecuteSQL(
                        string.Format(@"INSERT INTO stresstests(vApusInstanceId, StressTest, RunSynchronization, Connection, ConnectionProxy, ConnectionString, Scenarios, ScenarioRuleSet, Concurrencies, Runs,
InitialMinimumDelayInMilliseconds, InitialMaximumDelayInMilliseconds, MinimumDelayInMilliseconds, MaximumDelayInMilliseconds, Shuffle, ActionDistribution, MaximumNumberOfUserActions, MonitorBeforeInMinutes, MonitorAfterInMinutes,
UseParallelExecutionOfRequests, MaximumPersistentConnections, PersistentConnectionsPerHostname)
VALUES('{0}', '{1}', '{2}', '{3}', '{4}', '{5}', '{6}', '{7}', '{8}', '{9}', '{10}', '{11}', '{12}', '{13}', '{14}', '{15}', '{16}', '{17}', '{18}', '{19}', '{20}', '{21}')",
                                      _vApusInstanceId, stressTest, runSynchronization, connection, connectionProxy, connectionString.Encrypt(_passwordGUID, _salt), scenariosAndWeights.Combine(", "), scenarioRuleSet,
                                      concurrencies.Combine(", "), runs, initialMinimumDelayInMilliseconds, initialMaximumDelayInMilliseconds, minimumDelayInMilliseconds, maximumDelayInMilliseconds, shuffle ? 1 : 0, actionDistribution ? 1 : 0,
                                      maximumNumberOfUserActions, monitorBeforeInMinutes, monitorAfterInMinutes, useParallelExecutionOfRequests ? 1 : 0, maximumPersistentConnections, persistentConnectionsPerHostname)
                        );
                    _testId = (int)_databaseActions.GetLastInsertId();
                    return _testId;
                }
                return 0;
            }

            private void SetStressTestStarted(DateTime startedAt) {
                if (_databaseActions != null && _testId != -1) {
                    _databaseActions.ExecuteSQL(
                        string.Format(
                            "INSERT INTO stresstestresults(StressTestId, StartedAt, StoppedAt, Status, StatusMessage) VALUES('{0}', '{1}', '{2}', 'OK', '')",
                            _testId, Parse(startedAt), Parse(DateTime.MinValue))
                        );
                    _stressTestResultId = (int)_databaseActions.GetLastInsertId();
                }
            }

            private void SetConcurrencyStarted(int concurrency, DateTime startedAt) {
                if (_databaseActions != null && _stressTestResultId != -1) {
                    _databaseActions.ExecuteSQL(
                        string.Format(
                            "INSERT INTO concurrencyresults(StressTestResultId, Concurrency, StartedAt, StoppedAt) VALUES('{0}', '{1}', '{2}', '{3}')",
                            _stressTestResultId, concurrency, Parse(startedAt),
                            Parse(DateTime.MinValue))
                        );
                    _concurrencyResultId = (int)_databaseActions.GetLastInsertId();
                }
            }

            private void SetRunStarted(int run, DateTime startedAt) {
                if (_databaseActions != null && _concurrencyResultId != -1) {
                    _totalRequestCount = 0;
                    _databaseActions.ExecuteSQL(
                        string.Format(
                            "INSERT INTO runresults(ConcurrencyResultId, Run, TotalRequestCount, ReRunCount, StartedAt, StoppedAt) VALUES('{0}', '{1}', '0', '0', '{2}', '{3}')",
                            _concurrencyResultId, run, Parse(startedAt), Parse(DateTime.MinValue))
                        );
                    _runResultId = (int)_databaseActions.GetLastInsertId();
                }
            }

            private void SetRunStopped(DateTime stoppedAt) {
                if (_databaseActions != null && _runResultId != -1) {
                    _databaseActions.ExecuteSQL(string.Format("UPDATE runresults SET TotalRequestCount='{1}', StoppedAt='{2}' WHERE Id='{0}'", _runResultId, _totalRequestCount, Parse(stoppedAt)));
                    _totalRequestCount = 0;
                }
            }

            /// <summary>
            ///     Stopped at datetime now.
            /// </summary>
            /// <param name="concurrencyResult"></param>
            private void SetConcurrencyStopped(DateTime stoppedAt) {
                if (_databaseActions != null && _concurrencyResultId != -1)
                    _databaseActions.ExecuteSQL(string.Format("UPDATE concurrencyresults SET StoppedAt='{1}' WHERE Id='{0}'", _concurrencyResultId, Parse(stoppedAt)));
            }

            private void SetStressTestStopped(bool vApusIsMaster, DateTime stoppedAt, KeyValuePair<string, string>[] parameters) {
                if (_databaseActions != null) {
                    if (_stressTestResultId != -1) {
                        _databaseActions.ExecuteSQL(
                        string.Format(
                            "UPDATE stresstestresults SET StoppedAt='{1}', Status='{2}', StatusMessage='{3}' WHERE Id='{0}'",
                             _stressTestResultId, Parse(stoppedAt), GetValues(parameters, "Status")[0], GetValues(parameters, "StatusMessage")[0])
                         );
                    }

                    if (vApusIsMaster) {
                        _databaseActions.ExecuteSQL("DELETE from resultsreadystate;");
                        _databaseActions.ExecuteSQL("INSERT INTO resultsreadystate(State) VALUES('Ready');");
                    }
                }
            }
            private ulong SetMonitor(int stressTestId, string monitor, string monitorSource, string connectionString, string machineConfiguration) {
                if (_databaseActions != null) {
                    if (machineConfiguration == null) machineConfiguration = string.Empty;

                    DataTable dt = null;
                    do {
                        dt = _databaseActions.GetDataTable("SHOW TABLES LIKE 'stresstests';");
                        if (dt.Rows.Count != 0)
                            dt = _databaseActions.GetDataTable("Select Id from stresstests where Id = " + stressTestId + ";");
                        Thread.Sleep(100);

                    } while (dt.Rows.Count == 0);

                    _databaseActions.ExecuteSQL(
                        string.Format(
                            "INSERT INTO monitors(StressTestId, Monitor, MonitorSource, ConnectionString, MachineConfiguration, ResultHeaders) VALUES('{0}', ?Monitor, ?MonitorSource, ?ConnectionString, ?MachineConfiguration, '')", stressTestId),
                            CommandType.Text, new MySqlParameter("?Monitor", monitor), new MySqlParameter("?MonitorSource", monitorSource), new MySqlParameter("?ConnectionString", connectionString.Encrypt(_passwordGUID, _salt)),
                                new MySqlParameter("?MachineConfiguration", machineConfiguration)
                        );
                    return _databaseActions.GetLastInsertId();
                }
                return 0;
            }

            private ulong SetMonitor(ulong monitorId, string[] resultHeaders) {
                if (_databaseActions != null) {
                    _databaseActions.ExecuteSQL(
                        string.Format(
                            "UPDATE monitors SET ResultHeaders = ?ResultHeaders where Id = {0}", monitorId),
                            CommandType.Text, new MySqlParameter("?ResultHeaders", resultHeaders.Combine("; ", string.Empty, "TimestampInMillisecondsSinceEpoch"))
                        );
                    return _databaseActions.GetLastInsertId();
                }
                return 0;
            }

            /// <summary>
            /// Parse a date to a valid string for in a MySQL db.
            /// </summary>
            /// <param name="dateTime"></param>
            /// <returns></returns>
            private string Parse(DateTime dateTime) { return dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff"); }
            /// <summary>
            ///Mimics PHP's mysql_real_escape_string();
            /// </summary>
            /// <param name="s"></param>
            /// <returns></returns>
            private static string MySQLEscapeString(string s) { return System.Text.RegularExpressions.Regex.Replace(s, @"[\r\n\x00\x1a\\'""]", @"\$0"); }

            private List<string> GetValues(KeyValuePair<string, string>[] arr, string key, bool ignoreCase = true) {
                if (ignoreCase) key = key.ToLowerInvariant();
                var values = new List<string>();
                foreach (var kvp in arr)
                    if (ignoreCase) {
                        if (kvp.Key.ToLowerInvariant() == key) values.Add(kvp.Value);
                    }
                    else {
                        if (kvp.Key == key) values.Add(kvp.Value);
                    }

                return values;
            }

            private DateTime GetUtcDateTime(long publishItemTimestampInMillisecondsSinceEpochUtc) {
                return PublishItem.EpochUtc.AddMilliseconds(publishItemTimestampInMillisecondsSinceEpochUtc);
            }

            /// <summary>
            /// 
            /// </summary>
            public void Dispose() {
                _workQueue.Dispose();
                _databaseActions.Dispose();
            }
        }
    }
}
