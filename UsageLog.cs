#region Related components
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MongoDB.Bson.Serialization.Attributes;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.Logs
{
	[BsonIgnoreExtraElements, Entity(CollectionName = "UsageLogs", TableName = "T_Logs_Usages")]
	public class UsageLog : Repository<UsageLog>
	{
		public UsageLog() : base() { }

		[Property(MaxLength = 32, NotNull = true), Sortable(IndexName = "Correlations")]
		public string CorrelationID { get; set; }

		[Property(MaxLength = 32), Sortable(IndexName = "Traces")]
		public string TraceID { get; set; }

		[Property(MaxLength = 32)]
		public string DeveloperID { get; set; }

		[Property(MaxLength = 32)]
		public string AppID { get; set; }

		[Property(MaxLength = 32), Sortable(IndexName = "Users")]
		public string UserID { get; set; }

		[Property(MaxLength = 150)]
		public string NodeID { get; set; }

		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "Services")]
		public new string ServiceName { get; set; }

		[Property(MaxLength = 50), Sortable(IndexName = "Services")]
		public new string ObjectName { get; set; }

		public override string SystemID { get; set; }

		[Property(MaxLength = 250, NotNull = true), Sortable(IndexName = "Operations")]
		public string Operation { get; set; }

		[Property(MaxLength = 50), Sortable(IndexName = "Types")]
		public string Type { get; set; }

		[Property(MaxLength = 100), Sortable(IndexName = "Providers")]
		public string Provider { get; set; }

		[Property(MaxLength = 150), Sortable(IndexName = "Models")]
		public string Model { get; set; }

		public int? Attempt { get; set; }

		public int? ModelRequests { get; set; }

		public int? ToolCalls { get; set; }

		public int? MCPCalls { get; set; }

		public long? InputTokens { get; set; }

		public long? CachedInputTokens { get; set; }

		public long? CacheCreationInputTokens { get; set; }

		public long? OutputTokens { get; set; }

		public long? ReasoningTokens { get; set; }

		public long? TotalTokens { get; set; }

		public long? InputBytes { get; set; }

		public long? OutputBytes { get; set; }

		[Sortable(IndexName = "Times", CompoundIndexName = "Correlations", Reverse = true)]
		public DateTime StartTime { get; set; }

		public DateTime? EndTime { get; set; }

		public double? DurationMs { get; set; }

		public decimal? ProviderCost { get; set; }

		[Property(MaxLength = 10)]
		public string ProviderCurrency { get; set; }

		[Property(MaxLength = 100)]
		public string PricingVersion { get; set; }

		[Property(MaxLength = 20)]
		public string Status { get; set; }

		[AsJson]
		public JObject Details { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string Title { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string RepositoryID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string RepositoryEntityID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override Privileges OriginalPrivileges { get; set; }
	}
}
