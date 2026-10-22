using System.Text;
using Jarvis.Core;
using Xunit;

namespace Jarvis.Tests;

public sealed class SafeFileWriterTests
{
    [Fact]
    public void Writes_utf8_without_bom_and_creates_missing_parent_directories()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-safe-write-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "new", "notes.txt");
        try
        {
            var target = PolicyEngine.ResolveWriteTarget(path);
            SafeFileWriter.WriteAllText(path, "hello — JARVIS", target);

            Assert.Equal("hello — JARVIS", File.ReadAllText(path, Encoding.UTF8));
            Assert.Equal(Encoding.UTF8.GetBytes("hello — JARVIS"), File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Refuses_to_write_when_the_policy_target_does_not_match_the_requested_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-safe-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var requested = Path.Combine(root, "requested.txt");
        var different = Path.Combine(root, "different.txt");
        File.WriteAllText(requested, "keep this");

        try
        {
            Assert.Throws<IOException>(() => SafeFileWriter.WriteAllText(requested, "replace", different));
            Assert.Equal("keep this", File.ReadAllText(requested));
            Assert.False(File.Exists(different));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
