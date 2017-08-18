using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Microsoft.VisualStudio.Services.Agent.Util
{
    public static class IOUtil
    {
        public static string ExeExtension
        {
            get
            {
#if OS_WINDOWS
                return ".exe";
#else
                return string.Empty;
#endif
            }
        }

        public static StringComparison FilePathStringComparison
        {
            get
            {
                switch (Constants.Agent.Platform)
                {
                    case Constants.OSPlatform.Linux:
                        return StringComparison.Ordinal;
                    case Constants.OSPlatform.OSX:
                    case Constants.OSPlatform.Windows:
                        return StringComparison.OrdinalIgnoreCase;
                    default:
                        throw new NotSupportedException(); // Should never reach here.
                }
            }
        }

        public static void SaveObject(object obj, string path)
        {
            File.WriteAllText(path, StringUtil.ConvertToJson(obj), Encoding.UTF8);
        }

        public static T LoadObject<T>(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return StringUtil.ConvertFromJson<T>(json);
        }

        // TODO: Remove all of these directory functions from IOUtil and use IHostContext.GetDirectory(WellKnownDirectory) instead.
        public static string GetBinPath()
        {
            return Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        }

        public static string GetBinPathHash()
        {
            string hashString = GetBinPath().ToLowerInvariant();
            using (SHA256 sha256hash = SHA256.Create())
            {
                byte[] data = sha256hash.ComputeHash(Encoding.UTF8.GetBytes(hashString));
                StringBuilder sBuilder = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {
                    sBuilder.Append(data[i].ToString("x2"));
                }

                string hash = sBuilder.ToString();
                return hash;
            }
        }

        // TODO: Get rid of this too, use host context. Last reference to this is HostTraceContext, not sure how to remove there.
        public static string GetDiagPath()
        {
            return Path.Combine(
                Path.GetDirectoryName(GetBinPath()),
                Constants.Path.DiagDirectory);
        }

        // TODO: Get rid of this too, use host context.
        public static string GetExternalsPath()
        {
            return Path.Combine(
                GetRootPath(),
                Constants.Path.ExternalsDirectory);
        }

        public static string GetRootPath()
        {
            return new DirectoryInfo(GetBinPath()).Parent.FullName;
        }

        public static string GetConfigFilePath()
        {
            return Path.Combine(GetRootPath(), ".agent");
        }

        public static string GetCredFilePath()
        {
            return Path.Combine(GetRootPath(), ".credentials");
        }

        public static string GetServiceConfigFilePath()
        {
            return Path.Combine(GetRootPath(), ".service");
        }

        public static string GetRSACredFilePath()
        {
            return Path.Combine(GetRootPath(), ".credentials_rsaparams");
        }

#if OS_LINUX
        public static string GetAgentCredStoreFilePath()
        {
            return Path.Combine(GetRootPath(), ".credential_store");
        }
#endif
        public static string GetAgentCertificateSettingFilePath()
        {
            return Path.Combine(GetRootPath(), ".certificates");
        }

        public static string GetProxyConfigFilePath()
        {
            return Path.Combine(GetRootPath(), ".proxy");
        }

        public static string GetProxyCredentialsFilePath()
        {
            return Path.Combine(GetRootPath(), ".proxycredentials");
        }

        public static string GetProxyBypassFilePath()
        {
            return Path.Combine(GetRootPath(), ".proxybypass");
        }

        public static string GetAutoLogonSettingsFilePath()
        {
            return Path.Combine(GetRootPath(), ".autologon");
        }

        public static string GetWorkPath(IHostContext hostContext)
        {
            var configurationStore = hostContext.GetService<IConfigurationStore>();
            AgentSettings settings = configurationStore.GetSettings();
            return Path.Combine(
                Path.GetDirectoryName(GetBinPath()),
                settings.WorkFolder);
        }

        public static string GetTasksPath(IHostContext hostContext)
        {
            return Path.Combine(
                GetWorkPath(hostContext),
                Constants.Path.TasksDirectory);
        }

        public static void Delete(string path, CancellationToken cancellationToken)
        {
            DeleteDirectory(path, cancellationToken);
            DeleteFile(path);
        }

        public static string GetUpdatePath(IHostContext hostContext)
        {
            return Path.Combine(
                GetWorkPath(hostContext),
                Constants.Path.UpdateDirectory);
        }

        public static void DeleteDirectory(string path, CancellationToken cancellationToken)
        {
            DeleteDirectory(path, contentsOnly: false, continueOnContentDeleteError: false, cancellationToken: cancellationToken);
        }

        public static void DeleteDirectory(string path, bool contentsOnly, bool continueOnContentDeleteError, CancellationToken cancellationToken)
        {
            ArgUtil.NotNullOrEmpty(path, nameof(path));
            DirectoryInfo directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                return;
            }

            if (!contentsOnly)
            {
                // Remove the readonly flag.
                RemoveReadOnly(directory);

                // Check if the directory is a reparse point.
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Delete the reparse point directory and short-circuit.
                    directory.Delete();
                    return;
                }
            }

            // Initialize a concurrent stack to store the directories. The directories
            // cannot be deleted until the files are deleted.
            var directories = new ConcurrentStack<DirectoryInfo>();

            if (!contentsOnly)
            {
                directories.Push(directory);
            }

            // Create a new token source for the parallel query. The parallel query should be
            // canceled after the first error is encountered. Otherwise the number of exceptions
            // could get out of control for a large directory with access denied on every file.
            using (var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    // Recursively delete all files and store all subdirectories.
                    Enumerate(directory, tokenSource)
                        .AsParallel()
                        .WithCancellation(tokenSource.Token)
                        .ForAll((FileSystemInfo item) =>
                        {
                            bool success = false;
                            try
                            {
                                // Remove the readonly attribute.
                                RemoveReadOnly(item);

                                // Check if the item is a file.
                                if (item is FileInfo)
                                {
                                    // Delete the file.
                                    item.Delete();
                                }
                                else
                                {
                                    // Check if the item is a directory reparse point.
                                    var subdirectory = item as DirectoryInfo;
                                    ArgUtil.NotNull(subdirectory, nameof(subdirectory));
                                    if (subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                    {
                                        try
                                        {
                                            // Delete the reparse point.
                                            subdirectory.Delete();
                                        }
                                        catch (DirectoryNotFoundException)
                                        {
                                            // The target of the reparse point directory has been deleted.
                                            // Therefore the item is no longer a directory and is now a file.
                                            //
                                            // Deletion of reparse point directories happens in parallel. This case can occur
                                            // when reparse point directory FOO points to some other reparse point directory BAR,
                                            // and BAR is deleted after the DirectoryInfo for FOO has already been initialized.
                                            File.Delete(subdirectory.FullName);
                                        }
                                    }
                                    else
                                    {
                                        // Store the directory.
                                        directories.Push(subdirectory);
                                    }
                                }

                                success = true;
                            }
                            catch (Exception) when (continueOnContentDeleteError)
                            {
                                // ignore any exception when continueOnContentDeleteError is true.
                                success = true;
                            }
                            finally
                            {
                                if (!success)
                                {
                                    tokenSource.Cancel(); // Cancel is thread-safe.
                                }
                            }
                        });
                }
                catch (Exception)
                {
                    tokenSource.Cancel();
                    throw;
                }
            }

            // Delete the directories.
            foreach (DirectoryInfo dir in directories.OrderByDescending(x => x.FullName.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                dir.Delete();
            }
        }

        public static void DeleteFile(string path)
        {
            ArgUtil.NotNullOrEmpty(path, nameof(path));
            var file = new FileInfo(path);
            if (file.Exists)
            {
                RemoveReadOnly(file);
                file.Delete();
            }
        }

        /// <summary>
        /// Given a path and directory, return the path relative to the directory.  If the path is not
        /// under the directory the path is returned un modified.  Examples:
        /// MakeRelative(@"d:\src\project\foo.cpp", @"d:\src") -> @"project\foo.cpp"
        /// MakeRelative(@"d:\src\project\foo.cpp", @"d:\specs") -> @"d:\src\project\foo.cpp"
        /// MakeRelative(@"d:\src\project\foo.cpp", @"d:\src\proj") -> @"d:\src\project\foo.cpp"
        /// </summary>
        /// <remarks>Safe for remote paths.  Does not access the local disk.</remarks>
        /// <param name="path">Path to make relative.</param>
        /// <param name="folder">Folder to make it relative to.</param>
        /// <returns>Relative path.</returns>
        public static string MakeRelative(string path, string folder)
        {
            ArgUtil.NotNullOrEmpty(path, nameof(path));
            ArgUtil.NotNull(folder, nameof(folder));

            // Replace all Path.AltDirectorySeparatorChar with Path.DirectorySeparatorChar from both inputs
            path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            folder = folder.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            // Check if the dir is a prefix of the path (if not, it isn't relative at all).
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            // Dir is a prefix of the path, if they are the same length then the relative path is empty.
            if (path.Length == folder.Length)
            {
                return string.Empty;
            }

            // If the dir ended in a '\\' (like d:\) or '/' (like user/bin/)  then we have a relative path.
            if (folder.Length > 0 && folder[folder.Length - 1] == Path.DirectorySeparatorChar)
            {
                return path.Substring(folder.Length);
            }
            // The next character needs to be a '\\' or they aren't really relative.
            else if (path[folder.Length] == Path.DirectorySeparatorChar)
            {
                return path.Substring(folder.Length + 1);
            }
            else
            {
                return path;
            }
        }

        public static void CopyDirectory(string source, string target, CancellationToken cancellationToken)
        {
            // Validate args.
            ArgUtil.Directory(source, nameof(source));
            ArgUtil.NotNullOrEmpty(target, nameof(target));
            ArgUtil.NotNull(cancellationToken, nameof(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            // Create the target directory.
            Directory.CreateDirectory(target);

            // Get the file contents of the directory to copy.
            DirectoryInfo sourceDir = new DirectoryInfo(source);
            foreach (FileInfo sourceFile in sourceDir.GetFiles() ?? new FileInfo[0])
            {
                // Check if the file already exists.
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo targetFile = new FileInfo(Path.Combine(target, sourceFile.Name));
                if (!targetFile.Exists ||
                    sourceFile.Length != targetFile.Length ||
                    sourceFile.LastWriteTime != targetFile.LastWriteTime)
                {
                    // Copy the file.
                    sourceFile.CopyTo(targetFile.FullName, true);
                }
            }

            // Copy the subdirectories.
            foreach (DirectoryInfo subDir in sourceDir.GetDirectories() ?? new DirectoryInfo[0])
            {
                CopyDirectory(
                    source: subDir.FullName,
                    target: Path.Combine(target, subDir.Name),
                    cancellationToken: cancellationToken);
            }
        }

        public static void ValidateExecutePermission(string directory)
        {
            ArgUtil.Directory(directory, nameof(directory));
            string dir = directory;
            string failsafeString = Environment.GetEnvironmentVariable("AGENT_TEST_VALIDATE_EXECUTE_PERMISSIONS_FAILSAFE");
            int failsafe;
            if (string.IsNullOrEmpty(failsafeString) || !int.TryParse(failsafeString, out failsafe))
            {
                failsafe = 100;
            }

            for (int i = 0; i < failsafe; i++)
            {
                try
                {
                    Directory.EnumerateFileSystemEntries(dir).FirstOrDefault();
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Permission to read the directory contents is required for '{0}' and each directory up the hierarchy. {1}
                    string message = StringUtil.Loc("DirectoryHierarchyUnauthorized", directory, ex.Message);
                    throw new UnauthorizedAccessException(message, ex);
                }

                dir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir))
                {
                    return;
                }
            }

            // This should never happen.
            throw new NotSupportedException($"Unable to validate execute permissions for directory '{directory}'. Exceeded maximum iterations.");
        }

        /// <summary>
        /// Recursively enumerates a directory without following directory reparse points.
        /// </summary>
        private static IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo directory, CancellationTokenSource tokenSource)
        {
            ArgUtil.NotNull(directory, nameof(directory));
            ArgUtil.Equal(false, directory.Attributes.HasFlag(FileAttributes.ReparsePoint), nameof(directory.Attributes.HasFlag));

            // Push the directory onto the processing stack.
            var directories = new Stack<DirectoryInfo>(new[] { directory });
            while (directories.Count > 0)
            {
                // Pop the next directory.
                directory = directories.Pop();
                foreach (FileSystemInfo item in directory.GetFileSystemInfos())
                {
                    // Push non-reparse-point directories onto the processing stack.
                    directory = item as DirectoryInfo;
                    if (directory != null &&
                        !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        directories.Push(directory);
                    }

                    // Then yield the directory. Otherwise there is a race condition when this method attempts to initialize
                    // the Attributes and the caller is deleting the reparse point in parallel (FileNotFoundException).
                    yield return item;
                }
            }
        }

        private static void RemoveReadOnly(FileSystemInfo item)
        {
            ArgUtil.NotNull(item, nameof(item));
            if (item.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                item.Attributes = item.Attributes & ~FileAttributes.ReadOnly;
            }
        }
    }
}