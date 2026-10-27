#region Related components
using System;
using System.IO;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Logs
{
	public class ServiceComponent : ServiceBase
	{
		public override string ServiceName => "Logs";

		public override string ServiceDescription => "Logs, Traces, Usages, Audits & Observability Metrics";

		#region Properties
		Cache Cache { get; } = Cache.CreateInstance("VIEApps-Services-Logs", Components.Utility.Logger.GetLoggerFactory(), "true".IsEquals(UtilityService.GetAppSetting("Logs:Cache:L1")));

		string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		bool IsDebug { get; set; } = false;

		bool CleaningServiceLogs { get; set; } = false;

		bool FlushingServiceLogs { get; set; } = false;

		bool WriteLogsIntoSeparatedFiles { get; } = "true".IsEquals(UtilityService.GetAppSetting("Logs:WriteLogsIntoSeparatedFiles"));

		int MaxTriedTimes { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:MaxTriedTimes"), out var numbers) && numbers > 0 ? numbers : 2;

		int NumberOfLogItems { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:Numbers"), out var numbers) && numbers > 0 ? numbers : 2000;

		int QueueInterval { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:Queue:Interval"), out var interval) && interval > 0 ? interval : 7;

		static int QueueSize { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:Queue:Size"), out var size) && size > 0 ? size : 256;

		static BoundedChannelFullMode QueueMode { get; } = Enum.TryParse<BoundedChannelFullMode>(UtilityService.GetAppSetting("Logs:Queue:Mode"), out var mode) ? mode : BoundedChannelFullMode.DropOldest;

		Channel<IEnumerable<ServiceLog>> ServiceLogs { get; } = Channel.CreateBounded<IEnumerable<ServiceLog>>(new BoundedChannelOptions(QueueSize)
		{
			SingleWriter = false,
			SingleReader = true,
			FullMode = QueueMode
		});

		Task ServiceLogsFlusher { get; set; }

		Channel<IEnumerable<TraceLog>> TraceLogs { get; } = Channel.CreateBounded<IEnumerable<TraceLog>>(new BoundedChannelOptions(QueueSize)
		{
			SingleWriter = false,
			SingleReader = true,
			FullMode = QueueMode
		});

		Task TraceLogsFlusher { get; set; }

		Channel<IEnumerable<UsageLog>> UsageLogs { get; } = Channel.CreateBounded<IEnumerable<UsageLog>>(new BoundedChannelOptions(QueueSize)
		{
			SingleWriter = false,
			SingleReader = true,
			FullMode = QueueMode
		});

		Task UsageLogsFlusher { get; set; }

		Channel<(AuditLog Log, string FilePath)> AuditLogs { get; } = Channel.CreateBounded<(AuditLog Log, string FilePath)>(new BoundedChannelOptions(QueueSize)
		{
			SingleWriter = false,
			SingleReader = true,
			FullMode = BoundedChannelFullMode.Wait
		});

		Task AuditLogsFlusher { get; set; }

		ConcurrentDictionary<string, byte> PendingAuditFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

		bool IsPreparer { get; } = "true".IsEquals(UtilityService.GetAppSetting("Logs:Preparer"));

		bool IsCollector { get; } = "true".IsEquals(UtilityService.GetAppSetting("Logs:Collector"));

		bool IsServiceStatisticsSampleEnabled { get; } = "true".IsEquals(UtilityService.GetAppSetting("Logs:Statistics:Samples"));

		Channel<StatisticMessage> ServiceStatistics { get; set; }

		Channel<(DateTime Time, double CpuUsage, double MemoryUsage)> RouterStatistics { get; set; }

		((double Min, double Max, double Average) CpuUsage, (double Min, double Max, double Average) MemoryUsage) RouterStats { get; set; } = ((Min: 0.0, Max: 0.0, Average: 0.0), (Min: 0.0, Max: 0.0, Average: 0.0));

		(int Total, int User, int Visitor, int Crawler) Sessions { get; set; } = (Total: 0, User: 0, Visitor: 0, Crawler: 0);
		#endregion

		#region Start/Stop
		public override Task StartAsync(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			this.Syncable = false;
			this.IsDebug = this.IsDebugLogEnabled || args?.FirstOrDefault(arg => arg.IsStartsWith("/logs")) != null;

			// queues
			this.ServiceLogsFlusher = Task.Run(this.FlushServiceLogsAsync);
			this.StartTimer(this.PrepareServiceLogsAsync, this.QueueInterval);

			this.TraceLogsFlusher = Task.Run(this.FlushTraceLogsAsync);
			this.StartTimer(this.PrepareTraceLogsAsync, this.QueueInterval);

			this.UsageLogsFlusher = Task.Run(this.FlushUsageLogsAsync);
			this.StartTimer(this.PrepareUsageLogsAsync, this.QueueInterval);

			this.AuditLogsFlusher = Task.Run(this.FlushAuditLogsAsync);
			this.StartTimer(this.PrepareAuditLogsAsync, this.QueueInterval);

			// as log preparer
			if (this.IsPreparer)
				this.StartTimer(() => this.PrepareLogStoragesAsync(), 7 * 60);

			// as system metric collector
			if (this.IsCollector)
			{
				this.ServiceStatistics = Channel.CreateBounded<StatisticMessage>(new BoundedChannelOptions(1024 * 60)
				{
					SingleWriter = false,
					SingleReader = true,
					FullMode = BoundedChannelFullMode.DropOldest
				});

				this.RouterStatistics = Channel.CreateBounded<(DateTime Time, double CpuUsage, double MemoryUsage)>(new BoundedChannelOptions(256)
				{
					SingleWriter = false,
					SingleReader = true,
					FullMode = BoundedChannelFullMode.DropOldest
				});

				Router.OnRouterWebSocketMessageReceived = (_, message) => this.UpdateRouterStatistics(message);
				this.StartTimer(this.GetRouterStatisticsAsync, 1);

				var now = DateTime.Now;
				var time = now.AddMinutes(1);
				var delayMilliseconds = (int)(new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 1) - now).TotalMilliseconds;
				this.StartTimer(this.ProcessServiceStatisticsAsync, 60, delayMilliseconds);
			}

			return base.StartAsync(args, initializeRepository, this.Cache, next);
		}

		public override async Task StopAsync(string[] args = null, Action<IService> next = null)
		{
			this.ServiceLogs.Writer.TryComplete();
			await this.ServiceLogsFlusher.ConfigureAwait(false);

			this.TraceLogs.Writer.TryComplete();
			await this.TraceLogsFlusher.ConfigureAwait(false);

			this.UsageLogs.Writer.TryComplete();
			await this.UsageLogsFlusher.ConfigureAwait(false);

			this.AuditLogs.Writer.TryComplete();
			await this.AuditLogsFlusher.ConfigureAwait(false);

			if (this.IsCollector)
			{
				this.ServiceStatistics.Writer?.TryComplete();
				this.RouterStatistics.Writer?.TryComplete();
			}

			await base.StopAsync(args, next).ConfigureAwait(false);
		}
		#endregion

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			var stopwatch = Stopwatch.StartNew();
			this.Statistics.RpcEntered();
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationToken);
			try
			{
				switch (requestInfo.ObjectName.ToLower())
				{
					case "service":
					case "servicelog":
					case "servicelogs":
					case "service.log":
					case "service.logs":
						if (requestInfo.Verb.IsEquals("GET"))
						{
							var request = requestInfo.GetRequestExpando();
							var pagination = request.Get<ExpandoObject>("Pagination");
							var pageNumber = pagination.Get("PageNumber", 1);
							var pageSize = pagination.Get("PageSize", 100);
							var filterBy = request.Get<ExpandoObject>("FilterBy");
							return await this.FetchServiceLogsAsync(pageNumber > 0 ? pageNumber : 1, pageSize > 0 ? pageSize : 100, filterBy.Get<string>("CorrelationID"), filterBy.Get<string>("ServiceName"), filterBy.Get<string>("ObjectName"), filterBy.Get<string>("StartTime"), filterBy.Get<string>("EndTime"), cts.Token).ConfigureAwait(false);
						}
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");

					case "trace":
					case "tracelog":
					case "tracelogs":
					case "trace.log":
					case "trace.logs":
						if (requestInfo.Verb.IsEquals("GET"))
						{
							var request = requestInfo.GetRequestExpando();
							var pagination = request.Get<ExpandoObject>("Pagination");
							var pageNumber = pagination.Get("PageNumber", 1);
							var pageSize = pagination.Get("PageSize", 100);
							var filterBy = request.Get<ExpandoObject>("FilterBy");
							return await this.FetchTraceLogsAsync(pageNumber > 0 ? pageNumber : 1, pageSize > 0 ? pageSize : 100, filterBy.Get<string>("CorrelationID"), filterBy.Get<string>("ServiceName"), filterBy.Get<string>("Operation"), filterBy.Get<string>("StartTime"), filterBy.Get<string>("EndTime"), cts.Token).ConfigureAwait(false);
						}
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");

					case "usage":
					case "usagelog":
					case "usagelogs":
					case "usage.log":
					case "usage.logs":
						if (requestInfo.Verb.IsEquals("GET"))
						{
							var request = requestInfo.GetRequestExpando();
							var pagination = request.Get<ExpandoObject>("Pagination");
							var pageNumber = pagination.Get("PageNumber", 1);
							var pageSize = pagination.Get("PageSize", 100);
							var filterBy = request.Get<ExpandoObject>("FilterBy");
							return await this.FetchUsageLogsAsync(pageNumber > 0 ? pageNumber : 1, pageSize > 0 ? pageSize : 100,	filterBy.Get<string>("CorrelationID"), filterBy.Get<string>("TraceID"), filterBy.Get<string>("ServiceName"), filterBy.Get<string>("Operation"), filterBy.Get<string>("Type"), filterBy.Get<string>("Provider"), filterBy.Get<string>("Model"), filterBy.Get<string>("SystemID"), filterBy.Get<string>("UserID"), filterBy.Get<string>("StartTime"), filterBy.Get<string>("EndTime"), cts.Token).ConfigureAwait(false);
						}
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");

					case "audit":
					case "auditlog":
					case "auditlogs":
					case "audit.log":
					case "audit.logs":
						if (requestInfo.Verb.IsEquals("GET"))
						{
							var request = requestInfo.GetRequestExpando();
							var pagination = request.Get<ExpandoObject>("Pagination");
							var pageNumber = pagination.Get("PageNumber", 1);
							var pageSize = pagination.Get("PageSize", 100);
							var filterBy = request.Get<ExpandoObject>("FilterBy");
							return await this.FetchAuditLogsAsync(pageNumber > 0 ? pageNumber : 1, pageSize > 0 ? Math.Min(pageSize, 1000) : 100, filterBy.Get<string>("CorrelationID"), filterBy.Get<string>("TraceID"), filterBy.Get<string>("ActorType"), filterBy.Get<string>("ActorID"), filterBy.Get<string>("ServiceName"), filterBy.Get<string>("ObjectName"), filterBy.Get<string>("ObjectID"), filterBy.Get<string>("SystemID"), filterBy.Get<string>("Operation"), filterBy.Get<string>("Status"), filterBy.Get<string>("StartTime"), filterBy.Get<string>("EndTime"), cts.Token).ConfigureAwait(false);
						}
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");

					default:
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");
				}
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex);
			}
			finally
			{
				this.Statistics.RpcCompleted(stopwatch);
			}
		}

		#region Do sync-work & ensure indexes
		public override void DoWork(string[] args = null)
		{
			this.IsDebug = this.IsDebugLogEnabled || args?.FirstOrDefault(arg => arg.IsStartsWith("/logs")) != null;
			var stopwatch = Stopwatch.StartNew();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/flush")) != null)
			{
				if (this.IsDebug)
					this.Logger.LogInformation("Start flush service logs from files into database");

				if (this.MaxTriedTimes > 1)
				{
					var triedTimes = 0;
					this.FlushServiceLogsAsync((args ?? []).Concat(["/order-mode:Descending"])).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing service logs => {ex.Message}", ex));
					triedTimes++;
					while (triedTimes < this.MaxTriedTimes)
					{
						this.FlushServiceLogsAsync(args).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing service logs => {ex.Message}", ex));
						triedTimes++;
					}
				}
				else
					this.FlushServiceLogsAsync(args).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing service logs => {ex.Message}", ex));

				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete flush service logs from files into database - Execution times: {stopwatch.GetElapsedTimes()}");

				if (this.IsDebug)
					this.Logger.LogInformation("Start flush trace logs from files into database");

				if (this.MaxTriedTimes > 1)
				{
					var triedTimes = 0;
					this.FlushTraceLogsAsync((args ?? []).Concat(["/order-mode:Descending"])).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing trace logs => {ex.Message}", ex));
					triedTimes++;
					while (triedTimes < this.MaxTriedTimes)
					{
						this.FlushTraceLogsAsync(args).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing trace logs => {ex.Message}", ex));
						triedTimes++;
					}
				}
				else
					this.FlushTraceLogsAsync(args).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing trace logs => {ex.Message}", ex));

				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete flush trace logs from files into database - Execution times: {stopwatch.GetElapsedTimes()}");


				stopwatch.Restart();
				if (this.IsDebug)
					this.Logger.LogInformation("Start flush usage logs from files into database");

				if (this.MaxTriedTimes > 1)
				{
					var triedTimes = 0;
					this.FlushUsageLogsAsync((args ?? []).Concat(["/order-mode:Descending"])).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing usage logs => {ex.Message}", ex));
					triedTimes++;
					while (triedTimes < this.MaxTriedTimes)
					{
						this.FlushUsageLogsAsync(args).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing usage logs => {ex.Message}", ex));
						triedTimes++;
					}
				}
				else
					this.FlushUsageLogsAsync(args).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing usage logs => {ex.Message}", ex));

				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete flush usage logs from files into database - Execution times: {stopwatch.GetElapsedTimes()}");
			}

			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/clean")) != null)
			{
				stopwatch.Restart();
				if (this.IsDebug)
					this.Logger.LogInformation("Start clean old service logs from database");
				this.CleanServiceLogsAsync().Execute(true, ex => this.Logger.LogError($"Error occurred while cleaning service logs => {ex.Message}", ex));
				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete clean old service logs from database - Execution times: {stopwatch.GetElapsedTimes()}");

				stopwatch.Restart();
				if (this.IsDebug)
					this.Logger.LogInformation("Start clean old trace logs from database");
				this.CleanTraceLogsAsync().Execute(true, ex => this.Logger.LogError($"Error occurred while cleaning trace logs => {ex.Message}", ex));
				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete clean old trace logs from database - Execution times: {stopwatch.GetElapsedTimes()}");

				stopwatch.Restart();
				if (this.IsDebug)
					this.Logger.LogInformation("Start clean old usage logs from database");

				this.CleanUsageLogsAsync().Execute(true, ex => this.Logger.LogError($"Error occurred while cleaning usage logs => {ex.Message}", ex));

				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete clean old usage logs from database - Execution times: {stopwatch.GetElapsedTimes()}");
			}

			stopwatch.Restart();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/prepare")) != null || args?.FirstOrDefault(arg => arg.IsStartsWith("/storages")) != null)
			{
				if (this.IsDebug)
					this.Logger.LogInformation("Start re-prepare storages of logs");
				this.PrepareLogStoragesAsync(false, args?.FirstOrDefault(arg => arg.IsStartsWith("/dont-drop")) == null).Execute(true, ex => this.Logger.LogError($"Error occurred while preparing storages of service logs => {ex.Message}", ex));
				this.EnsureIndexesAsync(RepositoryMediator.GetEntityDefinition<TraceLog>()).Execute(true, ex => this.Logger.LogError($"Error occurred while preparing trace logs => {ex.Message}", ex));
				this.EnsureIndexesAsync(RepositoryMediator.GetEntityDefinition<UsageLog>()).Execute(true, ex => this.Logger.LogError($"Error occurred while preparing usage logs => {ex.Message}", ex));
				this.EnsureIndexesAsync(RepositoryMediator.GetEntityDefinition<AuditLog>()).Execute(true, ex => this.Logger.LogError($"Error occurred while preparing audit logs => {ex.Message}", ex));
				this.EnsureIndexesAsync(RepositoryMediator.GetEntityDefinition<SystemMetric>()).Execute(true, ex => this.Logger.LogError($"Error occurred while preparing system metrics => {ex.Message}", ex));
				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete re-prepare storages of service-logs - Execution times: {stopwatch.GetElapsedTimes()}");
			}

			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/rpc-gate-max")) != null || args?.FirstOrDefault(arg => arg.IsStartsWith("/change-rpc-gate")) != null)
			{
				var serviceName = args?.FirstOrDefault(arg => arg.IsStartsWith("/service:"))?.Replace("/service:", "", StringComparison.OrdinalIgnoreCase);
				var nodeID = args?.FirstOrDefault(arg => arg.IsStartsWith("/node:"))?.Replace("/node:", "", StringComparison.OrdinalIgnoreCase);
				var max = args?.FirstOrDefault(arg => arg.IsStartsWith("/max:"))?.Replace("/max:", "", StringComparison.OrdinalIgnoreCase);
				this.SendInterCommunicateMessage(new CommunicateMessage("APIGateway")
				{
					Type = "RpcGate#Max",
					Data = new JObject
					{
						["Service"] = serviceName,
						["NodeID"] = nodeID,
						["MaxCapacity"] = Int32.TryParse(max, out var maxCapacity) && maxCapacity > -1 && maxCapacity <= 20000 ? maxCapacity : 0
					}
				}, false, this.IsDebug);
			}
		}

		async Task EnsureIndexesAsync(EntityDefinition entityDefinition)
		{
			var dataSource = entityDefinition?.GetPrimaryDataSource();
			if (dataSource != null && dataSource.Mode == RepositoryMode.NoSQL)
				try
				{
					await entityDefinition.EnsureIndexesAsync(dataSource, (msg, ex) =>
					{
						if (ex != null)
							this.Logger.LogError(msg, ex);
						else if (this.IsDebug)
							this.Logger.LogInformation(msg);
					}).ConfigureAwait(false);
					if (this.IsDebug)
						this.Logger.LogInformation($"Indexes of the collection '{entityDefinition.CollectionName}' was re-prepared successful");
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while preparing indexes of '{entityDefinition.CollectionName}' => {ex.Message}", ex);
				}
		}

		async Task PrepareLogStoragesAsync(bool checkTime = true, bool dropCollection = true)
		{
			var entityDefinition = !checkTime || ((DateTime.Now.DayOfWeek == DayOfWeek.Wednesday || DateTime.Now.DayOfWeek == DayOfWeek.Saturday) && DateTime.Now.Hour == 23 && DateTime.Now.Minute > 45 && DateTime.Now.Minute < 57)
				? RepositoryMediator.GetEntityDefinition<ServiceLog>()
				: null;
			var dataSource = entityDefinition?.GetPrimaryDataSource();
			if (dataSource != null && dataSource.Mode == RepositoryMode.NoSQL)
				try
				{
					if (dropCollection)
					{
						await dataSource.DropCollectionAsync<ServiceLog>(entityDefinition, this.CancellationToken).ConfigureAwait(false);
						if (this.IsDebug)
							this.Logger.LogInformation("Collection of service logs was dropped successful");
					}
					await this.EnsureIndexesAsync(entityDefinition).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while preparing storages of service logs => {ex.Message}", ex);
				}
			else if (entityDefinition == null && DateTime.Now.Hour == 23 && DateTime.Now.Minute > 45 && DateTime.Now.Minute < 57)
				await this.EnsureIndexesAsync(RepositoryMediator.GetEntityDefinition<ServiceLog>()).ConfigureAwait(false);
		}
		#endregion

		#region Process inter-communicate messages
		protected override async Task ProcessInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default)
		{
			if (message.Type.IsEquals("ServiceLog#Clean") && !this.CleaningServiceLogs)
				try
				{
					this.CleaningServiceLogs = true;
					await this.CleanServiceLogsAsync().ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while cleaning old logs => {ex.Message}", ex);
				}
				finally
				{
					this.CleaningServiceLogs = false;
				}

			else if (message.Type.IsEquals("ServiceLog#Flush") && !this.FlushingServiceLogs)
				try
				{
					this.FlushingServiceLogs = true;
					await this.FlushServiceLogsAsync(null).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while flushing service logs => {ex.Message}", ex);
				}
				finally
				{
					this.FlushingServiceLogs = false;
				}
		}

		protected override async Task ProcessGatewayCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default)
		{
			if (message.Type.IsEquals("Service#Statistics") && this.IsCollector)
				await this.UpdateServiceStatisticsAsync(message).ConfigureAwait(false);

			else if (message.Type.IsEquals("Session#Statistics") && this.IsCollector)
				this.Sessions = (message.Data.Get("Total", 0), message.Data.Get("User", 0), message.Data.Get("Visitor", 0), message.Data.Get("Crawler", 0));
		}
		#endregion

		#region Working with service logs
		async Task<IEnumerable<ServiceLog>> PrepareServiceLogsAsync(string[] args)
		{
			var stopwatch = Stopwatch.StartNew();
			var numberOfLogs = Int32.TryParse(args?.FirstOrDefault(arg => arg.IsStartsWith("/number:"))?.Replace("/number:", ""), out var numberOfItems) && numberOfItems > 0 ? numberOfItems : this.NumberOfLogItems;
			if (this.IsDebug)
				this.Logger.LogInformation($"Get {numberOfLogs:###,###,##0} log files");

			var filePaths = UtilityService.GetFiles(this.LogsPath, "*.json", numberOfLogs, orderMode: args?.FirstOrDefault(arg => arg.IsStartsWith("/order-mode:"))?.Replace("/order-mode:", "") ?? "Ascending");
			if (this.IsDebug)
				this.Logger.LogInformation($"Done fetch {filePaths.Count:###,###,##0} log files - Times for fetching: {stopwatch.GetElapsedTimes()}");

			var logs = new List<ServiceLog>();
			stopwatch.Restart();
			await filePaths.ForEachAsync(async filePath =>
			{
				try
				{
					await UtilityService.ReadAsJsonAsync(filePath, this.CancellationToken, null, json => logs.Add(json.As<ServiceLog>(false))).ConfigureAwait(false);
				}
				catch (OperationCanceledException) { }
				catch (UnauthorizedAccessException) { }
				catch (FileNotFoundException) { }
				catch (Exception ex)
				{
					if (!ex.Message.IsContains("cannot access the file"))
						this.Logger.LogError($"Error occurred while reading JSON file => {ex.Message}", ex);
				}
				finally
				{
					try
					{
						File.Delete(filePath);
					}
					catch { }
				}
			}).ConfigureAwait(false);
			if (this.IsDebug)
				this.Logger.LogInformation($"Done prepare {filePaths.Count:###,###,##0} log files - Times for preparing: {stopwatch.GetElapsedTimes()}");

			return logs;
		}

		async Task PrepareServiceLogsAsync()
		{
			try
			{
				var logs = await this.PrepareServiceLogsAsync(null).ConfigureAwait(false);
				if (QueueMode == BoundedChannelFullMode.Wait)
					await this.ServiceLogs.Writer.WriteAsync(logs, this.CancellationToken).ConfigureAwait(false);
				else
					this.ServiceLogs.Writer.TryWrite(logs);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while preparing logs (queue) => {ex.Message}", ex);
			}
		}

		async Task FlushServiceLogsAsync(string[] args)
		{
			var logs = await this.PrepareServiceLogsAsync(args).ConfigureAwait(false);
			var stopwatch = Stopwatch.StartNew();
			await this.FlushServiceLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);
			if (this.IsDebug)
				this.Logger.LogInformation($"Done flush {logs.Count():###,###,##0} log items into database - Times for flushing: {stopwatch.GetElapsedTimes()}");
		}

		async Task FlushServiceLogsAsync()
		{
			try
			{
				while (await this.ServiceLogs.Reader.WaitToReadAsync(this.CancellationToken).ConfigureAwait(false))
					while (this.ServiceLogs.Reader.TryRead(out var logs))
					{
						await this.FlushServiceLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);
						if (this.IsDebug)
							this.Logger?.LogInformation($"Dequeue and flush {logs.Count():###,###,##0} log items into database successful");
					}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while flushing logs (queue) => {ex.Message}", ex);
			}
		}

		async Task FlushServiceLogsAsync(IEnumerable<ServiceLog> logs, CancellationToken cancellationToken)
		{
			async Task flushLogsAsync(IEnumerable<ServiceLog> input)
			{
				try
				{
					var stopwatch = Stopwatch.StartNew();
					var data = input.Where(log => log != null && !string.IsNullOrWhiteSpace(log.CorrelationID)).ToList();
					if (this.IsDebug)
						this.Logger.LogInformation($"Start flush {data.Count:###,###,##0} log items into database");
					await ServiceLog.CreateManyAsync(data, cancellationToken).ConfigureAwait(false);
					if (this.IsDebug)
						this.Logger.LogInformation($"Complete flush {data.Count:###,###,##0} log items into database in {stopwatch.GetElapsedTimes()}");
				}
				catch (OperationCanceledException) { }
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while flushing log into database => {ex.Message}", ex);
				}
			}

			var tasks = new List<Task>();
			var pageNumber = 0;
			var pageSize = this.NumberOfLogItems > 10000 ? this.NumberOfLogItems / 5 : 2000;
			var totalPages = Extensions.GetTotalPages(logs.Count(), pageSize);
			while (pageNumber < totalPages)
			{
				tasks.Add(flushLogsAsync(logs.Skip(pageNumber * pageSize).Take(pageSize)));
				pageNumber++;
			}
			await Task.WhenAll(tasks).ConfigureAwait(false);

			if (this.WriteLogsIntoSeparatedFiles)
				await logs.Where(log => log != null).ForEachAsync(async log =>
				{
					try
					{
						var content = $"{log.Time:HH:mm:ss.ffffff}{(string.IsNullOrWhiteSpace(log.DeveloperID) ? "" : $" [Dev: {log.DeveloperID}]")}{(string.IsNullOrWhiteSpace(log.AppID) ? "" : $" [App: {log.AppID}]")} {log.Logs} [{log.CorrelationID}]{(string.IsNullOrWhiteSpace(log.Stack) ? "" : $"\r\n{log.Stack}")}\r\n";
						var filename = $"{log.ServiceName}{(string.IsNullOrWhiteSpace(log.ObjectName) || log.ServiceName.IsEquals(log.ObjectName) ? "" : $".{log.ObjectName}")}-{log.Time:yyyyMMddHH}.txt";
						await content.SaveAsTextAsync(Path.Combine(this.LogsPath, filename), cancellationToken, true).ConfigureAwait(false);
					}
					catch (OperationCanceledException) { }
					catch (Exception ex)
					{
						this.Logger.LogError($"Error occurred while writting log into separated file => {ex.Message}", ex);
					}
				}, true, false).ConfigureAwait(false);
		}

		async Task<JToken> FetchServiceLogsAsync(int pageNumber, int pageSize, string correlationID, string serviceName, string objectName, string startTime, string endTime, CancellationToken cancellationToken)
		{
			var filter = Filters<ServiceLog>.And();
			if (!string.IsNullOrWhiteSpace(correlationID))
				filter.Add(Filters<ServiceLog>.Equals("CorrelationID", correlationID.Trim().ToLower()));
			if (!string.IsNullOrWhiteSpace(serviceName))
				filter.Add(Filters<ServiceLog>.Equals("ServiceName", serviceName.Trim().ToLower()));
			if (!string.IsNullOrWhiteSpace(objectName))
				filter.Add(Filters<ServiceLog>.Equals("ObjectName", objectName.Trim().ToLower()));
			if (DateTime.TryParse(startTime, out var start))
				filter.Add(Filters<ServiceLog>.GreaterOrEquals("Time", start));
			if (DateTime.TryParse(endTime, out var end))
				filter.Add(Filters<ServiceLog>.LessThanOrEquals("Time", end));

			var totalRecords = await ServiceLog.CountAsync(filter, null, false, null, 0, cancellationToken).ConfigureAwait(false);
			var totalPages = (totalRecords, pageSize).GetTotalPages();

			var sort = Sorts<ServiceLog>.Descending("Time");
			var objects = await ServiceLog.FindAsync(filter, sort, pageSize, pageNumber, null, false, null, 0, cancellationToken).ConfigureAwait(false);

			return new JObject
			{
				{ "FilterBy", filter.ToClientJson() },
				{ "SortBy", sort.ToClientJson() },
				{ "Pagination", (totalRecords, totalPages, pageSize, totalPages > 0 && pageNumber > totalPages ? totalPages : pageNumber).GetPagination() },
				{ "Objects", objects.Select(obj => obj.ToJson()).ToJArray() }
			};
		}

		Task CleanServiceLogsAsync()
		{
			if (this.IsDebug)
				this.Logger.LogDebug($"Clean old service logs");
			var filter = Filters<ServiceLog>.LessThan("Time", DateTime.Now.AddDays(0 - (Int32.TryParse(UtilityService.GetAppSetting("ServiceLogs:Days", "3"), out var days) && days > 0 ? days : 2)));
			return ServiceLog.DeleteManyAsync(filter, null, this.CancellationToken);
		}
		#endregion

		#region Working with trace logs
		async Task<IEnumerable<TraceLog>> PrepareTraceLogsAsync(string[] args)
		{
			var stopwatch = Stopwatch.StartNew();
			var numberOfLogs = Int32.TryParse(args?.FirstOrDefault(arg => arg.IsStartsWith("/number:"))?.Replace("/number:", ""), out var numberOfItems) && numberOfItems > 0 ? numberOfItems : this.NumberOfLogItems;

			if (this.IsDebug)
				this.Logger.LogInformation($"Get {numberOfLogs:###,###,##0} trace log files");

			var filePaths = UtilityService.GetFiles(this.LogsPath, "zlogs.traces.*.json", numberOfLogs, orderMode: args?.FirstOrDefault(arg => arg.IsStartsWith("/order-mode:"))?.Replace("/order-mode:", "") ?? "Ascending");

			if (this.IsDebug)
				this.Logger.LogInformation($"Done fetch {filePaths.Count:###,###,##0} trace log files - Times for fetching: {stopwatch.GetElapsedTimes()}");

			var logs = new List<TraceLog>();
			stopwatch.Restart();

			foreach (var filePath in filePaths)
			{
				var prepared = false;
				try
				{
					await UtilityService.ReadAsJsonAsync(filePath, this.CancellationToken, null, json =>
					{
						var log = json.As<TraceLog>(false);
						if (log != null	&& !string.IsNullOrWhiteSpace(log.ID)	&& !string.IsNullOrWhiteSpace(log.CorrelationID) && !string.IsNullOrWhiteSpace(log.Operation)	&& log.EndTime != null)
						{
							logs.Add(log);
							prepared = true;
						}
						else
							this.Logger.LogWarning($"Invalid trace log file => {filePath}");
					}).ConfigureAwait(false);
				}
				catch (OperationCanceledException) { }
				catch (UnauthorizedAccessException) { }
				catch (FileNotFoundException) { }
				catch (Exception ex)
				{
					if (!ex.Message.IsContains("cannot access the file"))
						this.Logger.LogError($"Error occurred while reading trace JSON file => {ex.Message}", ex);
				}
				finally
				{
					if (prepared)
						try
						{
							File.Delete(filePath);
						}
						catch { }
				}
			}

			if (this.IsDebug)
				this.Logger.LogInformation($"Done prepare {logs.Count:###,###,##0} trace logs - Times for preparing: {stopwatch.GetElapsedTimes()}");

			return logs;
		}

		async Task PrepareTraceLogsAsync()
		{
			try
			{
				var logs = await this.PrepareTraceLogsAsync(null).ConfigureAwait(false);
				if (!logs.Any())
					return;

				if (QueueMode == BoundedChannelFullMode.Wait)
					await this.TraceLogs.Writer.WriteAsync(logs, this.CancellationToken).ConfigureAwait(false);
				else
					this.TraceLogs.Writer.TryWrite(logs);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while preparing trace logs (queue) => {ex.Message}", ex);
			}
		}

		async Task FlushTraceLogsAsync(string[] args)
		{
			var logs = await this.PrepareTraceLogsAsync(args).ConfigureAwait(false);
			var stopwatch = Stopwatch.StartNew();

			await this.FlushTraceLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);

			if (this.IsDebug)
				this.Logger.LogInformation($"Done flush {logs.Count():###,###,##0} trace logs into database - Times for flushing: {stopwatch.GetElapsedTimes()}");
		}

		async Task FlushTraceLogsAsync()
		{
			try
			{
				while (await this.TraceLogs.Reader.WaitToReadAsync(this.CancellationToken).ConfigureAwait(false))
					while (this.TraceLogs.Reader.TryRead(out var logs))
					{
						await this.FlushTraceLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);
						if (this.IsDebug)
							this.Logger?.LogInformation($"Dequeue and flush {logs.Count():###,###,##0} trace logs into database completed");
					}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while flushing trace logs (queue) => {ex.Message}", ex);
			}
		}

		async Task FlushTraceLogsAsync(IEnumerable<TraceLog> logs, CancellationToken cancellationToken)
		{
			async Task flushLogsAsync(IEnumerable<TraceLog> input)
			{
				try
				{
					var stopwatch = Stopwatch.StartNew();
					var data = input.Where(log => log != null && !string.IsNullOrWhiteSpace(log.ID) && !string.IsNullOrWhiteSpace(log.CorrelationID) && !string.IsNullOrWhiteSpace(log.Operation)	&& log.EndTime != null).ToList();
					if (data.Count < 1)
						return;

					if (this.IsDebug)
						this.Logger.LogInformation($"Start flush {data.Count:###,###,##0} trace logs into database");

					await TraceLog.CreateManyAsync(data, cancellationToken).ConfigureAwait(false);

					if (this.IsDebug)
						this.Logger.LogInformation($"Complete flush {data.Count:###,###,##0} trace logs into database in {stopwatch.GetElapsedTimes()}");
				}
				catch (OperationCanceledException) { }
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while flushing trace logs into database => {ex.Message}", ex);
				}
			}

			var items = logs.ToList();
			var tasks = new List<Task>();
			var pageNumber = 0;
			var pageSize = this.NumberOfLogItems > 10000 ? this.NumberOfLogItems / 5 : 2000;
			var totalPages = Extensions.GetTotalPages(items.Count, pageSize);

			while (pageNumber < totalPages)
			{
				tasks.Add(flushLogsAsync(items.Skip(pageNumber * pageSize).Take(pageSize)));
				pageNumber++;
			}

			await Task.WhenAll(tasks).ConfigureAwait(false);
		}

		async Task<JToken> FetchTraceLogsAsync(int pageNumber, int pageSize, string correlationID, string serviceName, string operation, string startTime, string endTime, CancellationToken cancellationToken)
		{
			var filter = Filters<TraceLog>.And();

			if (!string.IsNullOrWhiteSpace(correlationID))
				filter.Add(Filters<TraceLog>.Equals("CorrelationID", correlationID.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(serviceName))
				filter.Add(Filters<TraceLog>.Equals("ServiceName", serviceName.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(operation))
				filter.Add(Filters<TraceLog>.Equals("Operation", operation.Trim()));

			if (DateTime.TryParse(startTime, out var start))
				filter.Add(Filters<TraceLog>.GreaterOrEquals("StartTime", start));

			if (DateTime.TryParse(endTime, out var end))
				filter.Add(Filters<TraceLog>.LessThanOrEquals("StartTime", end));

			var totalRecords = await TraceLog.CountAsync(filter, null, false, null, 0, cancellationToken).ConfigureAwait(false);
			var totalPages = (totalRecords, pageSize).GetTotalPages();

			var sort = Sorts<TraceLog>.Descending("StartTime");
			var objects = await TraceLog.FindAsync(filter, sort, pageSize, pageNumber, null, false, null, 0, cancellationToken).ConfigureAwait(false);

			return new JObject
			{
				{ "FilterBy", filter.ToClientJson() },
				{ "SortBy", sort.ToClientJson() },
				{ "Pagination", (totalRecords, totalPages, pageSize, totalPages > 0 && pageNumber > totalPages ? totalPages : pageNumber).GetPagination() },
				{ "Objects", objects.Select(obj => obj.ToJson()).ToJArray() }
			};
		}

		Task CleanTraceLogsAsync()
		{
			if (this.IsDebug)
				this.Logger.LogDebug("Clean old trace logs");

			var days = Int32.TryParse(UtilityService.GetAppSetting("TraceLogs:Days", "15"), out var value) && value > 0 ? value : 3;
			var filter = Filters<TraceLog>.LessThan("StartTime", DateTime.Now.AddDays(0 - days));

			return TraceLog.DeleteManyAsync(filter, null, this.CancellationToken);
		}
		#endregion

		#region Working with usage logs
		async Task<IEnumerable<UsageLog>> PrepareUsageLogsAsync(string[] args)
		{
			var stopwatch = Stopwatch.StartNew();
			var numberOfLogs = Int32.TryParse(args?.FirstOrDefault(arg => arg.IsStartsWith("/number:"))?.Replace("/number:", ""), out var numberOfItems) && numberOfItems > 0 ? numberOfItems : this.NumberOfLogItems;

			if (this.IsDebug)
				this.Logger.LogInformation($"Get {numberOfLogs:###,###,##0} usage log files");

			var filePaths = UtilityService.GetFiles(this.LogsPath, "zlogs.usages.*.json", numberOfLogs, orderMode: args?.FirstOrDefault(arg => arg.IsStartsWith("/order-mode:"))?.Replace("/order-mode:", "") ?? "Ascending");

			if (this.IsDebug)
				this.Logger.LogInformation($"Done fetch {filePaths.Count:###,###,##0} usage log files - Times for fetching: {stopwatch.GetElapsedTimes()}");

			var logs = new List<UsageLog>();
			stopwatch.Restart();

			foreach (var filePath in filePaths)
			{
				var prepared = false;
				try
				{
					await UtilityService.ReadAsJsonAsync(filePath, this.CancellationToken, null, json =>
					{
						var log = json.As<UsageLog>(false);
						if (log != null && !string.IsNullOrWhiteSpace(log.ID) && !string.IsNullOrWhiteSpace(log.CorrelationID) && !string.IsNullOrWhiteSpace(log.ServiceName) && !string.IsNullOrWhiteSpace(log.Operation) && !string.IsNullOrWhiteSpace(log.Type) && log.StartTime != default && log.EndTime != null	&& log.EndTime.Value >= log.StartTime)
						{
							logs.Add(log);
							prepared = true;
						}
						else
							this.Logger.LogWarning($"Invalid usage log file => {filePath}");
					}).ConfigureAwait(false);
				}
				catch (OperationCanceledException) { }
				catch (UnauthorizedAccessException) { }
				catch (FileNotFoundException) { }
				catch (Exception ex)
				{
					if (!ex.Message.IsContains("cannot access the file"))
						this.Logger.LogError($"Error occurred while reading usage JSON file => {ex.Message}", ex);
				}
				finally
				{
					if (prepared)
						try
						{
							File.Delete(filePath);
						}
						catch { }
				}
			}

			if (this.IsDebug)
				this.Logger.LogInformation($"Done prepare {logs.Count:###,###,##0} usage logs - Times for preparing: {stopwatch.GetElapsedTimes()}");

			return logs;
		}

		async Task PrepareUsageLogsAsync()
		{
			try
			{
				var logs = await this.PrepareUsageLogsAsync(null).ConfigureAwait(false);
				if (!logs.Any())
					return;

				if (QueueMode == BoundedChannelFullMode.Wait)
					await this.UsageLogs.Writer.WriteAsync(logs, this.CancellationToken).ConfigureAwait(false);
				else
					this.UsageLogs.Writer.TryWrite(logs);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while preparing usage logs (queue) => {ex.Message}", ex);
			}
		}

		async Task FlushUsageLogsAsync(string[] args)
		{
			var logs = await this.PrepareUsageLogsAsync(args).ConfigureAwait(false);
			var stopwatch = Stopwatch.StartNew();

			await this.FlushUsageLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);

			if (this.IsDebug)
				this.Logger.LogInformation($"Done flush {logs.Count():###,###,##0} usage logs into database - Times for flushing: {stopwatch.GetElapsedTimes()}");
		}

		async Task FlushUsageLogsAsync()
		{
			try
			{
				while (await this.UsageLogs.Reader.WaitToReadAsync(this.CancellationToken).ConfigureAwait(false))
					while (this.UsageLogs.Reader.TryRead(out var logs))
					{
						await this.FlushUsageLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);
						if (this.IsDebug)
							this.Logger?.LogInformation($"Dequeue and flush {logs.Count():###,###,##0} usage logs into database completed");
					}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while flushing usage logs (queue) => {ex.Message}", ex);
			}
		}

		async Task FlushUsageLogsAsync(IEnumerable<UsageLog> logs, CancellationToken cancellationToken)
		{
			async Task flushLogsAsync(IEnumerable<UsageLog> input)
			{
				try
				{
					var stopwatch = Stopwatch.StartNew();
					var data = input.Where(log => log != null && !string.IsNullOrWhiteSpace(log.ID) && !string.IsNullOrWhiteSpace(log.CorrelationID) && !string.IsNullOrWhiteSpace(log.ServiceName) && !string.IsNullOrWhiteSpace(log.Operation) && !string.IsNullOrWhiteSpace(log.Type) && log.StartTime != default && log.EndTime != null && log.EndTime.Value >= log.StartTime).ToList();
					if (data.Count < 1)
						return;

					if (this.IsDebug)
						this.Logger.LogInformation($"Start flush {data.Count:###,###,##0} usage logs into database");

					await UsageLog.CreateManyAsync(data, cancellationToken).ConfigureAwait(false);

					if (this.IsDebug)
						this.Logger.LogInformation($"Complete flush {data.Count:###,###,##0} usage logs into database in {stopwatch.GetElapsedTimes()}");
				}
				catch (OperationCanceledException) { }
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while flushing usage logs into database => {ex.Message}", ex);
				}
			}

			var items = logs.ToList();
			var tasks = new List<Task>();
			var pageNumber = 0;
			var pageSize = this.NumberOfLogItems > 10000 ? this.NumberOfLogItems / 5 : 2000;
			var totalPages = Extensions.GetTotalPages(items.Count, pageSize);

			while (pageNumber < totalPages)
			{
				tasks.Add(flushLogsAsync(items.Skip(pageNumber * pageSize).Take(pageSize)));
				pageNumber++;
			}

			await Task.WhenAll(tasks).ConfigureAwait(false);
		}

		async Task<JToken> FetchUsageLogsAsync(int pageNumber, int pageSize, string correlationID, string traceID, string serviceName, string operation, string type, string provider, string model, string systemID, string userID, string startTime, string endTime, CancellationToken cancellationToken)
		{
			var filter = Filters<UsageLog>.And();

			if (!string.IsNullOrWhiteSpace(correlationID))
				filter.Add(Filters<UsageLog>.Equals("CorrelationID", correlationID.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(traceID))
				filter.Add(Filters<UsageLog>.Equals("TraceID", traceID.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(serviceName))
				filter.Add(Filters<UsageLog>.Equals("ServiceName", serviceName.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(operation))
				filter.Add(Filters<UsageLog>.Equals("Operation", operation.Trim()));

			if (!string.IsNullOrWhiteSpace(type))
				filter.Add(Filters<UsageLog>.Equals("Type", type.Trim()));

			if (!string.IsNullOrWhiteSpace(provider))
				filter.Add(Filters<UsageLog>.Equals("Provider", provider.Trim()));

			if (!string.IsNullOrWhiteSpace(model))
				filter.Add(Filters<UsageLog>.Equals("Model", model.Trim()));

			if (!string.IsNullOrWhiteSpace(systemID))
				filter.Add(Filters<UsageLog>.Equals("SystemID", systemID.Trim()));

			if (!string.IsNullOrWhiteSpace(userID))
				filter.Add(Filters<UsageLog>.Equals("UserID", userID.Trim().ToLower()));

			if (DateTime.TryParse(startTime, out var start))
				filter.Add(Filters<UsageLog>.GreaterOrEquals("StartTime", start));

			if (DateTime.TryParse(endTime, out var end))
				filter.Add(Filters<UsageLog>.LessThanOrEquals("StartTime", end));

			var totalRecords = await UsageLog.CountAsync(filter, null, false, null, 0, cancellationToken).ConfigureAwait(false);
			var totalPages = (totalRecords, pageSize).GetTotalPages();

			var sort = Sorts<UsageLog>.Descending("StartTime");
			var objects = await UsageLog.FindAsync(filter, sort, pageSize, pageNumber, null, false, null, 0, cancellationToken).ConfigureAwait(false);

			return new JObject
			{
				{ "FilterBy", filter.ToClientJson() },
				{ "SortBy", sort.ToClientJson() },
				{ "Pagination", (totalRecords, totalPages, pageSize, totalPages > 0 && pageNumber > totalPages ? totalPages : pageNumber).GetPagination() },
				{ "Objects", objects.Select(obj => obj.ToJson()).ToJArray() }
			};
		}

		Task CleanUsageLogsAsync()
		{
			if (this.IsDebug)
				this.Logger.LogDebug("Clean old usage logs");

			var days = Int32.TryParse(UtilityService.GetAppSetting("UsageLogs:Days", "30"), out var value) && value > 0 ? value : 30;
			var filter = Filters<UsageLog>.LessThan("StartTime", DateTime.Now.AddDays(0 - days));

			return UsageLog.DeleteManyAsync(filter, null, this.CancellationToken);
		}
		#endregion

		#region Working with audit logs
		bool IsValidAuditLog(AuditLog log)
			=> log != null && !string.IsNullOrWhiteSpace(log.ID) && log.Time != default && !string.IsNullOrWhiteSpace(log.ServiceName) && !string.IsNullOrWhiteSpace(log.ObjectName) && !string.IsNullOrWhiteSpace(log.Operation) && !string.IsNullOrWhiteSpace(log.ActorType) && !string.IsNullOrWhiteSpace(log.ActorID) && (log.Status == "ok" || log.Status == "error" || log.Status == "denied" || log.Status == "canceled");

		async Task PrepareAuditLogsAsync()
		{
			try
			{
				var filePaths = UtilityService.GetFiles(this.LogsPath, "zlogs.audits.*.json", this.NumberOfLogItems, orderMode: "Ascending");
				foreach (var filePath in filePaths)
				{
					if (!this.PendingAuditFiles.TryAdd(filePath, 0))
						continue;

					var queued = false;
					try
					{
						AuditLog log = null;
						await UtilityService.ReadAsJsonAsync(filePath, this.CancellationToken, null, json => log = json.As<AuditLog>(false)).ConfigureAwait(false);

						if (!this.IsValidAuditLog(log))
						{
							this.Logger.LogWarning($"Invalid audit log file => {filePath}");
							continue;
						}

						await this.AuditLogs.Writer.WriteAsync((log, filePath), this.CancellationToken).ConfigureAwait(false);
						queued = true;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (FileNotFoundException) { }
					catch (Exception ex)
					{
						this.Logger.LogError($"Error preparing audit log [{filePath}] => {ex.Message}", ex);
					}
					finally
					{
						if (!queued)
							this.PendingAuditFiles.TryRemove(filePath, out _);
					}
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error preparing audit logs => {ex.Message}", ex);
			}
		}

		async Task FlushAuditLogsAsync()
		{
			try
			{
				while (await this.AuditLogs.Reader.WaitToReadAsync(this.CancellationToken).ConfigureAwait(false))
				{
					while (this.AuditLogs.Reader.TryRead(out var item))
					{
						try
						{
							if (!this.IsValidAuditLog(item.Log))
							{
								this.Logger.LogWarning($"Invalid queued audit log [{item.FilePath}]");
								continue;
							}

							await AuditLog.CreateAsync(item.Log, this.CancellationToken).ConfigureAwait(false);
							try
							{
								File.Delete(item.FilePath);
							}
							catch (Exception ex)
							{
								this.Logger.LogWarning($"Audit persisted, but source file could not be deleted [{item.FilePath}] => {ex.Message}");
							}
						}
						catch (OperationCanceledException)
						{
							throw;
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error persisting audit log [{item.Log?.ID}] => {ex.Message}", ex);
						}
						finally
						{
							this.PendingAuditFiles.TryRemove(item.FilePath, out _);
						}
					}
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error flushing audit logs => {ex.Message}", ex);
			}
		}

		async Task<JToken> FetchAuditLogsAsync(int pageNumber, int pageSize, string correlationID, string traceID, string actorType, string actorID, string serviceName, string objectName, string objectID, string systemID, string operation, string status, string startTime, string endTime, CancellationToken cancellationToken)
		{
			var filter = Filters<AuditLog>.And();

			if (!string.IsNullOrWhiteSpace(correlationID))
				filter.Add(Filters<AuditLog>.Equals("CorrelationID", correlationID.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(traceID))
				filter.Add(Filters<AuditLog>.Equals("TraceID", traceID.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(actorType))
				filter.Add(Filters<AuditLog>.Equals("ActorType", actorType.Trim()));

			if (!string.IsNullOrWhiteSpace(actorID))
				filter.Add(Filters<AuditLog>.Equals("ActorID", actorID.Trim()));

			if (!string.IsNullOrWhiteSpace(serviceName))
				filter.Add(Filters<AuditLog>.Equals("ServiceName", serviceName.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(objectName))
				filter.Add(Filters<AuditLog>.Equals("ObjectName", objectName.Trim().ToLower()));

			if (!string.IsNullOrWhiteSpace(objectID))
				filter.Add(Filters<AuditLog>.Equals("ObjectID", objectID.Trim()));

			if (!string.IsNullOrWhiteSpace(systemID))
				filter.Add(Filters<AuditLog>.Equals("SystemID", systemID.Trim()));

			if (!string.IsNullOrWhiteSpace(operation))
				filter.Add(Filters<AuditLog>.Equals("Operation", operation.Trim()));

			if (!string.IsNullOrWhiteSpace(status))
				filter.Add(Filters<AuditLog>.Equals("Status", status.Trim()));

			if (DateTime.TryParse(startTime, out var start))
				filter.Add(Filters<AuditLog>.GreaterOrEquals("Time", start));

			if (DateTime.TryParse(endTime, out var end))
				filter.Add(Filters<AuditLog>.LessThanOrEquals("Time", end));

			var totalRecords = await AuditLog.CountAsync(filter, null, false, null, 0, cancellationToken).ConfigureAwait(false);
			var totalPages = (totalRecords, pageSize).GetTotalPages();
			var sort = Sorts<AuditLog>.Descending("Time");

			var objects = await AuditLog.FindAsync(filter, sort, pageSize, pageNumber, null, false, null, 0, cancellationToken).ConfigureAwait(false);
			return new JObject
			{
				{ "FilterBy", filter.ToClientJson() },
				{ "SortBy", sort.ToClientJson() },
				{ "Pagination", (totalRecords, totalPages, pageSize, totalPages > 0 && pageNumber > totalPages ? totalPages : pageNumber).GetPagination() },
				{ "Objects", objects.Select(obj => obj.ToJson()).ToJArray() }
			};
		}
		#endregion

		#region Working with metrics (system statistics)
		async ValueTask UpdateServiceStatisticsAsync(CommunicateMessage message)
		{
			if (this.ServiceStatistics?.Writer != null)
				await this.ServiceStatistics.Writer.WriteAsync(new StatisticMessage(message.Data, DateTime.Now), this.CancellationToken).ConfigureAwait(false);
		}

		async Task ProcessServiceStatisticsAsync()
		{
			var time = DateTime.Now.AddMinutes(-1);
			time = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
			this.PrepareRouterStatistics(time);

			var (forAggregate, forReUpdate) = this.ServiceStatistics.GetMessages(time);
			var statistics = forAggregate.Any() ? forAggregate.Aggregate(time, this.IsServiceStatisticsSampleEnabled, json =>
			{
				var router = json.Get<JObject>("Router");
				if (router != null)
				{
					router["CPU"] = new JObject
					{
						["Min"] = Math.Round(this.RouterStats.CpuUsage.Min, 2),
						["Max"] = Math.Round(this.RouterStats.CpuUsage.Max, 2),
						["Average"] = Math.Round(this.RouterStats.CpuUsage.Average, 2)
					};
					router["Memory"] = new JObject
					{
						["Min"] = Math.Round(this.RouterStats.MemoryUsage.Min, 2),
						["Max"] = Math.Round(this.RouterStats.MemoryUsage.Max, 2),
						["Average"] = Math.Round(this.RouterStats.MemoryUsage.Average, 2)
					};
				}
				json["Sessions"] = new JObject
				{
					["Total"] = this.Sessions.Total,
					["User"] = this.Sessions.User,
					["Visitor"] = this.Sessions.Visitor,
					["Crawler"] = this.Sessions.Crawler
				};
			}) : null;

			if (statistics != null)
			{
				new UpdateMessage
				{
					Type = "System#Statistics",
					DeviceID = "*",
					Data = statistics
				}.Send();

				new CommunicateMessage("APIGateway")
				{
					Type = "System#Statistics",
					Data = statistics
				}.Send();

				SystemMetric.CreateAsync(new SystemMetric(time, statistics.Remove(["Time"]).AsString()), this.CancellationToken).Execute(ex => this.Logger.LogError($"Error occurred while update metrics => {ex.Message}", ex));
			}

			await forReUpdate.ForEachAsync(async message => await this.ServiceStatistics.Writer.WriteAsync(message, this.CancellationToken).ConfigureAwait(false), true, false).ConfigureAwait(false);
		}

		void PrepareRouterStatistics(DateTime time)
		{
			var forReUpdate = new List<(DateTime Time, double CpuUsage, double MemoryUsage)>();
			var forAggregate = new List<(DateTime Time, double CpuUsage, double MemoryUsage)>();
			while (this.RouterStatistics.Reader.TryRead(out var info))
			{
				if (info.Time.Hour == time.Hour && info.Time.Minute == time.Minute)
					forAggregate.Add(info);
				else if (info.Time > time)
					forReUpdate.Add(info);
			}

			double cpuMin = 0, cpuMax = 0, cpuAverage = 0;
			double memoryMin = 0, memoryMax = 0, memoryAverage = 0;

			if (forAggregate.Count > 0)
			{
				cpuMin = forAggregate.Min(info => info.CpuUsage);
				cpuMax = forAggregate.Max(info => info.CpuUsage);
				cpuAverage = forAggregate.Average(info => info.CpuUsage);

				memoryMin = forAggregate.Min(info => info.MemoryUsage);
				memoryMax = forAggregate.Max(info => info.MemoryUsage);
				memoryAverage = forAggregate.Average(info => info.MemoryUsage);
			}

			this.RouterStats = ((Min: cpuMin, Max: cpuMax, Average: cpuAverage), (Min: memoryMin, Max: memoryMax, Average: memoryAverage));
			forReUpdate.ForEach(info => this.RouterStatistics.Writer.TryWrite(info));
		}

		Task GetRouterStatisticsAsync()
			=> Router.SendMessageToRouterAsync(new JObject
			{
				["Command"] = "EnvironmentInfo"
			}.AsString());

		void UpdateRouterStatistics(string message)
		{
			var json = message.ToJson();
			var timeStr = json.Value<string>("Time");
			var cpuUsage = json.Value<object>("CpuUsage");
			var memoryUsage = json.Value<object>("MemoryUsage");
			if (timeStr != null && DateTime.TryParse(timeStr, out var time) && cpuUsage != null && memoryUsage != null)
				this.RouterStatistics.Writer.TryWrite((Time: time, CpuUsage: cpuUsage.As<double>(), MemoryUsage: memoryUsage.As<double>()));
		}
		#endregion

	}

	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}