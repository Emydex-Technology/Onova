﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Onova.Exceptions;
using Onova.Internal;
using Onova.Models;
using Onova.Services;

#if NETSTANDARD2_0
using System.Runtime.InteropServices;
#endif

namespace Onova
{
    /// <summary>
    /// Entry point for handling application updates.
    /// </summary>
    public class UpdateManager : IUpdateManager
    {
        private const string UpdaterResourceName = "Onova.Updater.exe";

        private readonly AssemblyMetadata _updatee;
        private readonly IPackageResolver _resolver;
        private readonly IPackageExtractor _extractor;

        private readonly string _storageDirPath;
        private readonly string _updaterFilePath;
        private readonly string _lockFilePath;

        private LockFile _lockFile;
        private bool _isDisposed;

        /// <summary>
        /// Initializes an instance of <see cref="UpdateManager"/>.
        /// </summary>
        public UpdateManager(AssemblyMetadata updatee, IPackageResolver resolver, IPackageExtractor extractor)
        {
#if NETSTANDARD2_0
            // Ensure that this is only used on Windows
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("Onova only supports Windows.");
#endif

            _updatee = updatee.GuardNotNull(nameof(updatee));
            _resolver = resolver.GuardNotNull(nameof(resolver));
            _extractor = extractor.GuardNotNull(nameof(extractor));

            // Set storage directory path
            _storageDirPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Onova", _updatee.Name);

            // Set updater executable file path
            _updaterFilePath = Path.Combine(_storageDirPath, $"{_updatee.Name}.Updater.exe");

            // Set lock file path
            _lockFilePath = Path.Combine(_storageDirPath, "Onova.lock");
        }

        /// <summary>
        /// Initializes an instance of <see cref="UpdateManager"/> on the entry assembly.
        /// </summary>
        public UpdateManager(IPackageResolver resolver, IPackageExtractor extractor)
            : this(AssemblyMetadata.FromEntryAssembly(), resolver, extractor)
        {
        }

        private string GetPackageFilePath(Version version) => Path.Combine(_storageDirPath, $"{version}.onv");

        private string GetPackageContentDirPath(Version version) => Path.Combine(_storageDirPath, $"{version}");

        private void EnsureNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        private void EnsureLockFileAcquired()
        {
            // Ensure storage directory exists
            Directory.CreateDirectory(_storageDirPath);

            // Try to acquire lock file if it's not acquired yet
            _lockFile = _lockFile ?? LockFile.TryAcquire(_lockFilePath);

            // If failed to acquire - throw
            if (_lockFile == null)
                throw new LockFileNotAcquiredException();
        }

        private void EnsureUpdaterNotLaunched()
        {
            // Check whether we have write access to updater executable
            // (this is a reasonably accurate check for whether that process is running)
            if (File.Exists(_updaterFilePath) && !FileEx.CheckWriteAccess(_updaterFilePath))
                throw new UpdaterAlreadyLaunchedException();
        }

        private void EnsureUpdatePrepared(Version version)
        {
            if (!IsUpdatePrepared(version))
                throw new UpdateNotPreparedException(version);
        }

        /// <inheritdoc />
        [NotNull]
        public async Task<CheckForUpdatesResult> CheckForUpdatesAsync()
        {
            // Ensure that the current state is valid for this operation
            EnsureNotDisposed();

            // Get versions
            var versions = await _resolver.GetVersionsAsync().ConfigureAwait(false);
            var lastVersion = versions.Max();
            var canUpdate = lastVersion != null && _updatee.Version < lastVersion;

            return new CheckForUpdatesResult(versions, lastVersion, canUpdate);
        }

        /// <inheritdoc />
        public bool IsUpdatePrepared(Version version)
        {
            version.GuardNotNull(nameof(version));

            // Ensure that the current state is valid for this operation
            EnsureNotDisposed();

            // Get package file path and content directory path
            var packageFilePath = GetPackageFilePath(version);
            var packageContentDirPath = GetPackageContentDirPath(version);

            // Package content directory should exist
            // Package file should have been deleted after extraction
            // Updater file should exist
            return !File.Exists(packageFilePath) &&
                   Directory.Exists(packageContentDirPath) &&
                   File.Exists(_updaterFilePath);
        }

        /// <inheritdoc />
        public async Task PrepareUpdateAsync(Version version,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            version.GuardNotNull(nameof(version));

            // Ensure that the current state is valid for this operation
            EnsureNotDisposed();
            EnsureLockFileAcquired();
            EnsureUpdaterNotLaunched();

            // Set up progress mixer
            var progressMixer = progress != null ? new ProgressMixer(progress) : null;

            // Get package file path and content directory path
            var packageFilePath = GetPackageFilePath(version);
            var packageContentDirPath = GetPackageContentDirPath(version);

            // Ensure storage directory exists
            Directory.CreateDirectory(_storageDirPath);

            // Download package
            await _resolver.DownloadAsync(version, packageFilePath,
                progressMixer?.Split(0.9), // 0% -> 90%
                cancellationToken).ConfigureAwait(false);

            // Ensure package content directory exists and is empty
            DirectoryEx.Reset(packageContentDirPath);

            // Extract package contents
            await _extractor.ExtractAsync(packageFilePath, packageContentDirPath,
                progressMixer?.Split(0.1), // 90% -> 100%
                cancellationToken).ConfigureAwait(false);

            // Delete package
            File.Delete(packageFilePath);

            // Extract updater
            await Assembly.GetExecutingAssembly().ExtractManifestResourceAsync(UpdaterResourceName, _updaterFilePath)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void LaunchUpdater(Version version, bool restart = true)
        {
            version.GuardNotNull(nameof(version));

            // Ensure that the current state is valid for this operation
            EnsureNotDisposed();
            EnsureLockFileAcquired();
            EnsureUpdaterNotLaunched();
            EnsureUpdatePrepared(version);

            // Get package content directory path
            var packageContentDirPath = GetPackageContentDirPath(version);

            // Prepare arguments
            var updaterArgs = $"\"{_updatee.FilePath}\" \"{packageContentDirPath}\" {restart}";

            // Decide if updater needs to be elevated
            var updateeDirPath = Path.GetDirectoryName(_updatee.FilePath);
            var isUpdaterElevated = !DirectoryEx.CheckWriteAccess(updateeDirPath);

            // Create updater process start info
            var updaterStartInfo = new ProcessStartInfo
            {
                FileName = _updaterFilePath,
                Arguments = updaterArgs,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            // If updater needs to be elevated - use shell execute with "runas"
            if (isUpdaterElevated)
            {
                updaterStartInfo.Verb = "runas";
                updaterStartInfo.UseShellExecute = true;
            }

            // Create and start updater process
            var updaterProcess = new Process {StartInfo = updaterStartInfo};
            using (updaterProcess)
                updaterProcess.Start();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _lockFile?.Dispose();
            }
        }
    }
}