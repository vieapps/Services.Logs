#region Related components
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
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

		bool IsServiceLogsWriterDisabled { get; set; } = false;

		bool IsServiceLogsFlusherDisabled { get; set; } = false;

		bool CleaningServiceLogs { get; set; } = false;

		bool FlushingServiceLogs { get; set; } = false;

		public override string ServiceName => "Logs";

		public override Task RegisterServiceAsync(IEnumerable<string> args = null, Action<IService> onSuccess = null, Action<Exception> onError = null)
			=> base.RegisterServiceAsync(args, async service =>
			{
				this.Logger.LogWarning("Initializing the instance of the logging service");

				if (args?.FirstOrDefault(arg => arg.IsStartsWith("/disable-write")) != null)
					this.IsServiceLogsWriterDisabled = true;
				if (args?.FirstOrDefault(arg => arg.IsStartsWith("/disable-flush")) != null)
					this.IsServiceLogsFlusherDisabled = true;

				if (this.ServiceLogsInstance != null)
					try
					{
						await this.ServiceLogsInstance.DisposeAsync().ConfigureAwait(false);
					}
					catch { }

				this.ServiceLogsInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<ILoggingService>(() => this, RegistrationInterceptor.Create()).ConfigureAwait(false);
				this.Logger.LogWarning($"The instance of the logging service was successfully registered - [{this.LogsPath} - {this.IsServiceLogsWriterDisabled} - {this.IsServiceLogsFlusherDisabled}]");

				onSuccess?.Invoke(service);
			}, onError);

		public override Task UnregisterServiceAsync(IEnumerable<string> args, bool available = true, Action<IService> onSuccess = null, Action<Exception> onError = null)
			=> base.UnregisterServiceAsync(args, available, async service =>
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
				onSuccess?.Invoke(service);
			}, onError);

		public override Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> Task.FromResult<JToken>(null);

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

		public Task WriteLogAsync(string correlationID, string developerID, string appID, string serviceName, string objectName, string log, string stack = null, CancellationToken cancellationToken = default)
			=> this.WriteLogsAsync(correlationID, developerID, appID, serviceName, objectName, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, stack, cancellationToken);

		public async Task WriteLogsAsync(string correlationID, string developerID, string appID, string serviceName, string objectName, List<string> logs, string stack = null, CancellationToken cancellationToken = default)
		{
			if (this.IsServiceLogsWriterDisabled)
				return;

			var loggingItem = new ServiceLog
			{
				CorrelationID = correlationID,
				DeveloperID = string.IsNullOrWhiteSpace(developerID) ? null : developerID,
				AppID = string.IsNullOrWhiteSpace(appID) ? null : appID,
				ServiceName = (string.IsNullOrWhiteSpace(serviceName) ? "APIGateway" : serviceName).ToLower(),
				ObjectName = (string.IsNullOrWhiteSpace(objectName) || objectName.IsEquals(serviceName) ? "" : objectName).ToLower(),
				Logs = "" + logs?.Where(log => !string.IsNullOrWhiteSpace(log)).Join("\r\n"),
				Stack = string.IsNullOrWhiteSpace(stack) ? null : stack
			};

			using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
			{
				using (var ctsrc = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, this.CancellationToken))
				{
					var filePath = Path.Combine(this.LogsPath, $"logs.services.{DateTime.Now:yyyyMMddHHmmss}.{UtilityService.NewUUID}.json");
					await UtilityService.WriteTextFileAsync(filePath, loggingItem.ToString(Formatting.Indented), false, null, ctsrc.Token).ConfigureAwait(false);
				}
			}
		}

		async Task FlushLogsAsync()
		{
			if (this.IsServiceLogsFlusherDisabled)
				return;

			var filePaths = Directory.EnumerateFiles(this.LogsPath, "logs.services.*.json").ToList();
			if (filePaths.Count > 0)
			{
				if (this.IsDebugLogEnabled)
					this.Logger.LogDebug($"Flush service logs from {filePaths.Count:###,###,##0} files");

				using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
				{
					using (var ctsrc = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, this.CancellationToken))
					{
						var logs = new List<ServiceLog>();
						await filePaths.ForEachAsync(async filePath =>
						{
							try
							{
								using (var reader = new StreamReader(filePath))
								{
									var json = await reader.ReadToEndAsync(ctsrc.Token).ConfigureAwait(false);
									logs.Add(json.FromJson<ServiceLog>());
								}
							}
							catch (Exception ex)
							{
								this.Logger.LogError($"Error occurred while reading JSON file => {ex.Message}", ex);
							}
							File.Delete(filePath);
						}).ConfigureAwait(false);
						await logs.OrderBy(log => log.Time).ForEachAsync(async log => await ServiceLog.CreateAsync(log, ctsrc.Token).ConfigureAwait(false), true, false).ConfigureAwait(false);
					}
				}
			}
		}

		/*
		public Task WriteLogsAsync(string correlationID, string developerID, string appID, string serviceName, string objectName, List<string> logs, string stack = null, CancellationToken cancellationToken = default)
		{
			this.Logger.LogDebug($"Write log => {logs.Join("\r\n")}");
			return Task.CompletedTask;
		}

		Task FlushLogsAsync()
		{
			this.Logger.LogDebug($"Flush logs");
			return Task.CompletedTask;
		}
		*/

		public async Task<JToken> FetchLogsAsync(int pageNumber = 1, int pageSize = 100, string correlationID = null, string developerID = null, string appID = null, string serviceName = null, string objectName = null, CancellationToken cancellationToken = default)
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
}