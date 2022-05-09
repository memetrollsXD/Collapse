﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using Newtonsoft.Json;

using Hi3Helper;
using Hi3Helper.Data;
using Hi3Helper.Preset;
using Hi3Helper.Shared.ClassStruct;

using static Hi3Helper.Logger;

using static Hi3Helper.Data.ConverterTool;
using static Hi3Helper.Shared.Region.LauncherConfig;

namespace CollapseLauncher
{
    public class GameConversionManagement : HttpClientHelper
    {
        public event EventHandler<ConvertProgress> ProgressChanged;

        UIElement ParentUI;
        PresetConfigClasses SourceProfile, TargetProfile;
        List<FileProperties> SourceFileManifest;
        List<FileProperties> TargetFileManifest;

        string SourceBaseURL, TargetBaseURL;
        string CookbookURL;
        string CookbookPath;
        Stopwatch ConvertSw;
        CancellationToken Token = new CancellationToken();
        private void ResetSw() => ConvertSw = Stopwatch.StartNew();
        string ConvertStatus, ConvertDetail;

        public GameConversionManagement(PresetConfigClasses SourceProfile, PresetConfigClasses TargetProfile,
            string SourceBaseURL, string TargetBaseURL, string GameVersion, UIElement ParentUI)
        {
            this.SourceProfile = SourceProfile;
            this.TargetProfile = TargetProfile;
            this.SourceBaseURL = SourceBaseURL;
            this.TargetBaseURL = TargetBaseURL;
            this.ParentUI = ParentUI;
            this.CookbookURL = string.Format(SourceProfile.ConvertibleCookbookURL,
                $"Cookbook_{SourceProfile.ProfileName}_{TargetProfile.ProfileName}_{GameVersion}_lzma2_crc32.diff");
            this.CookbookPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Temp",
                $"Cookbook_{SourceProfile.ProfileName}_{TargetProfile.ProfileName}_{GameVersion}_lzma2_crc32.diff");
        }

        public async Task StartPreparation()
        {
            List<FilePropertiesRemote> SourceFileRemote;
            List<FilePropertiesRemote> TargetFileRemote;
            ConvertSw = Stopwatch.StartNew();
            ConvertStatus = "Preparing Ingredients";

            string IngredientsPath = TargetProfile.ActualGameDataLocation + "_Ingredients";

            using (MemoryStream buffer = new MemoryStream())
            {
                ConvertDetail = "Fetching Source API";
                DownloadProgress += FetchIngredientsAPI_Progress;
                await DownloadFileAsync(SourceBaseURL + "index.json", buffer, new CancellationToken());
                DownloadProgress -= FetchIngredientsAPI_Progress;
                SourceFileRemote = JsonConvert.DeserializeObject<List<FilePropertiesRemote>>(Encoding.UTF8.GetString(buffer.ToArray()));
            }
            using (MemoryStream buffer = new MemoryStream())
            {
                ConvertDetail = "Fetching Target API";
                DownloadProgress += FetchIngredientsAPI_Progress;
                await DownloadFileAsync(TargetBaseURL + "index.json", buffer, new CancellationToken());
                DownloadProgress -= FetchIngredientsAPI_Progress;
                TargetFileRemote = JsonConvert.DeserializeObject<List<FilePropertiesRemote>>(Encoding.UTF8.GetString(buffer.ToArray()));
            }

            SourceFileManifest = BuildManifest(SourceFileRemote);
            TargetFileManifest = BuildManifest(TargetFileRemote);
            await Task.Run(() => PrepareIngredients(SourceFileManifest));
            await RepairIngredients(await VerifyIngredients(SourceFileManifest, IngredientsPath), IngredientsPath);
        }

        long MakeIngredientsRead = 0;
        long MakeIngredientsTotalSize = 0;
        private void PrepareIngredients(List<FileProperties> FileManifest)
        {
            ResetSw();
            MakeIngredientsRead = 0;
            MakeIngredientsTotalSize = FileManifest.Sum(x => x.FileSize);

            string InputPath;
            string OutputPath;

            ConvertStatus = "Preparing Ingredients";

            foreach (FileProperties Entry in FileManifest)
            {
                InputPath = Path.Combine(SourceProfile.ActualGameDataLocation, Entry.FileName);
                OutputPath = Path.Combine(TargetProfile.ActualGameDataLocation + "_Ingredients", Entry.FileName);

                if (!Directory.Exists(Path.GetDirectoryName(OutputPath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

                if (File.Exists(InputPath))
                {
                    ConvertDetail = $"Moving: {Entry.FileName} ({Entry.FileSizeStr})";
                    MakeIngredientsRead += Entry.FileSize;
                    File.Move(InputPath, OutputPath, true);
                    UpdateProgress(MakeIngredientsRead, MakeIngredientsTotalSize, 1, 1, ConvertSw.Elapsed, ConvertStatus, ConvertDetail);
                }
                else
                {
                    MakeIngredientsRead += Entry.FileSize;
                    UpdateProgress(MakeIngredientsRead, MakeIngredientsTotalSize, 1, 1, ConvertSw.Elapsed, ConvertStatus, ConvertDetail);
                }
            }
        }

        public async Task PostConversionVerify()
        {
            string TargetPath = TargetProfile.ActualGameDataLocation;
            await RepairIngredients(await VerifyIngredients(TargetFileManifest, TargetPath), TargetPath);
        }

        private async Task<List<FileProperties>> VerifyIngredients(List<FileProperties> FileManifest, string GamePath)
        {
            ResetSw();
            List<FileProperties> BrokenManifest = new List<FileProperties>();
            long CurRead = 0;
            long TotalSize = FileManifest.Sum(x => x.FileSize);
            string LocalHash;
            string OutputPath;

            ConvertStatus = "Verifying Ingredients";
            foreach (FileProperties Entry in FileManifest)
            {
                OutputPath = Path.Combine(GamePath, Entry.FileName);
                ConvertDetail = $"Checking: {Entry.FileName} ({Entry.FileSizeStr})";
                UpdateProgress(CurRead, TotalSize, 1, 1, ConvertSw.Elapsed, ConvertStatus, ConvertDetail);
                if (File.Exists(OutputPath))
                {
                    using (FileStream fs = new FileStream(OutputPath, FileMode.Open, FileAccess.Read))
                    {
                        await Task.Run(() =>
                        {
                            LocalHash = Entry.DataType != FileType.Blocks ?
                                BytesToCRC32Simple(fs) :
                                CreateMD5(fs);

                            if (LocalHash.ToLower() != Entry.CurrCRC)
                                BrokenManifest.Add(Entry);
                        });
                    }
                }
                else
                    BrokenManifest.Add(Entry);
                CurRead += Entry.FileSize;
            }

            return BrokenManifest;
        }

        private async Task Copy(string InputPath, string OutputPath, bool UseCopyTo = true)
        {
            byte[] buffer = new byte[20 << 20];
            using (FileStream source = new FileStream(InputPath, FileMode.Open, FileAccess.Read))
            {
                using (FileStream dest = new FileStream(OutputPath, FileMode.Create, FileAccess.Write))
                {
                    if (UseCopyTo)
                    {
                        await source.CopyToAsync(dest, Token);
                        MakeIngredientsRead += source.Length;
                        UpdateProgress(MakeIngredientsRead, MakeIngredientsTotalSize, 1, 1, ConvertSw.Elapsed, ConvertStatus, ConvertDetail);
                    }
                    else
                    {
                        int read = 0;
                        while ((read = await source.ReadAsync(buffer)) > 0)
                        {
                            Token.ThrowIfCancellationRequested();
                            dest.Write(buffer, 0, read);
                            MakeIngredientsRead += read;
                            UpdateProgress(MakeIngredientsRead, MakeIngredientsTotalSize, 1, 1, ConvertSw.Elapsed, ConvertStatus, ConvertDetail);
                        }
                    }
                }
            }
        }

        private List<FileProperties> BuildManifest(List<FilePropertiesRemote> FileRemote)
        {
            List<FileProperties> _out = new List<FileProperties>();
            
            foreach (FilePropertiesRemote Entry in FileRemote)
            {
                switch (Entry.FT)
                {
                    case FileType.Generic:
                        {
                            _out.Add(new FileProperties
                            {
                                FileName = Entry.N,
                                FileSize = Entry.S,
                                CurrCRC = Entry.CRC,
                                DataType = FileType.Generic
                            });
                        }
                        break;
                    case FileType.Blocks:
                        {
                            _out.AddRange(BuildBlockManifest(Entry.BlkC, Entry.N));
                        }
                        break;
                }
            }

            return _out;
        }

        private List<FileProperties> BuildBlockManifest(List<XMFDictionaryClasses.XMFBlockList> BlockC, string BaseName)
        {
            string Name;
            List<FileProperties> _out = new List<FileProperties>();

            foreach (XMFDictionaryClasses.XMFBlockList Block in BlockC)
            {
                Name = BaseName + "/" + Block.BlockHash + ".wmv";
                _out.Add(new FileProperties
                {
                    FileName = Name,
                    FileSize = Block.BlockSize,
                    CurrCRC = Block.BlockHash
                });
            }

            return _out;
        }

        public async Task StartDownloadRecipe()
        {
            ResetSw();
            ConvertStatus = "Downloading Recipe";
            ConvertDetail = $"Downloading recipe for conversion from {SourceProfile.ZoneName} to {TargetProfile.ZoneName}";

            if (File.Exists(CookbookPath))
                if (new FileInfo(CookbookPath).Length == GetContentLength(CookbookURL))
                    return;

            DownloadProgress += RecipeDownload_Progress;
            await DownloadFileAsync(CookbookURL, CookbookPath, GetAppConfigValue("DownloadThread").ToInt(), Token);
            DownloadProgress -= RecipeDownload_Progress;
        }

        private void RecipeDownload_Progress(object sender, _DownloadProgress e)
        {
            UpdateProgress(e.DownloadedSize, e.TotalSizeToDownload, 1, 1, ConvertSw.Elapsed,
                ConvertStatus, ConvertDetail);
        }

        long RepairRead = 0;
        long RepairTotalSize = 0;
        private async Task RepairIngredients(List<FileProperties> BrokenFile, string GamePath)
        {
            if (BrokenFile.Count == 0) return;

            ResetSw();
            string OutputPath;
            string InputURL;
            RepairTotalSize = BrokenFile.Sum(x => x.FileSize);

            ConvertStatus = "Repairing Ingredients";
            foreach (FileProperties Entry in BrokenFile)
            {
                Token.ThrowIfCancellationRequested();
                OutputPath = Path.Combine(GamePath, Entry.FileName);
                InputURL = SourceBaseURL + Entry.FileName;

                ConvertDetail = $"Downloading: {Entry.FileName} ({Entry.FileSizeStr})";
                if (!Directory.Exists(Path.GetDirectoryName(OutputPath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

                if (File.Exists(OutputPath))
                    File.Delete(OutputPath);

                DownloadProgress += RepairIngredients_Progress;
                if (Entry.FileSize >= 20 << 20)
                    await DownloadFileAsync(InputURL, OutputPath, 8, Token);
                else
                    await DownloadFileAsync(InputURL, new FileStream(OutputPath, FileMode.Create, FileAccess.Write), Token, null, null, true);
                DownloadProgress -= RepairIngredients_Progress;
            }
        }

        private void RepairIngredients_Progress(object sender, _DownloadProgress e)
        {
            if (e.DownloadState != State.Merging)
                RepairRead += e.CurrentRead;
            UpdateProgress(RepairRead, RepairTotalSize, 1, 1, ConvertSw.Elapsed,
                ConvertStatus, ConvertDetail);
        }

        private void FetchIngredientsAPI_Progress(object sender, _DownloadProgress e)
        {
            UpdateProgress(e.DownloadedSize, e.TotalSizeToDownload, 1, 1, ConvertSw.Elapsed,
                ConvertStatus, ConvertDetail);
        }

        FileSystemWatcher ConvertFsWatcher;

        long ConvertRead = 0;
        long ConvertTotalSize = 0;
        public async Task StartConversion()
        {
            ResetSw();

            try
            {
                string IngredientsPath = TargetProfile.ActualGameDataLocation + "_Ingredients";
                string OutputPath = TargetProfile.ActualGameDataLocation;

                if (Directory.Exists(OutputPath))
                    Directory.Delete(OutputPath, true);

                Directory.CreateDirectory(OutputPath);
                ConvertTotalSize = TargetFileManifest.Sum(x => x.FileSize);

                ConvertFsWatcher = new FileSystemWatcher()
                {
                    Path = OutputPath,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.Size,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                ConvertFsWatcher.Created += ConvertFsWatcher_Created;

                await Task.Run(() => new HPatchUtil().HPatchDir(IngredientsPath, CookbookPath, OutputPath));
                Directory.Delete(IngredientsPath, true);
                File.Delete(CookbookPath);
                MoveMiscSourceFiles(SourceProfile.ActualGameDataLocation, OutputPath);
                Directory.Delete(SourceProfile.ActualGameDataLocation, true);

                ConvertFsWatcher.Created -= ConvertFsWatcher_Created;
            }
            catch (Exception ex)
            {
                try
                {
                    RevertBackIngredients(SourceFileManifest);
                }
                catch (Exception exf)
                {
                    LogWriteLine($"Conversion process has failed and sorry, the files can't be reverted back :(\r\nInner Exception: {ex}\r\nReverting Exception: {exf}", LogType.Error, true);
                    throw new Exception($"Conversion process has failed and sorry, the files can't be reverted back :(\r\nInner Exception: {ex}\r\nReverting Exception: {exf}", new Exception($"Inner exception: {ex}", ex));
                }
                LogWriteLine($"Conversion process has failed! But don't worry, the files have been reverted :D\r\n{ex}", LogType.Error, true);
                throw new Exception($"Conversion process has failed! But don't worry, the file have been reverted :D\r\n{ex}", ex);
            }
        }

        private void RevertBackIngredients(List<FileProperties> FileManifest)
        {
            string InputPath, OutputPath;
            foreach (FileProperties Entry in FileManifest)
            {
                InputPath = Path.Combine(SourceProfile.ActualGameDataLocation, Entry.FileName);
                OutputPath = Path.Combine(TargetProfile.ActualGameDataLocation + "_Ingredients", Entry.FileName);

                if (!Directory.Exists(Path.GetDirectoryName(InputPath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(InputPath));

                if (File.Exists(InputPath))
                    File.Move(OutputPath, InputPath, true);
            }
        }

        private void MoveMiscSourceFiles(string InputPath, string OutputPath)
        {
            string OutputFile;
            IEnumerable<string> Files = Directory.EnumerateFiles(InputPath, "*", SearchOption.AllDirectories);
            foreach (string _Entry in Files)
            {
                OutputFile = Path.Combine(OutputPath, _Entry.Substring(InputPath.Length + 1));
                if (!File.Exists(OutputFile))
                {
                    if (!Directory.Exists(Path.GetDirectoryName(OutputFile)))
                        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile));
                    File.Move(_Entry, OutputFile);
                }
            }
        }

        string lastName = null;
        private void ConvertFsWatcher_Created(object sender, FileSystemEventArgs e)
        {
            if (!Directory.Exists(e.FullPath))
            {
                ConvertDetail = $"Converting: {e.Name}";
                UpdateProgress(ConvertRead, ConvertTotalSize, 1, 1, ConvertSw.Elapsed, ConvertStatus, ConvertDetail);
                if (lastName != null)
                    ConvertRead += new FileInfo(lastName).Length;
                lastName = e.FullPath;
            }
        }

        private Stopwatch InnerRefreshSw = Stopwatch.StartNew();
        private void ResetInnerRefreshSw() => InnerRefreshSw = Stopwatch.StartNew();
        public void UpdateProgress(long StartSize, long EndSize, int StartCount, int EndCount,
                TimeSpan TimeSpan, string StatusMsg = "", string DetailMsg = "", bool UseCountUnit = false)
        {
            double Lastms = InnerRefreshSw.Elapsed.TotalMilliseconds;
            if (Lastms >= 33)
            {
                ProgressChanged?.Invoke(this, new ConvertProgress(StartSize, EndSize, StartCount, EndCount,
                    TimeSpan, StatusMsg, DetailMsg, UseCountUnit));
                ResetInnerRefreshSw();
            }
        }

        public class ConvertProgress
        {
            public ConvertProgress(long StartSize, long EndSize, int StartCount, int EndCount,
                TimeSpan TimeSpan, string StatusMsg = "", string DetailMsg = "", bool UseCountUnit = false)
            {
                this.StartSize = StartSize;
                this.EndSize = EndSize;
                this.StartCount = StartCount;
                this.EndCount = EndCount;
                this.UseCountUnit = UseCountUnit;
                this._TimeSecond = TimeSpan.TotalSeconds;
                this._StatusMsg = StatusMsg;
                this._DetailMsg = DetailMsg;
            }

            private double _TimeSecond = 0f;
            private string _StatusMsg = "";
            private string _DetailMsg = "";
            public bool UseCountUnit { get; private set; }
            public long StartSize { get; private set; }
            public long EndSize { get; private set; }
            public int StartCount { get; private set; }
            public int EndCount { get; private set; }
            public double Percentage => UseCountUnit ? Math.Round((StartCount / (double)EndCount) * 100, 2) :
                                                       Math.Round((StartSize / (double)EndSize) * 100, 2);
            public long ProgressSpeed => (long)(StartSize / _TimeSecond);
            public TimeSpan RemainingTime => UseCountUnit ? TimeSpan.FromSeconds(0f) :
                                                            TimeSpan.FromSeconds((EndSize - StartSize) / Unzeroed(ProgressSpeed));
            private double Unzeroed(double i) => i == 0 ? 1 : i;
            public string ProgressStatus => _StatusMsg;
            public string ProgressDetail => string.Format(
                "[{0}] ({1})\r\n{2}...",
                UseCountUnit ? $"{StartCount}/{EndCount}" :
                               $"{SummarizeSizeSimple(StartSize)}/{SummarizeSizeSimple(EndSize)}",
                UseCountUnit ? $"{Percentage}%" :
                               string.Format("{0}% {1}/s - {2} left",
                                             Percentage,
                                             SummarizeSizeSimple(ProgressSpeed),
                                             string.Format("{0:%h}h{0:%m}m{0:%s}s", RemainingTime)),
                _DetailMsg
                );
        }
    }
}
