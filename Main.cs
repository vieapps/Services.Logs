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
		string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		bool CleaningServiceLogs { get; set; } = false;

		bool FlushingServiceLogs { get; set; } = false;

		bool WriteServiceLogsIntoSeparatedFiles { get; } = "true".IsEquals(UtilityService.GetAppSetting("Logs:WriteServiceLogsIntoSeparatedFiles"));

		public override string ServiceName => "Logs";

		public override void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			this.Syncable = false;
			base.Start(args, initializeRepository, next);
		}

		public override void DoWork(string[] args = null)
		{
			var isDebugLogEnabled = this.IsDebugLogEnabled || args?.FirstOrDefault(arg => arg.IsStartsWith("/debug-logs")) != null || args?.FirstOrDefault(arg => arg.IsStartsWith("/logs")) != null;

			var stopwatch = Stopwatch.StartNew();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/flush")) != null)
			{
				if (isDebugLogEnabled)
					this.Logger.LogDebug("Start flush logs from files into database");

				this.FlushLogsAsync(args).Run(true, ex => this.Logger.LogError($"Error occurred while flushing logs => {ex.Message}", ex));
				stopwatch.Stop();
				if (isDebugLogEnabled)
					this.Logger.LogDebug($"Complete flush logs from files into database - Execution times: {stopwatch.GetElapsedTimes()}");
			}

			stopwatch = Stopwatch.StartNew();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/clean")) != null)
			{
				if (isDebugLogEnabled)
					this.Logger.LogDebug("Start clean old logs from database");

				this.CleanLogsAsync().Run(true, ex => this.Logger.LogError($"Error occurred while cleaning logs => {ex.Message}", ex));
				stopwatch.Stop();
				if (isDebugLogEnabled)
					this.Logger.LogDebug($"Complete clean old logs from database - Execution times: {stopwatch.GetElapsedTimes()}");
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
								await this.WriteLogAsync(requestInfo.Body.ToJson().As<ServiceLog>(false, (log, _) =>
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
			=> logs.ForEachAsync(async log =>
			{
				var filePath = Path.Combine(this.LogsPath, $"logs.services.{DateTime.Now:yyyyMMddHHmmss}.{UtilityService.NewUUID}.json");
				await log.ToString(Formatting.Indented).ToBytes().SaveAsTextAsync(filePath, cancellationToken).ConfigureAwait(false);
			}, true, false);

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
			var isDebugLogEnabled = this.IsDebugLogEnabled || args?.FirstOrDefault(arg => arg.IsStartsWith("/debug-logs")) != null || args?.FirstOrDefault(arg => arg.IsStartsWith("/logs")) != null;

			var numberOfLogs = Int32.TryParse(args?.FirstOrDefault(arg => arg.IsStartsWith("/number:"))?.Replace("/number:", ""), out var numberOfItems) && numberOfItems > 0 ? numberOfItems : Int32.TryParse(UtilityService.GetAppSetting("Logs:Numbers"), out numberOfItems) && numberOfItems > 0 ? numberOfItems : 3000;
			if (isDebugLogEnabled)
				this.Logger.LogDebug($"Get {numberOfLogs:###,###,##0} log files");

			var stopwatch = Stopwatch.StartNew();
			var files = Directory.EnumerateFiles(this.LogsPath, "logs.services.*.json").Take(numberOfLogs).Select(path => new FileInfo(path)).OrderBy(fileInfo => fileInfo.Name).ToList();
			if (isDebugLogEnabled)
				this.Logger.LogDebug($"Done fetch {files.Count:###,###,##0} log files - Times for fetching: {stopwatch.GetElapsedTimes()}");

			if (files.Count > 0)
			{
				var logs = new List<ServiceLog>();
				stopwatch.Restart();
				await files.ForEachAsync(async file =>
				{
					try
					{
						var json = await file.ReadAsJsonAsync(this.CancellationToken).ConfigureAwait(false);
						logs.Add(json.As<ServiceLog>(false, (log, _) =>
						{
							log.ID = string.IsNullOrWhiteSpace(log.ID) ? UtilityService.NewUUID : log.ID;
							log.ServiceName = log.ServiceName?.ToLower();
							log.ObjectName = log.ObjectName?.ToLower();
						}));
						File.Delete(file.FullName);
					}
					catch (FileNotFoundException) { }
					catch (Exception ex)
					{
						this.Logger.LogError($"Error occurred while reading JSON file => {ex.Message}", ex);
					}
				}, true, false).ConfigureAwait(false);
				if (isDebugLogEnabled)
					this.Logger.LogDebug($"Done prepare logs from {files.Count:###,###,##0} files - Times for preparing: {stopwatch.GetElapsedTimes()}");

				stopwatch.Restart();
				await this.FlushLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);
				if (isDebugLogEnabled)
					this.Logger.LogDebug($"Done flush {logs.Count:###,###,##0} logs into database - Times for flushing: {stopwatch.GetElapsedTimes()}");
			}
		}

		Task FlushLogsAsync(IEnumerable<ServiceLog> logs, CancellationToken cancellationToken)
			=> logs.Where(log => log != null).ForEachAsync(async log =>
			{
				// update database
				try
				{
					await ServiceLog.CreateAsync(log, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while flushing log into database => {ex.Message}{(ex is RepositoryOperationException ? $"\r\n{log.ToJson()}" : "")}", ex);
				}

				// write to separated files
				if (this.WriteServiceLogsIntoSeparatedFiles)
					try
					{
						var content = $"{log.Time:HH:mm:ss.fff}{(string.IsNullOrWhiteSpace(log.DeveloperID) ? "" : $" [Dev: {log.DeveloperID}]")}{(string.IsNullOrWhiteSpace(log.AppID) ? "" : $" [App: {log.AppID}]")} {log.Logs} [{log.CorrelationID}]{(string.IsNullOrWhiteSpace(log.Stack) ? "" : $"\r\n{log.Stack}")}\r\n";
						var filename = $"{log.Time:yyyyMMddHH}_{log.ServiceName}{(string.IsNullOrWhiteSpace(log.ObjectName) || log.ServiceName.IsEquals(log.ObjectName) ? "" : $".{log.ObjectName}")}.txt";
						await content.ToBytes().SaveAsTextAsync(Path.Combine(this.LogsPath, filename), cancellationToken, true).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.Logger.LogError($"Error occurred while writting log into separated file => {ex.Message}", ex);
					}
			}, true, false);

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
			var filter = Filters<ServiceLog>.LessThan("Time", DateTime.Now.AddDays(0 - (Int32.TryParse(UtilityService.GetAppSetting("Logs:Days", "2"), out var days) && days > 0 ? days : 2)));
			return ServiceLog.DeleteManyAsync(filter, null, this.CancellationToken);
		}
	}

	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}