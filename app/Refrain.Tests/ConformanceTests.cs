using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refrain.Core;
using Xunit;

namespace Refrain.Tests;

/// <summary>The C# app's answers for the rewrite (SPEC §38): each case of <c>conformance/cases/&lt;area&gt;.json</c> run
/// through Refrain.Core. <c>REFRAIN_RECORD=1</c> writes them to <c>conformance/answers/&lt;area&gt;.json</c>, in the cases'
/// order, for the Rust core to replay; a normal run compares with them.</summary>
public class ConformanceTests
{
    static readonly JsonSerializerOptions Written = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static string Folder(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "conformance", "cases")))
                return Path.Combine(dir.FullName, "conformance", name);
        throw new DirectoryNotFoundException("conformance/cases wasn't found above the tests.");
    }

    public static TheoryData<string> Areas => new(Directory.GetFiles(Folder("cases"), "*.json")
        .Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal));

    [Theory, MemberData(nameof(Areas))]
    public void Answers(string area)
    {
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder("cases"), area + ".json")))!.AsArray();
        var answers = new JsonObject();
        foreach (var c in cases)
            answers[(string)c!["id"]!] = Answer((string)c["op"]!, c["input"]!.AsObject());

        var path = Path.Combine(Folder("answers"), area + ".json");
        if (PythonApp.Recording)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, answers.ToJsonString(Written) + "\n", new UTF8Encoding(false));
            return;
        }
        var recorded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(recorded.Select(kv => kv.Key), answers.Select(kv => kv.Key));
        foreach (var (id, answer) in answers)
            Assert.True(JsonNode.DeepEquals(answer, recorded[id]),
                $"{area} / {id}: {answer?.ToJsonString()} against the recorded {recorded[id]?.ToJsonString()}");
    }

    static JsonNode Answer(string op, JsonObject input) => op switch
    {
        "formats.classify" => Classify((string)input["ext"]!),
        _ => throw new NotSupportedException($"The C# app has no answer for \"{op}\"."),
    };

    static JsonObject Classify(string ext) => new()
    {
        ["audio"] = Formats.IsAudio(ext),
        ["read"] = Formats.IsRead(ext),
        ["replaygain"] = Formats.ReplayGain.Contains(ext),
        ["label"] = Formats.Label(ext),
    };
}
