using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Extensions;
using Raven.Client.Util;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.PeriodicBackup.Aws;
using Raven.Server.Documents.PeriodicBackup.Azure;
using Raven.Server.Documents.PeriodicBackup.GoogleCloud;
using Raven.Server.Documents.PeriodicBackup.Retention;
using Raven.Server.ServerWide;
using Raven.Server.Utils;
using Raven.Server.Utils.Metrics;
using Sparrow;
using Sparrow.Collections;
using Sparrow.Logging;
using Sparrow.Utils;
using Size = Sparrow.Size;

namespace Raven.Server.Documents.PeriodicBackup
{
    public class BackupUploader
    {
        private readonly BackupUploaderSettings _settings;
        private readonly List<PoolOfThreads.LongRunningWork> _threads;
        private readonly ConcurrentSet<Exception> _exceptions;

        private readonly RetentionPolicyBaseParameters _retentionPolicyParameters;
        private readonly bool _isFullBackup;

        public readonly OperationCancelToken TaskCancelToken;

        private readonly Logger _logger;
        private readonly BackupResult _backupResult;
        private readonly Action<IOperationProgress> _onProgress;

        private const string AzureName = "Azure";
        private const string S3Name = "S3";
        private const string GlacierName = "Glacier";
        private const string GoogleCloudName = "Google Cloud";
        private const string FtpName = "FTP";

        public BackupUploader(BackupUploaderSettings settings, RetentionPolicyBaseParameters retentionPolicyParameters, Logger logger, BackupResult backupResult, Action<IOperationProgress> onProgress, OperationCancelToken taskCancelToken)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _threads = new List<PoolOfThreads.LongRunningWork>();
            _exceptions = new ConcurrentSet<Exception>();

            _retentionPolicyParameters = retentionPolicyParameters;
            _isFullBackup = retentionPolicyParameters.IsFullBackup;

            TaskCancelToken = taskCancelToken;
            _logger = logger;
            _backupResult = backupResult;
            _onProgress = onProgress;
        }

        public void Execute()
        {
            CreateUploadTaskIfNeeded(_settings.S3Settings, UploadToS3, _backupResult.S3Backup, S3Name);
            CreateUploadTaskIfNeeded(_settings.GlacierSettings, UploadToGlacier, _backupResult.GlacierBackup, GlacierName);
            CreateUploadTaskIfNeeded(_settings.AzureSettings, UploadToAzure, _backupResult.AzureBackup, AzureName);
            CreateUploadTaskIfNeeded(_settings.GoogleCloudSettings, UploadToGoogleCloud, _backupResult.GoogleCloudBackup, GoogleCloudName);
            CreateUploadTaskIfNeeded(_settings.FtpSettings, UploadToFtp, _backupResult.FtpBackup, FtpName);

            _threads.ForEach(x => x.Join(int.MaxValue));

            if (_exceptions.Count > 0)
            {
                if (_exceptions.Count == 1)
                    throw _exceptions.First();

                if (_exceptions.All(x => x is OperationCanceledException))
                    throw _exceptions.First();

                throw new AggregateException(_exceptions);
            }
        }

        private void UploadToS3(S3Settings settings, Stream stream, Progress progress)
        {
            using (var client = new RavenAwsS3Client(settings, _settings.Configuration, progress, TaskCancelToken.Token))
            {
                var key = CombinePathAndKey(settings.RemoteFolderName);
                client.PutObject(key, stream, new Dictionary<string, string>
                {
                    { "Description", GetArchiveDescription() }
                });

                if (_logger.IsInfoEnabled)
                    _logger.Info($"{ReportSuccess(S3Name)} bucket named: {settings.BucketName}, with key: {key}");

                var runner = new S3RetentionPolicyRunner(_retentionPolicyParameters, client);
                runner.Execute();
            }
        }

        private void UploadToGlacier(GlacierSettings settings, Stream stream, Progress progress)
        {
            using (var client = new RavenAwsGlacierClient(settings, _settings.Configuration, progress, TaskCancelToken.Token))
            {
                var key = CombinePathAndKey(settings.RemoteFolderName ?? _settings.DatabaseName);
                var archiveId = client.UploadArchive(stream, key);
                if (_logger.IsInfoEnabled)
                    _logger.Info($"{ReportSuccess(GlacierName)}, archive ID: {archiveId}");

                var runner = new GlacierRetentionPolicyRunner(_retentionPolicyParameters, client);
                runner.Execute();
            }
        }

        private void UploadToFtp(FtpSettings settings, Stream stream, Progress progress)
        {
            using (var client = new RavenFtpClient(settings, progress, TaskCancelToken.Token))
            {
                client.UploadFile(_settings.FolderName, _settings.FileName, stream);

                if (_logger.IsInfoEnabled)
                    _logger.Info($"{ReportSuccess(FtpName)} server");

                var runner = new FtpRetentionPolicyRunner(_retentionPolicyParameters, client);
                runner.Execute();
            }
        }

        private void UploadToAzure(AzureSettings settings, Stream stream, Progress progress)
        {
            using (var client = RavenAzureClient.Create(settings, _settings.Configuration, progress, TaskCancelToken.Token))
            {
                var key = CombinePathAndKey(settings.RemoteFolderName);
                client.PutBlob(key, stream, new Dictionary<string, string>
                {
                    { "Description", GetArchiveDescription() }
                });

                if (_logger.IsInfoEnabled)
                    _logger.Info($"{ReportSuccess(AzureName)} container: {settings.StorageContainer}, with key: {key}");

                var runner = new AzureRetentionPolicyRunner(_retentionPolicyParameters, client);
                runner.Execute();
            }
        }

        private void UploadToGoogleCloud(GoogleCloudSettings settings, Stream stream, Progress progress)
        {
            using (var client = new RavenGoogleCloudClient(settings, _settings.Configuration, progress, TaskCancelToken.Token))
            {
                var key = CombinePathAndKey(settings.RemoteFolderName);
                client.UploadObject(key, stream, new Dictionary<string, string>
                {
                    { "Description", GetArchiveDescription() }
                });

                if (_logger.IsInfoEnabled)
                    _logger.Info($"{ReportSuccess(GoogleCloudName)} storage bucket: {settings.BucketName}");

                var runner = new GoogleCloudRetentionPolicyRunner(_retentionPolicyParameters, client);
                runner.Execute();
            }
        }

        private string CombinePathAndKey(string path)
        {
            var prefix = string.IsNullOrWhiteSpace(path) == false ? $"{path}/" : string.Empty;
            return $"{prefix}{_settings.FolderName}/{_settings.FileName}";
        }

        private void CreateUploadTaskIfNeeded<S, T>(S settings, Action<S, FileStream, Progress> uploadToServer, T uploadStatus, string targetName)
            where S : BackupSettings
            where T : CloudUploadStatus
        {
            if (Client.Documents.Operations.Backups.BackupConfiguration.CanBackupUsing(settings) == false)
                return;

            Debug.Assert(uploadStatus != null);

            var localUploadStatus = uploadStatus;
            var thread = PoolOfThreads.GlobalRavenThreadPool.LongRunning(_ =>
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    NativeMemory.EnsureRegistered();

                    using (localUploadStatus.UpdateStats(_isFullBackup))
                    using (var fileStream = File.OpenRead(_settings.BackupPath))
                    {
                        var uploadProgress = localUploadStatus.UploadProgress;
                        try
                        {
                            localUploadStatus.Skipped = false;
                            uploadProgress.ChangeState(UploadState.PendingUpload);
                            uploadProgress.SetTotal(fileStream.Length);

                            AddInfo($"Starting the upload of backup file to {targetName}.");

                            var bytesPutsPerSec = new MeterMetric();

                            long lastUploadedInBytes = 0;
                            var totalToUpload = new Size(uploadProgress.TotalInBytes, SizeUnit.Bytes).ToString();
                            var sw = Stopwatch.StartNew();
                            var progress = new Progress(uploadProgress)
                            {
                                OnUploadProgress = () =>
                                {
                                    if (sw.ElapsedMilliseconds <= 1000)
                                        return;

                                    var totalUploadedInBytes = uploadProgress.UploadedInBytes;
                                    bytesPutsPerSec.MarkSingleThreaded(totalUploadedInBytes - lastUploadedInBytes);
                                    lastUploadedInBytes = totalUploadedInBytes;
                                    var uploaded = new Size(totalUploadedInBytes, SizeUnit.Bytes);
                                    uploadProgress.BytesPutsPerSec = bytesPutsPerSec.MeanRate;
                                    AddInfo($"Uploaded: {uploaded} / {totalToUpload}");
                                    sw.Restart();
                                }
                            };

                            uploadToServer(settings, fileStream, progress);

                            AddInfo($"Total uploaded: {totalToUpload}, took: {MsToHumanReadableString(uploadProgress.UploadTimeInMs)}");
                        }
                        finally
                        {
                            uploadProgress.ChangeState(UploadState.Done);
                        }
                    }
                }
                catch (Exception e)
                {
                    var extracted = e.ExtractSingleInnerException();
                    var error = $"Failed to upload the backup file to {targetName}.";
                    Exception exception = null;
                    if (extracted is OperationCanceledException)
                    {
                        // shutting down or HttpClient timeout
                        exception = TaskCancelToken.Token.IsCancellationRequested ? extracted : new TimeoutException(error, e);
                    }

                    localUploadStatus.Exception = (exception ?? e).ToString();
                    _exceptions.Add(exception ?? new InvalidOperationException(error, e));
                }
            }, null, $"Upload backup file of database '{_settings.DatabaseName}' to {targetName} (task: '{_settings.TaskName}')");

            _threads.Add(thread);
        }

        private void AddInfo(string message)
        {
            _backupResult.AddInfo(message);
            _onProgress.Invoke(_backupResult.Progress);
        }

        private string GetArchiveDescription()
        {
            var fullBackupText = _settings.BackupType == BackupType.Backup ? "Full backup" : "A snapshot";
            return $"{(_isFullBackup ? fullBackupText : "Incremental backup")} for db {_settings.DatabaseName} at {SystemTime.UtcNow}";
        }

        private static string MsToHumanReadableString(long milliseconds)
        {
            var durationsList = new List<string>();
            var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
            var totalDays = (int)timeSpan.TotalDays;
            if (totalDays >= 1)
            {
                durationsList.Add($"{totalDays:#,#;;0} day{Pluralize(totalDays)}");
                timeSpan = timeSpan.Add(TimeSpan.FromDays(-1 * totalDays));
            }

            var totalHours = (int)timeSpan.TotalHours;
            if (totalHours >= 1)
            {
                durationsList.Add($"{totalHours:#,#;;0} hour{Pluralize(totalHours)}");
                timeSpan = timeSpan.Add(TimeSpan.FromHours(-1 * totalHours));
            }

            var totalMinutes = (int)timeSpan.TotalMinutes;
            if (totalMinutes >= 1)
            {
                durationsList.Add($"{totalMinutes:#,#;;0} minute{Pluralize(totalMinutes)}");
                timeSpan = timeSpan.Add(TimeSpan.FromMinutes(-1 * totalMinutes));
            }

            var totalSeconds = (int)timeSpan.TotalSeconds;
            if (totalSeconds >= 1)
            {
                durationsList.Add($"{totalSeconds:#,#;;0} second{Pluralize(totalSeconds)}");
                timeSpan = timeSpan.Add(TimeSpan.FromSeconds(-1 * totalSeconds));
            }

            var totalMilliseconds = (int)timeSpan.TotalMilliseconds;
            if (totalMilliseconds > 0)
            {
                durationsList.Add($"{totalMilliseconds:#,#;;0} ms");
            }

            return string.Join(' ', durationsList.Take(2));
        }

        private static string Pluralize(int number)
        {
            return number > 1 ? "s" : string.Empty;
        }

        private string ReportSuccess(string name)
        {
            return $"Successfully uploaded backup file '{_settings.FileName}' to {name}";
        }
    }

    public class BackupUploaderSettings
    {
        public readonly Config.Categories.BackupConfiguration Configuration;

        public BackupUploaderSettings(Config.Categories.BackupConfiguration configuration)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public S3Settings S3Settings;
        public GlacierSettings GlacierSettings;
        public AzureSettings AzureSettings;
        public GoogleCloudSettings GoogleCloudSettings;
        public FtpSettings FtpSettings;

        public string BackupPath;
        public string FolderName;
        public string FileName;
        public string DatabaseName;
        public string TaskName;

        public BackupType BackupType;
    }
}
