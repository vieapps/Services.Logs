using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using MongoDB.Bson.Serialization.Attributes;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;

namespace net.vieapps.Services.Logs
{
	[BsonIgnoreExtraElements, Entity(CollectionName = "AuditLogs", TableName = "T_Logs_Audits")]
	public class AuditLog : Repository<AuditLog>
	{
		public AuditLog() : base() { }

		[Property(MaxLength = 32), Sortable(IndexName = "Correlations")]
		public string CorrelationID { get; set; }

		[Property(MaxLength = 32), Sortable(IndexName = "Traces")]
		public string TraceID { get; set; }

		[Property(MaxLength = 32)]
		public string DeveloperID { get; set; }

		[Property(MaxLength = 32)]
		public string AppID { get; set; }

		[Property(MaxLength = 50)]
		public string ActorType { get; set; }

		[Property(MaxLength = 150), Sortable(IndexName = "Actors")]
		public string ActorID { get; set; }

		[Property(MaxLength = 150)]
		public string NodeID { get; set; }

		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "Services")]
		public new string ServiceName { get; set; }

		[Property(MaxLength = 150, NotNull = true), Sortable(IndexName = "Objects")]
		public new string ObjectName { get; set; }

		[Property(MaxLength = 150), Sortable(IndexName = "Objects")]
		public string ObjectID { get; set; }

		public override string SystemID { get; set; }

		[Property(MaxLength = 250, NotNull = true), Sortable(IndexName = "Operations")]
		public string Operation { get; set; }

		[Property(MaxLength = 20), Sortable(IndexName = "Statuses")]
		public string Status { get; set; }

		[Sortable(IndexName = "Times", CompoundIndexName = "Correlations", Reverse = true)]
		public DateTime Time { get; set; }

		[AsJson]
		public Dictionary<string, object> Changes { get; set; }

		[AsJson]
		public Dictionary<string, object> Details { get; set; }

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
