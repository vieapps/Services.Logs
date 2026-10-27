using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MongoDB.Bson.Serialization.Attributes;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;

namespace net.vieapps.Services.Logs
{
	[BsonIgnoreExtraElements, Entity(CollectionName = "TraceLogs", TableName = "T_Logs_Traces")]
	public class TraceLog : Repository<TraceLog>
	{
		public TraceLog() : base() { }

		[Property(MaxLength = 32, NotNull = true), Sortable(IndexName = "Correlations")]
		public string CorrelationID { get; set; }

		[Property(MaxLength = 16), Sortable(IndexName = "Parents")]
		public string ParentID { get; set; }

		[Property(MaxLength = 150)]
		public string NodeID { get; set; }

		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "Services")]
		public new string ServiceName { get; set; }

		[Property(MaxLength = 250, NotNull = true), Sortable(IndexName = "Operations")]
		public string Operation { get; set; }

		[Sortable(IndexName = "Times", CompoundIndexName = "Correlations", Reverse = true)]
		public DateTime StartTime { get; set; }

		[Sortable(IndexName = "Times", CompoundIndexName = "Correlations", Reverse = true)]
		public DateTime EndTime { get; set; }

		[Sortable(IndexName = "Durations")]
		public double DurationMs { get; set; }

		[Property(MaxLength = 20)]
		public string Kind { get; set; }

		[Property(MaxLength = 20)]
		public string Status { get; set; }

		[Property(MaxLength = 500)]
		public string StatusDescription { get; set; }

		[AsJson]
		public JObject Attributes { get; set; }

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
}
