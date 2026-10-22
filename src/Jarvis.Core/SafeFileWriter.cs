using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Jarvis.Core;

/// <summary>
/// Writes to the resolved, policy-approved destination through the exact file handle that was
/// checked. The open does not follow a final-component reparse point, and parent directory handles
/// stay open without delete sharing until the write completes.
/// </summary>
internal static class SafeFileWriter
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileShareReadWrite = (uint)(FileShare.Read | FileShare.Write);
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    public static void WriteAllText(string requestedPath, string content, string approvedTargetPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Safe file writes require Windows file handles.");

        RejectDevicePath(requestedPath);
        var currentTarget = NormalizePath(PolicyEngine.ResolveWriteTarget(requestedPath));
        var approvedTarget = NormalizePath(approvedTargetPath);
        if (!SamePath(currentTarget, approvedTarget))
            throw new IOException("The destination changed after policy approval. No file was written.");

        var name = Path.GetFileName(approvedTarget);
        if (string.IsNullOrWhiteSpace(name) || name.Contains(':'))
            throw new IOException("The destination must be a regular file path, not a drive or alternate data stream.");

        var parent = Path.GetDirectoryName(approvedTarget);
        if (string.IsNullOrWhiteSpace(parent))
            throw new IOException("The destination has no parent directory.");

        var directoryHandles = OpenOrCreateAndPinDirectories(parent);
        try
        {
            using var file = CreateFile(
                approvedTarget,
                GenericWrite | FileReadAttributes,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenAlways,
                FileFlagOpenReparsePoint | FileAttributeNormal,
                IntPtr.Zero);
            if (file.IsInvalid) throw LastError("Could not safely open the approved file");

            var info = GetAttributes(file);
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new IOException("The destination became a link or reparse point. No file was written.");
            if ((info.FileAttributes & FileAttributeDirectory) != 0)
                throw new IOException("The destination is a directory, not a file.");

            var actualPath = NormalizePath(GetFinalPath(file));
            if (!SamePath(actualPath, approvedTarget))
                throw new IOException("The opened file is not the approved destination. No file was written.");

            // OpenAlways never truncates. Only after the handle's attributes and resolved path
            // match the policy snapshot do we clear and write through that same handle.
            using var stream = new FileStream(file, FileAccess.Write, bufferSize: 4096, isAsync: false);
            stream.SetLength(0);
            stream.Position = 0;
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            for (var i = directoryHandles.Count - 1; i >= 0; i--)
                directoryHandles[i].Dispose();
        }
    }

    private static List<SafeFileHandle> OpenOrCreateAndPinDirectories(string path)
    {
        var fullPath = NormalizePath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("The destination is not a rooted Windows path.");

        var handles = new List<SafeFileHandle>();
        try
        {
            var current = NormalizePath(root);
            handles.Add(OpenDirectory(current, current));

            var relative = Path.GetRelativePath(root, fullPath);
            if (relative == ".") return handles;

            foreach (var segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(current, segment);
                var handle = TryOpenDirectory(next, out var error);
                if (handle is null && error is ErrorFileNotFound or ErrorPathNotFound)
                {
                    if (!CreateDirectory(next, IntPtr.Zero))
                    {
                        error = Marshal.GetLastWin32Error();
                        if (error is not (ErrorFileExists or ErrorAlreadyExists))
                            throw new Win32Exception(error, $"Could not create directory '{next}'");
                    }
                    handle = TryOpenDirectory(next, out error);
                }

                if (handle is null)
                    throw new Win32Exception(error, $"Could not safely open directory '{next}'");

                try
                {
                    ValidateDirectoryHandle(handle, next);
                    handles.Add(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
                current = next;
            }

            return handles;
        }
        catch
        {
            for (var i = handles.Count - 1; i >= 0; i--)
                handles[i].Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path, string expectedPath)
    {
        var handle = TryOpenDirectory(path, out var error);
        if (handle is null) throw new Win32Exception(error, $"Could not safely open directory '{path}'");
        try
        {
            ValidateDirectoryHandle(handle, expectedPath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? TryOpenDirectory(string path, out int error)
    {
        var handle = CreateFile(
            path,
            FileReadAttributes | Synchronize,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            error = 0;
            return handle;
        }

        error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return null;
    }

    private static void ValidateDirectoryHandle(SafeFileHandle handle, string expectedPath)
    {
        var info = GetAttributes(handle);
        if ((info.FileAttributes & FileAttributeDirectory) == 0)
            throw new IOException($"'{expectedPath}' is not a directory.");
        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new IOException($"'{expectedPath}' is a link or reparse point. No file was written.");

        var actualPath = NormalizePath(GetFinalPath(handle));
        if (!SamePath(actualPath, expectedPath))
            throw new IOException($"Directory '{expectedPath}' resolved to a different path. No file was written.");
    }

    private static FileAttributeTagInfo GetAttributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo,
                out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw LastError("Could not inspect the opened file handle");
        return info;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw LastError("Could not resolve the opened file handle");
        if (length >= buffer.Capacity) throw new IOException("The resolved destination path is too long.");
        return buffer.ToString();
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            path = path[4..];

        RejectDevicePath(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void RejectDevicePath(string path)
    {
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Device namespace paths cannot be written by this tool.");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static Win32Exception LastError(string operation) =>
        new(Marshal.GetLastWin32Error(), operation);
}
