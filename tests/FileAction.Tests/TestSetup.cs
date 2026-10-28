using System.Runtime.CompilerServices;
using FileAction.Core;

namespace FileAction.Tests;

internal static class TestSetup
{
    /// <summary>Tests assert Czech messages and file names, whatever the machine's language.</summary>
    [ModuleInitializer]
    internal static void Init() => Loc.Current = Language.Czech;
}

[CollectionDefinition(nameof(LanguageSwitching), DisableParallelization = true)]
public class LanguageSwitching;

[Collection(nameof(LanguageSwitching))]
public class LocalizationTests
{
    [Fact]
    public void English_labels_and_output_names()
    {
        try
        {
            Loc.Current = Language.English;
            var ops = FileAction.Core.Operations.Catalog.Build();
            Assert.Equal("Compress", ops.Single(o => o.Id == "image.compress").Label);
            Assert.Equal("Trim", ops.Single(o => o.Id == "video.trim").Label);
            Assert.Equal("3 items", Loc.Items(3));
            Assert.DoesNotContain(ops, o => o.Label.Any(c => "ěščřžýáíéůúťďňĚŠČŘŽÝÁÍÉŮÚŤĎŇ".Contains(c)));
            Assert.DoesNotContain(ops, o => (o.Subtitle ?? "").Any(c => "ěščřžýáíéůúťďňĚŠČŘŽÝÁÍÉŮÚŤĎŇ".Contains(c)));
        }
        finally
        {
            Loc.Current = Language.Czech;
        }
    }

    [Fact]
    public void Czech_plurals()
    {
        Assert.Equal("1 položka", Loc.Items(1));
        Assert.Equal("3 položky", Loc.Items(3));
        Assert.Equal("5 položek", Loc.Items(5));
    }
}
