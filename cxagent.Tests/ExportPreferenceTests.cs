using System.Text.Json.Nodes;
using CxAgent.Core.Export;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The export dialog's remembered choices — <c>export</c> in config.json.</summary>
public class ExportPreferenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("export-pref-").FullName;
    private string ConfigPath => Path.Combine(_dir, "config.json");
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NoFile_IsReadable_ToAFile_NotOpened() =>
        Assert.Equal(new ExportChoices(ExportOptions.Readable, false, false), ExportPreference.Read(ConfigPath));

    [Fact]
    public void WhatWasSaved_IsWhatIsRead()
    {
        var choices = new ExportChoices(ExportOptions.Full & ~ExportParts.Thinking, true, true);
        ExportPreference.Save(ConfigPath, choices);

        Assert.Equal(choices, ExportPreference.Read(ConfigPath));
    }

    /// <summary>A BROKEN FILE COSTS THE REMEMBERED CHOICE, never the dialog.</summary>
    [Fact]
    public void AnUnreadableFile_GivesTheDefaults()
    {
        File.WriteAllText(ConfigPath, "{ not json");
        Assert.Equal(new ExportChoices(ExportOptions.Readable, false, false), ExportPreference.Read(ConfigPath));
    }

    [Fact]
    public void Saving_KeepsTheRestOfTheConfig()
    {
        File.WriteAllText(ConfigPath, """{ "model": "local", "confirmQuit": false }""");
        ExportPreference.Save(ConfigPath, new ExportChoices(ExportOptions.Full, true, false));

        var root = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.Equal("local", (string?)root["model"]);
        Assert.False((bool)root["confirmQuit"]!);
        Assert.NotNull(root["export"]);
    }
}
