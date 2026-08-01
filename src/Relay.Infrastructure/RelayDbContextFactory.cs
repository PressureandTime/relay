using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Relay.Infrastructure;

public sealed class RelayDbContextFactory : IDesignTimeDbContextFactory<RelayDbContext>
{
    public RelayDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=5432;Database=relay;Username=postgres")
            .Options;

        return new RelayDbContext(options);
    }
}
