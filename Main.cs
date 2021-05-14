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
	public class ServiceComponent : ServiceBase, ILoggingService
	{
		IAsyncDisposable ServiceLogsInstance { get; set; }

		string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		bool IsServiceLogsDisabled { get; set; } = false;

		bool IsWriteServiceLogsDirectly { get; set; } = false;

		bool CleaningServiceLogs { get; set; } = false;

		bool FlushingServiceLogs { get; set; } = false;

		public override string ServiceName => "Logs";

		public override Task StartAsync(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/disable")) != null)
				this.IsServiceLogsDisabled = true;

			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/direct")) != null)
				this.IsWriteServiceLogsDirectly = true;

			return base.StartAsync(args, initializeRepository, next);
		}

		public override async Task RegisterServiceAsync(IEnumerable<string> args = null, Action<IService> onSuccess = null, Action<Exception> onError = null)
		{
			if (!this.IsServiceLogsDisabled)
			{
				this.Logger.LogWarning("Initializing the instance of the logging service");
				if (this.ServiceLogsInstance != null)
					try
					{
						await this.ServiceLogsInstance.DisposeAsync().ConfigureAwait(false);
					}
					catch { }
				this.ServiceLogsInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<ILoggingService>(() => this, RegistrationInterceptor.Create()).ConfigureAwait(false);
				this.Logger.LogWarning($"The instance of the logging service was successfully registered");
			}
			await base.RegisterServiceAsync(args, onSuccess, onError).ConfigureAwait(false);
		}

		public override async Task UnregisterServiceAsync(IEnumerable<string> args, bool available = true, Action<IService> onSuccess = null, Action<Exception> onError = null)
		{
			if (this.ServiceLogsInstance != null)
				try
				{
					await this.ServiceLogsInstance.DisposeAsync().ConfigureAwait(false);
					this.Logger.LogWarning("The instance of the logging service was successfully unregistered");
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while unregistering the instance of the logging service => {ex.Message}", ex);
				}
				finally
				{
					this.ServiceLogsInstance = null;
				}
			await base.UnregisterServiceAsync(args, available, onSuccess, onError).ConfigureAwait(false);
		}

		public override void DoWork(string[] args = null)
		{
			var stopwatch = Stopwatch.StartNew();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/flush")) != null)
			{
				if (this.IsDebugLogEnabled)
					this.Logger.LogDebug("Start flush logs from files into database");

				this.FlushLogsAsync()
#if NETSTANDARD2_0
					.Wait();
#else
					.ConfigureAwait(false)
					.GetAwaiter()
					.GetResult();
#endif

				stopwatch.Stop();
				if (this.IsDebugLogEnabled)
					this.Logger.LogDebug($"Complete flush logs from files into database - Execution times: {stopwatch.GetElapsedTimes()}");
			}

			stopwatch = Stopwatch.StartNew();
			if (args?.FirstOrDefault(arg => arg.IsStartsWith("/clean")) != null)
			{
				if (this.IsDebugLogEnabled)
					this.Logger.LogDebug("Start clean old logs from database");

				this.CleanLogsAsync()
#if NETSTANDARD2_0
					.Wait();
#else
					.ConfigureAwait(false)
					.GetAwaiter()
					.GetResult();
#endif

				stopwatch.Stop();
				if (this.IsDebugLogEnabled)
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
								await this.WriteLogAsync(requestInfo.Body.FromJson<ServiceLog>(false, (log, _) =>
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
			{
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
		}

		Task WriteLogAsync(ServiceLog log, CancellationToken cancellationToken)
			=> this.WriteLogsAsync(new[] { log }, cancellationToken);

		Task WriteLogsAsync(IEnumerable<ServiceLog> logs, CancellationToken cancellationToken)
			=> this.IsWriteServiceLogsDirectly
				? this.FlushLogsAsync(logs, cancellationToken)
				: logs.ForEachAsync(async log =>
				{
					var filePath = Path.Combine(this.LogsPath, $"logs.services.{DateTime.Now:yyyyMMddHHmmss}.{UtilityService.NewUUID}.json");
					await UtilityService.WriteTextFileAsync(filePath, log.ToString(Formatting.Indented), false, null, cancellationToken).ConfigureAwait(false);
				}, true, false);

		public Task WriteLogAsync(string correlationID, string developerID, string appID, string serviceName, string objectName, string log, string stack = null, CancellationToken cancellationToken = default)
			=> this.WriteLogsAsync(correlationID, developerID, appID, serviceName, objectName, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, stack, cancellationToken);

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

		async Task FlushLogsAsync()
		{
			var filePaths = Directory.EnumerateFiles(this.LogsPath, "logs.services.*.json").ToList();
			if (filePaths.Count > 0)
			{
				if (this.IsDebugLogEnabled)
					this.Logger.LogDebug($"Flush service logs from {filePaths.Count:###,###,##0} files");

				var logs = new List<ServiceLog>();
				await filePaths.ForEachAsync(async filePath =>
				{
					try
					{
						using (var reader = new StreamReader(filePath))
						{
							var json = await reader.ReadToEndAsync(this.CancellationToken).ConfigureAwait(false);
							logs.Add(json.FromJson<ServiceLog>(false, (log, _) =>
							{
								log.ID = string.IsNullOrWhiteSpace(log.ID) ? UtilityService.NewUUID : log.ID;
								log.ServiceName = log.ServiceName?.ToLower();
								log.ObjectName = log.ObjectName?.ToLower();
							}));
						}
					}
					catch (Exception ex)
					{
						this.Logger.LogError($"Error occurred while reading JSON file => {ex.Message}", ex);
					}
					File.Delete(filePath);
				}).ConfigureAwait(false);
				await this.FlushLogsAsync(logs, this.CancellationToken).ConfigureAwait(false);
			}
		}

		Task FlushLogsAsync(IEnumerable<ServiceLog> logs, CancellationToken cancellationToken)
			=> logs.Where(log => log != null).OrderBy(log => log.Time).ForEachAsync(async log =>
			{
				try
				{
					await ServiceLog.CreateAsync(log, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while flushing log into database => {ex.Message}", ex);
				}
			}, true, false);

		async Task<JToken> FetchLogsAsync(int pageNumber, int pageSize, string correlationID, string developerID, string appID, string serviceName, string objectName, CancellationToken cancellationToken)
		{
			var filter = Filters<ServiceLog>.And();
			if (!string.IsNullOrWhiteSpace(correlationID))
				filter.Add(Filters<ServiceLog>.Equals("CorrelationID", correlationID));
			if (!string.IsNullOrWhiteSpace(developerID))
				filter.Add(Filters<ServiceLog>.Equals("DeveloperID", developerID));
			if (!string.IsNullOrWhiteSpace(appID))
				filter.Add(Filters<ServiceLog>.Equals("AppID", appID));
			if (!string.IsNullOrWhiteSpace(serviceName))
				filter.Add(Filters<ServiceLog>.Equals("ServiceName", serviceName));
			if (!string.IsNullOrWhiteSpace(objectName))
				filter.Add(Filters<ServiceLog>.Equals("ObjectName", objectName));

			var totalRecords = await ServiceLog.CountAsync(filter, null, false, null, 0, cancellationToken).ConfigureAwait(false);
			var totalPages = new Tuple<long, int>(totalRecords, pageSize).GetTotalPages();
			var objects = await ServiceLog.FindAsync(filter, Sorts<ServiceLog>.Descending("Time"), pageSize, pageNumber, null, false, null, 0, cancellationToken).ConfigureAwait(false);

			return new JObject
			{
				{ "Pagination", new Tuple<long, int, int, int>(totalRecords, totalPages, pageSize, totalPages > 0 && pageNumber > totalPages ? totalPages : pageNumber).GetPagination() },
				{ "Objects", objects.Select(obj => obj.ToJson()).ToJArray() }
			};
		}

		Task CleanLogsAsync()
		{
			if (this.IsDebugLogEnabled)
				this.Logger.LogDebug($"Clean old service logs");
			var filter = Filters<ServiceLog>.LessThan("Time", DateTime.Now.AddDays(0 - (Int32.TryParse(UtilityService.GetAppSetting("Logs:Days", "3"), out var days) && days > 0 ? days : 3)));
			return ServiceLog.DeleteManyAsync(filter, null, this.CancellationToken);
		}
	}

	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}