using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;

namespace NzbWebDAV.Tests.Queue;

public class BlocklistedFilePostProcessorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DavDatabaseContext _context;
    private readonly DavDatabaseClient _dbClient;
    private readonly ConfigManager _config;

    public BlocklistedFilePostProcessorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"blocklist-test-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _context = new DavDatabaseContext(options);
        _context.Database.EnsureCreated();
        _dbClient = new DavDatabaseClient(_context);

        _config = new ConfigManager();
        _config.UpdateValues(
        [
            new()
            {
                ConfigName = ConfigKeys.ApiDownloadFileBlocklist,
                ConfigValue = "*.nfo, *.par2, *.sfv, *sample.mkv, *unpack.mkv, *.unpack.mp4",
            },
        ]);
    }

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }

    [Fact]
    public void RemoveBlocklistedFiles_RemovesMatchingAddedItemsWithoutThrowing()
    {
        var mount = SeedDirectory(DavItem.ContentFolder, "Show.Release");
        var keep = SeedNzbFile(mount, "Show.S01E01.mkv");
        var sample1 = SeedNzbFile(mount, "Show.S01E01.sample.mkv");
        var sample2 = SeedNzbFile(mount, "trailer.sample.mkv");

        var processor = new BlocklistedFilePostProcessor(_config, _dbClient);
        var exception = Record.Exception(processor.RemoveBlocklistedFiles);

        Assert.Null(exception);

        var remainingAdded = _context.ChangeTracker.Entries<DavItem>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Where(e => e.Type != DavItem.ItemType.Directory)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(keep.Name, remainingAdded);
        Assert.DoesNotContain(sample1.Name, remainingAdded);
        Assert.DoesNotContain(sample2.Name, remainingAdded);
        Assert.DoesNotContain(_context.BlobNzbFiles, b => b.Id == sample1.FileBlobId);
        Assert.DoesNotContain(_context.BlobNzbFiles, b => b.Id == sample2.FileBlobId);
        Assert.Contains(_context.BlobNzbFiles, b => b.Id == keep.FileBlobId);
    }

    private DavItem SeedDirectory(DavItem parent, string name)
    {
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        _context.Items.Add(item);
        return item;
    }

    private DavItem SeedNzbFile(DavItem parent, string name)
    {
        var blob = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = ["<seg@example.com>"],
        };
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, blob.Id);
        _context.Items.Add(item);
        _context.AddBlob(blob);
        return item;
    }
}
