using System;
using Newtonsoft.Json;
using MongoDB.Bson.Serialization.Attributes;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;

namespace net.vieapps.Services.Logs
{
	[BsonIgnoreExtraElements, Entity(CollectionName = "ServiceLogs", TableName = "T_Logs_Services")]
	public class ServiceLog : Repository<ServiceLog>
	{
		public ServiceLog() : base()
			=> this.ID = UtilityService.NewUUID;

		[Sortable(IndexName = "Time")]
		public DateTime Time { get; set; } = DateTime.Now;

		[Property(MaxLength = 32, NotNull = true), Sortable(IndexName = "IDs")]
		public string CorrelationID { get; set; }

		[Property(MaxLength = 32), Sortable(IndexName = "IDs")]
		public string DeveloperID { get; set; }

		[Property(MaxLength = 32), Sortable(IndexName = "IDs")]
		public string AppID { get; set; }

		[Property(MaxLength = 150), Sortable(IndexName = "IDs")]
		public string NodeID { get; set; }

		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "Services")]
		public new string ServiceName { get; set; }

		[Property(MaxLength = 50, NotNull = true), Sortable(IndexName = "Services")]
		public new string ObjectName { get; set; }

		[Property(NotNull = true, IsCLOB = true)]
		public string Logs { get; set; }

		[Property(IsCLOB = true)]
		public string Stack { get; set; }

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