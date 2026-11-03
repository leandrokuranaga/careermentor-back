using CareerMentor.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CareerMentor.Api.Infra;

public class Context : DbContext
{
    public Context(DbContextOptions<Context> options) : base(options)
    {
        Database.Migrate();
    }

    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<NextAction> NextActions => Set<NextAction>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Goal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Metric).HasMaxLength(200).IsRequired();
            e.HasMany(x => x.NextActions)
             .WithOne(x => x.Goal!)
             .HasForeignKey(x => x.GoalId);
        });

        mb.Entity<NextAction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
        });
    }
}
