#region Related components
using System;
using System.IO;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Logs
{
	public class ServiceComponent : ServiceBase
	{
		public override string ServiceName => "Logs";

		#region Properties
		string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		bool CleaningServiceLogs { get; set; } = false;

		bool FlushingServiceLogs { get; set; } = false;

		bool WriteServiceLogsIntoSeparatedFiles { get; } = "true".IsEquals(UtilityService.GetAppSetting("Logs:WriteServiceLogsIntoSeparatedFiles"));

		int MaxTriedTimes { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:MaxTriedTimes"), out var numbers) && numbers > 0 ? numbers : 2;

		int NumberOfLogItems { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:Numbers"), out var numbers) && numbers > 0 ? numbers : 2000;

		bool UseInternalQueue { get; } = "true".IsEquals(UtilityService.GetAppSetting("Logs:Queue", "true"));

		int InternalQueueInterval { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:Queue:Interval"), out var interval) && interval > 0 ? interval : 7;

		static int InternalQueueSize { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:Queue:Size"), out var size) && size > 0 ? size : 256;

		static BoundedChannelFullMode InternalQueueFullMode { get; } = Enum.TryParse<BoundedChannelFullMode>(UtilityService.GetAppSetting("Logs:Queue:Mode"), out var mode) ? mode : BoundedChannelFullMode.DropOldest;

		Channel<IEnumerable<ServiceLog>> Logs { get; } = Channel.CreateBounded<IEnumerable<ServiceLog>>(new BoundedChannelOptions(InternalQueueSize)
		{
			SingleWriter = false,
			SingleReader = true,
			FullMode = InternalQueueFullMode
		});

		Task Flusher { get; set; }

		bool IsDebug { get; set; } = false;

		bool IsPreparer { get; } = "true".IsEquals(UtilityService.GetAppSetting("Logs:Preparer", "true"));
		#endregion

		public override Task StartAsync(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			this.Syncable = false;
			this.IsDebug = this.IsDebugLogEnabled || args?.FirstOrDefault(arg => arg.IsStartsWith("/logs")) != null;
			if (this.UseInternalQueue)
			{
				this.Flusher = Task.Run(this.FlushLogsAsync);
				this.StartTimer(this.PrepareLogsAsync, this.InternalQueueInterval);
				if (this.IsDebug)
					this.Logger?.LogInformation($"Use internal queue [{this.InternalQueueInterval} second(s)]");
			}
			if (this.IsPreparer)
				this.StartTimer(() => this.PrepareStoragesAsync(), 7 * 60);
			return base.StartAsync(args, initializeRepository, next);
		}

		public override async Task StopAsync(string[] args = null, Action<IService> next = null)
		{
			if (this.UseInternalQueue)
			{
				this.Logs.Writer.TryComplete();
				await this.Flusher.ConfigureAwait(false);
			}
			await base.StopAsync(args, next).ConfigureAwait(false);
		}

		public override void DoWork(string[] args = null)
		{
			this.IsDebug = this.IsDebugLogEnabled || args?.FirstOrDefault(arg => arg.IsStartsWith("/logs")) != null;
			var stopwatch = Stopwatch.StartNew();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/flush")) != null)
			{
				if (this.IsDebug)
					this.Logger.LogInformation("Start flush logs from files into database");

				if (this.MaxTriedTimes > 1)
				{
					var triedTimes = 0;
					this.FlushLogsAsync((args ?? []).Concat(["/order-mode:Descending"])).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing logs => {ex.Message}", ex));
					triedTimes++;
					while (triedTimes < this.MaxTriedTimes)
					{
						this.FlushLogsAsync(args).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing logs => {ex.Message}", ex));
						triedTimes++;
					}
				}
				else
					this.FlushLogsAsync(args).Execute(true, ex => this.Logger.LogError($"Error occurred while flushing logs => {ex.Message}", ex));

				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete flush logs from files into database - Execution times: {stopwatch.GetElapsedTimes()}");
			}

			stopwatch.Restart();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/clean")) != null)
			{
				if (this.IsDebug)
					this.Logger.LogInformation("Start clean old logs from database");
				this.CleanLogsAsync().Execute(true, ex => this.Logger.LogError($"Error occurred while cleaning logs => {ex.Message}", ex));
				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete clean old logs from database - Execution times: {stopwatch.GetElapsedTimes()}");
			}

			stopwatch.Restart();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/prepare")) != null || args?.FirstOrDefault(arg => arg.IsStartsWith("/storages")) != null)
			{
				if (this.IsDebug)
					this.Logger.LogInformation("Start re-prepare storages of service-logs");
				this.PrepareStoragesAsync(false, args?.FirstOrDefault(arg => arg.IsStartsWith("/dont-drop")) == null).Execute(true, ex => this.Logger.LogError($"Error occurred while preparing storages of service-logs => {ex.Message}", ex));
				stopwatch.Stop();
				if (this.IsDebug)
					this.Logger.LogInformation($"Complete re-prepare storages of service-logs - Execution times: {stopwatch.GetElapsedTimes()}");
			}
		}

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationToken))
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
								return await this.FetchLogsAsync(pageNumber > 0 ? pageNumber : 1, pageSize > 0 ? pageSize : 100, filterBy.Get<string>("CorrelationID"), filterBy.Get<string>("DeveloperID"), filterBy.Get<string>("AppID"), filterBy.Get<string>("ServiceName"), filterBy.Get<string>("ObjectName"), cts.Token).ConfigureAwait(false);
							}
							else if (requestInfo.Verb.IsEquals("POST"))
							{
								await this.WriteLogAsync(requestInfo.BodyAsJson.As<ServiceLog>(false, (log, _) =>
								{
									log.ID = string.IsNullOrWhiteSpace(log.ID) ? UtilityService.NewUUID : log.ID;
									log.ServiceName = log.ServiceName?.ToLower();
									log.ObjectName = log.ObjectName?.ToLower();
								}), cts.Token).ConfigureAwait(false);
								return new JObject();
							}
							else
								throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");

						default:
							throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");
					}
				}
				catch (Exception ex)
				{
					throw this.GetRuntimeException(requestInfo, ex);
				}
		}

		protected override async Task ProcessInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default)
		{
			if (message.Type.IsEquals("Clean") && !this.CleaningServiceLogs)
				try
				{
					this.CleaningServiceLogs = true;
					await this.CleanLogsAsync().ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while cleaning old logs => {ex.Message}", ex);
				}
				finally
				{
					this.CleaningServiceLogs = false;
				}

			else if (message.Type.IsEquals("Flush"))
				if (!this.FlushingServiceLogs)
					try
					{
						this.FlushingServiceLogs = true;
						await this.FlushLogsAsync(null).ConfigureAwait(false);
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

		Task WriteLogAsync(ServiceLog log, CancellationToken cancellationToken)
			=> this.WriteLogsAsync([log], cancellationToken);

		Task WriteLogsAsync(IEnumerable<ServiceLog> logs, CancellationToken cancellationToken)
			=> logs.ForEachAsync(log => log.ToString(Formatting.Indented).SaveAsTextAsync(Path.Combine(this.LogsPath, $"zlogs.services.{DateTime.Now:yyyyMMddHHmmssffffff}.{UtilityService.NewUUID}.json"), cancellationToken), true, false);

		public Task WriteLogAsync(string correlationID, string developerID, string appID, string serviceName, string objectName, string log, string stack = null, CancellationToken cancellationToken = default)
			=> this.WriteLogsAsync(correlationID, developerID, appID, serviceName, objectName, string.IsNullOrWhiteSpace(log) ? null : [log], stack, cancellationToken);

		public Task WriteLogsAsync(string correlationID, string developerID, string appID, string serviceName, string objectName, List<string> logs, string stack = null, CancellationToken cancellationToken = default)
			=> this.WriteLogAsync(new ServiceLog
			{
				CorrelationID = correlationID,
				DeveloperID = string.IsNullOrWhiteSpace(developerID) ? null : developerID,
				AppID = string.IsNullOrWhiteSpace(appID) ? null : appID,
				ServiceName = (string.IsNullOrWhiteSpace(serviceName) ? "APIGateway" : serviceName).ToLower(),
				ObjectName = (string.IsNullOrWhiteSpace(objectName) || objectName.IsEquals(serviceName) ? "" : objectName).ToLower(),
				Logs = "" + logs?.Where(log => !string.IsNullOrWhiteSpace(log)).Join("\r\n"),
				Stack = string.IsNullOrWhiteSpace(stack) ? null : stack
			}, cancellationToken);

		async Task<IEnumerable<ServiceLog>> PrepareLogsAsync(string[] args)
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
					await UtilityService.ReadAsJsonAsync(filePath, this.CancellationToken, null, json => logs.Add(json.As<ServiceLog>(false, (log, _) =>
					{
						log.ID = string.IsNullOrWhiteSpace(log.ID) ? UtilityService.NewUUID : log.ID;
						log.ServiceName = log.ServiceName?.ToLower();
						log.ObjectName = log.ObjectName?.ToLower();
					}))).ConfigureAwait(false);
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

		async Task PrepareLogsAsync()
		{
			try
			{
				var logs = await this.PrepareLogsAsync(null).ConfigureAwait(false);
				if (InternalQueueFullMode == BoundedChannelFullMode.Wait)
					await this.Logs.Writer.WriteAsync(logs, this.CancellationToken).ConfigureAwait(false);
				else
					this.Logs.Writer.TryWrite(logs);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				this.Logger.LogError($"Error occurred while preparing logs (queue) => {ex.Message}", ex);
			}
		}

		async Task FlushLogsAsync(string[] args)
		{
			var logs = await this.PrepareLogsAsync(args).ConfigureAwait(false);
			var stopwatch = Stopwatch.StartNew();
			await this.FlushLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);
			if (this.IsDebug)
				this.Logger.LogInformation($"Done flush {logs.Count():###,###,##0} log items into database - Times for flushing: {stopwatch.GetElapsedTimes()}");
		}

		async Task FlushLogsAsync()
		{
			try
			{
				while (await this.Logs.Reader.WaitToReadAsync(this.CancellationToken).ConfigureAwait(false))
					while (this.Logs.Reader.TryRead(out var logs))
					{
						await this.FlushLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);
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

		async Task FlushLogsAsync(IEnumerable<ServiceLog> logs, CancellationToken cancellationToken)
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

			if (this.WriteServiceLogsIntoSeparatedFiles)
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

		async Task<JToken> FetchLogsAsync(int pageNumber, int pageSize, string correlationID, string developerID, string appID, string serviceName, string objectName, CancellationToken cancellationToken)
		{
			var filter = Filters<ServiceLog>.And();
			if (!string.IsNullOrWhiteSpace(correlationID))
				filter.Add(Filters<ServiceLog>.Equals("CorrelationID", correlationID.Trim().ToLower()));
			if (!string.IsNullOrWhiteSpace(developerID))
				filter.Add(Filters<ServiceLog>.Equals("DeveloperID", developerID.Trim().ToLower()));
			if (!string.IsNullOrWhiteSpace(appID))
				filter.Add(Filters<ServiceLog>.Equals("AppID", appID.Trim().ToLower()));
			if (!string.IsNullOrWhiteSpace(serviceName))
				filter.Add(Filters<ServiceLog>.Equals("ServiceName", serviceName.Trim().ToLower()));
			if (!string.IsNullOrWhiteSpace(objectName))
				filter.Add(Filters<ServiceLog>.Equals("ObjectName", objectName.Trim().ToLower()));

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

		Task CleanLogsAsync()
		{
			if (this.IsDebug)
				this.Logger.LogDebug($"Clean old service logs");
			var filter = Filters<ServiceLog>.LessThan("Time", DateTime.Now.AddDays(0 - (Int32.TryParse(UtilityService.GetAppSetting("Logs:Days", "3"), out var days) && days > 0 ? days : 2)));
			return ServiceLog.DeleteManyAsync(filter, null, this.CancellationToken);
		}

		async Task PrepareStoragesAsync(bool checkTime = true, bool dropCollection = true)
		{
			var entityDefinition = !checkTime || (DateTime.Now.DayOfWeek == DayOfWeek.Saturday && DateTime.Now.Hour == 23 && DateTime.Now.Minute > 45 && DateTime.Now.Minute < 57)
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
							this.Logger.LogInformation("Collection of service-logs was dropped successful");
					}
					await entityDefinition.EnsureIndexesAsync(dataSource, (msg, ex) =>
					{
						if (ex != null)
							this.Logger.LogError(msg, ex);
						else if (this.IsDebug)
							this.Logger.LogInformation(msg);
					}).ConfigureAwait(false);
					if (this.IsDebug)
						this.Logger.LogInformation("Collection of service-logs was re-prepared successful");
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while preparing storages of service-logs => {ex.Message}", ex);
				}
		}
	}

	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}