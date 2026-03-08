#region Related components
using System;
using System.IO;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
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
		#endregion

		public override void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			this.Syncable = false;
			base.Start(args, initializeRepository, next);
		}

		public override void DoWork(string[] args = null)
		{
			var isDebugLogEnabled = this.IsDebugLogEnabled || args?.FirstOrDefault(arg => arg.IsStartsWith("/logs")) != null;

			var stopwatch = Stopwatch.StartNew();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/flush")) != null)
			{
				if (isDebugLogEnabled)
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
				if (isDebugLogEnabled)
					this.Logger.LogInformation($"Complete flush logs from files into database - Execution times: {stopwatch.GetElapsedTimes()}");
			}

			stopwatch.Restart();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/clean")) != null)
			{
				if (isDebugLogEnabled)
					this.Logger.LogInformation("Start clean old logs from database");

				this.CleanLogsAsync().Execute(true, ex => this.Logger.LogError($"Error occurred while cleaning logs => {ex.Message}", ex));
				stopwatch.Stop();
				if (isDebugLogEnabled)
					this.Logger.LogInformation($"Complete clean old logs from database - Execution times: {stopwatch.GetElapsedTimes()}");
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
						await this.FlushLogsAsync().ConfigureAwait(false);
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

		async Task FlushLogsAsync(string[] args = null)
		{
			var isDebugLogEnabled = this.IsDebugLogEnabled || args?.FirstOrDefault(arg => arg.IsStartsWith("/logs")) != null;

			var numberOfLogs = Int32.TryParse(args?.FirstOrDefault(arg => arg.IsStartsWith("/number:"))?.Replace("/number:", ""), out var numberOfItems) && numberOfItems > 0 ? numberOfItems : this.NumberOfLogItems;
			if (isDebugLogEnabled)
				this.Logger.LogInformation($"Get {numberOfLogs:###,###,##0} log files");

			var stopwatch = Stopwatch.StartNew();
			var filePaths = UtilityService.GetFiles(this.LogsPath, "*.json", numberOfLogs, orderMode: args?.FirstOrDefault(arg => arg.IsStartsWith("/order-mode:"))?.Replace("/order-mode:", "") ?? "Ascending");
			if (isDebugLogEnabled)
				this.Logger.LogInformation($"Done fetch {filePaths.Count:###,###,##0} log files - Times for fetching: {stopwatch.GetElapsedTimes()}");

			if (filePaths.Count > 0)
			{
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
				if (isDebugLogEnabled)
					this.Logger.LogInformation($"Done prepare logs from {filePaths.Count:###,###,##0} files - Times for preparing: {stopwatch.GetElapsedTimes()}");

				stopwatch.Restart();
				await this.FlushLogsAsync(logs, isDebugLogEnabled, this.CancellationToken).ConfigureAwait(false);
				if (isDebugLogEnabled)
					this.Logger.LogInformation($"Done flush {logs.Count:###,###,##0} logs into database - Times for flushing: {stopwatch.GetElapsedTimes()}");
			}
		}

		async Task FlushLogsAsync(IEnumerable<ServiceLog> logs, bool isDebugLogEnabled, CancellationToken cancellationToken)
		{
			async Task flushLogsAsync(IEnumerable<ServiceLog> input)
			{
				try
				{
					var stopwatch = Stopwatch.StartNew();
					var data = input.Where(log => log != null && !string.IsNullOrWhiteSpace(log.CorrelationID)).ToList();
					if (isDebugLogEnabled)
						this.Logger.LogInformation($"Start flush {data.Count:###,###,##0} logs into database");
					await ServiceLog.CreateManyAsync(data, cancellationToken).ConfigureAwait(false);
					if (isDebugLogEnabled)
						this.Logger.LogInformation($"Complete flush {data.Count:###,###,##0} logs into database in {stopwatch.GetElapsedTimes()}");
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while flushing log into database => {ex.Message}", ex);
				}
			}

			var tasks = new List<Task>();
			var pageNumber = 0;
			var pageSize = this.NumberOfLogItems > 1000 ? this.NumberOfLogItems / 5 : 500;
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
			if (this.IsDebugLogEnabled)
				this.Logger.LogDebug($"Clean old service logs");
			var filter = Filters<ServiceLog>.LessThan("Time", DateTime.Now.AddDays(0 - (Int32.TryParse(UtilityService.GetAppSetting("Logs:Days", "3"), out var days) && days > 0 ? days : 2)));
			return ServiceLog.DeleteManyAsync(filter, null, this.CancellationToken);
		}
	}

	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}