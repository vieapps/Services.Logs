using net.vieapps.Components.Repository;

namespace net.vieapps.Services.Logs
{
	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}