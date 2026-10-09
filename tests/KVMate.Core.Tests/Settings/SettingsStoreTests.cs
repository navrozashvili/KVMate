using KVMate.Core.Settings;
using Xunit;

namespace KVMate.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "KVMate.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _path = Path.Combine(_directory, "settings.json");
        _store = new SettingsStore(_path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static KvmSettings Sample() => new()
    {
        DeviceInstanceId = @"USB\VID_3434&PID_0630\b&8bc2497&0&1",
        DeviceName = "Keyboard",
        SpeakerId = "{39c8f739-753e-5304-a7ff-e50ce6a516a7}",
        SpeakerName = "Speaker",
        StartWithWindows = true,
    };

    [Fact]
    public void Saved_settings_load_back_unchanged()
    {
        var saved = Sample();

        _store.Save(saved);
        var loaded = new SettingsStore(_path).Load();

        Assert.Equal(saved, loaded);
        Assert.True(loaded.IsConfigured);
    }

    [Fact]
    public void A_missing_file_yields_defaults_that_are_not_configured()
    {
        var loaded = _store.Load();

        Assert.Equal(KvmSettings.Default, loaded);
        Assert.False(loaded.IsConfigured);
        Assert.False(loaded.StartWithWindows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("{\"deviceInstanceId\": 42}")]
    public void A_corrupt_file_yields_defaults(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, contents);

        var loaded = _store.Load();

        Assert.Equal(KvmSettings.Default, loaded);
        Assert.False(loaded.IsConfigured);
    }

    [Fact]
    public void Saving_creates_the_folder()
    {
        _store.Save(Sample());

        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void A_save_replaces_the_file_and_leaves_no_temporary_file()
    {
        _store.Save(Sample());
        _store.Save(Sample() with { SpeakerName = "Renamed" });

        Assert.Equal("Renamed", _store.Load().SpeakerName);
        Assert.Equal([_path], Directory.GetFiles(_directory));
    }

    [Fact]
    public void A_save_that_cannot_replace_the_file_leaves_the_previous_one_whole()
    {
        var original = Sample();
        _store.Save(original);
        var before = File.ReadAllText(_path);

        // Open without delete sharing, so the file cannot be replaced.
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => _store.Save(original with { DeviceName = "Something else entirely" }));
        }

        Assert.Equal(before, File.ReadAllText(_path));
        Assert.Equal(original, _store.Load());
        Assert.Equal([_path], Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(null, "{x}", false)]
    [InlineData("dev", null, false)]
    [InlineData(" ", "{x}", false)]
    [InlineData("dev", "{x}", true)]
    public void Configured_means_both_a_device_and_a_speaker(string? device, string? speaker, bool expected) =>
        Assert.Equal(expected, new KvmSettings { DeviceInstanceId = device, SpeakerId = speaker }.IsConfigured);
}
