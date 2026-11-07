namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class GuardedTransitionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContext _db;

    public GuardedTransitionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task TryAsync_WhenSourceMatches_ReturnsApplied()
    {
        _db.Rows.Add(new TestRow { Id = 1, State = 0 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await GuardedTransition.TryAsync(
            _db.Rows,
            row => row.Id == 1 && row.State == 0,
            row => row.Id == 1 && row.State == 1,
            update => update.SetProperty(row => row.State, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(GuardedTransitionResult.Applied, result);
        Assert.Equal(1, await _db.Rows.Where(row => row.Id == 1)
            .Select(row => row.State).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryAsync_WhenCompetingRequestAlreadyReachedTarget_ReturnsAlreadyInTarget()
    {
        _db.Rows.Add(new TestRow { Id = 1, State = 1 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await GuardedTransition.TryAsync(
            _db.Rows,
            row => row.Id == 1 && row.State == 0,
            row => row.Id == 1 && row.State == 1,
            update => update.SetProperty(row => row.State, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(GuardedTransitionResult.AlreadyInTarget, result);
    }

    [Fact]
    public async Task TryAsync_WhenRowMovedElsewhere_ReturnsLost()
    {
        _db.Rows.Add(new TestRow { Id = 1, State = 2 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await GuardedTransition.TryAsync(
            _db.Rows,
            row => row.Id == 1 && row.State == 0,
            row => row.Id == 1 && row.State == 1,
            update => update.SetProperty(row => row.State, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(GuardedTransitionResult.Lost, result);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestRow> Rows => Set<TestRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<TestRow>().HasKey(row => row.Id);
    }

    private sealed class TestRow
    {
        public int Id { get; set; }
        public int State { get; set; }
    }
}
