#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MongoDB.Bson.Serialization.Attributes;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.Logs
{
	[BsonIgnoreExtraElements, Entity(CollectionName = "SystemMetrics", TableName = "T_Logs_Metrics")]
	public class SystemMetric : Repository<SystemMetric>
	{
		public SystemMetric() : base() { }

		public SystemMetric(DateTime time, string metrics)
		{
			this.Year = time.Year;
			this.Month = time.Month;
			this.Day = time.Day;
			this.Hour = time.Hour;
			this.Minute = time.Minute;
			this.ID = $"{this.Year:0000}{this.Month:00}{this.Day:00}{this.Hour:00}{this.Minute:00}{UtilityService.BlankUUID}".Left(32);
			this.Metrics = JObject.Parse(metrics);
		}

		[Sortable(IndexName = "Times", Reverse = true)]
		public int Year { get; set; }

		[Sortable(IndexName = "Times", Reverse = true)]
		public int Month { get; set; }

		[Sortable(IndexName = "Times", Reverse = true)]
		public int Day { get; set; }

		[Sortable(IndexName = "Times", Reverse = true)]
		public int Hour { get; set; }

		[Sortable(IndexName = "Times", Reverse = true)]
		public int Minute { get; set; }

		[AsJson]
		public JObject Metrics { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string Title { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string SystemID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string RepositoryID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string RepositoryEntityID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override Privileges OriginalPrivileges { get; set; }
	}

	internal static class SystemMetricExtensions
	{
		public static (IEnumerable<StatisticMessage> ForAggregate, IEnumerable<StatisticMessage> ForReUpdate) GetMessages(this Channel<StatisticMessage> messages, DateTime? time = null)
		{
			var forReUpdate = new List<StatisticMessage>();
			var forAggregate = new List<StatisticMessage>();
			time = time != null ? time : DateTime.Now.AddMinutes(-1);
			time = new DateTime(time.Value.Year, time.Value.Month, time.Value.Day, time.Value.Hour, time.Value.Minute, 0);

			while (messages.Reader.TryRead(out var message))
			{
				if (message.Time.Hour == time.Value.Hour && message.Time.Minute == time.Value.Minute)
					forAggregate.Add(message);
				else if (message.Time > time.Value)
					forReUpdate.Add(message);
			}

			return (forAggregate, forReUpdate);
		}

		public static JObject Aggregate(this IEnumerable<StatisticMessage> messages, DateTime time, bool addNodeSamples, Action<JObject> onCompleted = null)
		{
			var upstreamMessages = messages.Where(message => message.IsHttp);
			var upstreamServiceMessages = upstreamMessages.GroupBy(message => message.ServiceName);
			var upstreamNodeMessages = upstreamMessages.GroupBy(message => message.NodeID);
			var upstreamNumberOfNodes = upstreamMessages.Select(message => message.NodeID).Distinct().Count();
			var (upstreamServices, upstreamEnvironment, upstreamCache, router) = upstreamMessages.Aggregate(upstreamServiceMessages, upstreamNodeMessages, upstreamNumberOfNodes, true, addNodeSamples);

			var downstreamMessages = messages.Where(message => !message.IsHttp);
			var downstreamServiceMessages = downstreamMessages.GroupBy(message => message.ServiceName);
			var downstreamNodeMessages = downstreamMessages.GroupBy(message => message.NodeID);
			var downstreamNumberOfNodes = downstreamMessages.Select(message => message.NodeID).Distinct().Count();
			var (downstreamServices, downstreamEnvironment, downstreamCache, _) = downstreamMessages.Aggregate(downstreamServiceMessages, downstreamNodeMessages, downstreamNumberOfNodes, true, addNodeSamples, NotAvailableInDownstream.Concat(NotAvailableInAPIGateway));

			var statisticsJson = new JObject
			{
				["Time"] = new JObject
				{
					["At"] = time.ToIsoString(),
					["WindowSizeSeconds"] = 60
				},
				["Router"] = router,
				["Upstream"] = new JObject
				{
					["Environment"] = upstreamEnvironment,
					["Cache"] = upstreamCache,
					["Services"] = upstreamServices
				},
				["Downstream"] = new JObject
				{
					["Environment"] = downstreamEnvironment,
					["Cache"] = downstreamCache,
					["Services"] = downstreamServices
				}
			};
			onCompleted?.Invoke(statisticsJson);
			return statisticsJson;
		}

		static (JToken ServicesJson, JToken EnvironmentJson, JToken CacheJson, JToken RpcJson) Aggregate(this IEnumerable<StatisticMessage> originalMessages, IEnumerable<IGrouping<string, StatisticMessage>> groupbyServiceMessages, IEnumerable<IGrouping<string, StatisticMessage>> groupbyNodeMessages, int numberOfNodes, bool addNodes, bool addNodeSamples, IEnumerable<string> beRemoved = null)
		{
			var statisticsByNodes = groupbyNodeMessages.Select(group =>
			{
				var cpuUsage = group.Max(message => message.CpuUsage);
				var memoryUsage = group.Max(message => message.MemoryUsage);
				var maxThreadPoolWorkers = group.Max(message => message.ThreadPoolMaxWorkers);
				var currentThreadPoolWorkers = group.Max(message => message.ThreadPoolWorkers);
				var workersUsage = maxThreadPoolWorkers > 0 ? (double)currentThreadPoolWorkers / maxThreadPoolWorkers : 0;
				var maxRpcCurrent = group.Max(message => message.RpcSlotCurrent);
				var maxRpcSlot = group.Max(message => message.RpcSlotMax);
				return new
				{
					NodeID = group.Key,
					CpuUsage = cpuUsage,
					MemoryUsage = memoryUsage,
					Workers = currentThreadPoolWorkers,
					MaxWorkers = maxThreadPoolWorkers,
					WorkersUsage = workersUsage,
					RpcCurrentMax = maxRpcCurrent,
					RpcSlotMax = maxRpcSlot
				};
			}).ToList();
			var statistics = groupbyServiceMessages.Aggregate(!addNodes);
			var cacheStatuses = statistics.Where(message => message.CacheStatus != "OK");

			var cpuMin = originalMessages.Min(message => message.CpuUsage);
			var cpuMax = originalMessages.Max(message => message.CpuUsage);
			var cpuAverage = originalMessages.Average(message => message.CpuUsage);

			var memoryMin = originalMessages.Min(message => message.MemoryUsage);
			var memoryMax = originalMessages.Max(message => message.MemoryUsage);
			var memoryAverage = originalMessages.Average(message => message.MemoryUsage);

			var threadpoolWorkers = statistics.Sum(message => message.ThreadPoolWorkers);
			var threadpoolMaxWorkers = statistics.Max(message => message.ThreadPoolMaxWorkers);
			var threadpoolAsyncIO = statistics.Sum(message => message.ThreadPoolAsyncIO);
			var threadpoolMaxAsyncIO = statistics.Max(message => message.ThreadPoolMaxAsyncIO);
			var threadpoolWorkersUsage = threadpoolMaxWorkers > 0 ? (double)threadpoolWorkers / threadpoolMaxWorkers : 0;

			var cacheProvider = statistics.First().CacheProvider;
			var cacheStatus = (cacheStatuses.FirstOrDefault(message => message.CacheStatus == "🔥CRITICAL") ?? cacheStatuses.FirstOrDefault(message => message.CacheStatus == "⚠️WARN"))?.CacheStatus ?? "OK";
			var cacheTotalQueue = statistics.Sum(message => message.CacheTotalQueue);
			var cacheMaxQueue = originalMessages.Max(message => message.CacheTotalQueue);
			var cacheAverageQueue = numberOfNodes > 0 ? cacheTotalQueue * 1.0 / numberOfNodes : 0;
			var cacheTotalInteractiveQueue = statistics.Sum(message => message.CacheInteractiveQueue);
			var cacheMaxInteractiveQueue = originalMessages.Max(message => message.CacheInteractiveQueue);
			var cacheAverageInteractiveQueue = numberOfNodes > 0 ? (double)cacheTotalInteractiveQueue / numberOfNodes : 0;
			var cacheMaxPing = originalMessages.Max(message => message.CachePingMilliseconds);
			var cacheAveragePing = originalMessages.GroupBy(message => message.NodeID).Select(group => group.Max(msg => msg.CachePingMilliseconds)).Average();

			var rpcSlotCurrent = statistics.Sum(message => message.RpcSlotCurrent);
			var rpcSlotMax = statisticsByNodes.Sum(statisticsByNode => statisticsByNode.RpcSlotMax);
			var rpcSlotAvailable = rpcSlotMax - rpcSlotCurrent;
			var rpcSlotUsage = rpcSlotMax > 0 ? (double)rpcSlotCurrent / rpcSlotMax : 0;
			var rpcInFlight = statistics.Sum(message => message.RpcInFlight);

			var rpcRejected = statistics.Sum(message => message.RpcRejected);
			var rpcRejectedRate = statistics.Sum(message => message.RpcRejectedRate);
			var rpcRejectedTotal = statistics.Sum(message => message.RpcRejectedTotal);
			var rpcRejectedTotalRate = statistics.Sum(message => message.RpcRejectedTotalRate);

			var rpcEntered = statistics.Sum(message => message.RpcEntered);
			var rpcEnteredRate = statistics.Sum(message => message.RpcEnteredRate);
			var rpcEnteredTotal = statistics.Sum(message => message.RpcEnteredTotal);
			var rpcEnteredTotalRate = statistics.Sum(message => message.RpcEnteredTotalRate);

			var rpcCompleted = statistics.Sum(message => message.RpcCompleted);
			var rpcCompletedRate = statistics.Sum(message => message.RpcCompletedRate);
			var rpcCompletedTotal = statistics.Sum(message => message.RpcCompletedTotal);
			var rpcCompletedTotalRate = statistics.Sum(message => message.RpcCompletedTotalRate);

			var rpcAverageLatency = statistics.Any() ? statistics.Max(message => message.RpcAverageLatency) : 0;
			var rpcMaxLatency = statistics.Any() ? statistics.Max(message => message.RpcMaxLatency) : 0;
			var rpcAverageLatencyTotal = statistics.Any() ? statistics.Max(message => message.RpcAverageLatencyTotal) : 0;
			var rpcMaxLatencyTotal = statistics.Any() ? statistics.Max(message => message.RpcMaxLatencyTotal) : 0;

			var rpcBackpressure = rpcEntered - rpcCompleted;
			var rpcBackpressureRate = rpcEnteredRate - rpcCompletedRate;
			var rpcCompletionRatio = (rpcEnteredRate > 0 ? rpcCompletedRate / rpcEnteredRate : 1) * 100.0;
			var rpcCompletionRatioTotal = (rpcEnteredTotal > 0 ? (double)rpcCompletedTotal / rpcEnteredTotal : 1) * 100.0;

			var servicesJson = statistics.ToJArray(statistic =>
			{
				var json = statistic.AsJson();

				if (addNodes)
				{
					var serviceMessages = originalMessages.Where(message => message.ServiceName == statistic.ServiceName);
					var groupbyMessages = serviceMessages.GroupBy(message => message.NodeID);
					var numberOfServiceNodes = serviceMessages.Select(message => message.NodeID).Distinct().Count();
					var (nodesJson, _, _, _) = serviceMessages.Aggregate(groupbyMessages, groupbyMessages, numberOfServiceNodes, false, false, beRemoved);
					if (addNodeSamples)
						(nodesJson as JArray).ForEach(nodeJson =>
						{
							var nodeID = nodeJson.Get<string>("NodeID");
							var samples = new JObject[12];
							serviceMessages.Where(message => message.NodeID == nodeID).OrderBy(message => message.Time).ToList().ForEach(message =>
							{
								var index = message.Time.Second / 5;
								if (index > -1 && index < samples.Length)
									samples[index] = message.AsJson(sample => sample.Remove(NotAvailableInSample.Concat(beRemoved ?? Array.Empty<string>())));
							});
							nodeJson["Samples"] = new JObject
							{
								["IntervalSeconds"] = 5,
								["Expected"] = samples.Length,
								["Values"] = samples.ToJArray()
							};
						});
					json["Nodes"] = nodesJson;
					json.Remove("NodeID");
				}
				else
					json.Remove("ServiceName");

				if (statistic.ServiceName.IsEquals("APIGateway"))
				{
					json.Remove(NotAvailableInAPIGateway);
					if (addNodes)
						json.Get<JArray>("Nodes").ForEach(nodeJson => nodeJson.Remove(NotAvailableInAPIGateway));
				}

				return json.Remove(beRemoved);
			});

			var environmentJson = new JObject
			{
				["CPU"] = new JObject
				{
					["Min"] = Math.Round(cpuMin, 4),
					["Max"] = Math.Round(cpuMax, 4),
					["Average"] = Math.Round(cpuAverage, 4),
					["Total"] = Math.Round(statisticsByNodes.Sum(statisticsByNode => statisticsByNode.CpuUsage), 4)
				},
				["Memory"] = new JObject
				{
					["Min"] = memoryMin,
					["Max"] = memoryMax,
					["Average"] = Math.Round(memoryAverage, 2),
					["Total"] = statisticsByNodes.Sum(statisticsByNode => statisticsByNode.MemoryUsage)
				},
				["ThreadPool"] = new JObject
				{
					["Usage"] = Math.Round(threadpoolWorkersUsage, 6),
					["Workers"] = threadpoolWorkers,
					["MaxWorkers"] = threadpoolMaxWorkers,
					["AsyncIO"] = threadpoolAsyncIO,
					["MaxAsyncIO"] = threadpoolMaxAsyncIO
				}
			};

			var cacheJson = new JObject
			{
				["Provider"] = cacheProvider,
				["Status"] = cacheStatus,
				["Ping"] = new JObject
				{
					["Max"] = cacheMaxPing,
					["Average"] = Math.Round(cacheAveragePing, 2)
				},
				["Queue"] = new JObject
				{
					["Max"] = cacheMaxQueue,
					["Average"] = cacheAverageQueue,
					["Total"] = cacheTotalQueue
				},
				["Interactive"] = new JObject
				{
					["Max"] = cacheMaxInteractiveQueue,
					["Average"] = Math.Round(cacheAverageInteractiveQueue, 2),
					["Total"] = cacheTotalInteractiveQueue
				}
			};

			var rpcJson = new JObject
			{
				["RpcSlot"] = new JObject
				{
					["Usage"] = Math.Round(rpcSlotUsage, 6),
					["Current"] = rpcSlotCurrent,
					["Available"] = rpcSlotAvailable,
					["Max"] = rpcSlotMax
				},
				["RpcCall"] = new JObject
				{
					["InFlight"] = rpcInFlight,
					["Backpressure"] = new JObject
					{
						["Value"] = rpcBackpressure,
						["Rate"] = Math.Round(rpcBackpressureRate, 2)
					},
					["Completion"] = new JObject
					{
						["Ratio"] = Math.Round(Math.Min(100.0, rpcCompletionRatio), 2),
						["RawRatio"] = Math.Round(rpcCompletionRatio, 2),
						["TotalRatio"] = Math.Round(rpcCompletionRatioTotal, 4)
					},
					["Rejected"] = new JObject
					{
						["Count"] = rpcRejected,
						["Rate"] = Math.Round(rpcRejectedRate, 2),
						["Total"] = rpcRejectedTotal,
						["TotalRate"] = Math.Round(rpcRejectedTotalRate, 2)
					},
					["Entered"] = new JObject
					{
						["Count"] = rpcEntered,
						["Rate"] = Math.Round(rpcEnteredRate, 2),
						["Total"] = rpcEnteredTotal,
						["TotalRate"] = Math.Round(rpcEnteredTotalRate, 2)
					},
					["Completed"] = new JObject
					{
						["Count"] = rpcCompleted,
						["Rate"] = Math.Round(rpcCompletedRate, 2),
						["Total"] = rpcCompletedTotal,
						["TotalRate"] = Math.Round(rpcCompletedTotalRate, 2),
					},
					["Latency"] = new JObject
					{
						["Average"] = Math.Round(rpcAverageLatency, 2),
						["Max"] = rpcMaxLatency,
						["AverageTotal"] = Math.Round(rpcAverageLatencyTotal, 2),
						["MaxTotal"] = rpcMaxLatencyTotal
					}
				}
			};

			return (servicesJson, environmentJson, cacheJson, rpcJson);
		}

		static List<StatisticMessage> Aggregate(this IEnumerable<IGrouping<string, StatisticMessage>> groupbyMessages, bool isNodeScope)
		{
			var statistics = new List<StatisticMessage>();

			foreach (var groupMessages in groupbyMessages)
			{
				int threadpoolWorkers = 0, threadpoolAsyncIO = 0, threadpoolMaxWorkers = 0, threadpoolMaxAsyncIO = 0;

				double requestsRate = 0;
				long requestsTotal = 0, requestsInFlight = 0, requestsHttpTotal = 0;

				string cacheProvider = "Redis", cacheStatus = "OK";
				long cacheTotalQueue = 0, cacheInteractiveQueue = 0, cachePingMilliseconds = 0;
				long cacheL1Hit304 = 0, cacheL1Hit200 = 0, cacheL1Miss = 0, cacheL1Bypass = 0;
				long cacheL2Hit304 = 0, cacheL2Hit200 = 0, cacheL2Miss = 0, cacheL2Bypass = 0;

				long rpcSlotCurrent = 0, rpcSlotMax = 0;
				long rpcInFlight = 0, rpcRejected = 0, rpcEntered = 0, rpcCompleted = 0;
				double rpcRejectedRate = 0, rpcEnteredRate = 0, rpcCompletedRate = 0;
				long rpcRejectedTotal = 0, rpcEnteredTotal = 0, rpcCompletedTotal = 0;
				double rpcRejectedTotalRate = 0, rpcEnteredTotalRate = 0, rpcCompletedTotalRate = 0;

				double rpcWeightedLatency = 0, rpcWeightedLatencyTotal = 0;
				long rpcCompletedCount = 0, rpcMaxLatency = 0, rpcCompletedTotalCount = 0, rpcMaxLatencyTotal = 0;

				var useL1Cache = false;
				foreach (var message in groupMessages)
				{
					useL1Cache = useL1Cache || message.UseL1Cache;

					threadpoolMaxWorkers = Math.Max(threadpoolMaxWorkers, message.ThreadPoolMaxWorkers);
					threadpoolMaxAsyncIO = Math.Max(threadpoolMaxAsyncIO, message.ThreadPoolMaxAsyncIO);

					if (isNodeScope)
					{
						threadpoolWorkers = Math.Max(threadpoolWorkers, message.ThreadPoolWorkers);
						threadpoolAsyncIO = Math.Max(threadpoolAsyncIO, message.ThreadPoolAsyncIO);

						requestsTotal = Math.Max(requestsTotal, message.RequestsTotal);
						requestsInFlight = Math.Max(requestsInFlight, message.RequestsInFlight);
						requestsRate += message.RequestsRate;
						requestsHttpTotal = Math.Max(requestsHttpTotal, message.UseL1Cache ? message.CacheL1Hit304 + message.CacheL1Hit200 + message.CacheL1Miss + message.CacheL1Bypass : message.CacheL2Hit304 + message.CacheL2Hit200 + message.CacheL2Miss + message.CacheL2Bypass);

						cacheProvider = message.CacheProvider;
						cacheStatus = cacheStatus == "OK" && message.CacheStatus != "OK" ? message.CacheStatus : cacheStatus;
						cacheTotalQueue = message.CacheTotalQueue > cacheTotalQueue ? message.CacheTotalQueue : cacheTotalQueue;
						cacheInteractiveQueue = message.CacheInteractiveQueue > cacheInteractiveQueue ? message.CacheInteractiveQueue : cacheInteractiveQueue;
						cachePingMilliseconds = message.CachePingMilliseconds > cachePingMilliseconds ? message.CachePingMilliseconds : cachePingMilliseconds;

						cacheL1Hit304 = Math.Max(cacheL1Hit304, message.CacheL1Hit304);
						cacheL1Hit200 = Math.Max(cacheL1Hit200, message.CacheL1Hit200);
						cacheL1Miss = Math.Max(cacheL1Miss, message.CacheL1Miss);
						cacheL1Bypass = Math.Max(cacheL1Bypass, message.CacheL1Bypass);

						cacheL2Hit304 = Math.Max(cacheL2Hit304, message.CacheL2Hit304);
						cacheL2Hit200 = Math.Max(cacheL2Hit200, message.CacheL2Hit200);
						cacheL2Miss = Math.Max(cacheL2Miss, message.CacheL2Miss);
						cacheL2Bypass = Math.Max(cacheL2Bypass, message.CacheL2Bypass);

						rpcSlotCurrent = Math.Max(rpcSlotCurrent, message.RpcSlotCurrent);
						rpcSlotMax = Math.Max(rpcSlotMax, message.RpcSlotMax);
						rpcInFlight = Math.Max(rpcInFlight, message.RpcInFlight);

						rpcRejected = Math.Max(rpcRejected, message.RpcRejected);
						rpcRejectedRate += message.RpcRejectedRate;
						rpcRejectedTotal = Math.Max(rpcRejectedTotal, message.RpcRejectedTotal);
						rpcRejectedTotalRate += message.RpcRejectedTotalRate;

						rpcEntered = Math.Max(rpcEntered, message.RpcEntered);
						rpcEnteredRate += message.RpcEnteredRate;
						rpcEnteredTotal = Math.Max(rpcEnteredTotal, message.RpcEnteredTotal);
						rpcEnteredTotalRate += message.RpcEnteredTotalRate;

						rpcCompleted = Math.Max(rpcCompleted, message.RpcCompleted);
						rpcCompletedRate += message.RpcCompletedRate;
						rpcCompletedTotal = Math.Max(rpcCompletedTotal, message.RpcCompletedTotal);
						rpcCompletedTotalRate += message.RpcCompletedTotalRate;
					}

					if (message.RpcCompleted > 0)
					{
						rpcWeightedLatency += message.RpcAverageLatency * message.RpcCompleted;
						rpcCompletedCount += message.RpcCompleted;
					}

					if (message.RpcMaxLatency > rpcMaxLatency)
						rpcMaxLatency = message.RpcMaxLatency;

					if (message.RpcCompletedTotal > 0)
					{
						rpcWeightedLatencyTotal += message.RpcAverageLatencyTotal * message.RpcCompletedTotal;
						rpcCompletedTotalCount += message.RpcCompletedTotal;
					}

					if (message.RpcMaxLatencyTotal > rpcMaxLatencyTotal)
						rpcMaxLatencyTotal = message.RpcMaxLatencyTotal;
				}

				var sampleCount = groupMessages.Count();
				if (isNodeScope)
				{
					requestsRate = sampleCount > 0 ? requestsRate / sampleCount : requestsRate;
					rpcEnteredRate = sampleCount > 0 ? rpcEnteredRate / sampleCount : rpcEnteredRate;
					rpcCompletedRate = sampleCount > 0 ? rpcCompletedRate / sampleCount : rpcCompletedRate;
				}
				else
				{
					var nodeMessages = groupMessages.GroupBy(message => message.NodeID);

					threadpoolWorkers = nodeMessages.Sum(messages => messages.Max(message => message.ThreadPoolWorkers));
					threadpoolAsyncIO = nodeMessages.Sum(messages => messages.Max(message => message.ThreadPoolAsyncIO));

					requestsTotal = nodeMessages.Sum(messages => messages.Max(message => message.RequestsTotal));
					requestsInFlight = nodeMessages.Sum(messages => messages.Max(message => message.RequestsInFlight));
					requestsRate = nodeMessages.Sum(messages => messages.Average(message => message.RequestsRate));
					requestsHttpTotal = nodeMessages.Sum(messages => messages.Max(message => message.UseL1Cache ? message.CacheL1Hit304 + message.CacheL1Hit200 + message.CacheL1Miss + message.CacheL1Bypass : message.CacheL2Hit304 + message.CacheL2Hit200 + message.CacheL2Miss + message.CacheL2Bypass));

					cacheL1Hit304 = nodeMessages.Sum(messages => messages.Max(message => message.CacheL1Hit304));
					cacheL1Hit200 = nodeMessages.Sum(messages => messages.Max(message => message.CacheL1Hit200));
					cacheL1Miss = nodeMessages.Sum(messages => messages.Max(message => message.CacheL1Miss));
					cacheL1Bypass = nodeMessages.Sum(messages => messages.Max(message => message.CacheL1Bypass));

					cacheL2Hit304 = nodeMessages.Sum(messages => messages.Max(message => message.CacheL2Hit304));
					cacheL2Hit200 = nodeMessages.Sum(messages => messages.Max(message => message.CacheL2Hit200));
					cacheL2Miss = nodeMessages.Sum(messages => messages.Max(message => message.CacheL2Miss));
					cacheL2Bypass = nodeMessages.Sum(messages => messages.Max(message => message.CacheL2Bypass));

					rpcSlotCurrent = nodeMessages.Sum(messages => messages.Max(message => message.RpcSlotCurrent));
					rpcSlotMax = nodeMessages.Sum(messages => messages.Max(message => message.RpcSlotMax));
					rpcInFlight = nodeMessages.Sum(messages => messages.Max(message => message.RpcInFlight));

					rpcRejected = nodeMessages.Sum(messages => messages.Max(message => message.RpcRejected));
					rpcRejectedRate = nodeMessages.Sum(messages => messages.Average(message => message.RpcRejectedRate));
					rpcRejectedTotal = nodeMessages.Sum(messages => messages.Max(message => message.RpcRejectedTotal));
					rpcRejectedTotalRate = nodeMessages.Sum(messages => messages.Average(message => message.RpcRejectedTotalRate));

					rpcEntered = nodeMessages.Sum(messages => messages.Max(message => message.RpcEntered));
					rpcEnteredRate = nodeMessages.Sum(messages => messages.Average(message => message.RpcEnteredRate));
					rpcEnteredTotal = nodeMessages.Sum(messages => messages.Max(message => message.RpcEnteredTotal));
					rpcEnteredTotalRate = nodeMessages.Sum(messages => messages.Average(message => message.RpcEnteredTotalRate));

					rpcCompleted = nodeMessages.Sum(messages => messages.Max(message => message.RpcCompleted));
					rpcCompletedRate = nodeMessages.Sum(messages => messages.Average(message => message.RpcCompletedRate));
					rpcCompletedTotal = nodeMessages.Sum(messages => messages.Max(message => message.RpcCompletedTotal));
					rpcCompletedTotalRate = nodeMessages.Sum(messages => messages.Average(message => message.RpcCompletedTotalRate));
				}

				var rpcAverageLatency = rpcCompletedCount > 0 ? rpcWeightedLatency / rpcCompletedCount : 0;
				var rpcAverageLatencyTotal = rpcCompletedTotalCount > 0 ? rpcWeightedLatencyTotal / rpcCompletedTotalCount : 0;

				var key = groupMessages.Key;
				statistics.Add(new StatisticMessage
				{
					ServiceName = key,
					NodeID = key,

					CpuMin = groupMessages.Min(message => message.CpuUsage),
					CpuMax = groupMessages.Max(message => message.CpuUsage),
					CpuAverage = groupMessages.Average(message => message.CpuUsage),
					MemoryMin = groupMessages.Min(message => message.MemoryUsage),
					MemoryMax = groupMessages.Max(message => message.MemoryUsage),
					MemoryAverage = groupMessages.Average(message => message.MemoryUsage),

					ThreadPoolWorkers = threadpoolWorkers,
					ThreadPoolAsyncIO = threadpoolAsyncIO,
					ThreadPoolMaxWorkers = threadpoolMaxWorkers,
					ThreadPoolMaxAsyncIO = threadpoolMaxAsyncIO,

					RequestsTotal = requestsTotal,
					RequestsInFlight = requestsInFlight,
					RequestsRate = requestsRate,

					CacheProvider = cacheProvider,
					CacheStatus = cacheStatus,
					CacheTotalQueue = cacheTotalQueue,
					CacheInteractiveQueue = cacheInteractiveQueue,
					CachePingMilliseconds = cachePingMilliseconds,

					CacheL1Hit304 = cacheL1Hit304,
					CacheL1Hit200 = cacheL1Hit200,
					CacheL1HitRatio = requestsHttpTotal > 0 ? (cacheL1Hit304 + cacheL1Hit200) * 100.0 / requestsHttpTotal : 0,
					CacheL1Miss = cacheL1Miss,
					CacheL1MissRatio = requestsHttpTotal > 0 ? cacheL1Miss * 100.0 / requestsHttpTotal : 0,
					CacheL1Bypass = cacheL1Bypass,
					CacheL1BypassRatio = requestsHttpTotal > 0 ? cacheL1Bypass * 100.0 / requestsHttpTotal : 0,

					CacheL2Hit304 = cacheL2Hit304,
					CacheL2Hit200 = cacheL2Hit200,
					CacheL2HitRatio = requestsHttpTotal > 0 ? (cacheL2Hit304 + cacheL2Hit200) * 100.0 / requestsHttpTotal : 0,
					CacheL2Miss = cacheL2Miss,
					CacheL2MissRatio = requestsHttpTotal > 0 ? cacheL2Miss * 100.0 / requestsHttpTotal : 0,
					CacheL2Bypass = cacheL2Bypass,
					CacheL2BypassRatio = requestsHttpTotal > 0 ? cacheL2Bypass * 100.0 / requestsHttpTotal : 0,

					RpcSlotUsage = rpcSlotMax > 0 ? (double)rpcSlotCurrent / rpcSlotMax : 0,
					RpcSlotCurrent = rpcSlotCurrent,
					RpcSlotAvailable = rpcSlotMax - rpcSlotCurrent,
					RpcSlotMax = rpcSlotMax,

					RpcInFlight = rpcInFlight,

					RpcRejected = rpcRejected,
					RpcRejectedRate = rpcRejectedRate,
					RpcRejectedTotal = rpcRejectedTotal,
					RpcRejectedTotalRate = rpcRejectedTotalRate,

					RpcEntered = rpcEntered,
					RpcEnteredRate = rpcEnteredRate,
					RpcEnteredTotal = rpcEnteredTotal,
					RpcEnteredTotalRate = rpcEnteredTotalRate,

					RpcCompleted = rpcCompleted,
					RpcCompletedRate = rpcCompletedRate,
					RpcCompletedTotal = rpcCompletedTotal,
					RpcCompletedTotalRate = rpcCompletedTotalRate,

					RpcAverageLatency = rpcAverageLatency,
					RpcMaxLatency = rpcMaxLatency,
					RpcAverageLatencyTotal = rpcAverageLatencyTotal,
					RpcMaxLatencyTotal = rpcMaxLatencyTotal
				});
			}

			return statistics;
		}

		static JObject AsJson(this StatisticMessage statistic, Action<JObject> onCompleted = null)
		{
			var backpressure = statistic.RpcEntered - statistic.RpcCompleted;
			var completionRatio = (statistic.RpcEnteredRate > 0 ? statistic.RpcCompletedRate / statistic.RpcEnteredRate : 1) * 100.0;
			var completionRatioTotal = (statistic.RpcEnteredTotal > 0 ? (double)statistic.RpcCompletedTotal / statistic.RpcEnteredTotal : 1) * 100.0;
			var json = new JObject
			{
				["ServiceName"] = statistic.ServiceName,
				["NodeID"] = statistic.NodeID,
				["Cpu"] = new JObject
				{
					["Min"] = Math.Round(statistic.CpuMin, 2),
					["Max"] = Math.Round(statistic.CpuMax, 2),
					["Average"] = Math.Round(statistic.CpuAverage, 2)
				},
				["Memory"] = new JObject
				{
					["Min"] = statistic.MemoryMin,
					["Max"] = statistic.MemoryMax,
					["Average"] = Math.Round(statistic.MemoryAverage, 2)
				},
				["ThreadPool"] = new JObject
				{
					["Workers"] = statistic.ThreadPoolWorkers,
					["AsyncIO"] = statistic.ThreadPoolAsyncIO
				},
				["Cache"] = new JObject
				{
					["Provider"] = statistic.CacheProvider,
					["Status"] = statistic.CacheStatus,
					["Ping"] = statistic.CachePingMilliseconds,
					["Queue"] = new JObject
					{
						["Total"] = statistic.CacheTotalQueue,
						["Interactive"] = statistic.CacheInteractiveQueue
					},
					["L1"] = new JObject
					{
						["Hit304"] = statistic.CacheL1Hit304,
						["Hit200"] = statistic.CacheL1Hit200,
						["HitRatio"] = Math.Round(statistic.CacheL1HitRatio, 2),
						["Miss"] = statistic.CacheL1Miss,
						["MissRatio"] = Math.Round(statistic.CacheL1MissRatio, 2),
						["Bypass"] = statistic.CacheL1Bypass,
						["BypassRatio"] = Math.Round(statistic.CacheL1BypassRatio, 2),
					},
					["L2"] = new JObject
					{
						["Hit304"] = statistic.CacheL2Hit304,
						["Hit200"] = statistic.CacheL2Hit200,
						["HitRatio"] = Math.Round(statistic.CacheL2HitRatio, 2),
						["Miss"] = statistic.CacheL2Miss,
						["MissRatio"] = Math.Round(statistic.CacheL2MissRatio, 2),
						["Bypass"] = statistic.CacheL2Bypass,
						["BypassRatio"] = Math.Round(statistic.CacheL2BypassRatio, 2),
					}
				},
				["Request"] = new JObject
				{
					["Total"] = statistic.RequestsTotal,
					["InFlight"] = statistic.RequestsInFlight,
					["Rate"] = Math.Round(statistic.RequestsRate, 2)
				},
				["RpcSlot"] = new JObject
				{
					["Usage"] = Math.Round(statistic.RpcSlotUsage, 4),
					["Current"] = statistic.RpcSlotCurrent,
					["Max"] = statistic.RpcSlotMax,
					["Available"] = statistic.RpcSlotAvailable
				},
				["RpcCall"] = new JObject
				{
					["InFlight"] = statistic.RpcInFlight,
					["Backpressure"] = new JObject
					{
						["Value"] = backpressure,
						["Rate"] = Math.Round(statistic.RpcEnteredRate - statistic.RpcCompletedRate, 2)
					},
					["Completion"] = new JObject
					{
						["Ratio"] = Math.Round(Math.Min(100.0, completionRatio), 2),
						["RawRatio"] = Math.Round(completionRatio, 2),
						["TotalRatio"] = Math.Round(completionRatioTotal, 4)
					},
					["Rejected"] = new JObject
					{
						["Count"] = statistic.RpcRejected,
						["Rate"] = Math.Round(statistic.RpcRejectedRate, 2),
						["Total"] = statistic.RpcRejectedTotal,
						["TotalRate"] = Math.Round(statistic.RpcRejectedTotalRate, 2)
					},
					["Entered"] = new JObject
					{
						["Count"] = statistic.RpcEntered,
						["Rate"] = Math.Round(statistic.RpcEnteredRate, 2),
						["Total"] = statistic.RpcEnteredTotal,
						["TotalRate"] = Math.Round(statistic.RpcEnteredTotalRate, 2)
					},
					["Completed"] = new JObject
					{
						["Count"] = statistic.RpcCompleted,
						["Rate"] = Math.Round(statistic.RpcCompletedRate, 2),
						["Total"] = statistic.RpcCompletedTotal,
						["TotalRate"] = Math.Round(statistic.RpcCompletedTotalRate, 2)
					},
					["Latency"] = new JObject
					{
						["Average"] = Math.Round(statistic.RpcAverageLatency, 2),
						["Max"] = statistic.RpcMaxLatency,
						["AverageTotal"] = Math.Round(statistic.RpcAverageLatencyTotal, 2),
						["MaxTotal"] = statistic.RpcMaxLatencyTotal
					}
				}
			};
			onCompleted?.Invoke(json);
			return json;
		}

		internal static JObject Remove(this JObject json, IEnumerable<string> beRemoved)
		{
			beRemoved?.ForEach(name =>
			{
				var names = name.ToArray(".");
				if (names.Length > 1)
				{
					var jobject = json.Get<JObject>(names[0]);
					var index = 1;
					while (index < names.Length - 1 && jobject != null)
					{
						jobject = jobject.Get<JObject>(names[index]);
						index++;
					}
					if (jobject != null && !string.IsNullOrWhiteSpace(names[^1]))
						jobject.Remove(names[^1]);
				}
				else
					json.Remove(name);
			});
			return json;
		}

		static string[] NotAvailableInAPIGateway { get; } = new[] { "Cache.L1", "Cache.L2" };

		static string[] NotAvailableInDownstream { get; } = new[] { "Request", "RpcSlot", "RpcCall.Rejected" };

		static string[] NotAvailableInSample { get; } = new[] { "ServiceName", "NodeID", "Cpu", "Memory", "Cache.Provider", "Cache.Status", "Cache.L1.Hit304", "Cache.L1.Hit200", "Cache.L1.Miss", "Cache.L1.Bypass", "Cache.L2.Hit304", "Cache.L2.Hit200", "Cache.L2.Miss", "Cache.L2.Bypass", "Request.Total", "RpcSlot.Max", "RpcCall.Completion.TotalRatio", "RpcCall.Rejected.Total", "RpcCall.Rejected.TotalRate", "RpcCall.Entered.Total", "RpcCall.Entered.TotalRate", "RpcCall.Completed.Total", "RpcCall.Completed.TotalRate", "RpcCall.Latency.AverageTotal", "RpcCall.Latency.MaxTotal" };
	}
}